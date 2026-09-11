using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using UnityEditor.Compilation;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Validates everything currently staged under `&lt;projectRoot&gt;/
    /// UapStaging/` (design section 7.4/8.1 P1, staging location moved
    /// OUTSIDE Assets/ entirely by section 8.7) by compiling it into a
    /// throwaway assembly with UnityEditor.Compilation.AssemblyBuilder,
    /// then -- ONLY on a clean compile -- moves the staged files into
    /// Assets/ (Windows File.Copy+Delete, R11 section 5's File.Move
    /// overwrite trap) for the turn-end batched refresh (UapTurnScope) to
    /// pick up. A failed compile leaves the stage untouched and returns the
    /// compiler errors verbatim so the agent can fix them without ever
    /// having caused a domain reload.
    ///
    /// IMPORTANT implementation detail found empirically in an EARLIER
    /// version of this stream, when staging still lived under
    /// Assets/UapStaging~/ (kept here because the same risk would return
    /// if a future change ever moved staging back under Assets/): handing
    /// AssemblyBuilder source paths that live INSIDE Assets/ -- even under
    /// a tilde-suffixed folder -- makes Unity's own project-wide script
    /// compiler (the shared Tundra/bee_backend "ScriptAssemblies" graph)
    /// treat them as project sources too, causing real compile
    /// failures/conflicts against the main project the instant Build() is
    /// called (observed directly: a deliberately-broken staged file
    /// produced actual "Tundra build failed" errors in the SANDBOX
    /// PROJECT's own log, not just the isolated AssemblyBuilder result).
    /// The fix (still applied, now purely defensive since staging is
    /// outside Assets/ already): ALL staged files (.cs AND .asmdef) are
    /// copied to a throwaway mirror under Temp/ (OUTSIDE Assets/);
    /// AssemblyBuilder compiles the mirrored .cs and the asmdef JSON check
    /// runs against the mirrored copy; compiler messages are re-mapped
    /// back to the staged path for display.
    ///
    /// SEC-5 (TOCTOU): on success it is the MIRROR files -- the exact
    /// bytes that were validated -- that get copied into Assets/, never
    /// the originals still sitting under UapStaging/. AssemblyBuilder
    /// spans multiple editor ticks, so an agent (or anything else) can
    /// rewrite a staged file between validation and commit; with the
    /// mirror as the move source such a rewrite changes nothing about
    /// what lands in Assets/. The originals are deleted on success
    /// (commit consumes the stage), and the mirror is discarded only
    /// AFTER the move completes.
    ///
    /// Implements IUapPollableTool because AssemblyBuilder compiles
    /// asynchronously across editor ticks -- Execute() (the IUapTool
    /// member, used only by DirectUapToolExecutor/tests) busy-polls the
    /// SAME state machine for callers that bypass the dispatcher entirely.
    /// </summary>
    public sealed class UapScriptsCommitTool : IUapTool, IUapPollableTool
    {
        private sealed class CommitState
        {
            public ScriptCommitPipeline Pipeline;
            public AssemblyBuilder Builder;
            public volatile bool BuildFinishedFired;
            public CompilerMessage[] Messages;
            public List<string> StagedRelativePaths;
            public string StagingRoot;
            public string ProjectRoot;
            public string OutputDllPath;
            public string MirrorRoot;
            public Dictionary<string, string> MirrorToStagedRelative;
        }

        public string Name
        {
            get { return "uap_scripts_commit"; }
        }

        public string Description
        {
            get
            {
                return "Validates staged scripts (UapStaging/ at the project root, outside Assets/) by"
                    + " compiling them, then moves them into Assets/ ONLY if the compile is clean -- a"
                    + " failed compile leaves the stage untouched and returns the errors verbatim. Use"
                    + " after writing to the staging folder (direct Assets/*.cs writes are blocked by"
                    + " the script gate).";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject())
                    .Set("additionalProperties", false);
            }
        }

        /// <summary>Synchronous fallback for callers that bypass UapMainThreadDispatcher (DirectUapToolExecutor, tests). Production HTTP dispatch always uses Poll instead -- see IUapPollableTool's doc comment.</summary>
        public JsonNode Execute(JsonNode input)
        {
            object state = null;
            JsonNode result;
            while (!Poll(input, ref state, out result))
            {
                Thread.Sleep(10);
            }
            return result;
        }

        public bool Poll(JsonNode input, ref object stateObj, out JsonNode result)
        {
            var state = stateObj as CommitState;
            if (state == null)
            {
                state = BeginCommit();
                stateObj = state;
            }
            if (!state.BuildFinishedFired)
            {
                result = null;
                return false;
            }
            result = FinishCommit(state);
            return true;
        }

        private CommitState BeginCommit()
        {
            string projectRoot = GetProjectRoot();
            string stagingRoot = Path.Combine(projectRoot, "UapStaging");
            List<string> staged = ScriptStagingScanner.ListStagedScriptFiles(stagingRoot);
            var state = new CommitState
            {
                Pipeline = new ScriptCommitPipeline(),
                StagingRoot = stagingRoot,
                ProjectRoot = projectRoot,
                StagedRelativePaths = staged
            };

            if (staged.Count == 0)
            {
                state.Pipeline.MarkNothingStaged();
                state.BuildFinishedFired = true;
                return state;
            }

            string outputDir = Path.Combine(projectRoot, "Temp", "UapScriptsCommit");
            Directory.CreateDirectory(outputDir);
            string buildId = Guid.NewGuid().ToString("N");
            string outputDll = Path.Combine(outputDir, "UapStagingValidate_" + buildId + ".dll");
            string mirrorRoot = Path.Combine(outputDir, "src_" + buildId);
            Directory.CreateDirectory(mirrorRoot);
            state.MirrorRoot = mirrorRoot;

            // Mirror EVERY staged file OUTSIDE Assets/ before validating --
            // .cs for AssemblyBuilder (see this class's doc comment for why
            // source paths inside Assets/ are unsafe to hand it directly)
            // and .asmdef so the JSON check and the eventual commit read
            // the SAME bytes (SEC-5: the mirror is also the move source).
            var csRelativePaths = new List<string>();
            var sourceFiles = new List<string>();
            var mirrorToStagedRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string rel in staged)
                {
                    string stagedFull = Path.Combine(stagingRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    string mirrorFull = Path.Combine(mirrorRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    string mirrorDir = Path.GetDirectoryName(mirrorFull);
                    if (!string.IsNullOrEmpty(mirrorDir))
                    {
                        Directory.CreateDirectory(mirrorDir);
                    }
                    File.Copy(stagedFull, mirrorFull, true);
                    if (rel.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                    {
                        // Cheap fail-fast: a malformed asmdef would need the
                        // full project reimport we are trying to avoid to
                        // validate properly, so we only check that it is
                        // well-formed JSON -- against the MIRRORED copy, the
                        // bytes a successful commit will actually move.
                        JsonNode node;
                        string parseError;
                        if (!JsonParser.TryParse(File.ReadAllText(mirrorFull), out node, out parseError))
                        {
                            throw new InvalidOperationException("Malformed .asmdef '" + rel + "': " + parseError);
                        }
                    }
                    else
                    {
                        csRelativePaths.Add(rel);
                        sourceFiles.Add(mirrorFull);
                        mirrorToStagedRelative[Path.GetFullPath(mirrorFull)] = rel;
                    }
                }
            }
            catch
            {
                // Failed before a builder owns the mirror: do not leave the
                // orphaned src_<id> folder behind in Temp/.
                try
                {
                    Directory.Delete(mirrorRoot, true);
                }
                catch (IOException)
                {
                }
                throw;
            }
            state.MirrorToStagedRelative = mirrorToStagedRelative;

            if (csRelativePaths.Count == 0)
            {
                // Only .asmdef file(s) staged (already validated above as
                // well-formed JSON) -- nothing to hand AssemblyBuilder.
                state.Pipeline.MarkCompiling();
                state.Pipeline.CompleteCompilation(null);
                state.BuildFinishedFired = true;
                return state;
            }

            state.OutputDllPath = outputDll;

            var builder = new AssemblyBuilder(outputDll, sourceFiles.ToArray());
            builder.additionalReferences = GatherAdditionalReferences();
            // UseEngineModules swaps the default MONOLITHIC UnityEngine.dll
            // facade for the per-module engine DLLs (UnityEngine.CoreModule
            // included). Measured 2026-08-13 (design note
            // 2026-08-13-scripts-commit-cs0012): with the default set (246
            // refs, facade only) a staged script touching ANY type from an
            // already-compiled project assembly -- those are compiled
            // against the modules -- fails with CS0012 "You must add a
            // reference to assembly 'UnityEngine.CoreModule'" (reproduced
            // live with `class X : UnityEngine.UI.Selectable`, matching the
            // user-reported breakage). With UseEngineModules (312 refs) that
            // case AND the self-contained MonoBehaviour case both compile
            // clean, and the CS0433 duplicate-type failure that once forced
            // additionalReferences to stay UnityEditor-only does not return
            // (the facade in the module set carries forwarders, not types).
            builder.referencesOptions = ReferencesOptions.UseEngineModules;
            builder.buildFinished += delegate(string assemblyPath, CompilerMessage[] messages)
            {
                state.Messages = messages;
                state.BuildFinishedFired = true;
            };
            state.Builder = builder;
            state.Pipeline.MarkCompiling();
            if (!builder.Build())
            {
                throw new InvalidOperationException(
                    "Failed to start staged script compilation (already building, or no valid source files).");
            }
            return state;
        }

        private JsonNode FinishCommit(CommitState state)
        {
            if (state.Pipeline.Phase == ScriptCommitPhase.NothingStaged)
            {
                return UapToolResults.Text("Nothing staged under " + ScriptGate.StagingFolder + "; nothing to commit.");
            }
            if (state.Pipeline.Phase == ScriptCommitPhase.Compiling)
            {
                state.Pipeline.CompleteCompilation(ConvertMessages(state.Messages, state.MirrorToStagedRelative));
            }

            if (!state.Pipeline.ShouldMoveFilesToAssets)
            {
                CleanupBuildArtifacts(state);
                var sb = new StringBuilder();
                sb.Append("Compilation failed; nothing was moved into Assets/. Fix these errors and commit again:\n");
                foreach (ScriptCompileMessage error in state.Pipeline.Errors)
                {
                    sb.Append(error).Append('\n');
                }
                return UapToolResults.Text(sb.ToString());
            }

            var moved = new List<string>();
            foreach (string rel in state.StagedRelativePaths)
            {
                // SEC-5: the move SOURCE is the validated mirror copy, not
                // the original staged file -- AssemblyBuilder spans editor
                // ticks, and a staged file rewritten in that window must
                // not smuggle unvalidated bytes into Assets/. The mirror
                // still exists here: CleanupBuildArtifacts now runs AFTER
                // this loop (it used to run first, which would have deleted
                // the very files being committed).
                string src = Path.Combine(state.MirrorRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                string stagedOriginal = Path.Combine(state.StagingRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                string destRel = ScriptStagingScanner.ToAssetsRelativePath(rel);
                string dest = Path.Combine(state.ProjectRoot, "Assets", destRel.Replace('/', Path.DirectorySeparatorChar));
                string destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                // Windows File.Move refuses to overwrite an existing
                // destination (R11 section 5) -- Copy(overwrite:true) then
                // Delete the source is the documented-safe recommit path.
                File.Copy(src, dest, true);
                // Commit consumes the stage: the original staged file goes
                // away exactly as before, whatever its current content.
                File.Delete(stagedOriginal);
                moved.Add("Assets/" + destRel);
            }
            CleanupBuildArtifacts(state);
            // The "Committed " prefix comes from AutoContinueAfterCompilePolicy
            // (Model layer), not a second, independently-typed literal here
            // -- 2026-08-04 durability fix. AutoContinueAfterCompilePolicy.
            // ScriptCommitMovedFiles matches THIS exact prefix to decide
            // whether a domain reload may be attributed to the agent's own
            // work (Phase 5c L3 item 3, auto-continue after compile); before
            // this fix the two copies simply happened to agree, and nothing
            // would have caught a wording change to one of them silently
            // breaking that attribution while every test in both fixtures
            // stayed green.
            return UapToolResults.Text(AutoContinueAfterCompilePolicy.MovedFilesPrefix + moved.Count
                + " file(s) into Assets/ (validated, 0 compile errors): " + string.Join(", ", moved.ToArray())
                + ". They will be imported at the end of this turn (one batched refresh).");
        }

        /// <summary>Maps each message's file (the throwaway Temp/ mirror path) back to its UapStaging/-relative display path, so an error the agent sees points at the file it actually wrote.</summary>
        private static List<ScriptCompileMessage> ConvertMessages(CompilerMessage[] messages,
            Dictionary<string, string> mirrorToStagedRelative)
        {
            var result = new List<ScriptCompileMessage>();
            if (messages == null)
            {
                return result;
            }
            foreach (CompilerMessage m in messages)
            {
                string displayFile = m.file;
                string mappedRelative;
                if (mirrorToStagedRelative != null && !string.IsNullOrEmpty(m.file)
                    && mirrorToStagedRelative.TryGetValue(SafeFullPath(m.file), out mappedRelative))
                {
                    displayFile = ScriptGate.StagingFolder + mappedRelative;
                }
                result.Add(new ScriptCompileMessage
                {
                    File = displayFile,
                    Line = m.line,
                    Column = m.column,
                    Message = m.message,
                    IsError = m.type == CompilerMessageType.Error
                });
            }
            return result;
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        private static void CleanupBuildArtifacts(CommitState state)
        {
            TryDelete(state.OutputDllPath);
            if (!string.IsNullOrEmpty(state.OutputDllPath))
            {
                TryDelete(Path.ChangeExtension(state.OutputDllPath, ".pdb"));
                TryDelete(Path.ChangeExtension(state.OutputDllPath, ".dll.mdb"));
            }
            if (!string.IsNullOrEmpty(state.MirrorRoot) && Directory.Exists(state.MirrorRoot))
            {
                try
                {
                    Directory.Delete(state.MirrorRoot, true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only -- a locked temp file is not a commit failure.
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only -- a locked temp file is not a commit failure.
            }
        }

        /// <summary>
        /// UnityEditor.CoreModule.dll only (P1: "the only reference
        /// AssemblyBuilder does NOT auto-resolve from the project's own
        /// compiled assemblies"). Engine references are NOT added here:
        /// the Phase 5a attempt to append UnityEngine.CoreModule.dll on
        /// top of the default monolithic UnityEngine.dll produced CS0433
        /// (MonoBehaviour defined in both), and the default set alone
        /// produced the opposite CS0012 the moment a staged script touched
        /// a type from an already-compiled project assembly (measured
        /// 2026-08-13, design note 2026-08-13-scripts-commit-cs0012). The
        /// correct lever is referencesOptions = UseEngineModules on the
        /// builder (see BeginCommit), which REPLACES the monolithic facade
        /// with the module set instead of stacking a duplicate onto it.
        /// </summary>
        private static string[] GatherAdditionalReferences()
        {
            var refs = new List<string>();
            AddLocation(refs, typeof(UnityEditor.AssetDatabase));
            return refs.ToArray();
        }

        private static void AddLocation(List<string> refs, Type type)
        {
            string location = type.Assembly.Location;
            if (!string.IsNullOrEmpty(location) && !refs.Contains(location))
            {
                refs.Add(location);
            }
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }
}

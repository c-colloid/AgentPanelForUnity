using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// uLoop's execute-dynamic-code, native and safer (docs/design-notes/
    /// 2026-09-28-uloop-phase3.md, roadmap docs/design-notes/2026-09-22-
    /// uloop-full-native-roadmap.md section 4): compiles a snippet of C#
    /// (statements forming the body of a method returning object) with
    /// UnityEditor.Compilation.AssemblyBuilder -- the SAME API
    /// UapScriptsCommitTool already uses for staged-script validation, so
    /// this tool reuses its engine/precompiled reference sweep
    /// (<see cref="UapScriptsCommitTool.GatherAdditionalReferences"/>) and
    /// adds the project's own already-compiled Editor/Player assembly
    /// outputs on top (something a staged script does not need, since it
    /// is validated as a candidate to JOIN those assemblies rather than as
    /// a bystander that must reference them by name).
    ///
    /// Async exactly like uap_scripts_commit: <see cref="Poll"/> is what
    /// production dispatch calls (IUapPollableTool), so the compile's
    /// buildFinished (which fires on a LATER editor tick) never blocks the
    /// Unity main thread. A compile that outlives the dispatcher's wait is
    /// abandoned the same way SEC-7 abandons any other pollable
    /// (UapMainThreadDispatcher never polls it again); this tool has
    /// nothing held mid-hold to release (no key, no drag), so
    /// <see cref="OnAbandoned"/> only needs to stop tracking the in-flight
    /// build so a lingering buildFinished callback cannot try to load/run
    /// an assembly for a caller that already gave up.
    ///
    /// Safety model (roadmap section 4.3, the point of this tool):
    /// - Never auto-approved, at ANY auto-approve level including
    ///   AllUnityOps/AllTools -- see <see cref="IUapNeverAutoApproveTool"/>
    ///   for why <see cref="UapDestructiveToolBase"/>'s ordinary
    ///   confirm-gate is not enough by itself here.
    /// - CONDITIONALLY destructive, unlike UapDestructiveToolBase's
    ///   always-refuse-without-confirm dispatch: <see cref="IUapDestructiveTool"/>
    ///   is implemented DIRECTLY (not via the base class) because whether
    ///   confirm:true is required depends on the STATIC SCAN of this
    ///   particular call's code, decided at Execute/Poll time -- a snippet
    ///   with no risk flags runs on the first call, exactly like every
    ///   other non-destructive uap_* tool; a snippet the scan flags
    ///   returns a preview (the flags, why, the code hash) and runs
    ///   nothing until the SAME call is repeated with confirm:true.
    /// - The permission card ALWAYS appears anyway (ReadOnly is false,
    ///   Undoable is false, and IUapNeverAutoApproveTool blocks every
    ///   auto-approve level) and shows the full snippet -- see
    ///   ToolCardDescriber's dedicated uap_execute_code branch.
    /// </summary>
    public sealed class UapExecuteCodeTool : IUapTool, IUapPollableTool, IUapDestructiveTool,
        IUapAbandonableTool, IUapNeverAutoApproveTool
    {
        public const string ToolName = "uap_execute_code";

        // -- per-domain snippet cache (roadmap section 4.2: uLoop #949) ------

        private sealed class LoadedSnippet
        {
            public string Hash;
            public System.Reflection.Assembly Asm;
            public MethodInfo RunMethod;
            public long ApproxBytes;
            public string FirstLine;
            public long LoadedAtUtcTicks;
        }

        /// <summary>
        /// Keyed by content hash: identical (usings, code) never compiles
        /// or loads twice in this domain (roadmap section 4.2 item 1).
        /// Cleared only by a domain reload (Assembly.Load(byte[]) cannot be
        /// unloaded any other way) -- action:reset just REQUESTS one.
        /// </summary>
        private static readonly Dictionary<string, LoadedSnippet> _cache =
            new Dictionary<string, LoadedSnippet>(StringComparer.Ordinal);

        private static bool _prunedTempDirThisDomain;

        /// <summary>
        /// Hash -&gt; how many compiles FOR THAT HASH are currently in
        /// flight (not yet cached). Everything that touches this dictionary
        /// (Begin/Finish/OnAbandoned) runs on the Unity main thread only
        /// (via UapMainThreadDispatcher.Pump), so no lock is needed -- the
        /// hazard here is two INTERLEAVED calls across ticks, not two
        /// threads. Exists so two concurrent calls for the SAME (not yet
        /// cached) hash -- a real possibility: an agent retry racing the
        /// original after a perceived timeout, or two truly parallel tool
        /// calls -- never point two independent AssemblyBuilder instances
        /// at the exact same Temp/uap-exec/&lt;hash&gt;/*.dll output path,
        /// which risks a locked-file compile failure or one caller reading
        /// the other's half-written dll. See <see cref="ClaimInFlightDir"/>.
        /// </summary>
        private static readonly Dictionary<string, int> _inFlightCompiles =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Test seam: lets a fixture start from a clean cache/pruned-flag without a real domain reload.</summary>
        internal static void ResetForTests()
        {
            _cache.Clear();
            _inFlightCompiles.Clear();
            _prunedTempDirThisDomain = false;
        }

        /// <summary>
        /// Claims hash's in-flight slot and returns the Temp/uap-exec
        /// subdirectory NAME this compile should use: the hash itself the
        /// first time (the common case, unchanged from before), or a
        /// suffixed name for a second (or later) concurrent compile of the
        /// identical hash, so the two AssemblyBuilder instances never write
        /// to the same output file. Both eventually write into
        /// <see cref="_cache"/> under the SAME hash key (last one wins) --
        /// harmless, since identical hash means byte-identical wrapped
        /// source, so either loaded assembly is an equally valid answer.
        /// Paired with <see cref="ReleaseInFlight"/>, called exactly once
        /// per claim (Finish, the !started throw path, or OnAbandoned).
        /// </summary>
        private static string ClaimInFlightDir(string hash)
        {
            int count;
            _inFlightCompiles.TryGetValue(hash, out count);
            _inFlightCompiles[hash] = count + 1;
            return count == 0 ? hash : hash + "-c" + count.ToString(CultureInfo.InvariantCulture);
        }

        private static void ReleaseInFlight(string hash)
        {
            int count;
            if (!_inFlightCompiles.TryGetValue(hash, out count))
            {
                return;
            }
            if (count <= 1)
            {
                _inFlightCompiles.Remove(hash);
            }
            else
            {
                _inFlightCompiles[hash] = count - 1;
            }
        }

        // -- IUapTool metadata --------------------------------------------------

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Compiles and runs a one-off C# snippet (body of a method returning object) when no typed uap_*"
                    + " tool can do it; PREFER THE TYPED uap_* TOOLS. It is NEVER auto-approved at any level."
                    + " Input: code (required), usings (optional array added to the default System/System.Linq/"
                    + "System.Collections.Generic/UnityEngine/UnityEditor set), confirm (see below),"
                    + " action: run (default) / status / reset."
                    + " Compilation is asynchronous and does not block the Editor; compile errors come back as"
                    + " {line, column, message} relative to YOUR code."
                    + " Safety: a static scan flags risky APIs (starting a process, network calls, file/asset"
                    + " writes or deletes, clearing prefs, loading an assembly, reflection, native interop, unsafe"
                    + " code, Thread.Sleep, an unconditional loop with no break/return). Flags do not refuse, they"
                    + " require confirm:true: the first call without it returns a preview (flags, why, code hash)"
                    + " and runs nothing. THE SCAN IS A MITIGATION, NOT A SANDBOX: obfuscation can still evade it,"
                    + " so confirm:true means you trust the code. The permission card always shows the full snippet."
                    + " Refused while Unity is compiling; in Play Mode, scene changes are lost on exit."
                    + " It runs on the main thread with no time limit: avoid long-running or unbounded code."
                    + " Undo: own Undo group, but the reply says honestly whether any Undo record exists (only if"
                    + " the snippet itself calls Undo.RecordObject/etc.). Debug.Log output of the run is returned."
                    + " Snippet assemblies stay loaded until a domain reload; replies report loadedSnippets/"
                    + "loadedBytes and past " + UapExecuteCodePolicy.LoadedSnippetWarnThreshold
                    + " warn about action:reset (with confirm:true it requests a script reload);"
                    + " action:status shows the cache without running."
                    + " Procedure: uap_help topic:\"execute-code\".";
            }
        }

        public string Module
        {
            get { return "code"; }
        }

        /// <summary>
        /// Conservative false: whether a given run is undoable depends on
        /// whether the SNIPPET itself calls Undo.RecordObject/etc, which
        /// this metadata flag cannot express per-call -- the reply says so
        /// honestly instead (see the class doc comment). Also keeps the
        /// permission card's "not undoable" badge showing by default,
        /// which is the safe direction for an escape hatch like this one.
        /// </summary>
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
                    .Set("properties", JsonNode.NewObject()
                        .Set("code", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Statements forming the body of `object Run() { <code> }`."
                                + " Required for action:run. A trailing expression-less body returns null."))
                        .Set("usings", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string"))
                            .Set("description", "Extra namespaces (dotted identifiers only), added to the"
                                + " default System/System.Linq/System.Collections.Generic/UnityEngine/UnityEditor set."))
                        .Set("confirm", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Required to actually run a snippet the static scan flagged, or"
                                + " to apply action:reset. Without it, a flagged run or a reset only previews"
                                + " what would happen."))
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("run").Add("status").Add("reset"))
                            .Set("description", "'run' (default) compiles+executes. 'status' reports the"
                                + " loaded-snippet cache without running anything. 'reset' requests a script"
                                + " reload (confirm:true required) to free cached snippet assemblies.")))
                    .Set("additionalProperties", false);
            }
        }

        /// <summary>IUapDestructiveTool: what THIS call would do, without doing it. Never called by this class's own dispatch (see the class doc comment for why the confirm gate is conditional, not the base class's always-refuse one); kept so the interface's contract is honestly satisfiable for any future generic caller.</summary>
        public string Preview(JsonNode input)
        {
            if (input == null)
            {
                input = JsonNode.NewObject();
            }
            UapExecuteCodeAction action = UapExecuteCodePolicy.ParseAction(input["action"].AsString(null));
            if (action == UapExecuteCodeAction.Status)
            {
                return "Reports the loaded-snippet cache; runs nothing.";
            }
            if (action == UapExecuteCodeAction.Reset)
            {
                return "Requests a Unity script reload (EditorUtility.RequestScriptReload) to free "
                    + _cache.Count.ToString(CultureInfo.InvariantCulture) + " cached snippet assembly(ies);"
                    + " this reloads the domain.";
            }
            string code = input["code"].AsString(string.Empty);
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(code);
            return DescribeFlags(flags);
        }

        // -- synchronous fallback (DirectUapToolExecutor / tests) ------------

        public JsonNode Execute(JsonNode input)
        {
            object state = null;
            JsonNode result;
            while (!Poll(input, ref state, out result))
            {
                System.Threading.Thread.Sleep(10);
            }
            return result;
        }

        // -- pollable state machine ------------------------------------------

        private sealed class ExecState
        {
            public bool Done;
            public JsonNode Result;
            public string Hash;
            public string Code;
            public string DllPath;
            public volatile bool BuildFinishedFired;
            public CompilerMessage[] Messages;
            /// <summary>True from the moment this call claims an _inFlightCompiles slot (see ClaimInFlightDir) until it is released exactly once (Finish, the !started throw path, or OnAbandoned) -- guards against a double-release.</summary>
            public bool ClaimedInFlight;
        }

        public bool Poll(JsonNode input, ref object stateObj, out JsonNode result)
        {
            var state = stateObj as ExecState;
            if (state == null)
            {
                state = Begin(input);
                stateObj = state;
            }
            if (state.Done)
            {
                result = state.Result;
                return true;
            }
            if (!state.BuildFinishedFired)
            {
                result = null;
                return false;
            }
            result = Finish(state);
            return true;
        }

        /// <summary>
        /// SEC-7: a compile abandoned before it finishes has nothing to
        /// release that would affect the GAME (no held key/button/drag).
        /// It DOES need to release its <see cref="_inFlightCompiles"/> slot
        /// (docs/design-notes/2026-09-28-uloop-phase3.md lifecycle review):
        /// without this, an abandoned in-flight compile's hash would stay
        /// marked "in flight" forever (nobody ever calls Finish for it, per
        /// the dispatcher's SEC-7 contract -- see ProcessOnce), so every
        /// LATER call with the identical code would needlessly take the
        /// suffixed-directory branch in <see cref="ClaimInFlightDir"/> for
        /// the rest of this domain's life. Harmless either way (just an
        /// extra directory name), but there is no reason to leak it. The
        /// in-flight AssemblyBuilder keeps compiling on its own regardless;
        /// its buildFinished, if it ever fires, writes into an ExecState
        /// nobody polls again, which is harmless.
        /// </summary>
        public void OnAbandoned(object pollState)
        {
            var state = pollState as ExecState;
            if (state != null && state.ClaimedInFlight)
            {
                state.ClaimedInFlight = false;
                ReleaseInFlight(state.Hash);
            }
        }

        private ExecState Begin(JsonNode input)
        {
            if (input == null)
            {
                input = JsonNode.NewObject();
            }
            var state = new ExecState();
            UapExecuteCodeAction action = UapExecuteCodePolicy.ParseAction(input["action"].AsString(null));

            if (action == UapExecuteCodeAction.Status)
            {
                state.Done = true;
                state.Result = BuildStatusResult();
                return state;
            }

            bool confirm = input["confirm"].AsBool(false);

            if (action == UapExecuteCodeAction.Reset)
            {
                state.Done = true;
                if (!confirm)
                {
                    state.Result = UapToolResults.Text(
                        UapDestructiveToolBase.DescribeRefusal(ToolName, Preview(input)));
                    return state;
                }
                state.Result = ExecuteReset();
                return state;
            }

            // action:run ---------------------------------------------------
            string code = input["code"].AsString(null);
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("'code' is required for action:run.");
            }
            if (code.Length > UapExecuteCodePolicy.MaxCodeLength)
            {
                throw new ArgumentException("'code' is longer than the " + UapExecuteCodePolicy.MaxCodeLength
                    + "-character limit.");
            }
            if (EditorApplication.isCompiling)
            {
                throw new InvalidOperationException("Refused: Unity is compiling right now. Wait for uap_ping"
                    + " to answer (or uap_compile action:status), then try again.");
            }

            List<string> customUsings = ReadStringArray(input["usings"]);
            List<string> effectiveUsings = UapExecuteCodePolicy.BuildEffectiveUsings(customUsings);
            List<UapExecuteRiskFlag> flags = UapExecuteCodePolicy.ScanForRiskFlags(code);
            string hash = UapExecuteCodePolicy.ComputeHash(effectiveUsings, code);
            state.Hash = hash;
            state.Code = code;

            if (flags.Count > 0 && !confirm)
            {
                state.Done = true;
                state.Result = UapToolResults.Text(DescribeRunPreview(hash, flags));
                return state;
            }

            LoadedSnippet cached;
            if (_cache.TryGetValue(hash, out cached))
            {
                state.Done = true;
                state.Result = RunAndBuildResult(cached, code);
                return state;
            }

            // Not cached: compile it. Prune stale Temp/uap-exec leftovers
            // from a PREVIOUS domain the first time this tool compiles
            // anything in the current one (roadmap section 4.2 item 3 --
            // nothing from a prior domain is loaded here, so none of it is
            // worth keeping on disk either).
            PruneTempDirOnce();
            // Claim this hash's in-flight slot BEFORE picking the directory
            // name: a second concurrent call for the identical (not yet
            // cached) hash -- e.g. an agent's retry racing the original
            // after a perceived timeout -- gets its own suffixed directory
            // instead of pointing a second AssemblyBuilder at the exact
            // same output dll path (see ClaimInFlightDir's doc comment).
            state.ClaimedInFlight = true;
            string dirName = ClaimInFlightDir(hash);
            string projectRoot = GetProjectRoot();
            string snippetDir = Path.Combine(projectRoot, "Temp", "uap-exec", dirName);
            Directory.CreateDirectory(snippetDir);
            UapExecuteWrappedSource wrapped = UapExecuteCodePolicy.BuildWrappedSource(effectiveUsings, code, hash);
            string sourcePath = Path.Combine(snippetDir, "Snippet.cs");
            File.WriteAllText(sourcePath, wrapped.Source, new UTF8Encoding(false));
            string dllPath = Path.Combine(snippetDir, UapExecuteCodePolicy.DllFileName(hash));
            state.DllPath = dllPath;

            var builder = new AssemblyBuilder(dllPath, new[] { sourcePath });
            // Same lever UapScriptsCommitTool uses for engine types (see its
            // class comment): swap the monolithic UnityEngine.dll facade for
            // the per-module engine assemblies instead of stacking a second,
            // duplicate reference onto it.
            builder.referencesOptions = ReferencesOptions.UseEngineModules;
            builder.additionalReferences = GatherReferences(builder.defaultReferences);
            // CodeOptimization.Debug -> AssemblyBuilder writes a .pdb, which
            // is what lets a caught exception's stack trace report a real
            // line number to map back to the user's own code.
            builder.compilerOptions = new ScriptCompilerOptions { CodeOptimization = CodeOptimization.Debug };
            builder.buildFinished += delegate(string assemblyPath, CompilerMessage[] messages)
            {
                state.Messages = messages;
                state.BuildFinishedFired = true;
            };
            bool started = builder.Build();
            if (!started)
            {
                // Finish() will never run for this state (it never reached
                // BuildFinishedFired) -- release the in-flight claim here
                // instead, or it would leak for the rest of this domain.
                state.ClaimedInFlight = false;
                ReleaseInFlight(hash);
                throw new InvalidOperationException(
                    "Failed to start compiling the snippet (Unity is already compiling, or busy).");
            }
            return state;
        }

        private JsonNode Finish(ExecState state)
        {
            if (state.ClaimedInFlight)
            {
                state.ClaimedInFlight = false;
                ReleaseInFlight(state.Hash);
            }
            bool hasErrors = false;
            var mapped = new List<ScriptCompileMessage>();
            if (state.Messages != null)
            {
                for (int i = 0; i < state.Messages.Length; i++)
                {
                    CompilerMessage m = state.Messages[i];
                    bool isError = m.type == CompilerMessageType.Error;
                    hasErrors |= isError;
                    mapped.Add(new ScriptCompileMessage
                    {
                        File = "code",
                        Line = UapExecuteCodePolicy.MapCompilerLineToUserLine(m.line),
                        Column = m.column,
                        Message = m.message,
                        IsError = isError,
                    });
                }
            }
            if (hasErrors)
            {
                var sb = new StringBuilder("Compilation failed; nothing ran:\n");
                for (int i = 0; i < mapped.Count; i++)
                {
                    if (!mapped[i].IsError)
                    {
                        continue;
                    }
                    sb.Append("error(line ").Append(mapped[i].Line.ToString(CultureInfo.InvariantCulture))
                        .Append(",col ").Append(mapped[i].Column.ToString(CultureInfo.InvariantCulture))
                        .Append("): ").Append(mapped[i].Message).Append('\n');
                }
                TryDeleteSnippetFiles(state.DllPath);
                return UapToolResults.Text(sb.ToString());
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(state.DllPath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Compiled but could not read the snippet dll: " + ex.Message);
            }
            System.Reflection.Assembly asm = System.Reflection.Assembly.Load(bytes);
            string typeName = UapExecuteCodePolicy.SnippetNamespace + "." + UapExecuteCodePolicy.ClassName(state.Hash);
            Type type = asm.GetType(typeName, false);
            MethodInfo runMethod = type != null
                ? type.GetMethod(UapExecuteCodePolicy.RunMethodName, BindingFlags.Public | BindingFlags.Static)
                : null;
            if (runMethod == null)
            {
                throw new InvalidOperationException(
                    "Compiled successfully but could not find " + typeName + "." + UapExecuteCodePolicy.RunMethodName
                    + "() by reflection -- this should not happen; please report it.");
            }
            var loaded = new LoadedSnippet
            {
                Hash = state.Hash,
                Asm = asm,
                RunMethod = runMethod,
                ApproxBytes = bytes.LongLength,
                FirstLine = FirstLineOf(state.Code),
                LoadedAtUtcTicks = DateTime.UtcNow.Ticks,
            };
            _cache[state.Hash] = loaded;
            return RunAndBuildResult(loaded, state.Code);
        }

        // -- running a (freshly compiled or cached) snippet ------------------

        private JsonNode RunAndBuildResult(LoadedSnippet loaded, string code)
        {
            var logLines = new List<string>();
            int logChars = 0;
            Application.LogCallback logHandler = delegate(string condition, string stackTrace, LogType type)
            {
                if (logLines.Count >= UapExecuteCodePolicy.MaxLogCapturedLines
                    || logChars >= UapExecuteCodePolicy.MaxLogCapturedChars)
                {
                    return;
                }
                string line = "[" + type + "] " + condition;
                logChars += line.Length;
                logLines.Add(line);
            };

            List<string> undoRecordsBefore = TryGetUndoRecordsForTests != null
                ? TryGetUndoRecordsForTests() : TryReadUndoRecords();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(ToolName);

            object returnValue = null;
            Exception thrown = null;
            Application.logMessageReceived += logHandler;
            try
            {
                returnValue = loaded.RunMethod.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                thrown = tie.InnerException ?? tie;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                Application.logMessageReceived -= logHandler;
            }
            // Close our sub-group. Whatever the snippet recorded (if
            // anything) stays attributed to the group we just named above;
            // this second increment only marks its END so a later,
            // unrelated Undo.RecordObject in this same Editor turn does not
            // silently keep piling into a group still named "uap_execute_code".
            Undo.IncrementCurrentGroup();
            List<string> undoRecordsAfter = TryGetUndoRecordsForTests != null
                ? TryGetUndoRecordsForTests() : TryReadUndoRecords();
            bool? undoRecorded = (undoRecordsBefore != null && undoRecordsAfter != null)
                ? (bool?)(undoRecordsAfter.Count > undoRecordsBefore.Count)
                : null;

            var json = JsonNode.NewObject().Set("action", "run").Set("hash", loaded.Hash);
            if (thrown != null)
            {
                json.Set("threw", true)
                    .Set("exceptionType", thrown.GetType().FullName ?? thrown.GetType().Name)
                    .Set("message", thrown.Message ?? string.Empty);
                int mappedLine = TryMapExceptionLine(thrown);
                if (mappedLine > 0)
                {
                    json.Set("line", mappedLine);
                }
            }
            else
            {
                json.Set("threw", false)
                    .Set("returnValue", UapExecuteCodePolicy.ShapeReturnValue(returnValue, TryDescribeUnityObject));
            }
            json.Set("logs", ToJsonArray(logLines));
            if (undoRecorded.HasValue)
            {
                json.Set("undoRecorded", undoRecorded.Value);
                json.Set("undoNote", undoRecorded.Value
                    ? "This run produced Undo record(s); it is undoable via the turn's Ctrl+Z."
                    : "This run produced no Undo records Unity is aware of; any changes it made cannot be"
                      + " reverted with Ctrl+Z.");
            }
            else
            {
                json.Set("undoNote", "Could not determine whether this run produced Undo records.");
            }
            if (EditorApplication.isPlaying)
            {
                json.Set("playModeWarning", "Play Mode is running: any changes to scene objects are lost when"
                    + " Play Mode exits.");
            }
            int loadedCount = _cache.Count;
            long loadedBytes = 0;
            foreach (LoadedSnippet entry in _cache.Values)
            {
                loadedBytes += entry.ApproxBytes;
            }
            json.Set("loadedSnippets", loadedCount).Set("loadedBytes", loadedBytes);
            string memoryWarning = UapExecuteCodePolicy.DescribeMemoryWarning(loadedCount);
            if (memoryWarning.Length > 0)
            {
                json.Set("memoryWarning", memoryWarning);
            }
            return UapToolResults.Text(JsonWriter.Write(json));
        }

        /// <summary>Test seam: substitutes the (reflection-based, best-effort) Undo record probe so a fixture can assert the honesty path deterministically without depending on Unity's real Undo internals.</summary>
        internal static Func<List<string>> TryGetUndoRecordsForTests;

        private static int TryMapExceptionLine(Exception ex)
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(ex, true);
                System.Diagnostics.StackFrame[] frames = trace.GetFrames();
                if (frames == null)
                {
                    return 0;
                }
                for (int i = 0; i < frames.Length; i++)
                {
                    MethodBase method = frames[i].GetMethod();
                    Type declaring = method != null ? method.DeclaringType : null;
                    if (declaring != null && declaring.Namespace == UapExecuteCodePolicy.SnippetNamespace)
                    {
                        int fileLine = frames[i].GetFileLineNumber();
                        if (fileLine > 0)
                        {
                            return UapExecuteCodePolicy.MapCompilerLineToUserLine(fileLine);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort only -- a stack trace that cannot be read is
                // never a reason to fail an otherwise-reported exception.
            }
            return 0;
        }

        /// <summary>
        /// Best-effort probe into UnityEditor.Undo's PRIVATE record list
        /// (internal static Undo.GetRecords(List&lt;string&gt;, out int),
        /// verified against UnityCsReference's Editor/Mono/Undo/Undo.bindings.cs
        /// -- see the design note): there is no PUBLIC API that reports
        /// whether a call recorded anything, only ones that report the
        /// CURRENT group INDEX (Undo.GetCurrentGroup()), which does not
        /// change just because a record was added to it. Reflection into an
        /// internal engine method can break on a future Unity version with
        /// no warning; every caller treats a null result (reflection
        /// failed) as "unknown" rather than guessing, and the reply says so
        /// honestly instead of asserting a wrong answer.
        /// </summary>
        private static MethodInfo _getRecordsMethod;
        private static bool _getRecordsResolved;

        private static List<string> TryReadUndoRecords()
        {
            try
            {
                if (!_getRecordsResolved)
                {
                    _getRecordsResolved = true;
                    _getRecordsMethod = typeof(Undo).GetMethod("GetRecords",
                        BindingFlags.NonPublic | BindingFlags.Static, null,
                        new[] { typeof(List<string>), typeof(int).MakeByRefType() }, null);
                }
                if (_getRecordsMethod == null)
                {
                    return null;
                }
                var list = new List<string>();
                object[] args = { list, 0 };
                _getRecordsMethod.Invoke(null, args);
                return list;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryDescribeUnityObject(object value, out string name, out string typeName, out string path)
        {
            name = null;
            typeName = null;
            path = null;
            var obj = value as UnityEngine.Object;
            if (obj == null)
            {
                return false;
            }
            name = obj.name;
            typeName = obj.GetType().FullName;
            var component = obj as Component;
            Transform transform = component != null ? component.transform : obj as Transform;
            if (transform == null)
            {
                var go = obj as GameObject;
                if (go != null)
                {
                    transform = go.transform;
                }
            }
            if (transform != null && transform.gameObject.scene.IsValid())
            {
                path = UapAddressing.DescribeHierarchyPath(transform);
            }
            else
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                path = string.IsNullOrEmpty(assetPath) ? string.Empty : assetPath;
            }
            return true;
        }

        // -- action:status / action:reset ------------------------------------

        private JsonNode BuildStatusResult()
        {
            int loadedCount = _cache.Count;
            long loadedBytes = 0;
            JsonNode cacheArray = JsonNode.NewArray();
            foreach (LoadedSnippet entry in _cache.Values)
            {
                loadedBytes += entry.ApproxBytes;
                cacheArray.Add(JsonNode.NewObject()
                    .Set("hash", entry.Hash)
                    .Set("firstLine", entry.FirstLine ?? string.Empty)
                    .Set("bytes", entry.ApproxBytes)
                    .Set("loadedAt", new DateTime(entry.LoadedAtUtcTicks, DateTimeKind.Utc)
                        .ToString("o", CultureInfo.InvariantCulture)));
            }
            var json = JsonNode.NewObject()
                .Set("action", "status")
                .Set("loadedSnippets", loadedCount)
                .Set("loadedBytes", loadedBytes)
                .Set("cache", cacheArray);
            string warning = UapExecuteCodePolicy.DescribeMemoryWarning(loadedCount);
            if (warning.Length > 0)
            {
                json.Set("memoryWarning", warning);
            }
            return UapToolResults.Text(JsonWriter.Write(json));
        }

        private JsonNode ExecuteReset()
        {
            int freed = _cache.Count;
            // Deferred exactly like uap_compile's action:request (see that
            // class's own comment): RequestScriptReload triggers a domain
            // reload, which would tear down the HTTP server before this
            // call's own response reaches the worker thread if it ran
            // inline.
            Scheduler(delegate { EditorUtility.RequestScriptReload(); });
            var json = JsonNode.NewObject()
                .Set("action", "reset")
                .Set("message", "Script reload requested; scheduled to run right after this reply. "
                    + freed.ToString(CultureInfo.InvariantCulture) + " cached snippet assembly(ies) will be freed.")
                .Set("next", "Wait for uap_ping to answer, then call uap_execute_code action:status to confirm.");
            return UapToolResults.Text(JsonWriter.Write(json));
        }

        /// <summary>Test seam mirroring UapCompileTool.Scheduler: production is <see cref="ScheduleAfterResponse"/>; a fixture substitutes a recorder so it can prove ExecuteReset DEFERRED the reload request without a test run actually reloading the domain mid-suite.</summary>
        internal static Action<Action> Scheduler = ScheduleAfterResponse;

        /// <summary>Same "schedule a callback shortly after this reply" idiom as UapCompileTool.ScheduleAfterResponse/UapPlayModeTool -- kept as its own small copy rather than reaching into UapCompileTool's internals, since the two tools' scheduling needs are otherwise unrelated.</summary>
        private static void ScheduleAfterResponse(Action action)
        {
            double dueAt = EditorApplication.timeSinceStartup + 0.25;
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                if (EditorApplication.timeSinceStartup < dueAt)
                {
                    return;
                }
                EditorApplication.update -= tick;
                action();
            };
            EditorApplication.update += tick;
        }

        // -- preview / flag text ----------------------------------------------

        private static string DescribeRunPreview(string hash, List<UapExecuteRiskFlag> flags)
        {
            return "Refused: this snippet's static scan found " + flags.Count.ToString(CultureInfo.InvariantCulture)
                + " risk flag(s) and requires confirm:true to run. Nothing was compiled or executed. "
                + DescribeFlags(flags) + " Code hash: " + hash + ". Re-issue the SAME code with confirm:true"
                + " to run it.";
        }

        private static string DescribeFlags(List<UapExecuteRiskFlag> flags)
        {
            if (flags == null || flags.Count == 0)
            {
                return "No risk flags.";
            }
            var sb = new StringBuilder("Flags: ");
            for (int i = 0; i < flags.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append('[').Append(flags[i].Id).Append("] ").Append(flags[i].Message);
            }
            return sb.ToString();
        }

        // -- references (roadmap section 4.1) --------------------------------

        /// <summary>
        /// Engine/precompiled references (via UapScriptsCommitTool's own
        /// sweep, reused rather than duplicated) PLUS every already-
        /// compiled project Editor and Player assembly output -- something
        /// uap_scripts_commit does not need (a staged script is validated
        /// as a candidate to JOIN those assemblies, not as a bystander that
        /// must reference them by name), but a standalone snippet that
        /// wants to call a project type (docs/design-notes/2026-09-28-
        /// uloop-phase3.md section 4) does. Deduped by FILE NAME against
        /// what is already there (same guard as
        /// UapScriptsCommitTool.GatherAdditionalReferences, for the same
        /// CS0433-duplicate-type reason) -- a project assembly that
        /// defaultReferences/the precompiled sweep already carries is never
        /// added twice.
        /// </summary>
        internal static string[] GatherReferences(string[] defaultReferences)
        {
            var refs = new List<string>(UapScriptsCommitTool.GatherAdditionalReferences(defaultReferences));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFileNames(seen, defaultReferences);
            AddFileNames(seen, refs);
            AddProjectAssemblies(refs, seen, SafeGetAssemblies(AssembliesType.Editor));
            AddProjectAssemblies(refs, seen, SafeGetAssemblies(AssembliesType.Player));
            return refs.ToArray();
        }

        private static UnityEditor.Compilation.Assembly[] SafeGetAssemblies(AssembliesType type)
        {
            try
            {
                return CompilationPipeline.GetAssemblies(type);
            }
            catch (Exception)
            {
                // Never fail a run over the reference sweep -- the
                // engine/precompiled set alone is still a valid (if less
                // capable) build, same stance as GatherAdditionalReferences.
                return new UnityEditor.Compilation.Assembly[0];
            }
        }

        private static void AddProjectAssemblies(List<string> refs, HashSet<string> seen,
            UnityEditor.Compilation.Assembly[] assemblies)
        {
            if (assemblies == null)
            {
                return;
            }
            for (int i = 0; i < assemblies.Length; i++)
            {
                string outputPath = assemblies[i].outputPath;
                if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
                {
                    continue;
                }
                if (!seen.Add(Path.GetFileName(outputPath)))
                {
                    continue;
                }
                refs.Add(outputPath);
            }
        }

        private static void AddFileNames(HashSet<string> seen, IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return;
            }
            foreach (string path in paths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    seen.Add(Path.GetFileName(path));
                }
            }
        }

        // -- misc helpers -----------------------------------------------------

        private static List<string> ReadStringArray(JsonNode node)
        {
            if (node == null || !node.IsArray)
            {
                return null;
            }
            var result = new List<string>();
            foreach (JsonNode item in node.Items)
            {
                result.Add(item.AsString(string.Empty));
            }
            return result;
        }

        private static JsonNode ToJsonArray(List<string> lines)
        {
            JsonNode array = JsonNode.NewArray();
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    array.Add(lines[i]);
                }
            }
            return array;
        }

        private static string FirstLineOf(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.Empty;
            }
            int newline = code.IndexOf('\n');
            string first = newline >= 0 ? code.Substring(0, newline) : code;
            first = first.TrimEnd('\r').Trim();
            const int max = 80;
            return first.Length > max ? first.Substring(0, max) + "..." : first;
        }

        private static void TryDeleteSnippetFiles(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath))
            {
                return;
            }
            TryDelete(dllPath);
            TryDelete(Path.ChangeExtension(dllPath, ".pdb"));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only, same stance as UapScriptsCommitTool.TryDelete.
            }
        }

        /// <summary>Deletes every existing Temp/uap-exec/&lt;hash&gt; folder ONCE per domain (roadmap section 4.2 item 3: nothing on disk from a previous domain is loaded in THIS one, so none of it is worth keeping).</summary>
        private static void PruneTempDirOnce()
        {
            if (_prunedTempDirThisDomain)
            {
                return;
            }
            _prunedTempDirThisDomain = true;
            string dir = Path.Combine(GetProjectRoot(), "Temp", "uap-exec");
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException)
            {
                // Best-effort: a locked leftover file is not a reason to refuse this run.
            }
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }
}

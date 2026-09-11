using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Materializes PanelSettings.agentModelOverrides (Settings "Model"
    /// section detail table, docs/design-notes/2026-08-01-model-settings.md
    /// section 2) as `&lt;project&gt;/.claude/agents/&lt;AgentName&gt;.md` files,
    /// instead of the `--agents '&lt;json&gt;'` CLI argument the feature
    /// originally shipped with.
    ///
    /// Why: docs/research/07-model-configuration.md section 10 found that
    /// `--agents` is silently ignored by the CLI whenever it is combined
    /// with `--resume` (no error anywhere -- `result.modelUsage` simply
    /// never shows the overridden model), and the panel always spawns with
    /// `--resume` once a session exists. A `.claude/agents/*.md` file, by
    /// contrast, survives `--resume` -- including when the override
    /// targets a BUILT-IN agent name like "general-purpose" (section
    /// 10.2/10.5: the file simply replaces the built-in definition for
    /// that name, and a minimal `model:`-only frontmatter with an empty
    /// body was verified to leave the agent's tool access fully intact --
    /// section 10.5).
    ///
    /// IMPORTANT correction (section 10.8, "capture15" -- read this before
    /// touching call sites): the CLI does NOT reread `.claude/agents/*.md`
    /// on every process start. It snapshots the directory's contents at
    /// SESSION CREATION; `--resume` of an EXISTING session id replays that
    /// snapshot and never rescans disk. Section 10.3/10.4's "resume still
    /// works" result held only because the file already existed before
    /// that session was first created -- it does NOT mean editing the
    /// table and reconnecting an ALREADY-RUNNING session will ever pick up
    /// the change (it will not, no matter how many times you reconnect).
    /// The only spawn that observes a fresh Sync() is one with
    /// resumeSessionId == null (AgentHub.StartClient) -- i.e. a brand-new
    /// session, started from the header "+". Sync() is still called before
    /// EVERY spawn (resumed or new) so that a new session always gets the
    /// current table, but a resumed spawn's call to Sync() is effectively
    /// a no-op as far as that session's own subagents are concerned.
    ///
    /// File format (section 10.5/10.6, verified against a live CLI):
    /// <code>
    /// ---
    /// model: &lt;ModelAlias&gt;
    /// # unity-agent-panel:managed - regenerated from Settings &gt; Model overrides; do not edit by hand
    /// ---
    /// </code>
    /// `name`/`description` and a non-empty body are deliberately omitted --
    /// section 10.5 confirmed the CLI still lists, spawns and grants full
    /// tool access to a built-in agent name overridden this way.
    ///
    /// Ownership / never touch a hand-authored file: every file this class
    /// writes carries the marker comment above (MarkerToken). Sync() only
    /// ever overwrites or deletes a file that already carries that marker;
    /// a `.claude/agents/&lt;name&gt;.md` the user wrote by hand is left
    /// completely alone even if an override with that same agent name
    /// exists in the table (the override is simply skipped, with one log
    /// line, rather than clobbering the user's file).
    ///
    /// Contracts (mirroring CustomInstructionsFile/QuickActionStore):
    /// - Sync() is atomic per file (tmp file + replace) and never throws;
    ///   failures log one line through the injectable logger.
    /// - Pure C# + System.IO only (unit-testable without Unity
    ///   serialization).
    /// </summary>
    public sealed class AgentDefinitionFileWriter
    {
        /// <summary>Default agents directory, relative to the project root.</summary>
        public const string DefaultRelativePath = ".claude/agents";

        /// <summary>
        /// Substring identifying a file this class owns. Checked with a
        /// plain Contains (not an exact-line match) so the marker still
        /// works if the surrounding comment wording ever changes.
        /// </summary>
        internal const string MarkerToken = "unity-agent-panel:managed";

        private static readonly Regex ValidAgentNamePattern =
            new Regex("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

        private readonly string _directoryPath;
        private readonly Action<string> _log;

        public AgentDefinitionFileWriter(string directoryPath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException("directoryPath is required", "directoryPath");
            }
            _directoryPath = directoryPath;
            _log = log;
        }

        /// <summary>Absolute path of the managed agents directory.</summary>
        public string DirectoryPath
        {
            get { return _directoryPath; }
        }

        /// <summary>Writer rooted at the default location under the project root.</summary>
        public static AgentDefinitionFileWriter CreateDefault(string projectRoot, Action<string> log = null)
        {
            string relative = DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar);
            return new AgentDefinitionFileWriter(Path.Combine(projectRoot ?? ".", relative), log);
        }

        // -- Sync ------------------------------------------------------------

        /// <summary>
        /// Reconciles the agents directory with the override table: writes
        /// one file per valid entry (non-empty, filename-safe AgentName and
        /// a non-empty ModelAlias; later duplicates of the same AgentName
        /// overwrite earlier ones, matching the Settings UI's documented
        /// "last row wins" duplicate-name behavior), leaves any file it
        /// doesn't own untouched, and deletes previously panel-owned files
        /// that are no longer in the table. Never throws; every failure
        /// logs one line and Sync continues with the remaining entries.
        /// </summary>
        public void Sync(List<AgentModelOverride> overrides)
        {
            Dictionary<string, string> desired = BuildDesiredMap(overrides);

            bool directoryExists = Directory.Exists(_directoryPath);
            if (!directoryExists)
            {
                if (desired.Count == 0)
                {
                    // Nothing to write and nothing to clean up -- do not
                    // create an empty .claude/agents directory as a side
                    // effect of a no-op Sync.
                    return;
                }
                try
                {
                    Directory.CreateDirectory(_directoryPath);
                }
                catch (Exception ex)
                {
                    Log("Failed to create agents directory '" + _directoryPath + "': " + ex.Message);
                    return;
                }
            }

            var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in desired)
            {
                string filePath = Path.Combine(_directoryPath, entry.Key + ".md");
                if (TryWriteOne(filePath, entry.Key, entry.Value))
                {
                    keepPaths.Add(filePath);
                }
            }

            CleanupStaleFiles(keepPaths);
        }

        /// <summary>
        /// Builds the AgentName -&gt; ModelAlias map that will be materialized,
        /// skipping null entries, entries with an empty AgentName/ModelAlias,
        /// filename-unsafe AgentNames, and ModelAlias values that could not
        /// be embedded in the single-line YAML frontmatter safely (contains
        /// a newline). Later entries win over earlier ones sharing the same
        /// AgentName, mirroring the Settings UI's documented duplicate-row
        /// behavior ("only the last one will actually apply").
        /// </summary>
        private Dictionary<string, string> BuildDesiredMap(List<AgentModelOverride> overrides)
        {
            // MODEL-9: OrdinalIgnoreCase, matching the case-insensitive
            // filesystems this writes to (Windows/macOS default) and the
            // keepPaths comparison Sync already does. With Ordinal,
            // 'Explore' and 'explore' were two map keys colliding on ONE
            // Explore.md file, and the keep-set bookkeeping disagreed with
            // the map about what existed. Case-variant duplicates now fold
            // into one entry, last row wins -- the same documented
            // duplicate-row rule as exact duplicates.
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (overrides == null)
            {
                return result;
            }
            for (int i = 0; i < overrides.Count; i++)
            {
                AgentModelOverride entry = overrides[i];
                if (entry == null || string.IsNullOrEmpty(entry.agentName)
                    || string.IsNullOrEmpty(entry.modelAlias))
                {
                    continue;
                }
                if (!IsValidAgentName(entry.agentName))
                {
                    Log("Skipping agent model override for '" + entry.agentName
                        + "': agent names may only contain letters, digits, '-' and '_',"
                        + " and may not be a Windows reserved device name.");
                    continue;
                }
                if (entry.modelAlias.IndexOf('\n') >= 0 || entry.modelAlias.IndexOf('\r') >= 0)
                {
                    Log("Skipping agent model override for '" + entry.agentName
                        + "': model alias must be a single line.");
                    continue;
                }
                if (!IsYamlSafeModelAlias(entry.modelAlias))
                {
                    // MODEL-10: BuildFileContent embeds the alias as a bare
                    // YAML plain scalar ('model: <alias>'). ' #' starts a
                    // YAML comment mid-value, ': ' re-keys the line, and a
                    // leading indicator character changes how the scalar
                    // parses -- any of these silently truncates or corrupts
                    // the value the CLI reads back. Rejecting beats quoting:
                    // section 10.6 measured the exact bare form the CLI
                    // accepts, and no real model alias contains these.
                    Log("Skipping agent model override for '" + entry.agentName
                        + "': model alias '" + entry.modelAlias
                        + "' contains YAML-unsafe characters (a '#' comment, a ':', or a leading indicator).");
                    continue;
                }
                result[entry.agentName] = entry.modelAlias;
            }
            return result;
        }

        /// <summary>
        /// Writes (or refreshes) one managed file. Returns true when the
        /// file is now in a state Sync should keep track of (written by
        /// this call, or already correct); false when the path is occupied
        /// by a file this class does not own (never overwritten) or the
        /// write failed (logged; the stale/absent file is left as-is).
        /// </summary>
        private bool TryWriteOne(string filePath, string agentName, string modelAlias)
        {
            string desiredContent = BuildFileContent(modelAlias);
            try
            {
                if (File.Exists(filePath))
                {
                    string existing = File.ReadAllText(filePath, Encoding.UTF8);
                    if (!HasMarker(existing))
                    {
                        Log("Skipping agent model override for '" + agentName + "': '" + filePath
                            + "' already exists and is not managed by the panel.");
                        return false;
                    }
                    if (string.Equals(existing, desiredContent, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                AtomicWrite(filePath, desiredContent);
                return true;
            }
            catch (Exception ex)
            {
                Log("Failed to write agent definition '" + filePath + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Deletes every panel-owned (marker-carrying) *.md file in the
        /// directory that is not in keepPaths -- i.e. an override the
        /// table used to have and no longer does. Files without the
        /// marker (hand-authored by the user) are never inspected for
        /// deletion eligibility beyond the marker check itself.
        /// </summary>
        private void CleanupStaleFiles(HashSet<string> keepPaths)
        {
            string[] existingFiles;
            try
            {
                existingFiles = Directory.GetFiles(_directoryPath, "*.md");
            }
            catch (Exception ex)
            {
                Log("Failed to list agents directory '" + _directoryPath + "': " + ex.Message);
                return;
            }
            for (int i = 0; i < existingFiles.Length; i++)
            {
                string path = existingFiles[i];
                if (keepPaths.Contains(path))
                {
                    continue;
                }
                string content;
                try
                {
                    content = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log("Failed to read '" + path + "' during agent-file cleanup: " + ex.Message);
                    continue;
                }
                if (!HasMarker(content))
                {
                    continue;
                }
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log("Failed to delete stale agent definition '" + path + "': " + ex.Message);
                }
            }
        }

        // -- Pure helpers (unit-tested directly) ---------------------------------

        /// <summary>
        /// Filename-safe agent name: letters, digits, '-' and '_' only --
        /// and (MODEL-9) not a Windows reserved device name. CON.md,
        /// NUL.md etc. pass the character check but cannot be created as
        /// ordinary files on Windows (CreateFile resolves the DEVICE, with
        /// anything-goes behavior from hangs to phantom successes), so
        /// they are refused up front like any other invalid name. A name
        /// of only '-'/'_' is also refused -- it survives the character
        /// class but is a degenerate filename with no identifying content.
        /// </summary>
        internal static bool IsValidAgentName(string agentName)
        {
            if (string.IsNullOrEmpty(agentName) || !ValidAgentNamePattern.IsMatch(agentName))
            {
                return false;
            }
            bool hasIdentifyingChar = false;
            for (int i = 0; i < agentName.Length; i++)
            {
                if (agentName[i] != '-' && agentName[i] != '_')
                {
                    hasIdentifyingChar = true;
                    break;
                }
            }
            if (!hasIdentifyingChar)
            {
                return false;
            }
            for (int i = 0; i < WindowsReservedNames.Length; i++)
            {
                if (string.Equals(agentName, WindowsReservedNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>MODEL-9: names Windows reserves for devices regardless of extension.</summary>
        private static readonly string[] WindowsReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// MODEL-10: safe to embed as the bare YAML plain scalar
        /// BuildFileContent emits. Rejects ' #' (starts a comment
        /// mid-value), ': ' and a trailing ':' (re-keys the line), and a
        /// leading YAML indicator/quote character (changes how the scalar
        /// parses). Every real model alias ('haiku', 'claude-haiku-4', a
        /// full model id) passes untouched.
        /// </summary>
        internal static bool IsYamlSafeModelAlias(string modelAlias)
        {
            if (string.IsNullOrEmpty(modelAlias))
            {
                return false;
            }
            if (modelAlias.IndexOf(" #", StringComparison.Ordinal) >= 0
                || modelAlias.IndexOf("\t#", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            if (modelAlias.IndexOf(": ", StringComparison.Ordinal) >= 0
                || modelAlias.EndsWith(":", StringComparison.Ordinal))
            {
                return false;
            }
            const string leadingIndicators = "!&*-?|>%@`\"'#,[]{}";
            if (leadingIndicators.IndexOf(modelAlias[0]) >= 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// The exact minimal frontmatter body verified in docs/research/
        /// 07-model-configuration.md section 10.6 -- `model:` only, the
        /// panel-ownership marker as a YAML comment inside the frontmatter
        /// block, and an empty body (so an overridden built-in agent keeps
        /// its full default tool access).
        /// </summary>
        internal static string BuildFileContent(string modelAlias)
        {
            return "---\n"
                + "model: " + modelAlias + "\n"
                + "# " + MarkerToken + " - regenerated from Settings > Model overrides;"
                + " do not edit by hand\n"
                + "---\n";
        }

        /// <summary>True when the file content carries this class's ownership marker.</summary>
        internal static bool HasMarker(string content)
        {
            return content != null && content.IndexOf(MarkerToken, StringComparison.Ordinal) >= 0;
        }

        // -- Small helpers ------------------------------------------------------------

        private static void AtomicWrite(string filePath, string content)
        {
            // Shared MODEL-2 helper: staged tmp + swap, with the previous
            // generation parked as ".bak" through the fallback instead of
            // deleted (the old local copy of this dance had a crash window
            // that lost both files). Throwing variant on purpose -- this
            // class's callers catch and log per file.
            Colloid.AgentPanel.Core.FileIo.AtomicFile.WriteAllTextOrThrow(filePath, content);
        }

        private void Log(string message)
        {
            Action<string> log = _log;
            if (log != null)
            {
                log(message);
            }
        }
    }
}

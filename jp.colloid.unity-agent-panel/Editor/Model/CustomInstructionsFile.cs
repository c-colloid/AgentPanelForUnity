using System;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Persists the "Custom instructions" text (Settings UI, docs/design-
    /// notes/2026-08-01-settings-enrichment.md #1 -- appended to the CLI's
    /// system prompt via AgentClientOptions.AppendSystemPrompt /
    /// --append-system-prompt) as a plain UTF-8 text file (default:
    /// UserSettings/AgentPanel/CustomInstructions.txt), NOT inside
    /// PanelSettings/PanelStateStore's UnityYAML asset.
    ///
    /// Why not the .asset: this text is free-form and can be code-shaped
    /// (brace-heavy) just like a quick-action prompt or the transcript text
    /// SessionCacheFile already had to move out of UnityYAML for the same
    /// reason (see that class's doc comment for the State.asset corruption
    /// precedent) -- one bad string there corrupted the whole settings
    /// asset. A plain text sidecar has no such blast radius.
    ///
    /// Contracts (mirroring SessionCacheFile):
    /// - Save() is atomic (tmp file + replace) and never throws; failures
    ///   log one line through the injectable logger.
    /// - Load() never throws: a missing file returns empty text silently;
    ///   any IO failure logs one line, deletes the bad file and returns
    ///   empty text so a broken sidecar can never wedge the panel or leave
    ///   a stale value behind.
    /// - Pure C# + System.IO only (unit-testable without Unity
    ///   serialization).
    /// </summary>
    public sealed class CustomInstructionsFile
    {
        /// <summary>Default sidecar location relative to the project root.</summary>
        public const string DefaultRelativePath = "UserSettings/AgentPanel/CustomInstructions.txt";

        private readonly string _filePath;
        private readonly Action<string> _log;

        public CustomInstructionsFile(string filePath, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("filePath is required", "filePath");
            }
            _filePath = filePath;
            _log = log;
        }

        /// <summary>Absolute path of the sidecar file.</summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>Sidecar file at the default location under the project root.</summary>
        public static CustomInstructionsFile CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new CustomInstructionsFile(
                Path.Combine(projectRoot ?? ".", DefaultRelativePath), log);
        }

        // -- Save ----------------------------------------------------------------

        /// <summary>
        /// Writes the text atomically through the shared
        /// <see cref="AtomicFile"/> helper (staged tmp + swap, backup
        /// preserved through the fallback), so a crash mid-write can never
        /// leave a truncated -- or missing -- sidecar. A null/empty value
        /// still writes an empty file (clearing any previously saved
        /// instructions) rather than leaving a stale one on disk. Creates
        /// the parent directory as needed. Never throws.
        /// </summary>
        public void Save(string text)
        {
            AtomicFile.WriteAllText(_filePath, text ?? string.Empty, _log);
        }

        // -- Load ----------------------------------------------------------------

        /// <summary>
        /// Loads the saved text, or empty string when there is nothing to
        /// read. This sidecar is PLAIN TEXT the user typed by hand -- there
        /// is no parse step, so a read failure can only be environmental
        /// (cloud-sync/antivirus lock, permissions) and the file is ALWAYS
        /// kept: the old delete-on-any-failure recovery here was the purest
        /// form of the MODEL-1 data-loss bug (a transient lock permanently
        /// deleted hand-written instructions). Failures log one line and
        /// return empty text for this attempt. Never throws.
        /// </summary>
        public string Load()
        {
            try
            {
                return AtomicFile.ReadAllText(_filePath, _log) ?? string.Empty;
            }
            catch (Exception ex)
            {
                Log("Custom instructions '" + _filePath
                    + "' are temporarily unreadable (keeping the file): " + ex.Message);
                return string.Empty;
            }
        }

        // -- Small helpers ------------------------------------------------------------

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

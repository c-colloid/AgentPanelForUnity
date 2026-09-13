using System;
using System.IO;
using Colloid.AgentPanel.Model;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Process-wide access to the panel's own per-session metadata (pin /
    /// archive / rename / custom group / recorded scene), backed by
    /// <see cref="SessionMetaFile"/>. See
    /// docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// section 2.2.
    ///
    /// Why this lives in Integration rather than Model: it needs the Unity
    /// project root and the active scene, and Model is deliberately kept
    /// Unity-free so it stays directly EditMode-testable (D9 layering). The
    /// data classes and the file I/O both stay in Model; only the Unity
    /// glue is here.
    ///
    /// The store is loaded once per domain load and held in memory. Writes
    /// are explicit (<see cref="Save"/>) because every mutation site is a
    /// deliberate user action -- there is no autosave tick to reason about,
    /// and a lost pin is preferable to a file rewritten on every repaint.
    /// </summary>
    public static class SessionMetaStoreAccess
    {
        private static SessionMetaFile _file;
        private static SessionMetaStore _store;

        /// <summary>Raised after any successful <see cref="Save"/> so open views can re-render.</summary>
        public static event Action Changed;

        /// <summary>The in-memory store, loaded on first access. Never null.</summary>
        public static SessionMetaStore Store
        {
            get
            {
                if (_store == null)
                {
                    _store = File.Load() ?? new SessionMetaStore();
                }
                return _store;
            }
        }

        private static SessionMetaFile File
        {
            get
            {
                if (_file == null)
                {
                    _file = SessionMetaFile.CreateDefault(AgentHub.ProjectRoot, AgentHub.Log);
                }
                return _file;
            }
        }

        /// <summary>Persists the store and notifies listeners. Never throws
        /// (SessionMetaFile.Save swallows and logs its own IO failures).</summary>
        public static void Save()
        {
            File.Save(Store);
            Action handler = Changed;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>
        /// Records the scene that is active right now as this session's
        /// scene, so History can group by it.
        ///
        /// Called on turn completion rather than once at session start: a
        /// session commonly outlives several scene changes, and the scene
        /// the user was last working in is far more useful for finding the
        /// conversation again than whichever scene happened to be open when
        /// they first typed. Writing only when the value actually changes
        /// keeps this off the per-turn IO path in the common case.
        ///
        /// No-ops for an unsaved/untitled scene (empty path): recording ""
        /// would be indistinguishable from "never recorded", and sessions
        /// that predate this feature must keep reading as the latter (there
        /// is NO scene information anywhere in the CLI transcript -- see the
        /// design note section 2.1 -- so backfill is impossible).
        /// </summary>
        public static void RecordActiveScene(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }
            string scenePath;
            try
            {
                scenePath = SceneManager.GetActiveScene().path;
            }
            catch (Exception)
            {
                // Scene access can fail in odd editor states (very early
                // domain load, batch mode with no scene). A missing scene
                // must never break turn completion.
                return;
            }
            if (string.IsNullOrEmpty(scenePath))
            {
                return;
            }
            SessionMeta meta = Store.Edit(sessionId);
            if (meta == null
                || string.Equals(meta.recordedScenePath, scenePath, StringComparison.Ordinal))
            {
                return;
            }
            meta.recordedScenePath = scenePath;
            Save();
        }

        /// <summary>
        /// Moves a session's transcript out of the CLI's project directory
        /// into <c>UserSettings/AgentPanel/DeletedSessions/</c> and drops its
        /// metadata. Returns the destination path on success, or null when
        /// the move failed (the caller surfaces that to the user rather than
        /// pretending the row is gone).
        ///
        /// A MOVE, not an unlink: the transcript is the CLI's own canonical
        /// record of a real conversation and lives under the user's
        /// ~/.claude, so destroying it irreversibly on a single click is not
        /// a call this panel should make. Moving it out of
        /// ~/.claude/projects/&lt;dir&gt; entirely (rather than into a
        /// subfolder there) avoids assuming anything about how the CLI globs
        /// its own session directory.
        /// </summary>
        public static string MoveTranscriptToDeleted(string sessionId, string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath))
            {
                return null;
            }
            try
            {
                string destinationDir = DeletedSessionsDirectory();
                Directory.CreateDirectory(destinationDir);
                string destination = Path.Combine(destinationDir,
                    Path.GetFileName(transcriptPath));
                if (System.IO.File.Exists(destination))
                {
                    // Same session deleted twice (restored by hand in
                    // between, or two projects sharing a name): keep both
                    // rather than silently overwriting the earlier copy.
                    destination = Path.Combine(destinationDir,
                        Path.GetFileNameWithoutExtension(transcriptPath)
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                        + Path.GetExtension(transcriptPath));
                }
                System.IO.File.Move(transcriptPath, destination);
                if (!string.IsNullOrEmpty(sessionId))
                {
                    Store.bySessionId.Remove(sessionId);
                    Save();
                }
                return destination;
            }
            catch (Exception e)
            {
                AgentHub.Log("Could not move transcript aside: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Moves a session's pin/archive/rename/group state to a new id: an
        /// ACP agent without `session/load` re-issues the SAME conversation
        /// under a new id on every reconnect (design note
        /// 2026-09-13-acp-feature-parity.md section 1.3), and the user's
        /// customization must follow it. No-op when the old id has none.
        /// </summary>
        public static void CarryOver(string oldSessionId, string newSessionId)
        {
            if (string.IsNullOrEmpty(oldSessionId) || string.IsNullOrEmpty(newSessionId)
                || string.Equals(oldSessionId, newSessionId, StringComparison.Ordinal))
            {
                return;
            }
            SessionMeta old;
            if (!Store.bySessionId.TryGetValue(oldSessionId, out old) || old == null)
            {
                return;
            }
            SessionMeta target = Store.Edit(newSessionId);
            target.pinned = old.pinned;
            target.archived = old.archived;
            target.titleOverride = old.titleOverride;
            target.customGroupId = old.customGroupId;
            if (string.IsNullOrEmpty(target.recordedScenePath))
            {
                target.recordedScenePath = old.recordedScenePath;
            }
            Store.bySessionId.Remove(oldSessionId);
            Save();
        }

        /// <summary>Where <see cref="MoveTranscriptToDeleted"/> puts transcripts (shown in the confirm bar).</summary>
        public static string DeletedSessionsDirectory()
        {
            return Path.Combine(AgentHub.ProjectRoot ?? ".",
                "UserSettings", "AgentPanel", "DeletedSessions");
        }

        /// <summary>Drops the cached store so the next access reloads from disk (tests / project switch).</summary>
        internal static void ResetForTests()
        {
            _file = null;
            _store = null;
        }
    }
}

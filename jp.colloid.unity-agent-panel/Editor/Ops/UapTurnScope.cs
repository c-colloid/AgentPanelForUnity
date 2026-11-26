using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The per-turn safety wrapper AgentHub arms around every EXECUTING
    /// turn (design section 1.3 "turn = one Undo group" and section 8.2/7.4
    /// "DisallowAutoRefresh held per turn"): one Undo group so a turn's
    /// scene/asset mutations collapse into a single Ctrl+Z, and one
    /// AssetDatabase refresh-suppression window so any Assets/ writes that
    /// happen mid-turn (including uap_scripts_commit's successful file
    /// moves) only trigger ONE AssetDatabase.Refresh at turn end instead of
    /// one per write (L3 batching). Idempotent: a turn with several
    /// sends/tool calls only ever begins once and ends once.
    ///
    /// Domain-reload safety (R11 section 2's "unbalanced native counter is
    /// the dangerous failure mode", applied here to DisallowAutoRefresh
    /// rather than LockReloadAssemblies): AssemblyReloadEvents.
    /// beforeAssemblyReload force-releases the single outstanding native
    /// disallow (if any) and resets bookkeeping BEFORE the reload, so a
    /// mid-turn domain reload (a manual compile, a package change, ...)
    /// can never leave AssetDatabase.AllowAutoRefresh permanently
    /// unbalanced. The Undo group is simply abandoned in that case; what
    /// survives the reload is the request checkpoint this class feeds
    /// (UapCheckpointRecorder, design note 2026-11-23-request-
    /// checkpoints.md): it remembers each turn's Undo group and every
    /// file the request changed, so "revert this request" still works
    /// after the reload even though the single-Ctrl+Z grouping does not.
    /// </summary>
    public static class UapTurnScope
    {
        private static readonly RefreshSuppressionCounter RefreshCounter = new RefreshSuppressionCounter();
        private static bool _undoGroupActive;
        private static int _undoGroupStart;

        /// <summary>
        /// [InitializeOnLoadMethod] rather than an [InitializeOnLoad] static
        /// constructor (2026-09-06, same reasoning as ReloadLifecycle.Install):
        /// a throwing static constructor would poison this type, and the
        /// whole point of this class is the beforeAssemblyReload release
        /// that keeps AssetDatabase auto-refresh from staying disallowed
        /// forever -- it must never be the thing that silently disappears.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Install()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseForDomainReload;
        }

        /// <summary>True while a turn scope is currently open (test/diagnostics seam).</summary>
        public static bool IsActive
        {
            get { return _undoGroupActive; }
        }

        /// <summary>
        /// Begins the turn scope if one is not already open. Safe to call
        /// for every mid-turn send -- only the FIRST call in a turn opens
        /// the Undo group / disallows refresh.
        /// </summary>
        public static void BeginIfNeeded()
        {
            if (_undoGroupActive)
            {
                return;
            }
            _undoGroupActive = true;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Agent turn");
            _undoGroupStart = Undo.GetCurrentGroup();
            // Request checkpoints (design note 2026-11-23): the open
            // recording window, if AgentHub opened one for this turn,
            // remembers which Undo group holds the turn's scene edits.
            UapCheckpointRecorder.NoteUndoGroup(_undoGroupStart);
            if (RefreshCounter.Increment())
            {
                AssetDatabase.DisallowAutoRefresh();
            }
        }

        /// <summary>
        /// Ends the turn scope if one is open: collapses every Undo
        /// operation recorded since BeginIfNeeded into the single group,
        /// then re-allows auto refresh and performs exactly one explicit
        /// Refresh (L3's "one refresh per turn, at the end" batching).
        /// </summary>
        public static void EndIfActive()
        {
            if (!_undoGroupActive)
            {
                return;
            }
            _undoGroupActive = false;
            // Before the collapse: the open recording window compares the
            // Undo stack with what it was when the turn began.
            UapCheckpointRecorder.NoteTurnUndoRecords();
            Undo.CollapseUndoOperations(_undoGroupStart);
            if (RefreshCounter.Decrement())
            {
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.Refresh();
            }
            // AFTER the refresh above: the assets it imports (a new file's
            // .meta, for one) still belong to the request being recorded.
            UapCheckpointRecorder.EndTurn();
        }

        private static void ReleaseForDomainReload()
        {
            _undoGroupActive = false;
            if (RefreshCounter.IsSuppressed)
            {
                AssetDatabase.AllowAutoRefresh();
            }
            RefreshCounter.ForceReset();
            UapCheckpointRecorder.NoteTurnUndoRecords();
            // The recording window does not cross a reload: whatever the
            // editor imports afterwards is not this turn's doing. The
            // journal is on disk, and a continuation turn re-opens the same
            // checkpoint (UapCheckpointRecorder.ContinueRequest).
            UapCheckpointRecorder.EndTurn();
        }

        /// <summary>Test-only: resets to a clean, never-started slate without touching AssetDatabase (for tests that only exercise RefreshCounter's own class directly, this is unused; kept for symmetry/future EditMode coverage).</summary>
        internal static void ResetForTests()
        {
            _undoGroupActive = false;
            RefreshCounter.ForceReset();
        }
    }
}

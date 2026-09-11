using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using UnityEditor;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Domain-reload and editor-quit survival (ARCHITECTURE.md D4/D5).
    /// Before a reload it synchronously persists the session id, the
    /// running-turn flag and the was-running flag to SessionState, then
    /// shuts the CLI down (interrupt, stdin close, brief WaitForExit, tree
    /// kill -- all inside AgentHub.Shutdown via the transport). After the
    /// reload the first EditorApplication.update tick reaps orphans,
    /// reconciles the Phase 5c L3 item 3 (auto-continue after compile)
    /// bookkeeping UNCONDITIONALLY (see OnFirstUpdate's own doc comment --
    /// 2026-08-04 defect 2/3 fixes), restores the cached transcript and
    /// reconnects with --resume through AgentHub.RestoreAfterReload ONLY
    /// when a client was actually running before the reload, then drains
    /// any sends queued by CompileGate. The static constructor only
    /// subscribes events; all I/O is deferred to callbacks or the first
    /// update tick.
    ///
    /// Ordering hardening (2026-09-06, docs/design-notes/2026-09-06-domain-
    /// reload-resilience-and-hot-reload.md section 2.1): the post-reload
    /// sequence lives in <see cref="EnsureStartupReconciled"/>, which is
    /// idempotent and is ALSO called by AgentHub.EnsureStarted before it
    /// does anything else. The first update tick is not the only thing
    /// that can start the CLI after a reload -- AgentPanelWindow.CreateGUI
    /// schedules its own deferred AgentHub.EnsureStarted (StartHubDeferred)
    /// -- and whichever of the two runs first must perform the SAME ordered
    /// reconciliation. A bare EnsureStarted running first would call
    /// StartClient -> TearDownClient -> AbortOpenTurn, which rewrites
    /// SessionStateBridge.TurnRunning to false (no client yet), and would
    /// clear the auto-continue attribution ticket -- both BEFORE
    /// RestoreAfterReload/TryAutoContinueAfterCompile ever read them. The
    /// visible result of losing that race is a mid-turn reload that
    /// reconnects silently: no "interrupted" nudge, no auto-continue.
    /// Whether delayCall or update fires first after a reload is not a
    /// documented contract, so the code no longer depends on it.
    /// </summary>
    public static class ReloadLifecycle
    {
        private static bool _startupDone;

        /// <summary>
        /// Hooks the editor events. [InitializeOnLoadMethod] rather than an
        /// [InitializeOnLoad] static constructor (2026-09-06): a static
        /// constructor that throws poisons the TYPE -- every later access
        /// to ReloadLifecycle raises TypeInitializationException, so the
        /// beforeAssemblyReload teardown, the quit handlers and the
        /// post-reload reconciliation would all silently vanish for the
        /// rest of the editor session. A method that throws is logged by
        /// Unity and the class stays usable. Cost is identical: both run
        /// once per domain load and only subscribe here (all I/O is
        /// deferred to the callbacks / first update tick).
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Install()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.wantsToQuit += OnWantsToQuit;
            EditorApplication.update += OnFirstUpdate;
        }

        /// <summary>
        /// Runs one reconciliation step, logging (never propagating) a
        /// failure so the steps after it still run: the ordered sequence
        /// in <see cref="EnsureStartupReconciled"/> is only as robust as
        /// its weakest step, and an exception from, say, the orphan reaper
        /// must not cost the user their session restore.
        /// </summary>
        private static void Guarded(string step, System.Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning("[AgentPanel] Reload step '" + step + "' failed: " + ex);
            }
        }

        /// <summary>
        /// True once the post-reload reconciliation has run in this domain
        /// (test/diagnostics seam; see <see cref="EnsureStartupReconciled"/>).
        /// </summary>
        internal static bool StartupReconciled
        {
            get { return _startupDone; }
        }

        /// <summary>
        /// Design note 2026-09-10 section 3: reads AgentHub.PendingPermission
        /// (if any) and writes its display name into
        /// SessionStateBridge.ReloadDroppedPermissionTool -- CanUseTool.
        /// DisplayName, falling back to ToolName, matching the same
        /// precedence AgentHub.RespondToPendingPermission uses for its own
        /// deny note. Writes an empty string when nothing is pending, so a
        /// reload that drops nothing never leaves a stale name from an
        /// earlier reload for AgentHub.ConsumeReloadDroppedPermission to
        /// misreport. Split out of OnBeforeAssemblyReload as its own
        /// internal static entry point so a unit test can exercise the
        /// capture directly (via AgentHub.WireClientForTests + a real
        /// pending can_use_tool) without needing an actual domain reload,
        /// which EditMode tests cannot trigger.
        /// </summary>
        internal static void CaptureReloadDroppedPermissionTool()
        {
            string tool = string.Empty;
            try
            {
                ControlRequestMessage pending = AgentHub.PendingPermission;
                if (pending != null && pending.CanUseTool != null)
                {
                    CanUseToolRequest request = pending.CanUseTool;
                    if (!string.IsNullOrEmpty(request.DisplayName))
                    {
                        tool = request.DisplayName;
                    }
                    else if (!string.IsNullOrEmpty(request.ToolName))
                    {
                        tool = request.ToolName;
                    }
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log("[AgentPanel] Capturing the pending permission before reload failed: " + ex.Message);
            }
            SessionStateBridge.ReloadDroppedPermissionTool = tool;
        }

        // -- Before reload (synchronous, main thread) --------------------------

        private static void OnBeforeAssemblyReload()
        {
            AgentClient client = AgentHub.Client;
            bool clientWasRunning = client != null
                && client.State != AgentClientState.NotStarted
                && client.State != AgentClientState.Errored;
            bool turnWasRunning = client != null && client.TurnActive;

            // Persist the session id first; AgentClient keeps
            // SessionStateBridge.CurrentSessionId current via
            // SessionIdChanged, but re-writing it here guards against a
            // reload that races the very first init message.
            if (client != null && !string.IsNullOrEmpty(client.SessionId))
            {
                SessionStateBridge.CurrentSessionId = client.SessionId;
            }

            // Design note 2026-09-10 section 3: capture the pending
            // permission's display name BEFORE ShutdownForReload below
            // (via AgentHub.AbortOpenTurn) unconditionally discards it --
            // the CLI process that would have received the answer is about
            // to die with the reload either way, but until this capture
            // existed that discard left no trace anywhere: no transcript
            // note, nothing in the interrupted-turn continuation message.
            // Runs BEFORE the Guarded shutdown call (not inside it) so it
            // is written no matter what that teardown does.
            CaptureReloadDroppedPermissionTool();

            // Interrupt -> stdin close -> brief WaitForExit -> tree kill,
            // plus transcript save (SessionCacheFile.Save). The reload
            // variant uses a short exit grace so the teardown stays inside
            // the domain-reload budget (the session resumes via --resume).
            // Guarded: whatever happens in the teardown, the flags below
            // MUST still be written -- they are what the next domain reads
            // to resume at all.
            Guarded("shutdown for reload", AgentHub.ShutdownForReload);

            // Shutdown cleared TurnRunning and the process record; re-set
            // the flags the post-reload restore path needs.
            SessionStateBridge.TurnRunning = turnWasRunning;
            SessionStateBridge.ClientWasRunning = clientWasRunning;

            // HUB-8: capture the compile result while the OLD domain still
            // holds it. ConsoleErrorProvider's entries are statics that do
            // not survive the reload, and compiler errors are delivered to
            // THIS domain (assemblyCompilationFinished) -- so the
            // post-reload TryAutoContinueAfterCompile, reading a
            // freshly-empty provider, reported "compile SUCCEEDED" to the
            // model whenever a partial failure still triggered a reload,
            // with an empty error list attached. VISIBLE count only, the
            // same basis FormatDigest uses (ignored errors stay ignored).
            try
            {
                bool hadErrors = ConsoleErrorProvider.VisibleCount > 0;
                SessionStateBridge.LastCompileHadErrors = hadErrors;
                SessionStateBridge.LastCompileErrorDigest =
                    hadErrors ? ConsoleErrorProvider.FormatDigest() : string.Empty;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log("[AgentPanel] Capturing the pre-reload compile result failed: " + ex.Message);
            }

            // UapOps (Phase 5a): explicitly release the HttpListener before
            // the AppDomain unloads -- otherwise the bound socket lingers
            // until GC finalization runs on the old objects, which is
            // avoidable. AgentHub.StartClient calls UapOpsServer.
            // EnsureStarted() again on the very first post-reload spawn
            // (RestoreAfterReload -> EnsureStarted -> StartClient), on a
            // freshly OS-assigned port, satisfying the design's "listen
            // BEFORE spawn" ordering with no special-casing needed here.
            try
            {
                UapOpsServer.Stop();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log("[AgentPanel] UapOpsServer.Stop() on beforeAssemblyReload failed: " + ex.Message);
            }
        }

        // -- After reload (first update tick, deferred I/O) --------------------

        private static void OnFirstUpdate()
        {
            EnsureStartupReconciled();
        }

        /// <summary>
        /// Runs the post-reload reconciliation exactly once per domain
        /// (reap orphans -> reconcile auto-continue bookkeeping -> judge/
        /// queue auto-continue -> restore the session when a client was
        /// running -> drain queued sends). Returns true when it ran now,
        /// false when it had already run. Called from the first
        /// EditorApplication.update tick AND from AgentHub.EnsureStarted --
        /// see the class comment for why the second caller exists.
        /// Re-entrancy: RestoreAfterReload below calls EnsureStarted, which
        /// calls back here; _startupDone is set FIRST so that inner call is
        /// a no-op rather than a recursion.
        /// </summary>
        internal static bool EnsureStartupReconciled()
        {
            if (_startupDone)
            {
                return false;
            }
            _startupDone = true;
            EditorApplication.update -= OnFirstUpdate;

            // Verifies the pre-reload kill (ShutdownForReload deliberately
            // KEEPS the process record -- see its doc comment): a CLI that
            // survived the tree kill is found by PID + start time + name
            // and killed here, before any --resume spawn could share its
            // session file with it. A dead PID just clears the record. This
            // is also the crashed/hard-killed-editor path.
            Guarded("reap orphans", AgentHub.ReapOrphansNow);

            // Defect 2 fix (2026-08-04): both of these MUST run on EVERY
            // reload, unconditionally, BEFORE the wasRunning check below --
            // they were previously bundled inside AgentHub.RestoreAfterReload,
            // which only ever runs from the `if (wasRunning)` branch. Any
            // reload where the client was null or already Errored BEFORE
            // the reload (a CLI crash-loop, an auth failure that never got
            // past NotStarted) left ClientWasRunning false, so the ticket-
            // consuming call never ran at all and a ticket HandleAutoContinueArming
            // had armed just sat in SessionState, waiting to mislead the
            // NEXT reload that finally did have ClientWasRunning true. See
            // AgentHub.TryAutoContinueAfterCompile/ReconcileInterruptedAutoContinueSend's
            // own doc comments for what each one consumes and why order
            // between them does not matter (they read/write disjoint
            // SessionState fields).
            //
            // The wasRunning flag is read BEFORE these two so it can be
            // passed in: TryAutoContinueAfterCompile always spends the
            // ticket, but only SENDS when a turn was actually interrupted.
            // Making the send unconditional as well would let the panel
            // spawn a CLI and tell it to continue a session that had already
            // died -- see that method's own comment.
            bool wasRunning = SessionStateBridge.ClientWasRunning;
            SessionStateBridge.ClientWasRunning = false;

            Guarded("reconcile auto-continue", AgentHub.ReconcileInterruptedAutoContinueSend);
            // HUB-6: this step now only JUDGES and QUEUES -- it consumes the
            // ticket and decides whether to continue, but never sends. The
            // old version sent synchronously from right here, ahead of
            // RestoreAfterReload: that opened a new turn before the resume,
            // clobbering the SessionStateBridge.TurnRunning flag
            // RestoreAfterReload reads to detect a mid-turn resume, and
            // spawning the CLI before the transcript/session restore had
            // run. Order is load-bearing: judge/queue -> restore -> drain.
            Guarded("auto-continue after compile", delegate { AgentHub.TryAutoContinueAfterCompile(wasRunning); });

            if (wasRunning)
            {
                // Repaints the cached transcript immediately (Changed fires
                // inside) and reconnects with --resume in the background;
                // AgentHub.ResumedMidTurn exposes the mid-turn flag.
                Guarded("restore after reload", AgentHub.RestoreAfterReload);
                // Design note 2026-09-10 section 3: consume the pending-
                // permission capture and announce it to the transcript
                // BEFORE TryAutoContinueInterruptedTurn below reads the
                // value back off AgentHub's own private field to fold into
                // the continuation message. Order is load-bearing for the
                // same reason as the rest of this sequence.
                Guarded("consume reload-dropped permission",
                    delegate { AgentHub.ConsumeReloadDroppedPermission(true); });
                // Opt-in (PanelSettings.autoContinueInterruptedTurn): queue
                // the "continue" the ResumeBanner button would send when
                // this reload cut a running turn short. Queue only; the
                // drain below sends once the resumed client is sendable.
                Guarded("auto-continue interrupted turn", AgentHub.TryAutoContinueInterruptedTurn);
            }
            else
            {
                // No turn to nudge -- consume without announcing so the
                // captured name never leaks into a LATER reload's note
                // (this field is not otherwise cleared).
                Guarded("consume reload-dropped permission",
                    delegate { AgentHub.ConsumeReloadDroppedPermission(false); });
            }

            // HUB-6: now that the resume is established, release whatever
            // the auto-continue step queued.
            Guarded("auto-continue drain", AgentHub.StartAutoContinueDrain);

            // Release messages queued while the compile was running. Safe
            // when the queue is empty or no client was running: draining
            // starts the client on demand.
            Guarded("drain queued sends", CompileGate.DrainPending);
            return true;
        }

        // -- Editor quit --------------------------------------------------------

        private static bool OnWantsToQuit()
        {
            // HUB-10: do NOT shut the CLI down here. wantsToQuit fires while
            // the quit can still be VETOED -- another package returning
            // false, or the user pressing Cancel on the unsaved-scenes
            // dialog -- and the old Shutdown() call killed the CLI anyway,
            // leaving the panel silently disconnected (TurnRunning and all)
            // in an editor that went right on running. The leak this was
            // guarding against is already covered without killing anything:
            // the spawn records pid/start-ticks through ZombieReaper, and
            // the next editor start reaps orphans (AgentHub.ReapOrphansNow
            // on the first update tick). Real teardown belongs to
            // OnQuitting below, which only runs once the quit is settled.
            // Refreshing the reaper record is the most this may do, and it
            // never blocks the quit.
            try
            {
                AgentHub.RefreshProcessRecordForQuit();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log("[AgentPanel] Refreshing the CLI process record on wantsToQuit failed: " + ex.Message);
            }
            return true;
        }

        private static void OnQuitting()
        {
            // Shutdown is idempotent; this covers quits that skip
            // wantsToQuit (e.g. some scripted exits).
            try
            {
                AgentHub.Shutdown();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.Log("[AgentPanel] Shutdown on quitting failed: " + ex.Message);
            }
            SessionStateBridge.ClientWasRunning = false;
        }
    }
}

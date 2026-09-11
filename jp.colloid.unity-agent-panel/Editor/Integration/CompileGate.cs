using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Model;
using UnityEditor;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Send gate for script compilation (Phase 1: no LockReloadAssemblies).
    /// While EditorApplication.isCompiling, outgoing user sends are queued
    /// instead of written to a process that is about to be torn down by the
    /// domain reload. The queue is persisted to SessionState (as a JSON
    /// array) so it survives the reload; ReloadLifecycle calls DrainPending
    /// afterwards and the messages go out once the resumed client reaches a
    /// sendable state. The UI should call SendOrQueue instead of
    /// AgentHub.SendUserMessage directly.
    ///
    /// Each entry carries the WIRE text (the full delimited compose the CLI
    /// receives) plus the display text and structured context attachments,
    /// so a message queued across a domain reload still renders with
    /// attachment chips instead of the raw wire form.
    /// </summary>
    public static class CompileGate
    {
        /// <summary>Give up waiting for a sendable client after this long.</summary>
        private const double DrainTimeoutSeconds = 60.0;

        private static List<CompileGateQueue.Entry> _pending;
        private static bool _hooked;
        private static double _waitStartedAt;

        /// <summary>Number of queued, not-yet-sent user messages.</summary>
        public static int PendingCount
        {
            get { return Pending.Count; }
        }

        /// <summary>Plain-text send: display text equals the wire text.</summary>
        public static void SendOrQueue(string text)
        {
            SendOrQueue(text, null, null);
        }

        /// <summary>
        /// Sends the message now, or queues it when the editor is
        /// compiling, when earlier messages are still queued (order
        /// preservation), or when the client is not in a sendable state
        /// (Starting after a domain reload, WaitingPermission, Errored, not
        /// started yet). Queued messages survive the reload and drain once
        /// the client is sendable again -- nothing typed by the user is
        /// ever dropped or falsely shown as delivered. wireText is what
        /// the CLI receives; displayText (null = wireText) and attachments
        /// (null = none) shape the transcript rendering.
        /// </summary>
        public static void SendOrQueue(string wireText, string displayText,
            List<ContextAttachment> attachments)
        {
            SendOrQueue(wireText, displayText, attachments, null);
        }

        /// <summary>
        /// As the 3-arg overload, plus image attachments (design note
        /// 2026-09-07 section 2.3.1) that ride along as content blocks.
        /// wireText may be empty when at least one image is given.
        /// </summary>
        public static void SendOrQueue(string wireText, string displayText,
            List<ContextAttachment> attachments, List<ImageAttachment> images)
        {
            bool hasImages = images != null && images.Count > 0;
            if (string.IsNullOrEmpty(wireText) && !hasImages)
            {
                return;
            }
            var send = new CompileGateQueue.Entry
            {
                WireText = wireText ?? string.Empty,
                DisplayText = displayText,
                Attachments = attachments,
                Images = images
            };
            if (EditorApplication.isCompiling || Pending.Count > 0 || !IsClientSendable())
            {
                Pending.Add(send);
                SaveQueue();
                Hook();
                return;
            }
            if (!AgentHub.SendUserMessage(send.WireText, send.DisplayText, send.Attachments, send.Images))
            {
                // Lost the race with a state change: keep the message.
                Pending.Add(send);
                SaveQueue();
                Hook();
            }
        }

        /// <summary>True when AgentHub's client would accept a send right now.</summary>
        private static bool IsClientSendable()
        {
            AgentClient client = AgentHub.Client;
            return client != null
                && (client.State == AgentClientState.Ready
                    || client.State == AgentClientState.Streaming
                    || client.State == AgentClientState.ToolRunning);
        }

        /// <summary>
        /// Starts draining the persisted queue. Called by ReloadLifecycle
        /// after a domain reload; safe to call any time (no-op when empty).
        /// Messages are released one per update tick once the client is in
        /// a sendable state (Ready/Streaming/ToolRunning).
        /// </summary>
        public static void DrainPending()
        {
            if (Pending.Count == 0)
            {
                return;
            }
            Hook();
        }

        // -- Update-tick drain --------------------------------------------------

        private static void Hook()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            _waitStartedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnUpdate;
        }

        private static void Unhook()
        {
            if (!_hooked)
            {
                return;
            }
            _hooked = false;
            EditorApplication.update -= OnUpdate;
        }

        /// <summary>
        /// What one drain tick should do -- INFRA-2c's pure decision table,
        /// switched on by <see cref="OnUpdate"/> and pinned directly by
        /// CompileGateDrainDecisionTests.
        /// </summary>
        internal enum DrainStep
        {
            /// <summary>Nothing queued: unhook.</summary>
            UnhookIdle,
            /// <summary>Editor compiling: reset the wait clock, keep the hook.</summary>
            WaitCompiling,
            /// <summary>Crash-loop suspension (HUB-2): warn once and unhook;
            /// the queue stays persisted for a later re-arm.</summary>
            SuspendedUnhook,
            /// <summary>Client not sendable, within the wait budget: keep waiting.</summary>
            WaitForClient,
            /// <summary>Client not sendable past the budget: warn and unhook
            /// (never drop -- the queue stays persisted).</summary>
            TimeoutUnhook,
            /// <summary>Sendable: release exactly the head message.</summary>
            SendOne
        }

        /// <summary>
        /// The tick decision, pure. Order matters and mirrors the
        /// long-standing inline logic exactly: an empty queue wins over
        /// everything; compiling wins over suspension (the reload will
        /// resolve it); suspension wins over sendability (HUB-2: never
        /// respawn-fight the suspension); only a sendable client releases
        /// a message.
        /// </summary>
        internal static DrainStep DecideDrainStep(int pendingCount, bool isCompiling,
            bool crashLoopSuspended, bool clientSendable,
            double waitedSeconds, double timeoutSeconds)
        {
            if (pendingCount == 0)
            {
                return DrainStep.UnhookIdle;
            }
            if (isCompiling)
            {
                return DrainStep.WaitCompiling;
            }
            if (crashLoopSuspended)
            {
                return DrainStep.SuspendedUnhook;
            }
            if (!clientSendable)
            {
                return waitedSeconds > timeoutSeconds
                    ? DrainStep.TimeoutUnhook
                    : DrainStep.WaitForClient;
            }
            return DrainStep.SendOne;
        }

        /// <summary>
        /// One drain tick (EditorApplication.update). Internal purely as the
        /// EditMode test seam for the crash-loop guard -- production only
        /// ever reaches it through Hook(). Gathers the live signals (with
        /// the EnsureStarted revival attempt for a dead client), asks
        /// <see cref="DecideDrainStep"/>, and acts.
        /// </summary>
        internal static void OnUpdate()
        {
            bool isCompiling = EditorApplication.isCompiling;
            bool suspended = AgentHub.IsCrashLoopSuspended;

            bool sendable = false;
            if (Pending.Count > 0 && !isCompiling && !suspended)
            {
                // Revive a dead/never-started client before judging
                // sendability -- but only when a send could actually follow
                // (HUB-2: never EnsureStarted while suspended, that would
                // respawn-fight the suspension the panel just reported).
                AgentClient client = AgentHub.Client;
                if (client == null
                    || client.State == AgentClientState.NotStarted
                    || client.State == AgentClientState.Errored)
                {
                    AgentHub.EnsureStarted();
                    client = AgentHub.Client;
                }
                sendable = client != null
                    && (client.State == AgentClientState.Ready
                        || client.State == AgentClientState.Streaming
                        || client.State == AgentClientState.ToolRunning);
            }

            DrainStep step = DecideDrainStep(Pending.Count, isCompiling, suspended,
                sendable, EditorApplication.timeSinceStartup - _waitStartedAt,
                DrainTimeoutSeconds);
            switch (step)
            {
                case DrainStep.UnhookIdle:
                    Unhook();
                    return;
                case DrainStep.WaitCompiling:
                    // Keep waiting; the reload (or a compile error) ends this.
                    _waitStartedAt = EditorApplication.timeSinceStartup;
                    return;
                case DrainStep.SuspendedUnhook:
                    UnityEngine.Debug.LogWarning(
                        "[AgentPanel] CompileGate: CLI connection is suspended after"
                        + " repeated crashes; " + Pending.Count
                        + " message(s) remain queued.");
                    Unhook();
                    return;
                case DrainStep.WaitForClient:
                    return;
                case DrainStep.TimeoutUnhook:
                    // Do not drop user text: keep it persisted, stop
                    // spinning. The next SendOrQueue/DrainPending re-arms.
                    UnityEngine.Debug.LogWarning(
                        "[AgentPanel] CompileGate: client not sendable after "
                        + (int)DrainTimeoutSeconds + "s; " + Pending.Count
                        + " message(s) remain queued.");
                    Unhook();
                    return;
            }

            // SendOne. One message per tick keeps ordering and the frame
            // budget. Dequeue only after a confirmed wire write: when the
            // send is refused (state changed within this tick) the message
            // stays queued and the next tick retries.
            CompileGateQueue.Entry send = Pending[0];
            if (!AgentHub.SendUserMessage(send.WireText, send.DisplayText, send.Attachments, send.Images))
            {
                return;
            }
            Pending.RemoveAt(0);
            SaveQueue();
            if (Pending.Count == 0)
            {
                Unhook();
            }
        }

        /// <summary>
        /// Test-only: clears the in-memory queue, its SessionState
        /// persistence and the update hook, so one test's queued sends can
        /// never leak into another (CompileGate statics are shared across
        /// the whole EditMode run, the same caveat as AgentHub's
        /// SetClientForTests).
        /// </summary>
        internal static void ResetForTests()
        {
            Unhook();
            _pending = new List<CompileGateQueue.Entry>();
            SessionStateBridge.PendingSendsJson = string.Empty;
        }

        // -- SessionState persistence --------------------------------------------

        private static List<CompileGateQueue.Entry> Pending
        {
            get
            {
                if (_pending == null)
                {
                    _pending = LoadQueue();
                }
                return _pending;
            }
        }

        private static List<CompileGateQueue.Entry> LoadQueue()
        {
            // The parse/shape rules live in CompileGateQueue (INFRA-2c,
            // pure and table-tested); this wrapper owns the SessionState
            // read and the discard warning.
            string discardError;
            List<CompileGateQueue.Entry> list = CompileGateQueue.Deserialize(
                SessionStateBridge.PendingSendsJson, out discardError);
            if (discardError != null)
            {
                UnityEngine.Debug.LogWarning(
                    "[AgentPanel] CompileGate: discarding unreadable pending-send queue ("
                    + discardError + ").");
                SessionStateBridge.PendingSendsJson = string.Empty;
            }
            return list;
        }

        private static void SaveQueue()
        {
            SessionStateBridge.PendingSendsJson = CompileGateQueue.Serialize(Pending);
        }
    }
}

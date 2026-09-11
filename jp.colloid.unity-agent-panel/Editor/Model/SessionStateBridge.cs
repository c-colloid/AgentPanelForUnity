using System.Globalization;
using UnityEditor;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Central definition of every SessionState key the panel uses
    /// (ARCHITECTURE.md D5, persistence layer 1: survives domain reloads,
    /// cleared when the editor exits). Keeping them behind typed properties
    /// prevents key-string drift between ReloadLifecycle, AgentHub and the
    /// UI.
    /// </summary>
    public static class SessionStateBridge
    {
        private const string Prefix = "Colloid.AgentPanel.";
        private const string KeySessionId = Prefix + "SessionId";
        private const string KeyTurnRunning = Prefix + "TurnRunning";
        private const string KeyPid = Prefix + "Pid";
        private const string KeyProcessStartTicks = Prefix + "ProcessStartTicks";
        private const string KeyClientWasRunning = Prefix + "ClientWasRunning";
        private const string KeyPendingSends = Prefix + "PendingSends";
        private const string KeyInputDraft = Prefix + "InputDraft";
        private const string KeyScrollPosition = Prefix + "ScrollPosition";
        private const string KeyCliVersion = Prefix + "CliVersion";
        private const string KeyHandoverPendingFrom = Prefix + "HandoverPendingFrom";
        private const string KeyCliBinaryVersion = Prefix + "CliBinaryVersion";
        private const string KeyCliBinaryVersionPath = Prefix + "CliBinaryVersionPath";
        private const string KeyAutoContinueTurnIsContinuation = Prefix + "AutoContinueTurnIsContinuation";
        private const string KeyAutoContinuePendingAttribution = Prefix + "AutoContinuePendingAttribution";
        private const string KeyAutoContinuePendingWasContinuation = Prefix + "AutoContinuePendingWasContinuation";
        private const string KeyAutoContinuePendingArmedAtUtcTicks = Prefix + "AutoContinuePendingArmedAtUtcTicks";
        private const string KeyAutoContinuePendingSendUnconfirmed = Prefix + "AutoContinuePendingSendUnconfirmed";
        private const string KeyLastCompileHadErrors = Prefix + "LastCompileHadErrors";
        private const string KeyLastCompileErrorDigest = Prefix + "LastCompileErrorDigest";
        private const string KeyUloopInstallInFlight = Prefix + "UloopInstallInFlight";
        private const string KeyUloopInstallStartedAtUtcTicks = Prefix + "UloopInstallStartedAtUtcTicks";
        private const string KeyUnityPluginInstallInFlight = Prefix + "UnityPluginInstallInFlight";
        private const string KeyUnityPluginInstallStartedAtUtcTicks = Prefix + "UnityPluginInstallStartedAtUtcTicks";
        private const string KeySceneMarkers = Prefix + "SceneMarkers";
        private const string KeyComposerImages = Prefix + "ComposerImages";
        private const string KeyReloadDroppedPermissionTool = Prefix + "ReloadDroppedPermissionTool";

        /// <summary>
        /// Last claude_code_version seen on ANY connection this editor
        /// session. Survives domain reloads (a resumed connection may never
        /// re-emit system/init, so the in-memory init message alone is not
        /// enough for the About row). Empty = none seen yet.
        /// </summary>
        public static string LastCliVersion
        {
            get { return SessionState.GetString(KeyCliVersion, string.Empty); }
            set { SessionState.SetString(KeyCliVersion, value ?? string.Empty); }
        }

        /// <summary>
        /// Version string read directly from the resolved CLI binary
        /// (`&lt;cliPath&gt; --version`, <c>CliVersionProbe</c>), independent
        /// of the stream-json protocol -- unlike <see cref="LastCliVersion"/>
        /// (which needs `system/init` to have actually arrived at least
        /// once), this is available even for a connection that has never
        /// sent a message. Only valid for the path recorded in
        /// <see cref="CliBinaryVersionPath"/>; a caller MUST compare that
        /// path against the currently resolved one before trusting this
        /// value, since a changed CLI path must re-probe rather than show
        /// the previous binary's stale version. Empty = never probed
        /// successfully. See docs/design-notes/2026-08-01-cli-binary-
        /// version-probe.md.
        /// </summary>
        public static string CliBinaryVersion
        {
            get { return SessionState.GetString(KeyCliBinaryVersion, string.Empty); }
            set { SessionState.SetString(KeyCliBinaryVersion, value ?? string.Empty); }
        }

        /// <summary>The resolved CLI path <see cref="CliBinaryVersion"/> was probed for.</summary>
        public static string CliBinaryVersionPath
        {
            get { return SessionState.GetString(KeyCliBinaryVersionPath, string.Empty); }
            set { SessionState.SetString(KeyCliBinaryVersionPath, value ?? string.Empty); }
        }

        /// <summary>Current CLI session id (for post-reload --resume).</summary>
        public static string CurrentSessionId
        {
            get { return SessionState.GetString(KeySessionId, string.Empty); }
            set { SessionState.SetString(KeySessionId, value ?? string.Empty); }
        }

        /// <summary>
        /// AgentBackend (as int) whose conversation is still to be handed
        /// over to the newly selected backend with the next user message
        /// (ConversationHandover), or -1 when none is pending. Survives a
        /// domain reload like the session id it accompanies.
        /// </summary>
        public static int HandoverPendingFrom
        {
            get { return SessionState.GetInt(KeyHandoverPendingFrom, -1); }
            set { SessionState.SetInt(KeyHandoverPendingFrom, value); }
        }

        /// <summary>True when a turn was running when the reload hit (mid-turn nudge).</summary>
        public static bool TurnRunning
        {
            get { return SessionState.GetBool(KeyTurnRunning, false); }
            set { SessionState.SetBool(KeyTurnRunning, value); }
        }

        /// <summary>PID of the live CLI process (0 = none).</summary>
        public static int Pid
        {
            get { return SessionState.GetInt(KeyPid, 0); }
            set { SessionState.SetInt(KeyPid, value); }
        }

        /// <summary>Process start time in UTC ticks (PID-reuse guard). 0 = unknown.</summary>
        public static long ProcessStartTicks
        {
            get
            {
                string raw = SessionState.GetString(KeyProcessStartTicks, "0");
                long value;
                return long.TryParse(raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) ? value : 0L;
            }
            set
            {
                SessionState.SetString(KeyProcessStartTicks,
                    value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// True when a client was alive when the reload started. Set by
        /// ReloadLifecycle after AgentHub.Shutdown (which clears the other
        /// process flags) so the post-reload tick knows whether to reconnect.
        /// </summary>
        public static bool ClientWasRunning
        {
            get { return SessionState.GetBool(KeyClientWasRunning, false); }
            set { SessionState.SetBool(KeyClientWasRunning, value); }
        }

        /// <summary>
        /// JSON array (string) of user messages queued by CompileGate while
        /// the editor was compiling. Survives the domain reload that follows
        /// a successful compile; drained by CompileGate afterwards.
        /// </summary>
        public static string PendingSendsJson
        {
            get { return SessionState.GetString(KeyPendingSends, string.Empty); }
            set { SessionState.SetString(KeyPendingSends, value ?? string.Empty); }
        }

        /// <summary>Composer draft text preserved across reloads.</summary>
        public static string InputDraft
        {
            get { return SessionState.GetString(KeyInputDraft, string.Empty); }
            set { SessionState.SetString(KeyInputDraft, value ?? string.Empty); }
        }

        /// <summary>Message list scroll position preserved across reloads.</summary>
        public static float ScrollPosition
        {
            get { return SessionState.GetFloat(KeyScrollPosition, -1f); }
            set { SessionState.SetFloat(KeyScrollPosition, value); }
        }

        /// <summary>Clears the process bookkeeping after a clean shutdown.</summary>
        public static void ClearProcessRecord()
        {
            Pid = 0;
            ProcessStartTicks = 0;
        }

        // -- Auto-continue after compile (Phase 5c L3 item 3, docs/design-
        // notes/2026-08-01-phase5-unity-ops-design.md section 3 item 3 /
        // 8.3) -- three SessionState-backed flags AutoContinueAfterCompile
        // Policy.ShouldAutoContinue's caller (AgentHub) reads/writes across
        // the exact domain reload this feature reacts to. Plain statics do
        // not survive that reload (see this class's own doc comment); these
        // three are the ONLY store for "was the last turn attributable" and
        // "did we already use our one continuation" once the reload wipes
        // AgentHub's in-memory fields. ------------------------------------

        /// <summary>
        /// True while the CURRENTLY OPEN turn is itself an auto-continuation
        /// sent by AgentHub.TryAutoContinueAfterCompile. Set immediately
        /// before that turn's own SendUserMessage call is confirmed to have
        /// written to the wire (rolled back to false if the send attempt
        /// fails), read and reset to false by the NEXT OnTurnCompleted
        /// (AgentHub.HandleAutoContinueArming), which copies the value into
        /// <see cref="AutoContinuePendingWasContinuation"/> for
        /// AutoContinueAfterCompilePolicy.ShouldAutoContinue to consume
        /// after a LATER reload. SessionState-backed rather than a plain
        /// static specifically so a reload that interrupts THIS SAME turn
        /// (the rare ResumedMidTurn case, e.g. the continuation itself
        /// stages more scripts and its own commit reloads mid-flight) does
        /// not lose track of "this open turn is a continuation" -- a plain
        /// in-memory field would silently reset to false across that
        /// reload and let the interrupted continuation's own eventual
        /// completion arm a second, unbounded reprompt cycle.
        /// </summary>
        public static bool AutoContinueTurnIsContinuation
        {
            get { return SessionState.GetBool(KeyAutoContinueTurnIsContinuation, false); }
            set { SessionState.SetBool(KeyAutoContinueTurnIsContinuation, value); }
        }

        /// <summary>
        /// The auto-continue "ticket": true when the turn that most
        /// recently completed reported (via AutoContinueAfterCompilePolicy.
        /// ScriptCommitMovedFiles) that its own uap_scripts_commit call
        /// moved staged files into Assets/. Written unconditionally by
        /// EVERY OnTurnCompleted (so a later, non-attributable turn
        /// correctly clears any stale true left over, rather than this
        /// only ever being set and never cleared) and consumed -- read,
        /// then reset to false, regardless of the outcome -- exactly once,
        /// by AgentHub.TryAutoContinueAfterCompile right after the domain
        /// reload that commit is expected to cause. SessionState-backed
        /// because that reload is precisely the event standing between the
        /// write and the read.
        /// </summary>
        public static bool AutoContinuePendingAttribution
        {
            get { return SessionState.GetBool(KeyAutoContinuePendingAttribution, false); }
            set { SessionState.SetBool(KeyAutoContinuePendingAttribution, value); }
        }

        /// <summary>
        /// Travels alongside <see cref="AutoContinuePendingAttribution"/>
        /// across the same reload: a snapshot, taken by AgentHub.
        /// HandleAutoContinueArming, of whether the turn that just
        /// completed was itself an auto-continuation (see
        /// <see cref="AutoContinueTurnIsContinuation"/>). AgentHub.
        /// TryAutoContinueAfterCompile reads both together as the
        /// "alreadyContinuedThisTurn" argument to AutoContinueAfterCompile
        /// Policy.ShouldAutoContinue, then clears both -- this is the field
        /// that makes a continuation structurally unable to trigger a
        /// second continuation (design guardrail 1).
        /// </summary>
        public static bool AutoContinuePendingWasContinuation
        {
            get { return SessionState.GetBool(KeyAutoContinuePendingWasContinuation, false); }
            set { SessionState.SetBool(KeyAutoContinuePendingWasContinuation, value); }
        }

        /// <summary>
        /// UTC DateTime.Ticks captured by AgentHub.HandleAutoContinueArming
        /// at the exact moment it writes <see cref="AutoContinuePendingAttribution"/>
        /// -- the attribution ticket's missing expiry (2026-08-04 defect
        /// fix). Before this field existed, the ticket was a bare bool with
        /// no notion of "how long ago was this armed", which let a very
        /// specific, confirmed scenario slip through: uap_scripts_commit
        /// reports success (it validated a Temp/ mirror as a SEPARATE
        /// assembly and moved the real files into Assets/), the ticket
        /// arms, but the REAL project-wide compile of those same files
        /// then fails (e.g. a name collision the isolated mirror could not
        /// see) -- and Unity performs NO domain reload at all after a
        /// failed compile, so nothing ever consumes the ticket. Hours
        /// later the user hand-fixes the collision in their own IDE, that
        /// unrelated compile succeeds, Unity reloads, and -- with nothing
        /// but a bool to consult -- AgentHub.TryAutoContinueAfterCompile
        /// would read a still-true ticket and resume the agent on a reload
        /// it did not cause. Note that a NAIVE "count the reloads since
        /// arming" counter does NOT fix this: the failed compile above
        /// causes zero reloads, so the eventual unrelated reload is
        /// STILL, arithmetically, "the very next reload since arm" in
        /// both the legitimate and the buggy case -- a reload-count alone
        /// cannot tell them apart. Wall-clock elapsed time can: see
        /// AutoContinueAfterCompilePolicy.TicketIsFresh/TicketMaxAgeSeconds.
        /// Stored as a string, same pattern as ProcessStartTicks above
        /// (SessionState has no native 64-bit integer accessor). 0 means
        /// "nothing armed" (also the value ClearAutoContinuePendingState
        /// resets it to).
        /// </summary>
        public static long AutoContinuePendingArmedAtUtcTicks
        {
            get
            {
                string raw = SessionState.GetString(KeyAutoContinuePendingArmedAtUtcTicks, "0");
                long value;
                return long.TryParse(raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) ? value : 0L;
            }
            set
            {
                SessionState.SetString(KeyAutoContinuePendingArmedAtUtcTicks,
                    value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// True from the moment AgentHub's "the panel is sending the next
        /// message automatically" system note (L10n's HubAutoContinueResuming)
        /// is actually appended to the transcript, until the send it
        /// describes is CONFIRMED (cleared to false) or explicitly
        /// abandoned with a retraction note (also cleared to false) --
        /// 2026-08-04 defect fix: that note used to be written the instant
        /// AgentHub.TryAutoContinueAfterCompile DECIDED to continue, before
        /// the client was even known to be sendable. Either of two things
        /// could then happen with NOTHING further ever touching the
        /// transcript: the 60-second drain timeout gave up (previously
        /// logged only to the Console, never to the saved transcript), or
        /// a SECOND domain reload wiped AgentHub's plain
        /// _pendingAutoContinueMessage static (statics do not survive a
        /// reload) before the first one ever got a sendable client. Either
        /// way the persisted transcript was left asserting as settled fact
        /// that a turn had been sent when the CLI never received it.
        /// SessionState-backed (not a plain static) specifically so it
        /// survives that second scenario: AgentHub.
        /// ReconcileInterruptedAutoContinueSend reads it on the very next
        /// startup (ReloadLifecycle.OnFirstUpdate, unconditionally) and, if
        /// still true, appends the retraction note the interrupted domain
        /// never got a chance to add itself.
        /// </summary>
        public static bool AutoContinuePendingSendUnconfirmed
        {
            get { return SessionState.GetBool(KeyAutoContinuePendingSendUnconfirmed, false); }
            set { SessionState.SetBool(KeyAutoContinuePendingSendUnconfirmed, value); }
        }

        // -- Compile-result carry-over (HUB-8) ---------------------------
        // ConsoleErrorProvider's entries are plain statics: they do NOT
        // survive a domain reload, and compiler errors land in the OLD
        // domain's provider (assemblyCompilationFinished) before the reload
        // wipes it. TryAutoContinueAfterCompile ran AFTER the reload and
        // read a freshly-empty provider, so it reported "compile SUCCEEDED"
        // to the model no matter what -- with an empty digest -- whenever a
        // partial failure still triggered a reload. These two fields are
        // written in beforeAssemblyReload, while the old domain still holds
        // the truth, and consumed once on the other side.

        /// <summary>
        /// HUB-8: whether the pre-reload domain had VISIBLE (non-ignored)
        /// errors when the reload began. Consumed and cleared by
        /// AgentHub.TryAutoContinueAfterCompile, which falls back to the
        /// live ConsoleErrorProvider when nothing was carried over.
        /// </summary>
        public static bool LastCompileHadErrors
        {
            get { return SessionState.GetBool(KeyLastCompileHadErrors, false); }
            set { SessionState.SetBool(KeyLastCompileHadErrors, value); }
        }

        /// <summary>
        /// HUB-8: the pre-reload ConsoleErrorProvider.FormatDigest() text,
        /// so a "compile FAILED" continuation can name the actual errors
        /// instead of shipping an empty list. Empty string means "nothing
        /// carried over".
        /// </summary>
        public static string LastCompileErrorDigest
        {
            get { return SessionState.GetString(KeyLastCompileErrorDigest, string.Empty); }
            set { SessionState.SetString(KeyLastCompileErrorDigest, value ?? string.Empty); }
        }

        /// <summary>HUB-8: clears the carry-over pair after it has been consumed.</summary>
        public static void ClearLastCompileResult()
        {
            // Plain assignment, matching ClearProcessRecord's idiom in this
            // file (the Erase* overloads exist but nothing else here uses them).
            LastCompileHadErrors = false;
            LastCompileErrorDigest = string.Empty;
        }

        // -- uLoop install progress (docs/design-notes/2026-08-12-uloop-
        // install-progress.md section 2a) -- the in-flight marker pair
        // SettingsView writes on a successful Apply and UloopInstallProgress
        // evaluates on every refresh. SessionState-backed for the same
        // reason as the auto-continue flags above: the event these exist to
        // survive IS a domain reload (a successful package resolve imports
        // the package, recompiles, and reloads, destroying the AddRequest
        // object mid-install), so a plain static would silently revert the
        // section to "Not installed" at the exact moment the install is
        // landing. ------------------------------------------------------

        /// <summary>
        /// True from the moment SettingsView's Apply handler confirms
        /// UloopInstaller.Apply dispatched Client.Add, until the install
        /// progress machine reaches a terminal state (installed detected /
        /// resolve failed / stale) and orders the flag cleared
        /// (UloopInstallProgressResult.ShouldClearFlag). While the live
        /// AddRequest exists this flag is redundant; after the reload
        /// destroys that object, this flag plus
        /// <see cref="UloopInstallStartedAtUtcTicks"/> is the ONLY evidence
        /// an install is still in flight.
        /// </summary>
        public static bool UloopInstallInFlight
        {
            get { return SessionState.GetBool(KeyUloopInstallInFlight, false); }
            set { SessionState.SetBool(KeyUloopInstallInFlight, value); }
        }

        /// <summary>
        /// UTC DateTime.Ticks captured when <see cref="UloopInstallInFlight"/>
        /// was set. A bare bool is not enough for the same reason
        /// <see cref="AutoContinuePendingArmedAtUtcTicks"/> exists: if the
        /// resolve dies without a reload ever consuming the flag (editor
        /// crash, resolve failure during the reload window), a flag with no
        /// age would render "Installing..." forever on every future editor
        /// session's refresh. The age gates Installing vs Stalled against
        /// UloopInstallProgress.StaleThresholdSeconds. Stored as a string,
        /// same pattern as ProcessStartTicks above (SessionState has no
        /// native 64-bit integer accessor). 0 means "never set".
        /// </summary>
        public static long UloopInstallStartedAtUtcTicks
        {
            get
            {
                string raw = SessionState.GetString(KeyUloopInstallStartedAtUtcTicks, "0");
                long value;
                return long.TryParse(raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) ? value : 0L;
            }
            set
            {
                SessionState.SetString(KeyUloopInstallStartedAtUtcTicks,
                    value.ToString(CultureInfo.InvariantCulture));
            }
        }

        // -- Unity official plugin install (design note 2026-09-10 section
        // 2.3): the same reload-surviving pair as uLoop's above, for the
        // ThreadPool worker that a domain reload orphans. ---------------

        public static bool UnityPluginInstallInFlight
        {
            get { return SessionState.GetBool(KeyUnityPluginInstallInFlight, false); }
            set { SessionState.SetBool(KeyUnityPluginInstallInFlight, value); }
        }

        public static long UnityPluginInstallStartedAtUtcTicks
        {
            get
            {
                string raw = SessionState.GetString(KeyUnityPluginInstallStartedAtUtcTicks, "0");
                long value;
                return long.TryParse(raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) ? value : 0L;
            }
            set
            {
                SessionState.SetString(KeyUnityPluginInstallStartedAtUtcTicks,
                    value.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Scene-view markers (design note 2026-09-07 section 1.3.1 / M4):
        /// the SceneMarkerStore's JSON, written on every change and read
        /// back by its [InitializeOnLoadMethod] so the agent's "here" and
        /// the user's pins survive the domain reload a script save causes.
        /// Empty = no markers. SessionState, so an editor restart starts
        /// clean -- markers only mean something in the session that placed
        /// them.
        /// </summary>
        public static string SceneMarkersJson
        {
            get { return SessionState.GetString(KeySceneMarkers, string.Empty); }
            set { SessionState.SetString(KeySceneMarkers, value ?? string.Empty); }
        }

        /// <summary>
        /// Images attached in the composer but not yet sent (design note
        /// 2026-09-07 section 2.3.6): a JSON array of ImageAttachment
        /// objects (paths only), kept next to <see cref="InputDraft"/> so a
        /// domain reload never loses an attached-but-unsent picture.
        /// </summary>
        public static string ComposerImagesJson
        {
            get { return SessionState.GetString(KeyComposerImages, string.Empty); }
            set { SessionState.SetString(KeyComposerImages, value ?? string.Empty); }
        }

        /// <summary>
        /// Design note 2026-09-10 section 3: the display name of the
        /// can_use_tool request that was still awaiting the user's answer
        /// when a domain reload hit. ReloadLifecycle.OnBeforeAssemblyReload
        /// captures AgentHub.PendingPermission's name into this field
        /// BEFORE AgentHub.ShutdownForReload runs -- that teardown
        /// unconditionally clears the pending permission (the CLI process
        /// answering it is gone), and until this field existed that
        /// discard was silent: no transcript note, no mention in the
        /// interrupted-turn continuation message, leaving the model free to
        /// assume the tool call it never heard back from had simply
        /// finished. AgentHub.ConsumeReloadDroppedPermission reads and
        /// clears this exactly once per reload, on the post-reload
        /// reconciliation tick, regardless of whether it ends up
        /// announcing it (see that method's own doc comment). Empty string
        /// means "no permission was pending" -- also the value a consume
        /// resets it to.
        /// </summary>
        public static string ReloadDroppedPermissionTool
        {
            get { return SessionState.GetString(KeyReloadDroppedPermissionTool, string.Empty); }
            set { SessionState.SetString(KeyReloadDroppedPermissionTool, value ?? string.Empty); }
        }
}
}

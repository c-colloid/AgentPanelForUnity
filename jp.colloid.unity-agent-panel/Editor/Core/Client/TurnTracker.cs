namespace Colloid.AgentPanel.Core.Client
{
    /// <summary>
    /// Tracks the current turn (ARCHITECTURE.md 3.4):
    /// - Turn ids are assigned panel-side so residual events arriving after
    ///   an interrupt can be discarded deterministically (avoids the
    ///   300ms-heuristic trap from the ComfyUI panel postmortem).
    /// - isReplay echo tracking: the CLI replays our user message with
    ///   isReplay:true, which we treat as the delivery ack.
    /// - Silence backstop: when a result never arrives, the last-activity
    ///   clock lets AgentClient force the turn closed after 10 minutes.
    /// Time is passed in by the caller (monotonic seconds) for testability.
    ///
    /// Open-turn accounting -- corrected 2026-08-03 (docs/research/
    /// 02-claude-cli-protocol.md section 4.1, design note
    /// 2026-08-03-subagent-ux-and-midturn-input.md section 3): a SEQUENTIAL
    /// send (no turn currently open) always starts a new counted turn, same
    /// as always. A MID-TURN send -- a turn is already open and has NOT
    /// been interrupted -- is STEERING, not queueing: the CLI folds it into
    /// the turn already running and still emits exactly ONE result for the
    /// whole thing. Measured twice against the real CLI: a plain streaming
    /// turn where message 2 was written at 4922ms while turn 1's result did
    /// not arrive until 22568ms (one result total; both instructions were
    /// carried out), and the user's literal scenario where message 2 was
    /// sent 195ms after a Task subagent spawned (one result, turn completed
    /// normally at 51206ms, no error). BeginTurn therefore does NOT
    /// increment the open-turn counter for that case -- only for a
    /// genuinely fresh turn, or for a send issued while the PREVIOUS turn
    /// is already being torn down by Interrupt (that teardown still ends
    /// with its own result, so a send issued before that result arrives
    /// starts a real second turn; this path is untouched by the
    /// correction and was already covered by existing tests). Getting this
    /// wrong in the other direction is the concrete failure being guarded
    /// against: if a mid-turn send counted as a second open turn, the
    /// single result the CLI actually sends would only balance one of the
    /// two, and the panel would read "busy" forever.
    ///
    /// Three call sites (AgentClient.cs's SendUserText/HandleResult and this
    /// class) originally cited section 4.1 for "one result per user
    /// message" -- that section only ever measured SEQUENTIAL sends; it
    /// said nothing about mid-turn sends until the correction above.
    /// </summary>
    public sealed class TurnTracker
    {
        /// <summary>Default silence backstop: 10 minutes.</summary>
        public const double DefaultSilenceTimeoutSeconds = 600.0;

        private int _lastTurnId;
        private int _openTurns;

        public TurnTracker()
        {
            SilenceTimeoutSeconds = DefaultSilenceTimeoutSeconds;
        }

        /// <summary>
        /// Id of the current (or most recent) turn. 0 = none yet. A
        /// mid-turn steering send (see class remarks) does not bump this --
        /// it folds into the same turn rather than starting a new one.
        /// </summary>
        public int CurrentTurnId { get; private set; }

        /// <summary>
        /// True while at least one turn is open. This is a counter, not a
        /// plain flag, because a send issued while an earlier turn is being
        /// torn down by Interrupt genuinely produces two results (the
        /// interrupted turn's own, then the next turn's). Ordinary mid-turn
        /// STEERING sends do not add to the counter at all (see class
        /// remarks / BeginTurn) since they share the running turn's single
        /// eventual result. Only when the counter reaches zero is the
        /// conversation really idle.
        /// </summary>
        public bool TurnActive
        {
            get { return _openTurns > 0; }
        }

        /// <summary>
        /// Number of genuinely distinct open turns (started, result not yet
        /// seen). Mid-turn steering sends folded via BeginTurn do NOT add to
        /// this -- see class remarks.
        /// </summary>
        public int OpenTurnCount
        {
            get { return _openTurns; }
        }

        /// <summary>True after BeginTurn until the isReplay echo arrives.</summary>
        public bool AwaitingAck { get; private set; }

        /// <summary>
        /// True after MarkInterrupted until the turn ends: streaming deltas
        /// for this turn should be dropped.
        /// </summary>
        public bool InterruptRequested { get; private set; }

        /// <summary>Seconds without inbound lines before the turn is force-closed.</summary>
        public double SilenceTimeoutSeconds { get; set; }

        /// <summary>Monotonic timestamp (seconds) of the last inbound activity.</summary>
        public double LastActivitySeconds { get; private set; }

        /// <summary>
        /// Records a user message being written to the CLI and returns the
        /// turn id it belongs to.
        ///
        /// Opens a genuinely NEW counted turn when either (a) no turn is
        /// currently open (the ordinary sequential case, unchanged), or
        /// (b) a turn IS open but has already been interrupted -- that
        /// turn is being torn down and will still surface its own result,
        /// so this send starts a real second turn (the existing fence
        /// semantics below still apply to it, unchanged).
        ///
        /// Otherwise -- a turn is open and has NOT been interrupted -- this
        /// is a mid-turn STEERING send: measured against the real CLI, it
        /// gets folded into the turn already running rather than queued as
        /// a second one, and the CLI emits exactly one result for the
        /// merged turn (see class remarks for the two probes). The
        /// open-turn counter and CurrentTurnId are deliberately left
        /// untouched in this branch so that the single incoming result
        /// closes the turn via one EndTurn call, instead of leaving the
        /// counter stuck above zero -- which would read as "busy" forever.
        ///
        /// Does NOT clear InterruptRequested in the interrupted-reopen
        /// case: a send queued right after an interrupt must not un-fence
        /// residual deltas of the interrupted turn -- the fence is lifted
        /// by EndTurn (result received) instead.
        /// </summary>
        public int BeginTurn(double nowSeconds)
        {
            bool opensNewTurn = _openTurns == 0 || InterruptRequested;
            if (opensNewTurn)
            {
                _lastTurnId++;
                CurrentTurnId = _lastTurnId;
                _openTurns++;
            }
            AwaitingAck = true;
            LastActivitySeconds = nowSeconds;
            return CurrentTurnId;
        }

        /// <summary>Records the isReplay delivery ack.</summary>
        public void MarkAck()
        {
            AwaitingAck = false;
        }

        /// <summary>Flags the current turn as interrupted (deltas get dropped).</summary>
        public void MarkInterrupted()
        {
            if (TurnActive)
            {
                InterruptRequested = true;
            }
        }

        /// <summary>Records inbound activity (any parsed line) for the backstop.</summary>
        public void MarkActivity(double nowSeconds)
        {
            LastActivitySeconds = nowSeconds;
        }

        /// <summary>
        /// Closes one open turn (one result received, or forced). A turn
        /// that only ever had mid-turn STEERING sends folded into it (see
        /// BeginTurn) closes to zero on this single call, matching the
        /// single result the CLI actually sends for it -- no matter how
        /// many user messages were written into that turn. A turn reopened
        /// by a post-interrupt queued send still needs one EndTurn per
        /// result, same as before the correction. The interrupt fence is
        /// lifted here because a result marks the boundary between the
        /// interrupted turn's residue and the next (queued) turn's fresh
        /// stream.
        /// </summary>
        public void EndTurn()
        {
            if (_openTurns > 0)
            {
                _openTurns--;
            }
            AwaitingAck = false;
            InterruptRequested = false;
        }

        /// <summary>True when the active turn has been silent for too long.</summary>
        public bool IsSilenceExceeded(double nowSeconds)
        {
            return TurnActive
                && SilenceTimeoutSeconds > 0
                && (nowSeconds - LastActivitySeconds) >= SilenceTimeoutSeconds;
        }

        /// <summary>Resets all turn state (process restart). Turn ids keep increasing.</summary>
        public void Reset()
        {
            _openTurns = 0;
            AwaitingAck = false;
            InterruptRequested = false;
        }
    }
}

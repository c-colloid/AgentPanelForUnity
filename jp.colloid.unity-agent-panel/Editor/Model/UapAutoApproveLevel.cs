namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// How much of the agent's Unity work the panel approves on the user's
    /// behalf, without showing a permission card. Each level INCLUDES every
    /// level above it in this list.
    ///
    /// Why this is panel-side policy rather than a CLI --permission-mode
    /// value, measured 2026-08-03 against the real CLI (five modes, one run
    /// each, PreToolUse hook installed, observing the wire):
    ///
    ///   mode         can_use_tool  hook fires  deny blocks  init echoes
    ///   default      yes           yes         yes          default
    ///   acceptEdits  NO            yes         yes          acceptEdits
    ///   auto         yes           yes         yes          DEFAULT
    ///   dontAsk      NO            yes         yes          dontAsk
    ///   manual       yes           yes         yes          DEFAULT
    ///
    /// Three conclusions that rule the CLI out as the mechanism:
    ///   * `auto` and `manual` are accepted but resolve to `default` -- both
    ///     the system/init echo AND the permission_mode passed to the hook
    ///     say so. Exposing them as modes would promise a behaviour change
    ///     that does not happen.
    ///   * `dontAsk` auto-DENIES. The hook fired for both writes in that run
    ///     and NEITHER file appeared on disk, including the one nothing was
    ///     supposed to block. It stops the asking by refusing the work.
    ///   * `acceptEdits` genuinely skips the round trip, but only for
    ///     Write/Edit-family tools; MCP tools still fire it (design note
    ///     2026-08-01 section 8.7). Every Unity action this panel offers is
    ///     an MCP (UapOps) tool, so it barely touches the actual fatigue.
    ///
    /// The one piece of good news from the same run: the PreToolUse hook
    /// fired AND its deny still blocked in all five modes, so the script
    /// gate's hook layer is mode-independent. Nothing here can be used to
    /// argue that gate away.
    /// </summary>
    public enum UapAutoApproveLevel
    {
        /// <summary>
        /// Show a permission card for everything. Nothing is approved on the
        /// user's behalf.
        /// </summary>
        Ask = 0,

        /// <summary>
        /// Silently approve UapOps tools that cannot change anything
        /// (<c>IUapTool.ReadOnly</c>). This is what the v0.14.0
        /// <c>autoApproveReadOnlyOps</c> toggle did, and existing settings
        /// migrate to exactly this level so nobody's behaviour changes on
        /// upgrade.
        /// </summary>
        ReadOnly = 1,

        /// <summary>
        /// Also approve UapOps tools whose effect Undo can reverse
        /// (<c>IUapTool.Undoable</c>). The safety argument is concrete
        /// rather than a vibe: whatever the agent does at this level, Ctrl+Z
        /// takes back.
        /// </summary>
        Undoable = 2,

        /// <summary>
        /// Also approve UapOps tools whose effect Undo CANNOT reverse. The
        /// turn-end "this turn ran operations Undo cannot take back" warning
        /// still fires -- but at this level it is a notification after the
        /// fact, not a chance to stop it. Chosen deliberately by the user;
        /// never a default.
        /// </summary>
        AllUnityOps = 3,

        /// <summary>
        /// Design note 2026-09-10 (auto-approve-all-tools): also approve
        /// every CLI-native tool -- Bash, Edit, Write, Read, Glob, Grep,
        /// WebFetch, other MCP servers -- so nothing but an AskUserQuestion
        /// (requires_user_interaction) ever shows a card. The script-
        /// validation gate's auto-DENY still runs first (pinned by
        /// AgentHubPermissionOrderTests), and the end-of-turn non-undoable
        /// warning still fires. This reverses the v0.14.0 decision that
        /// "Bash-shaped tools stay a slow, confirmation-heavy path no
        /// matter the level": a live session measured 10+ cards per single
        /// user action at AllUnityOps, which trains the user to click
        /// Allow without reading -- worse for safety than an explicit,
        /// confirmed, never-default opt-in. The user chooses it through the
        /// same escalation confirmation AllUnityOps has.
        /// </summary>
        AllTools = 4
    }
}

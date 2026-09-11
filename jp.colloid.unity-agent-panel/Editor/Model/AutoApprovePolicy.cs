namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure decision function behind the panel-side auto-approve feature
    /// (see UapAutoApproveLevel's doc comment for why this lives in the
    /// panel instead of as a CLI --permission-mode value). No Unity API,
    /// no side effects -- this is the tested core of an otherwise
    /// untestable flow: the caller (AgentHub.OnPermissionRequested, via a
    /// TryAutoApprove-shaped helper analogous to the existing
    /// TryAutoApproveReadOnlyUapOpsTool/TryAutoDenyForScriptGate pair) is
    /// wired to a live can_use_tool control_request, a real AgentClient,
    /// and PanelStateStore -- none of which this function touches.
    /// </summary>
    public static class AutoApprovePolicy
    {
        /// <summary>
        /// True when a can_use_tool request for a tool with the given
        /// metadata should be auto-approved at the given level, without
        /// ever showing a permission card.
        /// </summary>
        /// <param name="level">
        /// The user's chosen auto-approve level (Settings). Each level
        /// includes every level above it in the enum's declared order
        /// (Ask &lt; ReadOnly &lt; Undoable &lt; AllUnityOps).
        /// </param>
        /// <param name="isUapOpsTool">
        /// True only when the request resolved (via
        /// UapOpsServer.FindByWireName or equivalent) to a tool this
        /// codebase registered in the UapOps MCP server. This is the
        /// load-bearing parameter: when it is false, this method ALWAYS
        /// returns false, at every level up to and including AllUnityOps
        /// (AllTools is the one exception, and only through the 5-argument
        /// overload below -- design note 2026-09-10-auto-approve-all-tools) -- no
        /// exceptions, no escape hatch.
        ///
        /// Why: the CLI's own Bash/Write/Edit/MultiEdit/etc. tools must
        /// keep going through the ordinary permission card, because the
        /// script-validation gate's can_use_tool pre-filter
        /// (AgentHub.TryAutoDenyForScriptGate) depends on that card path
        /// running first for those tools -- an auto-approve here would
        /// bypass a check the gate assumes always gets a chance to run.
        /// More fundamentally, Bash is the general-purpose escape hatch
        /// (dynamic/arbitrary code execution, up to and including the
        /// uloop workflow this project treats as a hard operational
        /// hazard -- see the repo's HARD SAFETY RULES on `uloop`). The
        /// v0.14.0 design note explicitly considered extending
        /// auto-approval to non-UapOps tools and REJECTED it: Bash-shaped
        /// tools stay a slow, confirmation-heavy path ON PURPOSE, no
        /// matter how permissive the user's chosen level is. AllUnityOps
        /// only ever means "all UapOps tools" -- never "all tools".
        /// </param>
        /// <param name="readOnly">
        /// IUapTool.ReadOnly for the resolved tool: true when it never
        /// mutates the project/scene. Ignored when isUapOpsTool is false.
        /// </param>
        /// <param name="undoable">
        /// IUapTool.Undoable for the resolved tool: true when its mutation
        /// is covered by a Unity Undo operation. Ignored when
        /// isUapOpsTool is false.
        /// </param>
        /// <returns>
        /// True when the request should be silently allowed; false when
        /// it must fall through to the normal permission card. An
        /// out-of-range/unknown <paramref name="level"/> (e.g. a future
        /// enum value this build predates, or a corrupted/hand-edited
        /// settings asset) falls back to the SAFEST behaviour -- false,
        /// i.e. "show the card" -- never to the most permissive one. This
        /// mirrors PermissionModeMapping.FromCliValue's degrade-to-safe
        /// pattern for unknown input, except the safe direction here is
        /// "ask" rather than "default".
        /// </returns>
        public static bool ShouldAutoApprove(UapAutoApproveLevel level, bool isUapOpsTool, bool readOnly, bool undoable)
        {
            return ShouldAutoApprove(level, isUapOpsTool, readOnly, undoable, false);
        }

        /// <summary>
        /// Design note 2026-09-10 (auto-approve-all-tools): the full
        /// decision. <paramref name="requiresUserInteraction"/> is the
        /// request's requires_user_interaction flag (AskUserQuestion): such
        /// a request is a question for the human and is NEVER auto-answered
        /// at any level, including <see cref="UapAutoApproveLevel.AllTools"/>.
        /// AllTools approves everything else, UapOps or not -- the
        /// "isUapOpsTool false always means false" rule of the 4-argument
        /// overload holds for every level below it. The script gate's
        /// auto-deny is not modelled here: AgentHub runs it BEFORE this
        /// decision (AgentHubPermissionOrderTests), so a gated write never
        /// reaches this method.
        /// </summary>
        public static bool ShouldAutoApprove(UapAutoApproveLevel level, bool isUapOpsTool, bool readOnly,
            bool undoable, bool requiresUserInteraction)
        {
            if (requiresUserInteraction)
            {
                return false;
            }
            if (level == UapAutoApproveLevel.AllTools)
            {
                return true;
            }
            if (!isUapOpsTool)
            {
                return false;
            }
            switch (level)
            {
                case UapAutoApproveLevel.Ask:
                    return false;
                case UapAutoApproveLevel.ReadOnly:
                    return readOnly;
                case UapAutoApproveLevel.Undoable:
                    return readOnly || undoable;
                case UapAutoApproveLevel.AllUnityOps:
                    return true;
                default:
                    // Unknown/out-of-range enum value (e.g. (UapAutoApproveLevel)999):
                    // safest possible behaviour is to show the card, exactly as
                    // if the level were Ask. Never fall back to permissive.
                    return false;
            }
        }
    }
}

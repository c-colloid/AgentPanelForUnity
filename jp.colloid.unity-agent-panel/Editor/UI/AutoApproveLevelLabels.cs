using System.Collections.Generic;
using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The single place a <see cref="UapAutoApproveLevel"/> becomes text.
    ///
    /// Two surfaces show this setting -- the always-visible header control
    /// and the Settings dropdown -- and they must never word it differently:
    /// a user who reads "Undoable Unity ops" in one place and something else
    /// in the other has no way to tell whether they are looking at the same
    /// setting. Both go through here.
    /// </summary>
    public static class AutoApproveLevelLabels
    {
        /// <summary>
        /// Every level, least permissive first. The order is the order both
        /// the header menu and the Settings dropdown present, and it matches
        /// the enum's own ascending order so "higher is more permissive"
        /// reads the same in the code and on screen.
        /// </summary>
        public static readonly UapAutoApproveLevel[] Ordered =
        {
            UapAutoApproveLevel.Ask,
            UapAutoApproveLevel.ReadOnly,
            UapAutoApproveLevel.Undoable,
            UapAutoApproveLevel.AllUnityOps,
            UapAutoApproveLevel.AllTools
        };

        /// <summary>
        /// Localized name for one level. An unrecognised value falls back to
        /// the SAFEST label rather than an empty string, so a settings asset
        /// carrying a level from some future build still reads as something
        /// truthful about how little is being auto-approved -- matching
        /// <see cref="AutoApprovePolicy"/>, which also treats an unknown
        /// level as "approve nothing".
        /// </summary>
        public static string Describe(UapAutoApproveLevel level)
        {
            switch (level)
            {
                case UapAutoApproveLevel.ReadOnly:
                    return L10n.S.AutoApproveLevelReadOnly;
                case UapAutoApproveLevel.Undoable:
                    return L10n.S.AutoApproveLevelUndoable;
                case UapAutoApproveLevel.AllUnityOps:
                    return L10n.S.AutoApproveLevelAllUnityOps;
                case UapAutoApproveLevel.AllTools:
                    return L10n.S.AutoApproveLevelAllTools;
                default:
                    return L10n.S.AutoApproveLevelAsk;
            }
        }

        /// <summary>
        /// Compact localized name for the header chip. Measured in the live
        /// panel: with the full names the chip was squeezed to its 46 px
        /// min-width and showed roughly three characters -- and a truncated
        /// level name is worse than a short one, because it reads as a
        /// different level. Everything with room (the menu, the Settings
        /// dropdown, the tooltip) uses <see cref="Describe"/> instead.
        /// Unknown values fall back to the safest label, matching
        /// <see cref="Describe"/> and <see cref="AutoApprovePolicy"/>.
        /// </summary>
        public static string DescribeShort(UapAutoApproveLevel level)
        {
            switch (level)
            {
                case UapAutoApproveLevel.ReadOnly:
                    return L10n.S.AutoApproveShortReadOnly;
                case UapAutoApproveLevel.Undoable:
                    return L10n.S.AutoApproveShortUndoable;
                case UapAutoApproveLevel.AllUnityOps:
                    return L10n.S.AutoApproveShortAllUnityOps;
                case UapAutoApproveLevel.AllTools:
                    return L10n.S.AutoApproveShortAllTools;
                default:
                    return L10n.S.AutoApproveShortAsk;
            }
        }

        /// <summary>Localized names in <see cref="Ordered"/> order (dropdown choices).</summary>
        public static List<string> DescribeAll()
        {
            var names = new List<string>(Ordered.Length);
            for (int i = 0; i < Ordered.Length; i++)
            {
                names.Add(Describe(Ordered[i]));
            }
            return names;
        }

        /// <summary>Index of a level within <see cref="Ordered"/>; 0 (the safest) when unknown.</summary>
        public static int IndexOf(UapAutoApproveLevel level)
        {
            for (int i = 0; i < Ordered.Length; i++)
            {
                if (Ordered[i] == level)
                {
                    return i;
                }
            }
            return 0;
        }

        /// <summary>Level at an index in <see cref="Ordered"/>; Ask when out of range.</summary>
        public static UapAutoApproveLevel At(int index)
        {
            return index >= 0 && index < Ordered.Length
                ? Ordered[index] : UapAutoApproveLevel.Ask;
        }

        /// <summary>
        /// UXA-3: whether switching from <paramref name="current"/> to
        /// <paramref name="target"/> needs an explicit confirmation. Only
        /// the escalation INTO <see cref="UapAutoApproveLevel.AllUnityOps"/>
        /// qualifies -- by the enum's own doc it is the one level where
        /// "Undo CANNOT reverse" the auto-approved effects and the
        /// end-of-turn warning becomes a notification after the fact
        /// ("never a default"). Every other transition, including any
        /// DOWNWARD move away from AllUnityOps, stays frictionless: the
        /// dialog exists to stop an accidental one-click escalation, not to
        /// slow down retreat to safety. Pure (no UI), so the decision is
        /// unit-tested; both write surfaces (the header menu and the
        /// Settings dropdown) gate through
        /// <see cref="ConfirmEscalationIfNeeded"/> below.
        /// </summary>
        public static bool RequiresConfirmation(
            UapAutoApproveLevel current, UapAutoApproveLevel target)
        {
            // Any move UP into AllUnityOps or AllTools confirms; a move
            // down (AllTools -> AllUnityOps) does not, since it narrows.
            return (target == UapAutoApproveLevel.AllUnityOps || target == UapAutoApproveLevel.AllTools)
                && (int)target > (int)current;
        }

        /// <summary>
        /// UXA-3: the shared confirmation gate both write surfaces call
        /// BEFORE assigning PanelSettings.autoApproveLevel. Ordering is
        /// safety-critical: applying the level immediately auto-answers a
        /// permission request already waiting on screen
        /// (AgentHub.ApplyAutoApproveLevelChanged), and an approved request
        /// cannot be un-answered -- so the dialog must run first. Returns
        /// true when the change may proceed (no confirmation needed, or the
        /// user confirmed). Follows the package's two existing
        /// EditorUtility.DisplayDialog call sites (dedicated
        /// Title/Body/Confirm/Cancel L10n keys, early-return on cancel).
        /// </summary>
        public static bool ConfirmEscalationIfNeeded(
            UapAutoApproveLevel current, UapAutoApproveLevel target)
        {
            if (!RequiresConfirmation(current, target))
            {
                return true;
            }
            bool allTools = target == UapAutoApproveLevel.AllTools;
            return UnityEditor.EditorUtility.DisplayDialog(
                allTools ? L10n.S.AutoApproveConfirmAllToolsTitle : L10n.S.AutoApproveConfirmAllTitle,
                allTools ? L10n.S.AutoApproveConfirmAllToolsBody : L10n.S.AutoApproveConfirmAllBody,
                L10n.S.AutoApproveConfirmAllConfirmButton,
                L10n.S.AutoApproveConfirmAllCancelButton);
        }
    }
}

using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.UI
{
    // MODEL-5 (D9 layering): this file LIVES in the UI layer now. It sat
    // in Colloid.AgentPanel.Model while referencing L10n (a UI-layer
    // catalog), violating the one-way UI -> Model -> Core rule. The class
    // is a view model by nature -- its whole reason for resolving labels
    // here is so tests can pin real L10n strings (2026-08-14 design note)
    // -- so it moved to the layer its dependency already lived in, GUID
    // preserved. UI -> Model (SessionGroup) is the legal direction. The
    // alternative (returning label KEYS and resolving in the view) is
    // recorded in the design note as the fallback if Model purity is ever
    // enforced mechanically.

    /// <summary>
    /// One row of the history row-action popup (docs/design-notes/
    /// 2026-08-14-history-menu-and-token-hygiene.md Part 2), as data. A
    /// plain field bag rather than a GenericMenu call, so
    /// HistoryRowMenuModelTests can exercise every state combination
    /// without an EditorWindow -- the same "pure model, dumb view" split
    /// HistoryListModel already established for this same class
    /// (HistoryView.ShowRowMenu only turns these into VisualElements and
    /// maps <see cref="Id"/> back to its existing handlers).
    /// </summary>
    public sealed class HistoryRowMenuItem
    {
        /// <summary>Stable action id (see the HistoryRowMenuModel.Action*
        /// constants), or a "group:&lt;id&gt;" id for a custom-group entry
        /// on the group page. Empty for a separator.</summary>
        public string Id = string.Empty;

        /// <summary>
        /// The exact L10n string for this row (or a raw
        /// <c>SessionGroup.name</c> for a group entry) -- resolved HERE
        /// rather than by the caller so HistoryRowMenuModelTests can pin
        /// real label text via L10n.S, matching the design note's "prefer
        /// resolving in the model" call. Any directional decoration
        /// ("Group &gt;", "&lt; Back") is added by the popover at render
        /// time, never baked in here, so this field always equals the
        /// catalog string (or the group's own name) exactly.
        /// </summary>
        public string Label = string.Empty;

        /// <summary>Checkmark state (pinned/archived/current-group).</summary>
        public bool Checked;

        /// <summary>False for Delete on a foreign or live-session row, and
        /// for Open on a foreign row (UXIA-1).</summary>
        public bool Enabled = true;

        /// <summary>True for a divider row; every other field is unused.</summary>
        public bool IsSeparator;

        /// <summary>
        /// Disabled-row explanation (Delete and Open) -- empty otherwise.
        /// GenericMenu's AddDisabledItem could gray a row out but never
        /// say why; a popup row can carry a real tooltip instead.
        /// </summary>
        public string Tooltip = string.Empty;
    }

    /// <summary>
    /// Builds the two pages of the history row-action popup as pure data
    /// (docs/design-notes/2026-08-14-history-menu-and-token-hygiene.md
    /// Part 2's "Pure model for tests" section). HistoryView.ShowRowMenu
    /// used to build a GenericMenu directly with no seam a test could
    /// reach; every decision that menu made -- item order, which entries
    /// are checked, which is disabled and why -- now lives here instead,
    /// list-order-deterministic and covered by HistoryRowMenuModelTests.
    /// </summary>
    public static class HistoryRowMenuModel
    {
        // -- Item ids (also HistoryView.OnRowMenuChoice's dispatch keys) ---------

        /// <summary>UXIA-1: the restore action itself, so the row's most
        /// important operation finally has a non-mouse path (the row body
        /// is keyboard-activatable too; this covers the menu route).</summary>
        public const string ActionOpen = "open";
        public const string ActionPin = "pin";
        public const string ActionRename = "rename";
        public const string ActionArchive = "archive";
        /// <summary>Main-page row that navigates to the group page (never
        /// reaches HistoryView's callback -- the popover handles paging
        /// itself, see HistoryRowMenuPopover.OnItemChosen).</summary>
        public const string ActionGroupOpen = "group-open";
        public const string ActionDelete = "delete";
        /// <summary>Group-page row that navigates back to the main page
        /// (also handled entirely inside the popover).</summary>
        public const string ActionGroupBack = "group-back";
        public const string ActionGroupNone = "group-none";
        public const string ActionGroupNew = "group-new";
        /// <summary>Prefix for a custom-group entry's id: "group:" + SessionGroup.id.</summary>
        public const string GroupIdPrefix = "group:";

        /// <summary>
        /// Main page: Open (UXIA-1), Pin/Unpin, Rename, Archive/Unarchive,
        /// separator, "Move to group" (opens the group page), separator,
        /// Delete -- exact order and separator positions from the design
        /// note. <paramref name="deleteDisabledBecauseForeign"/> doubles as
        /// the plain "this row is foreign" fact: it disables Open (a
        /// foreign session cannot be resumed against this project, the
        /// same rule OnRowClicked enforces) and, while
        /// <paramref name="canDelete"/> is false, picks which of the two
        /// disabled-Delete tooltips applies (the foreign-project case and
        /// the live-session case are mutually exclusive by construction --
        /// see HistoryView.CanDeleteRow -- so the caller only ever needs
        /// to pass whichever ONE reason actually applies).
        /// </summary>
        public static List<HistoryRowMenuItem> BuildMainItems(bool pinned, bool archived,
            bool canDelete, bool deleteDisabledBecauseForeign)
        {
            var items = new List<HistoryRowMenuItem>(8);

            items.Add(new HistoryRowMenuItem
            {
                Id = ActionOpen,
                Label = L10n.S.HistoryActionOpen,
                Enabled = !deleteDisabledBecauseForeign,
                Tooltip = deleteDisabledBecauseForeign
                    ? L10n.S.HistoryOpenDisabledForeignTooltip
                    : string.Empty
            });
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionPin,
                Label = pinned ? L10n.S.HistoryActionUnpin : L10n.S.HistoryActionPin,
                Checked = pinned
            });
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionRename,
                Label = L10n.S.HistoryActionRename
            });
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionArchive,
                Label = archived ? L10n.S.HistoryActionUnarchive : L10n.S.HistoryActionArchive,
                Checked = archived
            });
            items.Add(Separator());
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionGroupOpen,
                Label = L10n.S.HistoryActionGroupSubmenu
            });
            items.Add(Separator());
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionDelete,
                Label = L10n.S.HistoryActionDelete,
                Enabled = canDelete,
                Tooltip = canDelete
                    ? string.Empty
                    : (deleteDisabledBecauseForeign
                        ? L10n.S.HistoryDeleteDisabledForeignTooltip
                        : L10n.S.HistoryDeleteDisabledLiveTooltip)
            });

            return items;
        }

        /// <summary>
        /// Group page: back row, "(none)", every custom group in STORE
        /// order (skipping a null/empty-id entry -- same defensive rule
        /// ShowRowMenu already applied before this model existed), "New
        /// group..." last. No separators here (only the main page has
        /// any). The checked entry follows <paramref name="currentGroupId"/>:
        /// "(none)" when empty, the matching group otherwise; a group id
        /// that no longer exists in <paramref name="groups"/> simply
        /// leaves nothing checked, same as the old submenu.
        /// </summary>
        public static List<HistoryRowMenuItem> BuildGroupItems(string currentGroupId,
            IList<SessionGroup> groups)
        {
            currentGroupId = currentGroupId ?? string.Empty;
            var items = new List<HistoryRowMenuItem>(groups != null ? groups.Count + 3 : 3);

            items.Add(new HistoryRowMenuItem
            {
                Id = ActionGroupBack,
                Label = L10n.S.HistoryMenuGroupBack
            });
            items.Add(new HistoryRowMenuItem
            {
                Id = ActionGroupNone,
                Label = L10n.S.HistoryActionGroupNone,
                Checked = string.IsNullOrEmpty(currentGroupId)
            });

            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    SessionGroup group = groups[i];
                    if (group == null || string.IsNullOrEmpty(group.id))
                    {
                        continue;
                    }
                    items.Add(new HistoryRowMenuItem
                    {
                        Id = GroupIdPrefix + group.id,
                        Label = group.name ?? string.Empty,
                        Checked = string.Equals(currentGroupId, group.id, StringComparison.Ordinal)
                    });
                }
            }

            items.Add(new HistoryRowMenuItem
            {
                Id = ActionGroupNew,
                Label = L10n.S.HistoryActionGroupNew
            });

            return items;
        }

        private static HistoryRowMenuItem Separator()
        {
            return new HistoryRowMenuItem { IsSeparator = true };
        }
    }
}

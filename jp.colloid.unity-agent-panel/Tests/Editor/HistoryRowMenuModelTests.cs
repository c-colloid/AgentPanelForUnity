using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Exhaustive pure-logic coverage for HistoryRowMenuModel -- every
    /// decision the history row's action popup makes (item order, checked
    /// state, which reason a disabled Delete carries), now that it lives
    /// as data instead of inside a GenericMenu no test could ever open
    /// (docs/design-notes/2026-08-14-history-menu-and-token-hygiene.md
    /// Part 2). Every label assertion compares against the live L10n
    /// catalog (never a hardcoded literal) so a future catalog edit can
    /// never silently desync this file from what the UI actually shows.
    /// </summary>
    public class HistoryRowMenuModelTests
    {
        [SetUp]
        public void SetUp()
        {
            // Same rationale as HistoryViewLogicTests: pin English so these
            // assertions never depend on this machine's OS language or on
            // test execution order relative to a test that applies Japanese.
            L10n.OverrideForTests(PanelLanguage.English);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
        }

        // -- BuildMainItems: shape and order -----------------------------------------

        [Test]
        public void BuildMainItems_OrderAndSeparatorPositions_MatchTheDesignNote()
        {
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false);

            Assert.AreEqual(8, items.Count);
            Assert.AreEqual(HistoryRowMenuModel.ActionOpen, items[0].Id,
                "UXIA-1: the restore action leads the menu -- it is the row's most important operation");
            Assert.AreEqual(HistoryRowMenuModel.ActionPin, items[1].Id);
            Assert.AreEqual(HistoryRowMenuModel.ActionRename, items[2].Id);
            Assert.AreEqual(HistoryRowMenuModel.ActionArchive, items[3].Id);
            Assert.IsTrue(items[4].IsSeparator, "index 4 must be the separator before Group");
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupOpen, items[5].Id);
            Assert.IsTrue(items[6].IsSeparator, "index 6 must be the separator before Delete");
            Assert.AreEqual(HistoryRowMenuModel.ActionDelete, items[7].Id);

            // Every non-separator index above must NOT itself be a separator,
            // and every separator index must carry no id/label -- the two
            // states are mutually exclusive by construction.
            int[] separatorIndexes = { 4, 6 };
            foreach (int i in separatorIndexes)
            {
                Assert.AreEqual(string.Empty, items[i].Id);
                Assert.AreEqual(string.Empty, items[i].Label);
            }
            int[] nonSeparatorIndexes = { 0, 1, 2, 3, 5, 7 };
            foreach (int i in nonSeparatorIndexes)
            {
                Assert.IsFalse(items[i].IsSeparator, "index " + i + " must not be a separator");
            }
        }

        [Test]
        public void BuildMainItems_Open_EnabledLocally_NoTooltip()
        {
            HistoryRowMenuItem open = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[0];

            Assert.AreEqual(L10n.S.HistoryActionOpen, open.Label);
            Assert.IsTrue(open.Enabled);
            Assert.AreEqual(string.Empty, open.Tooltip);
        }

        [Test]
        public void BuildMainItems_Open_DisabledWithTooltip_WhenForeign()
        {
            HistoryRowMenuItem open = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: false, deleteDisabledBecauseForeign: true)[0];

            Assert.IsFalse(open.Enabled,
                "a foreign session cannot be resumed against this project (OnRowClicked's rule)");
            Assert.AreEqual(L10n.S.HistoryOpenDisabledForeignTooltip, open.Tooltip);
        }

        [Test]
        public void BuildMainItems_ActionIds_MatchTheDesignNotesSpelling()
        {
            // The design note spells out the exact id strings HistoryView
            // dispatches on; pinning them here catches an accidental rename
            // that HistoryView's own string.Equals checks would otherwise
            // only fail at runtime.
            Assert.AreEqual("open", HistoryRowMenuModel.ActionOpen);
            Assert.AreEqual("pin", HistoryRowMenuModel.ActionPin);
            Assert.AreEqual("rename", HistoryRowMenuModel.ActionRename);
            Assert.AreEqual("archive", HistoryRowMenuModel.ActionArchive);
            Assert.AreEqual("group-open", HistoryRowMenuModel.ActionGroupOpen);
            Assert.AreEqual("delete", HistoryRowMenuModel.ActionDelete);
            Assert.AreEqual("group-back", HistoryRowMenuModel.ActionGroupBack);
            Assert.AreEqual("group-none", HistoryRowMenuModel.ActionGroupNone);
            Assert.AreEqual("group-new", HistoryRowMenuModel.ActionGroupNew);
            Assert.AreEqual("group:", HistoryRowMenuModel.GroupIdPrefix);
        }

        // -- Pin / Unpin ---------------------------------------------------------------

        [Test]
        public void BuildMainItems_NotPinned_ShowsPinLabel_Unchecked()
        {
            HistoryRowMenuItem pin = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[1];

            Assert.AreEqual(L10n.S.HistoryActionPin, pin.Label);
            Assert.IsFalse(pin.Checked);
            Assert.IsTrue(pin.Enabled);
        }

        [Test]
        public void BuildMainItems_Pinned_ShowsUnpinLabel_Checked()
        {
            HistoryRowMenuItem pin = HistoryRowMenuModel.BuildMainItems(
                pinned: true, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[1];

            Assert.AreEqual(L10n.S.HistoryActionUnpin, pin.Label);
            Assert.IsTrue(pin.Checked);
            Assert.IsTrue(pin.Enabled);
        }

        // -- Rename ----------------------------------------------------------------------

        [Test]
        public void BuildMainItems_Rename_AlwaysUnchecked_AlwaysEnabled()
        {
            HistoryRowMenuItem rename = HistoryRowMenuModel.BuildMainItems(
                pinned: true, archived: true, canDelete: false, deleteDisabledBecauseForeign: true)[2];

            Assert.AreEqual(L10n.S.HistoryActionRename, rename.Label);
            Assert.IsFalse(rename.Checked);
            Assert.IsTrue(rename.Enabled);
        }

        // -- Archive / Unarchive ---------------------------------------------------------

        [Test]
        public void BuildMainItems_NotArchived_ShowsArchiveLabel_Unchecked()
        {
            HistoryRowMenuItem archive = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[3];

            Assert.AreEqual(L10n.S.HistoryActionArchive, archive.Label);
            Assert.IsFalse(archive.Checked);
            Assert.IsTrue(archive.Enabled);
        }

        [Test]
        public void BuildMainItems_Archived_ShowsUnarchiveLabel_Checked()
        {
            HistoryRowMenuItem archive = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: true, canDelete: true, deleteDisabledBecauseForeign: false)[3];

            Assert.AreEqual(L10n.S.HistoryActionUnarchive, archive.Label);
            Assert.IsTrue(archive.Checked);
            Assert.IsTrue(archive.Enabled);
        }

        // -- Group nav row (main page) ----------------------------------------------------

        [Test]
        public void BuildMainItems_GroupOpen_UsesGroupSubmenuLabel_NeverChecked()
        {
            HistoryRowMenuItem groupOpen = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[5];

            Assert.AreEqual(L10n.S.HistoryActionGroupSubmenu, groupOpen.Label);
            Assert.IsFalse(groupOpen.Checked);
            Assert.IsTrue(groupOpen.Enabled);
        }

        // -- Delete: enabled/disabled and the right per-reason tooltip -------------------

        [Test]
        public void BuildMainItems_DeleteAllowed_IsEnabled_NoTooltip()
        {
            HistoryRowMenuItem delete = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false)[7];

            Assert.AreEqual(L10n.S.HistoryActionDelete, delete.Label);
            Assert.IsTrue(delete.Enabled);
            Assert.AreEqual(string.Empty, delete.Tooltip);
        }

        [Test]
        public void BuildMainItems_DeleteDisabledForeign_CarriesForeignTooltip()
        {
            HistoryRowMenuItem delete = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: false, deleteDisabledBecauseForeign: true)[7];

            Assert.IsFalse(delete.Enabled);
            Assert.AreEqual(L10n.S.HistoryDeleteDisabledForeignTooltip, delete.Tooltip);
        }

        [Test]
        public void BuildMainItems_DeleteDisabledLive_CarriesLiveTooltip()
        {
            HistoryRowMenuItem delete = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: false, deleteDisabledBecauseForeign: false)[7];

            Assert.IsFalse(delete.Enabled);
            Assert.AreEqual(L10n.S.HistoryDeleteDisabledLiveTooltip, delete.Tooltip);
        }

        // -- BuildGroupItems: shape and order -----------------------------------------

        private static SessionGroup Group(string id, string name, int order)
        {
            return new SessionGroup { id = id, name = name, order = order };
        }

        [Test]
        public void BuildGroupItems_NoGroups_IsBackNoneNew_InThatOrder()
        {
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(
                string.Empty, new List<SessionGroup>());

            Assert.AreEqual(3, items.Count);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupBack, items[0].Id);
            Assert.AreEqual(L10n.S.HistoryMenuGroupBack, items[0].Label);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNone, items[1].Id);
            Assert.AreEqual(L10n.S.HistoryActionGroupNone, items[1].Label);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNew, items[2].Id);
            Assert.AreEqual(L10n.S.HistoryActionGroupNew, items[2].Label);

            // No separators anywhere on the group page (only the main page has any).
            foreach (HistoryRowMenuItem item in items)
            {
                Assert.IsFalse(item.IsSeparator);
            }
        }

        [Test]
        public void BuildGroupItems_BackRow_IsAlwaysFirst_NoneRowIsAlwaysSecond()
        {
            var groups = new List<SessionGroup> { Group("g1", "Bugs", 0) };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            Assert.AreEqual(HistoryRowMenuModel.ActionGroupBack, items[0].Id);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNone, items[1].Id);
        }

        [Test]
        public void BuildGroupItems_NewGroupRow_IsAlwaysLast()
        {
            var groups = new List<SessionGroup>
            {
                Group("g1", "Bugs", 0),
                Group("g2", "Features", 1),
                Group("g3", "Research", 2),
            };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            HistoryRowMenuItem last = items[items.Count - 1];
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNew, last.Id);
        }

        [Test]
        public void BuildGroupItems_PreservesStoreOrder()
        {
            var groups = new List<SessionGroup>
            {
                Group("g-c", "Charlie", 2),
                Group("g-a", "Alpha", 0),
                Group("g-b", "Bravo", 1),
            };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            // items[0]=back, items[1]=none, then groups verbatim in the
            // order the store's own list handed them over -- BuildGroupItems
            // must not re-sort by SessionGroup.order or by name.
            Assert.AreEqual("group:g-c", items[2].Id);
            Assert.AreEqual("Charlie", items[2].Label);
            Assert.AreEqual("group:g-a", items[3].Id);
            Assert.AreEqual("Alpha", items[3].Label);
            Assert.AreEqual("group:g-b", items[4].Id);
            Assert.AreEqual("Bravo", items[4].Label);
        }

        [Test]
        public void BuildGroupItems_SkipsGroupsWithNullOrEmptyId()
        {
            var groups = new List<SessionGroup>
            {
                Group(string.Empty, "No id", 0),
                Group(null, "Null id", 1),
                Group("g-real", "Real group", 2),
            };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            // back, none, one real group, new -- the two invalid entries
            // never turn into rows, same defensive rule ShowRowMenu applied
            // before this model existed.
            Assert.AreEqual(4, items.Count);
            Assert.AreEqual("group:g-real", items[2].Id);
        }

        [Test]
        public void BuildGroupItems_SkipsNullGroupEntry()
        {
            var groups = new List<SessionGroup> { null, Group("g-real", "Real group", 0) };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            Assert.AreEqual(4, items.Count);
            Assert.AreEqual("group:g-real", items[2].Id);
        }

        [Test]
        public void BuildGroupItems_NullGroupsList_StillReturnsBackNoneNew()
        {
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, null);

            Assert.AreEqual(3, items.Count);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupBack, items[0].Id);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNone, items[1].Id);
            Assert.AreEqual(HistoryRowMenuModel.ActionGroupNew, items[2].Id);
        }

        // -- BuildGroupItems: checked state -------------------------------------------

        [Test]
        public void BuildGroupItems_EmptyCurrentGroupId_ChecksNoneOnly()
        {
            var groups = new List<SessionGroup> { Group("g1", "Bugs", 0) };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            HistoryRowMenuItem none = items[1];
            HistoryRowMenuItem group = items[2];
            Assert.IsTrue(none.Checked, "(none) must be checked when CustomGroupId is empty");
            Assert.IsFalse(group.Checked);
        }

        [Test]
        public void BuildGroupItems_MatchingCurrentGroupId_ChecksThatGroupOnly()
        {
            var groups = new List<SessionGroup>
            {
                Group("g1", "Bugs", 0),
                Group("g2", "Features", 1),
            };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems("g2", groups);

            HistoryRowMenuItem none = items[1];
            HistoryRowMenuItem g1 = items[2];
            HistoryRowMenuItem g2 = items[3];
            Assert.IsFalse(none.Checked);
            Assert.IsFalse(g1.Checked);
            Assert.IsTrue(g2.Checked);
        }

        [Test]
        public void BuildGroupItems_CurrentGroupIdNoLongerExists_ChecksNothing()
        {
            // A group the session was assigned to but that has since been
            // deleted from the store -- same outcome as the old submenu had
            // (nothing checked) rather than throwing or silently checking
            // "(none)" as a false substitute.
            var groups = new List<SessionGroup> { Group("g1", "Bugs", 0) };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems("stale-id", groups);

            foreach (HistoryRowMenuItem item in items)
            {
                Assert.IsFalse(item.Checked, item.Id + " must not be checked");
            }
        }

        [Test]
        public void BuildGroupItems_NullGroupName_BecomesEmptyLabel()
        {
            var groups = new List<SessionGroup> { Group("g1", null, 0) };
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            Assert.AreEqual(string.Empty, items[2].Label);
        }

        // -- Every catalog-backed label matches L10n.S exactly (catalog-driven) ----------

        [Test]
        public void EveryStaticLabel_EqualsItsL10nCatalogString()
        {
            List<HistoryRowMenuItem> main = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false);
            Assert.AreEqual(L10n.S.HistoryActionOpen, main[0].Label);
            Assert.AreEqual(L10n.S.HistoryActionPin, main[1].Label);
            Assert.AreEqual(L10n.S.HistoryActionRename, main[2].Label);
            Assert.AreEqual(L10n.S.HistoryActionArchive, main[3].Label);
            Assert.AreEqual(L10n.S.HistoryActionGroupSubmenu, main[5].Label);
            Assert.AreEqual(L10n.S.HistoryActionDelete, main[7].Label);

            List<HistoryRowMenuItem> groupPage = HistoryRowMenuModel.BuildGroupItems(
                string.Empty, new List<SessionGroup>());
            Assert.AreEqual(L10n.S.HistoryMenuGroupBack, groupPage[0].Label);
            Assert.AreEqual(L10n.S.HistoryActionGroupNone, groupPage[1].Label);
            Assert.AreEqual(L10n.S.HistoryActionGroupNew, groupPage[2].Label);
        }

        // -- Popover height budget (2026-08-14 review finding: the group
        // page grows with the user's own group count, so the height must
        // cap and the overflow must scroll -- an uncapped popover put the
        // bottom groups off-screen with no way to reach them) ------------

        [Test]
        public void ComputeRowMenuHeight_ManyGroups_IsCappedAtMaxHeight()
        {
            var groups = new List<SessionGroup>();
            for (int i = 0; i < 40; i++)
            {
                groups.Add(new SessionGroup { id = "g" + i, name = "Group " + i });
            }
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildGroupItems(string.Empty, groups);

            Assert.AreEqual(HistoryView.RowMenuMaxHeight, HistoryView.ComputeRowMenuHeight(items),
                "a group page taller than the cap must clamp (the ScrollView carries the overflow)");
        }

        [Test]
        public void ComputeRowMenuHeight_MainItems_MatchesLinearFormulaBelowCap()
        {
            List<HistoryRowMenuItem> items = HistoryRowMenuModel.BuildMainItems(
                pinned: false, archived: false, canDelete: true, deleteDisabledBecauseForeign: false);
            float expected = HistoryView.RowMenuChromePadding;
            for (int i = 0; i < items.Count; i++)
            {
                expected += items[i].IsSeparator
                    ? HistoryView.RowMenuSepHeight : HistoryView.RowMenuRowHeight;
            }

            Assert.Less(expected, HistoryView.RowMenuMaxHeight,
                "the main page must fit uncapped -- if this ever grows past the cap, rethink the cap");
            Assert.AreEqual(expected, HistoryView.ComputeRowMenuHeight(items));
        }

        [Test]
        public void ComputeRowMenuHeight_NullOrEmpty_ReturnsTheOneRowFloor()
        {
            float floor = HistoryView.RowMenuRowHeight + HistoryView.RowMenuChromePadding;
            Assert.AreEqual(floor, HistoryView.ComputeRowMenuHeight(null));
            Assert.AreEqual(floor, HistoryView.ComputeRowMenuHeight(new List<HistoryRowMenuItem>()));
        }
    }
}

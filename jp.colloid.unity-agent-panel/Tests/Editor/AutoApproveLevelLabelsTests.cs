using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards AutoApproveLevelLabels -- the single shared mapping the header
    /// control's menu AND the Settings dropdown (SettingsView.
    /// BuildUapOpsSection, via <c>new List&lt;UapAutoApproveLevel&gt;(
    /// AutoApproveLevelLabels.Ordered)</c> as the PopupField's choice list
    /// and <see cref="AutoApproveLevelLabels.Describe"/> as its formatter)
    /// both build from (see that class's own doc comment: "a user who reads
    /// ... in one place and something else in the other has no way to tell
    /// whether they are looking at the same setting"). SettingsView itself
    /// needs no PopupField-index bookkeeping of its own -- it stores the
    /// enum value directly, so IndexOf/At have no call site there today --
    /// but the header control (per that class's doc comment) is the other
    /// consumer, and both surfaces sharing one correct mapping is exactly
    /// the invariant this file exists to pin.
    /// </summary>
    public class AutoApproveLevelLabelsTests
    {
        // -- Ordered: exactly the five levels, ascending enum order (so
        // "higher is more permissive" reads the same in code and on
        // screen, per the class's own doc comment). AllTools was added by
        // design note 2026-09-10-auto-approve-all-tools-and-lean-auto-
        // continue and must sort last. -----------------------------------

        [Test]
        public void Ordered_ContainsExactlyTheFiveLevels_InAscendingEnumOrder()
        {
            UapAutoApproveLevel[] expected =
            {
                UapAutoApproveLevel.Ask,
                UapAutoApproveLevel.ReadOnly,
                UapAutoApproveLevel.Undoable,
                UapAutoApproveLevel.AllUnityOps,
                UapAutoApproveLevel.AllTools
            };
            CollectionAssert.AreEqual(expected, AutoApproveLevelLabels.Ordered);
        }

        // -- Describe: every level maps to its own dedicated L10n string,
        // and an unknown value falls back to the SAFEST label (Ask) rather
        // than an empty string or throwing -- matching AutoApprovePolicy's
        // own "unknown level -> safest behaviour" contract. ------------------

        [Test]
        public void Describe_EachKnownLevel_ReturnsItsOwnDedicatedLabel()
        {
            Assert.AreEqual(L10n.S.AutoApproveLevelAsk, AutoApproveLevelLabels.Describe(UapAutoApproveLevel.Ask));
            Assert.AreEqual(L10n.S.AutoApproveLevelReadOnly, AutoApproveLevelLabels.Describe(UapAutoApproveLevel.ReadOnly));
            Assert.AreEqual(L10n.S.AutoApproveLevelUndoable, AutoApproveLevelLabels.Describe(UapAutoApproveLevel.Undoable));
            Assert.AreEqual(L10n.S.AutoApproveLevelAllUnityOps, AutoApproveLevelLabels.Describe(UapAutoApproveLevel.AllUnityOps));
            Assert.AreEqual(L10n.S.AutoApproveLevelAllTools, AutoApproveLevelLabels.Describe(UapAutoApproveLevel.AllTools));
        }

        [Test]
        public void DescribeShort_EachKnownLevel_ReturnsItsOwnDedicatedLabel()
        {
            Assert.AreEqual(L10n.S.AutoApproveShortAsk, AutoApproveLevelLabels.DescribeShort(UapAutoApproveLevel.Ask));
            Assert.AreEqual(L10n.S.AutoApproveShortReadOnly, AutoApproveLevelLabels.DescribeShort(UapAutoApproveLevel.ReadOnly));
            Assert.AreEqual(L10n.S.AutoApproveShortUndoable, AutoApproveLevelLabels.DescribeShort(UapAutoApproveLevel.Undoable));
            Assert.AreEqual(L10n.S.AutoApproveShortAllUnityOps, AutoApproveLevelLabels.DescribeShort(UapAutoApproveLevel.AllUnityOps));
            Assert.AreEqual(L10n.S.AutoApproveShortAllTools, AutoApproveLevelLabels.DescribeShort(UapAutoApproveLevel.AllTools));
        }

        [Test]
        public void Describe_UnknownEnumValue_FallsBackToTheAskLabel()
        {
            var bogusLevel = (UapAutoApproveLevel)999;
            Assert.AreEqual(L10n.S.AutoApproveLevelAsk, AutoApproveLevelLabels.Describe(bogusLevel));
        }

        [Test]
        public void Describe_NoTwoLevels_ShareTheSameLabel()
        {
            // A PopupField whose choices resolve to duplicate display
            // strings makes two distinct settings look identical in the UI
            // -- exactly the confusion this whole class exists to prevent.
            for (int i = 0; i < AutoApproveLevelLabels.Ordered.Length; i++)
            {
                for (int j = i + 1; j < AutoApproveLevelLabels.Ordered.Length; j++)
                {
                    string labelI = AutoApproveLevelLabels.Describe(AutoApproveLevelLabels.Ordered[i]);
                    string labelJ = AutoApproveLevelLabels.Describe(AutoApproveLevelLabels.Ordered[j]);
                    Assert.AreNotEqual(labelI, labelJ,
                        "Ordered[" + i + "] and Ordered[" + j + "] must not share a label.");
                }
            }
        }

        // -- DescribeAll: same strings as Describe, in Ordered's order --
        // this is literally the PopupField choice-label list both surfaces
        // would render if they used the string-list form instead of the
        // enum-typed PopupField SettingsView actually uses. ------------------

        [Test]
        public void DescribeAll_MatchesDescribe_ForEveryEntry_InOrderedSequence()
        {
            var names = AutoApproveLevelLabels.DescribeAll();
            Assert.AreEqual(AutoApproveLevelLabels.Ordered.Length, names.Count);
            for (int i = 0; i < AutoApproveLevelLabels.Ordered.Length; i++)
            {
                Assert.AreEqual(AutoApproveLevelLabels.Describe(AutoApproveLevelLabels.Ordered[i]), names[i]);
            }
        }

        // -- IndexOf / At: inverse of each other for every level in
        // Ordered -- the round-trip an index-based dropdown (the header
        // control) depends on to select the right entry and to read the
        // right level back. -----------------------------------------------

        [Test]
        public void IndexOf_And_At_RoundTrip_ForEveryOrderedLevel()
        {
            for (int i = 0; i < AutoApproveLevelLabels.Ordered.Length; i++)
            {
                UapAutoApproveLevel level = AutoApproveLevelLabels.Ordered[i];
                Assert.AreEqual(i, AutoApproveLevelLabels.IndexOf(level));
                Assert.AreEqual(level, AutoApproveLevelLabels.At(i));
            }
        }

        [Test]
        public void IndexOf_UnknownEnumValue_ReturnsZero_TheSafestIndex()
        {
            var bogusLevel = (UapAutoApproveLevel)999;
            Assert.AreEqual(0, AutoApproveLevelLabels.IndexOf(bogusLevel));
        }

        [Test]
        public void At_OutOfRangeIndex_FallsBackToAsk()
        {
            Assert.AreEqual(UapAutoApproveLevel.Ask, AutoApproveLevelLabels.At(-1));
            Assert.AreEqual(UapAutoApproveLevel.Ask, AutoApproveLevelLabels.At(AutoApproveLevelLabels.Ordered.Length));
            Assert.AreEqual(UapAutoApproveLevel.Ask, AutoApproveLevelLabels.At(999));
        }

        // -- SettingsView's actual dropdown-choice construction: a fresh
        // copy of Ordered, so mutating the PopupField's choices list (UI
        // Toolkit owns that list once handed to the constructor) can never
        // reach back into the shared static array both surfaces read. -------

        [Test]
        public void SettingsDropdownChoiceList_IsAFreshCopyOfOrdered_NotTheSameArrayInstance()
        {
            var choices = new System.Collections.Generic.List<UapAutoApproveLevel>(AutoApproveLevelLabels.Ordered);
            CollectionAssert.AreEqual(AutoApproveLevelLabels.Ordered, choices);
            choices.Add(UapAutoApproveLevel.Ask);
            Assert.AreEqual(5, AutoApproveLevelLabels.Ordered.Length,
                "Mutating the PopupField's own choice list must never resize the shared Ordered array.");
        }

        // -- UXA-3: RequiresConfirmation (the pure half of the escalation
        // gate both write surfaces share; the DisplayDialog half has no
        // test seam, same as the package's two existing dialogs). Design
        // note 2026-09-10-auto-approve-all-tools-and-lean-auto-continue
        // section 1: AllTools joins AllUnityOps as a confirmed escalation
        // target -- "any move UP into AllUnityOps or AllTools confirms".
        // ---------------------------------------------------------------

        [Test]
        public void RequiresConfirmation_EscalatingIntoAllUnityOps_FromEveryLowerLevel()
        {
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.Ask, UapAutoApproveLevel.AllUnityOps));
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.ReadOnly, UapAutoApproveLevel.AllUnityOps));
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.Undoable, UapAutoApproveLevel.AllUnityOps));
        }

        [Test]
        public void RequiresConfirmation_EscalatingIntoAllTools_FromEveryLowerLevel()
        {
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.Ask, UapAutoApproveLevel.AllTools));
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.ReadOnly, UapAutoApproveLevel.AllTools));
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.Undoable, UapAutoApproveLevel.AllTools));
            Assert.IsTrue(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.AllUnityOps, UapAutoApproveLevel.AllTools));
        }

        [Test]
        public void RequiresConfirmation_NarrowingFromAllToolsToAllUnityOps_IsFrictionless()
        {
            // A move DOWN (AllTools -> AllUnityOps) narrows what is
            // auto-approved -- the dialog exists to stop accidental
            // escalation, not to slow down retreat to safety.
            Assert.IsFalse(AutoApproveLevelLabels.RequiresConfirmation(
                UapAutoApproveLevel.AllTools, UapAutoApproveLevel.AllUnityOps));
        }

        [Test]
        public void RequiresConfirmation_EveryOtherTransition_IsFrictionless()
        {
            // Downward moves away from AllUnityOps/AllTools must never be
            // slowed -- the dialog stops accidental escalation, not
            // retreat to safety.
            foreach (UapAutoApproveLevel from in AutoApproveLevelLabels.Ordered)
            {
                foreach (UapAutoApproveLevel to in AutoApproveLevelLabels.Ordered)
                {
                    bool isConfirmedEscalation =
                        (to == UapAutoApproveLevel.AllUnityOps || to == UapAutoApproveLevel.AllTools)
                        && (int)to > (int)from;
                    if (isConfirmedEscalation)
                    {
                        continue;
                    }
                    Assert.IsFalse(AutoApproveLevelLabels.RequiresConfirmation(from, to),
                        from + " -> " + to + " must not require confirmation");
                }
            }
        }

        [Test]
        public void RequiresConfirmation_SameLevel_IsFrictionless()
        {
            // Both call sites early-return on same-value anyway, but the
            // predicate's own contract must not depend on that.
            foreach (UapAutoApproveLevel level in AutoApproveLevelLabels.Ordered)
            {
                Assert.IsFalse(AutoApproveLevelLabels.RequiresConfirmation(level, level),
                    level + " -> " + level + " (same level) must not require confirmation");
            }
        }
    }
}

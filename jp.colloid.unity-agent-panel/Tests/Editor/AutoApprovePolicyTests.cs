using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards AutoApprovePolicy.ShouldAutoApprove -- the pure, tested core
    /// behind the panel-side auto-approve feature (UapAutoApproveLevel's
    /// doc comment records the CLI --permission-mode measurements that
    /// rule out doing this in the CLI instead). Every rule in the method's
    /// own doc comment gets its own case here, plus the full non-UapOps
    /// matrix and the unknown-enum fallback.
    /// </summary>
    public class AutoApprovePolicyTests
    {
        // -- The load-bearing rule: isUapOpsTool == false is ALWAYS false,
        // at every level BELOW AllTools, for every readOnly/undoable
        // combination. Loops the full 4 (levels) x 2 (readOnly) x 2
        // (undoable) matrix rather than picking a few representative
        // cases, since this is the one rule the task explicitly calls out
        // as load-bearing (Bash/Write/Edit must never be auto-approved
        // through this path). AllTools is DELIBERATELY excluded from this
        // loop: design note 2026-09-10-auto-approve-all-tools-and-lean-
        // auto-continue section 1 makes AllTools the one level where the
        // "isUapOpsTool false always means false" rule no longer holds --
        // see AllTools_ApprovesNonUapOpsTool_RegardlessOfReadOnlyOrUndoable
        // below for AllTools's own (opposite) contract. ---------------------

        [Test]
        public void NonUapOpsTool_IsNeverAutoApproved_AtAnyLevel_ForAnyReadOnlyUndoableCombination()
        {
            UapAutoApproveLevel[] levels =
            {
                UapAutoApproveLevel.Ask,
                UapAutoApproveLevel.ReadOnly,
                UapAutoApproveLevel.Undoable,
                UapAutoApproveLevel.AllUnityOps
                // AllTools intentionally omitted -- see comment above.
            };
            bool[] boolValues = { false, true };

            for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
            {
                for (int readOnlyIndex = 0; readOnlyIndex < boolValues.Length; readOnlyIndex++)
                {
                    for (int undoableIndex = 0; undoableIndex < boolValues.Length; undoableIndex++)
                    {
                        UapAutoApproveLevel level = levels[levelIndex];
                        bool readOnly = boolValues[readOnlyIndex];
                        bool undoable = boolValues[undoableIndex];

                        bool result = AutoApprovePolicy.ShouldAutoApprove(level, false, readOnly, undoable);

                        Assert.IsFalse(result,
                            "level=" + level + " readOnly=" + readOnly + " undoable=" + undoable
                            + " must never auto-approve a non-UapOps tool (Bash/Write/Edit/etc.)"
                            + " -- the script-validation gate and the uloop escape-hatch rejection"
                            + " both depend on this staying false.");
                    }
                }
            }
        }

        // -- Ask: always false, regardless of tool metadata. -----------------

        [Test]
        public void Ask_NeverAutoApproves_EvenAUapOpsReadOnlyUndoableTool()
        {
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Ask, true, false, false));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Ask, true, true, false));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Ask, true, false, true));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Ask, true, true, true));
        }

        // -- ReadOnly: true only when readOnly, regardless of undoable. ------

        [Test]
        public void ReadOnly_ApprovesOnlyReadOnlyUapOpsTools()
        {
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.ReadOnly, true, true, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.ReadOnly, true, true, true));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.ReadOnly, true, false, false));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.ReadOnly, true, false, true));
        }

        // -- Undoable: true when readOnly OR undoable. -----------------------

        [Test]
        public void Undoable_ApprovesReadOnlyOrUndoableUapOpsTools()
        {
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Undoable, true, true, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Undoable, true, false, true));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Undoable, true, true, true));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.Undoable, true, false, false));
        }

        // -- AllUnityOps: true for any UapOps tool, regardless of metadata. --

        [Test]
        public void AllUnityOps_ApprovesAnyUapOpsTool_RegardlessOfReadOnlyOrUndoable()
        {
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.AllUnityOps, true, false, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.AllUnityOps, true, true, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.AllUnityOps, true, false, true));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(UapAutoApproveLevel.AllUnityOps, true, true, true));
        }

        // -- Unknown/out-of-range enum value: must fall back to the SAFEST
        // behaviour (false), never to permissive, even though a naive
        // "unhandled switch falls through to the most-permissive case"
        // implementation could plausibly land on AllUnityOps's true. -------

        [Test]
        public void UnknownEnumValue_FallsBackToFalse_ForUapOpsTool_EvenWhenReadOnlyAndUndoable()
        {
            var bogusLevel = (UapAutoApproveLevel)999;

            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(bogusLevel, true, false, false));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(bogusLevel, true, true, false));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(bogusLevel, true, false, true));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(bogusLevel, true, true, true));
        }

        [Test]
        public void UnknownEnumValue_FallsBackToFalse_ForNonUapOpsTool()
        {
            var bogusLevel = (UapAutoApproveLevel)999;

            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(bogusLevel, false, true, true));
        }

        // -- AllTools (design note 2026-09-10-auto-approve-all-tools-and-
        // lean-auto-continue section 1): the one level where a non-UapOps
        // tool CAN be auto-approved -- approves everything except a
        // requires_user_interaction request, regardless of isUapOpsTool/
        // readOnly/undoable. ---------------------------------------------

        [Test]
        public void AllTools_ApprovesNonUapOpsTool_RegardlessOfReadOnlyOrUndoable()
        {
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, false, false, false, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, false, true, false, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, false, false, true, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, false, true, true, false));
        }

        [Test]
        public void AllTools_ApprovesUapOpsTool_RegardlessOfReadOnlyOrUndoable()
        {
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, true, false, false, false));
            Assert.IsTrue(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, true, true, true, false));
        }

        [Test]
        public void AllTools_WithRequiresUserInteraction_IsFalse()
        {
            // AskUserQuestion is a question for the human -- never
            // auto-answered at any level, including the most permissive.
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, false, false, false, true));
            Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(
                UapAutoApproveLevel.AllTools, true, true, true, true));
        }

        [Test]
        public void EveryLevel_WithRequiresUserInteraction_IsFalse()
        {
            UapAutoApproveLevel[] levels =
            {
                UapAutoApproveLevel.Ask,
                UapAutoApproveLevel.ReadOnly,
                UapAutoApproveLevel.Undoable,
                UapAutoApproveLevel.AllUnityOps,
                UapAutoApproveLevel.AllTools
            };
            foreach (UapAutoApproveLevel level in levels)
            {
                Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(level, true, true, true, true),
                    level + " must never auto-approve a requires_user_interaction request.");
                Assert.IsFalse(AutoApprovePolicy.ShouldAutoApprove(level, false, true, true, true),
                    level + " must never auto-approve a requires_user_interaction request.");
            }
        }

        // -- The 4-arg overload is exactly the 5-arg overload with
        // requiresUserInteraction: false. ------------------------------------

        [Test]
        public void FourArgOverload_EqualsFiveArgOverload_WithRequiresUserInteractionFalse()
        {
            UapAutoApproveLevel[] levels =
            {
                UapAutoApproveLevel.Ask,
                UapAutoApproveLevel.ReadOnly,
                UapAutoApproveLevel.Undoable,
                UapAutoApproveLevel.AllUnityOps,
                UapAutoApproveLevel.AllTools,
                (UapAutoApproveLevel)999
            };
            bool[] boolValues = { false, true };

            foreach (UapAutoApproveLevel level in levels)
            {
                foreach (bool isUapOpsTool in boolValues)
                {
                    foreach (bool readOnly in boolValues)
                    {
                        foreach (bool undoable in boolValues)
                        {
                            bool viaFourArg = AutoApprovePolicy.ShouldAutoApprove(
                                level, isUapOpsTool, readOnly, undoable);
                            bool viaFiveArg = AutoApprovePolicy.ShouldAutoApprove(
                                level, isUapOpsTool, readOnly, undoable, false);
                            Assert.AreEqual(viaFiveArg, viaFourArg,
                                "level=" + level + " isUapOpsTool=" + isUapOpsTool
                                + " readOnly=" + readOnly + " undoable=" + undoable);
                        }
                    }
                }
            }
        }
    }
}

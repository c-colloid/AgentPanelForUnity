using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-model coverage for the AskUserQuestion stepper (design note
    /// 2026-08-05-askuserquestion-stepper.md section 3): the auto-advance
    /// must only ever target UNANSWERED questions (wrapping once, staying
    /// put when none remain), multiSelect indices must never advance, GoTo
    /// must clamp, and AllAnswered must gate exactly on "every question
    /// has a selection". These rules run with no Unity API, so they are
    /// tested here directly rather than through a constructed card.
    /// </summary>
    [TestFixture]
    public class QuestionStepperModelTests
    {
        private static QuestionStepperModel AllSingle(int count)
        {
            return new QuestionStepperModel(count, new bool[count]);
        }

        // -- Construction ---------------------------------------------------------

        [Test]
        public void Ctor_StartsAtIndexZero_NothingAnswered()
        {
            QuestionStepperModel model = AllSingle(3);
            Assert.AreEqual(3, model.Count);
            Assert.AreEqual(0, model.CurrentIndex);
            Assert.IsFalse(model.AllAnswered);
            Assert.IsFalse(model.IsAnswered(0));
            Assert.IsFalse(model.IsAnswered(1));
            Assert.IsFalse(model.IsAnswered(2));
        }

        [Test]
        public void Ctor_NullMultiSelectArray_TreatsEveryQuestionAsSingleSelect()
        {
            var model = new QuestionStepperModel(2, null);
            Assert.IsFalse(model.IsMultiSelect(0));
            Assert.IsFalse(model.IsMultiSelect(1));
            // And the advance path treats them as single-select:
            model.NotifyAnswered(0, true);
            model.AdvanceAfterSingleSelect(0);
            Assert.AreEqual(1, model.CurrentIndex);
        }

        [Test]
        public void Ctor_ShortMultiSelectArray_MissingEntriesDefaultToSingleSelect()
        {
            var model = new QuestionStepperModel(3, new bool[] { true });
            Assert.IsTrue(model.IsMultiSelect(0));
            Assert.IsFalse(model.IsMultiSelect(1));
            Assert.IsFalse(model.IsMultiSelect(2));
        }

        [Test]
        public void Ctor_NegativeCount_ClampsToEmpty()
        {
            var model = new QuestionStepperModel(-1, null);
            Assert.AreEqual(0, model.Count);
            Assert.AreEqual(0, model.CurrentIndex);
        }

        // -- GoTo -----------------------------------------------------------------

        [Test]
        public void GoTo_InRange_Moves()
        {
            QuestionStepperModel model = AllSingle(3);
            model.GoTo(2);
            Assert.AreEqual(2, model.CurrentIndex);
        }

        [Test]
        public void GoTo_BelowZero_ClampsToZero()
        {
            QuestionStepperModel model = AllSingle(3);
            model.GoTo(1);
            model.GoTo(-5);
            Assert.AreEqual(0, model.CurrentIndex);
        }

        [Test]
        public void GoTo_PastEnd_ClampsToLast()
        {
            QuestionStepperModel model = AllSingle(3);
            model.GoTo(99);
            Assert.AreEqual(2, model.CurrentIndex);
        }

        // -- NotifyAnswered / AllAnswered ------------------------------------------

        [Test]
        public void AllAnswered_TrueOnlyWhenEveryQuestionHasASelection()
        {
            QuestionStepperModel model = AllSingle(3);
            model.NotifyAnswered(0, true);
            model.NotifyAnswered(2, true);
            Assert.IsFalse(model.AllAnswered, "question 1 is still unanswered");
            model.NotifyAnswered(1, true);
            Assert.IsTrue(model.AllAnswered);
        }

        [Test]
        public void NotifyAnswered_False_UnanswersAQuestion()
        {
            // The multiSelect toggle-off-the-last-option case: Submit must
            // go back to disabled.
            QuestionStepperModel model = AllSingle(2);
            model.NotifyAnswered(0, true);
            model.NotifyAnswered(1, true);
            Assert.IsTrue(model.AllAnswered);
            model.NotifyAnswered(1, false);
            Assert.IsFalse(model.AllAnswered);
            Assert.IsFalse(model.IsAnswered(1));
        }

        [Test]
        public void NotifyAnswered_OutOfRange_IsIgnored()
        {
            QuestionStepperModel model = AllSingle(2);
            model.NotifyAnswered(-1, true);
            model.NotifyAnswered(2, true);
            Assert.IsFalse(model.IsAnswered(-1));
            Assert.IsFalse(model.IsAnswered(2));
            Assert.IsFalse(model.AllAnswered);
        }

        // -- AdvanceAfterSingleSelect ------------------------------------------------

        [Test]
        public void Advance_MovesToTheNextUnansweredQuestion()
        {
            QuestionStepperModel model = AllSingle(3);
            model.NotifyAnswered(0, true);
            model.AdvanceAfterSingleSelect(0);
            Assert.AreEqual(1, model.CurrentIndex);
        }

        [Test]
        public void Advance_SkipsAlreadyAnsweredQuestions()
        {
            QuestionStepperModel model = AllSingle(4);
            model.NotifyAnswered(1, true); // answered out of order earlier
            model.NotifyAnswered(0, true);
            model.AdvanceAfterSingleSelect(0);
            Assert.AreEqual(2, model.CurrentIndex, "index 1 is answered and must be skipped");
        }

        [Test]
        public void Advance_WrapsAroundOnce_ToAnEarlierUnansweredQuestion()
        {
            QuestionStepperModel model = AllSingle(3);
            model.GoTo(2);
            model.NotifyAnswered(2, true);
            model.AdvanceAfterSingleSelect(2);
            Assert.AreEqual(0, model.CurrentIndex, "search wraps past the end to index 0");
        }

        [Test]
        public void Advance_WrapSkipsAnswered_LandsOnTheOnlyRemainingQuestion()
        {
            QuestionStepperModel model = AllSingle(3);
            model.NotifyAnswered(0, true);
            model.GoTo(2);
            model.NotifyAnswered(2, true);
            model.AdvanceAfterSingleSelect(2);
            Assert.AreEqual(1, model.CurrentIndex, "0 is answered; the wrap must skip it");
        }

        [Test]
        public void Advance_AllAnswered_StaysPut()
        {
            QuestionStepperModel model = AllSingle(3);
            model.NotifyAnswered(0, true);
            model.NotifyAnswered(1, true);
            model.GoTo(2);
            model.NotifyAnswered(2, true);
            model.AdvanceAfterSingleSelect(2);
            Assert.AreEqual(2, model.CurrentIndex,
                "with nothing left to answer the user is one Submit away; jumping is noise");
        }

        [Test]
        public void Advance_ReansweringASingleSelect_StillTargetsOnlyUnanswered()
        {
            // Changing an earlier answer re-triggers the advance; it must
            // move to the remaining unanswered question, not to answered
            // neighbors.
            QuestionStepperModel model = AllSingle(3);
            model.NotifyAnswered(0, true);
            model.AdvanceAfterSingleSelect(0); // -> 1
            model.NotifyAnswered(1, true);
            model.AdvanceAfterSingleSelect(1); // -> 2
            model.GoTo(0);
            model.NotifyAnswered(0, true); // revised answer
            model.AdvanceAfterSingleSelect(0);
            Assert.AreEqual(2, model.CurrentIndex, "1 is answered; 2 is the only open one");
        }

        [Test]
        public void Advance_MultiSelectIndex_IsANoOp()
        {
            // The UI never calls this for multiSelect questions (a toggle
            // set has no completion signal), but the model refuses anyway
            // so a future call site cannot reintroduce the mid-selection
            // yank -- see QuestionStepperModel's class comment.
            var model = new QuestionStepperModel(3, new bool[] { false, true, false });
            model.GoTo(1);
            model.NotifyAnswered(1, true);
            model.AdvanceAfterSingleSelect(1);
            Assert.AreEqual(1, model.CurrentIndex);
        }

        [Test]
        public void Advance_OutOfRangeIndex_IsANoOp()
        {
            QuestionStepperModel model = AllSingle(2);
            model.GoTo(1);
            model.AdvanceAfterSingleSelect(-1);
            model.AdvanceAfterSingleSelect(2);
            Assert.AreEqual(1, model.CurrentIndex);
        }

        // -- Empty model (defensive; the card never builds the variant with 0) -----

        [Test]
        public void EmptyModel_IsInertAndVacuouslyAnswered()
        {
            var model = new QuestionStepperModel(0, null);
            model.GoTo(5);
            model.AdvanceAfterSingleSelect(0);
            model.NotifyAnswered(0, true);
            Assert.AreEqual(0, model.Count);
            Assert.AreEqual(0, model.CurrentIndex);
            Assert.IsTrue(model.AllAnswered, "vacuously true -- documented on the property");
        }
    }
}

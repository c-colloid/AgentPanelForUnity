using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure step state for the AskUserQuestion stepper (design note
    /// 2026-08-05-askuserquestion-stepper.md). No Unity API on purpose so
    /// every rule below is EditMode-testable without building a card.
    ///
    /// Why a stepper exists at all (measured, same note section 1): a
    /// real-shaped 4-question request built into the 420x360
    /// PermissionWindow produced a 736px-tall card root inside a 360px
    /// window -- twice the window height -- while the internal ScrollView
    /// sat at viewport 707px vs content 699px and therefore never scrolled
    /// once. Each question section measures roughly 175px, so stacking all
    /// four costs 699px; showing ONE question at a time cuts the required
    /// height to about a quarter and is what makes the card fit either
    /// host again. This class owns which question that one is.
    ///
    /// Design decisions carried by this model:
    ///
    /// - Single-select answers AUTO-ADVANCE to the next unanswered
    ///   question (Claude Desktop behavior): a radio-style pick is
    ///   complete the instant it happens, so moving on saves one click per
    ///   question with no information loss.
    ///
    /// - multiSelect questions NEVER auto-advance. They are toggle sets:
    ///   after any individual toggle the user may intend to keep toggling,
    ///   and neither the UI nor this model can know when the set is
    ///   "done" -- there is no completion signal short of Submit itself.
    ///   Auto-advancing on the first toggle would yank the user away
    ///   mid-selection. <see cref="AdvanceAfterSingleSelect"/> is
    ///   additionally a no-op safeguard when pointed at a multiSelect
    ///   index, so no future caller can reintroduce the yank by accident.
    ///
    /// - Navigation is FREE (every tab clickable at any time) rather than
    ///   gated ("answer before you may proceed"). Submit already gates on
    ///   all questions being answered, so gating navigation would protect
    ///   nothing while preventing the user from reading ahead or revising
    ///   an earlier answer -- both of which Claude Desktop allows.
    /// </summary>
    public sealed class QuestionStepperModel
    {
        private readonly bool[] _multiSelect;
        private readonly bool[] _answered;
        private readonly int _count;
        private int _currentIndex;

        /// <summary>
        /// Creates the model for <paramref name="count"/> questions.
        /// <paramref name="multiSelect"/> flags parallel the question
        /// list; null (or a short array) treats the missing entries as
        /// single-select -- optional-first, matching how
        /// AskUserQuestionInput defaults an absent multiSelect field to
        /// false.
        /// </summary>
        public QuestionStepperModel(int count, bool[] multiSelect)
        {
            _count = count > 0 ? count : 0;
            _multiSelect = new bool[_count];
            _answered = new bool[_count];
            if (multiSelect != null)
            {
                int copy = Math.Min(_count, multiSelect.Length);
                for (int i = 0; i < copy; i++)
                {
                    _multiSelect[i] = multiSelect[i];
                }
            }
            _currentIndex = 0;
        }

        /// <summary>Number of questions this model tracks.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>Index of the question currently shown (0 when empty).</summary>
        public int CurrentIndex
        {
            get { return _currentIndex; }
        }

        /// <summary>
        /// True when every question has at least one selection. Vacuously
        /// true for an empty model -- PermissionCard never builds the
        /// question variant with zero questions (it falls back to the tool
        /// variant), so the empty case only exists for defensive callers.
        /// </summary>
        public bool AllAnswered
        {
            get
            {
                for (int i = 0; i < _count; i++)
                {
                    if (!_answered[i])
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>Answered state of one question (false out of range).</summary>
        public bool IsAnswered(int index)
        {
            return index >= 0 && index < _count && _answered[index];
        }

        /// <summary>multiSelect flag of one question (false out of range).</summary>
        public bool IsMultiSelect(int index)
        {
            return index >= 0 && index < _count && _multiSelect[index];
        }

        /// <summary>
        /// Free navigation (tab click): clamps into [0, Count-1] rather
        /// than rejecting, so a stale index from a caller can never leave
        /// the model pointing outside the question list.
        /// </summary>
        public void GoTo(int index)
        {
            if (_count == 0)
            {
                _currentIndex = 0;
                return;
            }
            if (index < 0)
            {
                index = 0;
            }
            else if (index > _count - 1)
            {
                index = _count - 1;
            }
            _currentIndex = index;
        }

        /// <summary>
        /// Mirrors the card's per-question selection state into the model:
        /// answered means "has at least one selection". hasSelection false
        /// covers the multiSelect case of toggling the last selected
        /// option back OFF, which legitimately un-answers the question
        /// (and must re-disable Submit). Out-of-range indices are ignored.
        /// </summary>
        public void NotifyAnswered(int index, bool hasSelection)
        {
            if (index < 0 || index >= _count)
            {
                return;
            }
            _answered[index] = hasSelection;
        }

        /// <summary>
        /// Claude Desktop auto-advance after a SINGLE-select answer: moves
        /// CurrentIndex to the next UNANSWERED question, searching forward
        /// from <paramref name="answeredIndex"/> + 1 and wrapping around
        /// the list exactly once, skipping every already-answered
        /// question. Stays put when no unanswered question remains (the
        /// user is then one Submit click away; jumping anywhere would be
        /// noise). Callers must call
        /// <see cref="NotifyAnswered"/> for the answered question BEFORE
        /// this, or the just-answered question still counts as unanswered
        /// and the search can land back on it.
        ///
        /// NEVER called for multiSelect questions by the UI (toggles never
        /// navigate -- see the class comment for why the UI cannot know
        /// when a multiSelect is done). Kept as an explicit no-op
        /// safeguard here anyway: if a future caller wires it up for a
        /// multiSelect index, the model refuses to yank the user away
        /// rather than relying on every call site remembering the rule.
        /// </summary>
        public void AdvanceAfterSingleSelect(int answeredIndex)
        {
            if (answeredIndex < 0 || answeredIndex >= _count)
            {
                return;
            }
            if (_multiSelect[answeredIndex])
            {
                return;
            }
            for (int step = 1; step <= _count; step++)
            {
                int candidate = (answeredIndex + step) % _count;
                if (!_answered[candidate])
                {
                    _currentIndex = candidate;
                    return;
                }
            }
            // Every question answered: stay put.
        }
    }
}

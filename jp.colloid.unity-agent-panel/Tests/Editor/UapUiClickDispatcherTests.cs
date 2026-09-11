using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapUiClickDispatcher, exercised against standalone (unattached)
    /// elements.
    ///
    /// The interesting history: the first version of this fixture asserted
    /// that clicking an unattached Button does NOT fire its callback, and
    /// documented that as an unavoidable gap ("UI Toolkit only delivers
    /// events through a real panel"). That was true of the old
    /// synthetic-event implementation -- and it also meant the fixture was
    /// pinning the very behaviour that turned out to be the bug: measured
    /// in a live editor, the shipped tool reported success while the
    /// button's callback never ran, on a properly laid-out button in a real
    /// window. Nine dispatch strategies were then measured; only invoking
    /// the Clickable manipulator works (see UapUiClickDispatcher's doc
    /// comment for the table).
    ///
    /// Because the manipulator is invoked directly rather than routed
    /// through the panel's input system, the click now DOES work without a
    /// panel -- which is what makes these tests possible at all. They are
    /// still not a substitute for the live check (a real window is the only
    /// place the resolver, the guards and the dispatch run together), but
    /// the one step that used to be unprovable here no longer is.
    /// </summary>
    [TestFixture]
    public class UapUiClickDispatcherTests
    {
        [Test]
        public void Click_Button_InvokesClicked_AndReportsObserved()
        {
            int clicks = 0;
            var button = new Button(delegate { clicks++; });

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(button);

            Assert.AreEqual(1, clicks, "the button's callback must actually run");
            Assert.IsTrue(outcome.Observed, "and the outcome must report that it was observed");
        }

        [Test]
        public void Click_ElementWithNoClickable_ReportsNotObserved_WithAReason()
        {
            var plain = new VisualElement();

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(plain);

            Assert.IsFalse(outcome.Observed);
            StringAssert.Contains("no Clickable", outcome.Detail);
        }

        [Test]
        public void Click_Label_ReportsNotObserved_RatherThanPretending()
        {
            // A Label has no click behaviour. The tool must say so instead of
            // returning a success the caller would then screenshot as proof.
            var label = new Label("static");

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(label);

            Assert.IsFalse(outcome.Observed);
            Assert.IsNotEmpty(outcome.Detail);
        }

        [Test]
        public void Click_ButtonWithNoCallback_ReportsNotObserved()
        {
            // The hole in the FIRST version of the observation check, found
            // by probing a live editor on 2026-08-04. That version did
            // `clickable.clicked += probe` and reported success when the
            // probe fired -- so on a Button with nothing wired to it, the
            // probe was the only subscriber, fired every time, and an inert
            // control reported a successful click. Exactly the defect class
            // the observation check was added to close, reintroduced by the
            // check itself.
            var inert = new Button();

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(inert);

            Assert.IsFalse(outcome.Observed, "nothing is wired to this button, so nothing was observed");
            StringAssert.Contains("nothing is wired", outcome.Detail);
        }

        [Test]
        public void Click_Toggle_IsRefused_AndNamesSetValue()
        {
            // MEASURED 2026-08-04 in a live editor: dispatching a ClickEvent
            // at a Toggle runs its handler (which lives on Clickable's
            // clickedWithEventInfo channel) and leaves the value exactly
            // where it was, because BaseBoolField.OnClickEvent filters on the
            // event type. Reporting anything but a refusal here would be
            // reporting a no-op.
            var toggle = new Toggle("label");
            bool before = toggle.value;

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(toggle);

            Assert.IsFalse(outcome.Observed);
            Assert.AreEqual(before, toggle.value, "and the refusal must not have changed anything");
            StringAssert.Contains("uap_editor_ui_set_value", outcome.Detail);
        }

        [Test]
        public void Click_Button_RestoresItsHandlersAfterwards()
        {
            // The observation check wraps the pre-existing delegates and must
            // put them back, including after the handler throws -- otherwise
            // one click through this tool would permanently replace the
            // control's callback with the wrapper.
            int clicks = 0;
            var button = new Button(delegate { clicks++; });
            System.Reflection.FieldInfo clickedField = typeof(Clickable).GetField("clicked",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            var before = (System.Delegate)clickedField.GetValue(button.clickable);

            UapUiClickDispatcher.Click(button);
            UapUiClickDispatcher.Click(button);

            var after = (System.Delegate)clickedField.GetValue(button.clickable);
            Assert.AreEqual(2, clicks, "each click runs the original callback exactly once");
            Assert.AreSame(before, after, "the wrapper must be removed, not left stacked on the control");
        }

        [Test]
        public void Click_Null_ReportsNotObserved_WithoutThrowing()
        {
            UapUiClickDispatcher.ClickOutcome outcome = null;
            Assert.DoesNotThrow(delegate { outcome = UapUiClickDispatcher.Click(null); });
            Assert.IsFalse(outcome.Observed);
            Assert.IsNotEmpty(outcome.Detail);
        }

        [Test]
        public void Click_ButtonWhoseHandlerThrows_ReportsNotObserved_AndCarriesTheReason()
        {
            var button = new Button(delegate { throw new System.InvalidOperationException("boom"); });

            UapUiClickDispatcher.ClickOutcome outcome = UapUiClickDispatcher.Click(button);

            Assert.IsFalse(outcome.Observed, "a handler that threw did not complete the click");
            StringAssert.Contains("boom", outcome.Detail);
        }
    }
}

using System;
using System.Reflection;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Clicks a UI Toolkit element by invoking its Clickable manipulator,
    /// and reports whether the click was actually OBSERVED to take effect.
    ///
    /// MEASURED 2026-08-03 against a real laid-out Button inside a live
    /// EditorWindow (worldBound 80x20, so every guard upstream passed).
    /// The first version of this class dispatched a synthetic
    /// MouseDownEvent + MouseUpEvent pair -- reasoning, correctly as far as
    /// it went, that Clickable subscribes to those events in Unity's source.
    /// It returned success and the button's callback never ran. Nine
    /// dispatch strategies were then measured, each against its own probe
    /// button whose callback incremented a counter:
    ///
    ///   MouseDown+MouseUp, positional ................. NOT fired
    ///   MouseDown+MouseUp from an IMGUI Event ......... NOT fired
    ///   ...same, sent via panel.visualTree ............ NOT fired
    ///   PointerDown+PointerUp, parameterless .......... NOT fired
    ///   PointerDown+PointerUp from an IMGUI Event ..... NOT fired
    ///   ClickEvent alone .............................. NOT fired
    ///   PointerDown+PointerUp then ClickEvent ......... NOT fired
    ///   Clickable.Invoke(ClickEvent) .................. FIRED
    ///
    /// (The pointer routes were re-measured with real world-centre
    /// positions after the first round left position at (0,0), since
    /// Clickable tests containment -- they still did not fire.) So:
    /// **synthetic event dispatch does not drive Clickable in 2022.3.**
    /// Do not reintroduce it. Sending events works when the panel's own
    /// input system originates them; a tool cannot reproduce enough of that
    /// pointer state from outside.
    ///
    /// The second half of this class matters more than the first, and is
    /// the reason the defect above was possible at all: the tool used to
    /// report "clicked: true" unconditionally. It now SUBSCRIBES to the
    /// manipulator's clicked event around the invocation and reports what
    /// actually happened. That check costs nothing, survives Unity changing
    /// the internals underneath it, and turns a future regression into a
    /// visible "dispatched but not observed" instead of a lie -- the same
    /// lesson uap_material_set learned the same day.
    /// </summary>
    internal static class UapUiClickDispatcher
    {
        /// <summary>
        /// Outcome of a click attempt. <see cref="Observed"/> is the only
        /// field callers should treat as success.
        /// </summary>
        internal sealed class ClickOutcome
        {
            /// <summary>True only when the manipulator's clicked event actually ran.</summary>
            public bool Observed;
            /// <summary>Human-readable explanation, always set when Observed is false.</summary>
            public string Detail = string.Empty;
        }

        /// <summary>
        /// Invokes <paramref name="element"/>'s Clickable manipulator and
        /// reports whether its clicked event fired. Callers must already
        /// have verified the element is enabled and laid out
        /// (UapUiElementResolver's clickable guard). Never throws for an
        /// element that simply has no click behaviour -- that is a reported
        /// outcome, not an exception.
        /// </summary>
        public static ClickOutcome Click(VisualElement element)
        {
            var outcome = new ClickOutcome();
            if (element == null)
            {
                outcome.Detail = "No element to click.";
                return outcome;
            }

            Clickable clickable = FindClickable(element);
            if (clickable == null)
            {
                outcome.Detail = "Element type '" + element.GetType().FullName + "' has no Clickable"
                    + " manipulator, so there is no click behaviour to invoke. Only controls built on"
                    + " Clickable (Button and friends) can be clicked by this tool; for anything else,"
                    + " look for a menu path via uap_editor_execute_menu instead.";
                return outcome;
            }

            // Clickable.Invoke(EventBase) is not public. Reflection is a real
            // cost, taken deliberately because it is the ONLY measured path
            // that works -- and it is made safe by the observation check
            // below: if a future Unity renames or reshapes this, the click
            // is reported as not observed rather than silently claimed.
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                outcome.Detail = "This Unity version's Clickable has no Invoke(EventBase) method, so the"
                    + " click could not be delivered. Nothing was changed.";
                return outcome;
            }

            // A bool-valued control (Toggle and everything else built on
            // BaseBoolField) is refused rather than clicked. MEASURED
            // 2026-08-04: Toggle's manipulator carries its behaviour on
            // clickedWithEventInfo, that handler DOES run when a ClickEvent
            // is invoked, and the toggle's value stays exactly where it was
            // -- BaseBoolField.OnClickEvent filters on the event type and a
            // synthesised ClickEvent is not what it wants. Clicking one is a
            // guaranteed no-op, so the honest answer is to name the tool that
            // does work instead of dispatching and reporting something.
            if (element is INotifyValueChanged<bool>)
            {
                outcome.Detail = "'" + element.GetType().Name + "' is a bool-valued control, and a"
                    + " dispatched click provably does not change it in this Unity version (its handler"
                    + " runs and ignores the event). Use uap_editor_ui_set_value with the value you want"
                    + " instead -- that path writes through the control's own value API.";
                return outcome;
            }

            // Whether a click was OBSERVED means "the control's own handler
            // ran", never "a handler ran". Those came apart in the first
            // version of this check, which did `clickable.clicked += probe`
            // and reported success if the probe fired. On a control with no
            // handler wired at all the probe was then the ONLY subscriber, so
            // it fired every time and an inert Button reported a successful
            // click -- measured 2026-08-04, and the very defect class this
            // class was rewritten to close. The pre-existing delegates are
            // therefore WRAPPED rather than joined: the flag can only be set
            // by code that was already there.
            FieldInfo clickedField = typeof(Clickable).GetField("clicked",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo infoField = typeof(Clickable).GetField("clickedWithEventInfo",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (clickedField == null || infoField == null)
            {
                outcome.Detail = "This Unity version's Clickable does not expose the handler fields this"
                    + " tool needs to tell a real click apart from a dispatched one, so the click was not"
                    + " attempted. Nothing was changed.";
                return outcome;
            }

            var originalClicked = clickedField.GetValue(clickable) as Action;
            var originalInfo = infoField.GetValue(clickable) as Action<EventBase>;
            if (originalClicked == null && originalInfo == null)
            {
                outcome.Detail = "This '" + element.GetType().Name + "' has a Clickable manipulator but"
                    + " nothing is wired to it, so clicking it would do nothing. It is decorative, or its"
                    + " behaviour lives somewhere this tool cannot reach.";
                return outcome;
            }

            bool fired = false;
            if (originalClicked != null)
            {
                Action captured = originalClicked;
                clickedField.SetValue(clickable, (Action)delegate { fired = true; captured(); });
            }
            if (originalInfo != null)
            {
                Action<EventBase> captured = originalInfo;
                infoField.SetValue(clickable, (Action<EventBase>)delegate (EventBase e) { fired = true; captured(e); });
            }
            try
            {
                using (ClickEvent evt = ClickEvent.GetPooled())
                {
                    evt.target = element;
                    invoke.Invoke(clickable, new object[] { evt });
                }
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                outcome.Detail = "The click handler threw " + inner.GetType().Name + ": " + inner.Message;
                return outcome;
            }
            catch (Exception e)
            {
                outcome.Detail = "Could not deliver the click: " + e.GetType().Name + ": " + e.Message;
                return outcome;
            }
            finally
            {
                clickedField.SetValue(clickable, originalClicked);
                infoField.SetValue(clickable, originalInfo);
            }

            outcome.Observed = fired;
            if (!fired)
            {
                outcome.Detail = "The click was delivered to the element's Clickable manipulator, but the"
                    + " handler that was already attached to it did not run. Treat the click as NOT having"
                    + " taken effect and confirm with uap_editor_screenshot before continuing.";
            }
            return outcome;
        }

        /// <summary>
        /// Finds the element's Clickable. Button exposes one directly;
        /// other controls (Toggle and friends) keep theirs in a field, so
        /// both properties and fields of that type are scanned, walking up
        /// the type hierarchy because the member is often declared on a
        /// base class.
        /// </summary>
        private static Clickable FindClickable(VisualElement element)
        {
            Button button = element as Button;
            if (button != null && button.clickable != null)
            {
                return button.clickable;
            }
            for (Type t = element.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                PropertyInfo[] properties = t.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < properties.Length; i++)
                {
                    if (properties[i].PropertyType != typeof(Clickable)
                        || properties[i].GetIndexParameters().Length != 0)
                    {
                        continue;
                    }
                    var found = properties[i].GetValue(element, null) as Clickable;
                    if (found != null)
                    {
                        return found;
                    }
                }
                FieldInfo[] fields = t.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType != typeof(Clickable))
                    {
                        continue;
                    }
                    var found = fields[i].GetValue(element) as Clickable;
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }
    }
}

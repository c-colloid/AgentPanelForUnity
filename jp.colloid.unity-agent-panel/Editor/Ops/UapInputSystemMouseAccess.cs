using System;
using System.Reflection;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reflection into the Input System package (com.unity.inputsystem) for
    /// uap_game_mouse: the current Mouse device, MouseState's fields and
    /// InputSystem.QueueStateEvent&lt;MouseState&gt; -- the same package,
    /// the same missing-package story and the same generic-method lookup as
    /// <see cref="UapInputSystemKeyboardAccess"/> (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.2).
    ///
    /// A MouseState queued this way is a FULL snapshot of position, delta,
    /// scroll and buttons together -- the Input System does not merge a
    /// partial one into whatever a control already reports, it just
    /// overwrites every field (verified against the real package source,
    /// com.unity.inputsystem 1.7.0, Devices/Mouse.cs and InputSystem.cs).
    /// So every call here that queues a state must supply all four fields,
    /// which is why <see cref="BuildState"/> always takes all four rather
    /// than defaulting the ones a caller does not care about.
    /// </summary>
    internal static class UapInputSystemMouseAccess
    {
        private const string InputSystemTypeName = "UnityEngine.InputSystem.InputSystem";
        private const string MouseTypeName = "UnityEngine.InputSystem.Mouse";
        private const string MouseStateTypeName = "UnityEngine.InputSystem.LowLevel.MouseState";
        private const string MouseButtonTypeName = "UnityEngine.InputSystem.LowLevel.MouseButton";
        private const string InputDeviceTypeName = "UnityEngine.InputSystem.InputDevice";

        /// <summary>What to tell the agent when the package is not there.</summary>
        public const string MissingPackageMessage =
            "The Input System package (com.unity.inputsystem) is not in this project, and the legacy Input"
            + " Manager (UnityEngine.Input) offers no way to inject a mouse event from the Editor. Either"
            + " install the Input System (Package Manager) and read input through it (Mouse.current), drive"
            + " uGUI widgets with uap_game_pointer instead, or use dry_run:true for a 3D/2D raycast that needs"
            + " neither.";

        private static bool _loaded;
        private static string _loadError;
        private static Type _inputSystemType;
        private static Type _mouseType;
        private static Type _mouseStateType;
        private static Type _mouseButtonType;
        private static MethodInfo _queueStateEventMethod;
        private static FieldInfo _positionField;
        private static FieldInfo _deltaField;
        private static FieldInfo _scrollField;
        private static FieldInfo _buttonsField;

        /// <summary>Resolves every type, field and method once; false with the reason when any is missing.</summary>
        public static bool TryLoad(out string error)
        {
            if (_loaded)
            {
                error = _loadError;
                return _loadError == null;
            }
            _loaded = true;
            _inputSystemType = UapUguiEventAccess.FindType(InputSystemTypeName);
            if (_inputSystemType == null)
            {
                _loadError = MissingPackageMessage;
                error = _loadError;
                return false;
            }
            _mouseType = UapUguiEventAccess.FindType(MouseTypeName);
            _mouseStateType = UapUguiEventAccess.FindType(MouseStateTypeName);
            _mouseButtonType = UapUguiEventAccess.FindType(MouseButtonTypeName);
            Type inputDeviceType = UapUguiEventAccess.FindType(InputDeviceTypeName);
            if (_mouseType == null || _mouseStateType == null || _mouseButtonType == null || inputDeviceType == null)
            {
                _loadError = "The Input System is installed but Mouse / MouseState / MouseButton could not be"
                    + " resolved; this package version lays them out differently.";
                error = _loadError;
                return false;
            }
            _positionField = _mouseStateType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
            _deltaField = _mouseStateType.GetField("delta", BindingFlags.Public | BindingFlags.Instance);
            _scrollField = _mouseStateType.GetField("scroll", BindingFlags.Public | BindingFlags.Instance);
            _buttonsField = _mouseStateType.GetField("buttons", BindingFlags.Public | BindingFlags.Instance);
            _queueStateEventMethod = FindQueueStateEvent(inputDeviceType);
            if (_positionField == null || _deltaField == null || _scrollField == null || _buttonsField == null
                || _queueStateEventMethod == null)
            {
                _loadError = "The Input System is installed but MouseState's position/delta/scroll/buttons fields"
                    + " or InputSystem.QueueStateEvent could not be resolved; this package version lays them"
                    + " out differently.";
                error = _loadError;
                return false;
            }
            _loadError = null;
            error = null;
            return true;
        }

        /// <summary>Mouse.current, or null with the reason (no mouse device: the Input System is not the active input handler, or no pointer is connected).</summary>
        public static object CurrentMouse(out string error)
        {
            string loadError;
            if (!TryLoad(out loadError))
            {
                error = loadError;
                return null;
            }
            PropertyInfo current = _mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            object mouse = current != null ? current.GetValue(null, null) : null;
            if (mouse == null)
            {
                error = "The Input System has no Mouse device (Mouse.current is null). Check Project Settings >"
                    + " Player > Active Input Handling: it must be 'Input System Package (New)' or 'Both' for"
                    + " the package to create devices, and the game must read input through Mouse.current rather"
                    + " than the legacy Input class.";
                return null;
            }
            error = null;
            return mouse;
        }

        /// <summary>
        /// Mouse.current.position.ReadValue() -- the device's live position
        /// (bottom-left origin, pixels), read fresh on every call so a
        /// state event that is not moving the mouse (a click, a scroll)
        /// leaves it exactly where it already was instead of snapping it to
        /// a stale cached value.
        /// </summary>
        public static Vector2 ReadPosition(object mouse)
        {
            PropertyInfo positionProperty = _mouseType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
            object control = positionProperty != null ? positionProperty.GetValue(mouse, null) : null;
            if (control == null)
            {
                return Vector2.zero;
            }
            MethodInfo readValue = control.GetType().GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            object value = readValue != null ? readValue.Invoke(control, null) : null;
            return value is Vector2 ? (Vector2)value : Vector2.zero;
        }

        /// <summary>
        /// Mouse.current.scroll.ReadValue() -- the device's scroll delta for
        /// THIS sample. Added for uap_input_record (design note
        /// docs/design-notes/2026-09-23-uloop-phase2.md section 3.4), which
        /// samples it every tick the same way uap_game_mouse's own
        /// dry_run-free actions read <see cref="ReadPosition"/>: 'scroll' is
        /// a Mouse-only property (Pointer does not declare it), so this
        /// resolves it the same lazy way as position rather than caching the
        /// PropertyInfo in TryLoad -- it is only ever needed by the record
        /// path, never by uap_game_mouse's own inject-only use of this class.
        /// </summary>
        public static Vector2 ReadScroll(object mouse)
        {
            PropertyInfo scrollProperty = _mouseType.GetProperty("scroll", BindingFlags.Public | BindingFlags.Instance);
            object control = scrollProperty != null ? scrollProperty.GetValue(mouse, null) : null;
            if (control == null)
            {
                return Vector2.zero;
            }
            MethodInfo readValue = control.GetType().GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            object value = readValue != null ? readValue.Invoke(control, null) : null;
            return value is Vector2 ? (Vector2)value : Vector2.zero;
        }

        /// <summary>
        /// The MouseState.buttons bit index for left/right/middle, read
        /// from the resolved MouseButton enum rather than assumed, in case
        /// a future package version reorders it (UapGamePointerButton's
        /// names match MouseButton's member names exactly: Left/Right/Middle).
        /// </summary>
        public static int ButtonBit(UapGamePointerButton button)
        {
            object value = Enum.Parse(_mouseButtonType, button.ToString(), true);
            return Convert.ToInt32(value);
        }

        /// <summary>Builds a MouseState (boxed, for reflection) with every field set: buttons is a bitmask, one bit per pressed button (see <see cref="ButtonBit"/>).</summary>
        public static object BuildState(Vector2 position, Vector2 delta, Vector2 scroll, int buttonsMask)
        {
            object state = Activator.CreateInstance(_mouseStateType);
            _positionField.SetValue(state, position);
            _deltaField.SetValue(state, delta);
            _scrollField.SetValue(state, scroll);
            _buttonsField.SetValue(state, (ushort)buttonsMask);
            return state;
        }

        /// <summary>Queues the given MouseState for the next input update, exactly like the package's own test fixture.</summary>
        public static void QueueState(object mouse, object mouseState)
        {
            _queueStateEventMethod.MakeGenericMethod(_mouseStateType)
                .Invoke(null, new[] { mouse, mouseState, (object)-1.0 });
        }

        /// <summary>
        /// Runs InputSystem.Update() when the project processes events
        /// manually (InputSettings.updateMode == ProcessEventsManually),
        /// because nothing else would ever consume the queued state. In the
        /// default modes the next player loop update does it (same
        /// rationale as UapInputSystemKeyboardAccess.FlushIfManual).
        /// </summary>
        public static bool FlushIfManual()
        {
            PropertyInfo settingsProperty = _inputSystemType.GetProperty("settings", BindingFlags.Public | BindingFlags.Static);
            object settings = settingsProperty != null ? settingsProperty.GetValue(null, null) : null;
            if (settings == null)
            {
                return false;
            }
            PropertyInfo updateModeProperty = settings.GetType().GetProperty("updateMode", BindingFlags.Public | BindingFlags.Instance);
            object updateMode = updateModeProperty != null ? updateModeProperty.GetValue(settings, null) : null;
            if (updateMode == null || updateMode.ToString() != "ProcessEventsManually")
            {
                return false;
            }
            MethodInfo update = _inputSystemType.GetMethod("Update", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (update != null)
            {
                update.Invoke(null, null);
                return true;
            }
            return false;
        }

        /// <summary>Whether the device reports the given button pressed right now (after the last input update) -- the readback after a release, or to flag a stuck button.</summary>
        public static bool IsPressed(object mouse, UapGamePointerButton button)
        {
            string propertyName = button == UapGamePointerButton.Left ? "leftButton"
                : button == UapGamePointerButton.Right ? "rightButton" : "middleButton";
            PropertyInfo controlProperty = _mouseType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            object control = controlProperty != null ? controlProperty.GetValue(mouse, null) : null;
            if (control == null)
            {
                return false;
            }
            PropertyInfo isPressed = control.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
            object value = isPressed != null ? isPressed.GetValue(control, null) : null;
            return value is bool && (bool)value;
        }

        private static MethodInfo FindQueueStateEvent(Type inputDeviceType)
        {
            MethodInfo[] methods = _inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "QueueStateEvent" || !methods[i].IsGenericMethodDefinition)
                {
                    continue;
                }
                ParameterInfo[] parameters = methods[i].GetParameters();
                if (parameters.Length == 3 && parameters[0].ParameterType == inputDeviceType
                    && parameters[2].ParameterType == typeof(double))
                {
                    return methods[i];
                }
            }
            return null;
        }
    }
}

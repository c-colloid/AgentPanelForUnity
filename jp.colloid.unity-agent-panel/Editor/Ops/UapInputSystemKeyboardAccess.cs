using System;
using System.Collections.Generic;
using System.Reflection;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reflection into the Input System package (com.unity.inputsystem)
    /// for uap_game_key: the current Keyboard device, the Key enum, and
    /// InputSystem.QueueStateEvent with a KeyboardState carrying every key
    /// this tool holds.
    ///
    /// Reflection for the same reason as <see cref="UapUguiEventAccess"/>:
    /// the package is optional, this one more so than uGUI, and the panel
    /// must compile without it. The legacy Input Manager
    /// (UnityEngine.Input) has no injection API at all, which is why this
    /// tool is Input System only and says so instead of pretending
    /// (design note docs/design-notes/2026-09-22-game-input-tools.md
    /// section 4.2).
    ///
    /// A KeyboardState is a full snapshot -- queuing one with only the new
    /// key would release every other key -- so callers always pass the
    /// complete held set, and the tool keeps that set.
    /// </summary>
    internal static class UapInputSystemKeyboardAccess
    {
        private const string InputSystemTypeName = "UnityEngine.InputSystem.InputSystem";
        private const string KeyboardTypeName = "UnityEngine.InputSystem.Keyboard";
        private const string KeyTypeName = "UnityEngine.InputSystem.Key";
        private const string KeyboardStateTypeName = "UnityEngine.InputSystem.LowLevel.KeyboardState";
        private const string InputDeviceTypeName = "UnityEngine.InputSystem.InputDevice";

        /// <summary>What to tell the agent when the package is not there.</summary>
        public const string MissingPackageMessage =
            "The Input System package (com.unity.inputsystem) is not in this project, and the legacy Input"
            + " Manager (UnityEngine.Input) offers no way to inject a key from the Editor. Either install"
            + " the Input System (Package Manager) and read input through it (Keyboard.current), or drive"
            + " the game through its uGUI buttons with uap_game_pointer.";

        private static bool _loaded;
        private static string _loadError;
        private static Type _inputSystemType;
        private static Type _keyboardType;
        private static Type _keyType;
        private static Type _keyboardStateType;
        private static ConstructorInfo _keyboardStateCtor;
        private static MethodInfo _queueStateEventMethod;
        private static PropertyInfo _keyIndexer;

        /// <summary>Resolves every type and method once; false with the reason when any is missing.</summary>
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
            _keyboardType = UapUguiEventAccess.FindType(KeyboardTypeName);
            _keyType = UapUguiEventAccess.FindType(KeyTypeName);
            _keyboardStateType = UapUguiEventAccess.FindType(KeyboardStateTypeName);
            Type inputDeviceType = UapUguiEventAccess.FindType(InputDeviceTypeName);
            if (_keyboardType == null || _keyType == null || _keyboardStateType == null || inputDeviceType == null)
            {
                _loadError = "The Input System is installed but Keyboard / Key / KeyboardState could not be"
                    + " resolved; this package version lays them out differently.";
                error = _loadError;
                return false;
            }
            _keyboardStateCtor = _keyboardStateType.GetConstructor(new[] { _keyType.MakeArrayType() });
            _queueStateEventMethod = FindQueueStateEvent(inputDeviceType);
            _keyIndexer = _keyboardType.GetProperty("Item", new[] { _keyType });
            if (_keyboardStateCtor == null || _queueStateEventMethod == null || _keyIndexer == null)
            {
                _loadError = "The Input System is installed but KeyboardState(Key[]) / InputSystem.QueueStateEvent /"
                    + " Keyboard[Key] could not be resolved; this package version lays them out differently.";
                error = _loadError;
                return false;
            }
            _loadError = null;
            error = null;
            return true;
        }

        /// <summary>Keyboard.current, or null with the reason (no keyboard device: the Input System is not the active input handler, or no keyboard is connected).</summary>
        public static object CurrentKeyboard(out string error)
        {
            string loadError;
            if (!TryLoad(out loadError))
            {
                error = loadError;
                return null;
            }
            PropertyInfo current = _keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            object keyboard = current != null ? current.GetValue(null, null) : null;
            if (keyboard == null)
            {
                error = "The Input System has no Keyboard device (Keyboard.current is null). Check Project"
                    + " Settings > Player > Active Input Handling: it must be 'Input System Package (New)' or"
                    + " 'Both' for the package to create devices, and the game must read input through"
                    + " Keyboard.current rather than the legacy Input class.";
                return null;
            }
            error = null;
            return keyboard;
        }

        /// <summary>The Key enum value for a canonical name from <see cref="UapGameKeyPolicy.NormalizeKeyName"/>; null with the reason when this package version lacks it.</summary>
        public static object ParseKey(string canonicalName, out string error)
        {
            try
            {
                object key = Enum.Parse(_keyType, canonicalName, true);
                error = null;
                return key;
            }
            catch (ArgumentException)
            {
                error = "This Input System version has no Key named '" + canonicalName + "'.";
                return null;
            }
        }

        /// <summary>
        /// Queues a KeyboardState snapshot holding exactly
        /// <paramref name="pressedKeys"/> (Key enum values) for the next
        /// input update -- the same call the package's own test fixture
        /// uses to press keys.
        /// </summary>
        public static void QueueState(object keyboard, ICollection<object> pressedKeys)
        {
            Array keys = Array.CreateInstance(_keyType, pressedKeys.Count);
            int index = 0;
            foreach (object key in pressedKeys)
            {
                keys.SetValue(key, index++);
            }
            object state = _keyboardStateCtor.Invoke(new object[] { keys });
            _queueStateEventMethod.MakeGenericMethod(_keyboardStateType)
                .Invoke(null, new[] { keyboard, state, (object)-1.0 });
        }

        /// <summary>
        /// Runs InputSystem.Update() when the project processes events
        /// manually (InputSettings.updateMode == ProcessEventsManually),
        /// because nothing else would ever consume the queued state. In
        /// the default modes the next player loop update does it.
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

        /// <summary>Keyboard[key].isPressed as the device reports it now (after the last input update).</summary>
        public static bool IsPressed(object keyboard, object key)
        {
            object control = _keyIndexer.GetValue(keyboard, new[] { key });
            if (control == null)
            {
                return false;
            }
            PropertyInfo isPressed = control.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
            object value = isPressed != null ? isPressed.GetValue(control, null) : null;
            return value is bool && (bool)value;
        }

        /// <summary>Every Key the device reports pressed right now, by canonical name -- the readback after a release.</summary>
        public static List<string> PressedKeyNames(object keyboard)
        {
            var pressed = new List<string>();
            foreach (object key in Enum.GetValues(_keyType))
            {
                string name = key.ToString();
                if (name == "None" || name == "IMESelected")
                {
                    continue;
                }
                bool isPressed;
                try
                {
                    isPressed = IsPressed(keyboard, key);
                }
                catch (Exception)
                {
                    // A Key enum member without a control on this layout
                    // (OEM keys on some platforms) throws from the indexer.
                    continue;
                }
                if (isPressed)
                {
                    pressed.Add(name);
                }
            }
            return pressed;
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

using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_game_key is asked to do.</summary>
    public enum UapGameKeyAction
    {
        /// <summary>Down, hold for the duration (at least one input update), up.</summary>
        Press,
        /// <summary>Down and stay down until an Up (or Play Mode ends).</summary>
        Down,
        /// <summary>Release a key this tool is holding.</summary>
        Up,
        /// <summary>Release everything this tool is holding, and report what the device still shows pressed.</summary>
        ReleaseAll
    }

    /// <summary>
    /// The argument rules for uap_game_key, with no Input System in sight
    /// (design note docs/design-notes/2026-09-22-game-input-tools.md
    /// section 4). Key names are checked against a copy of the Input
    /// System's Key enum so that a wrong name is answered with a
    /// suggestion instead of a reflection failure, and so the check runs
    /// in the license-free smoke gate; the real enum is still what the
    /// Editor half parses the canonical name into.
    /// </summary>
    public static class UapGameKeyPolicy
    {
        /// <summary>
        /// UnityEngine.InputSystem.Key member names (Input System 1.x),
        /// spelled as the enum spells them. Digit keys are Digit0..Digit9,
        /// modifiers are Left/Right-prefixed; the aliases below map the
        /// shorthand an agent is likely to write onto these.
        /// </summary>
        public static readonly string[] KnownKeys =
        {
            "Space", "Enter", "Tab", "Backquote", "Quote", "Semicolon", "Comma", "Period", "Slash",
            "Backslash", "LeftBracket", "RightBracket", "Minus", "Equals",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
            "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9", "Digit0",
            "LeftShift", "RightShift", "LeftAlt", "RightAlt", "LeftCtrl", "RightCtrl", "LeftMeta", "RightMeta",
            "ContextMenu", "Escape", "LeftArrow", "RightArrow", "UpArrow", "DownArrow", "Backspace",
            "PageDown", "PageUp", "Home", "End", "Insert", "Delete", "CapsLock", "NumLock", "PrintScreen",
            "ScrollLock", "Pause", "NumpadEnter", "NumpadDivide", "NumpadMultiply", "NumpadPlus",
            "NumpadMinus", "NumpadPeriod", "NumpadEquals",
            "Numpad0", "Numpad1", "Numpad2", "Numpad3", "Numpad4", "Numpad5", "Numpad6", "Numpad7", "Numpad8", "Numpad9",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "OEM1", "OEM2", "OEM3", "OEM4", "OEM5"
        };

        private static readonly Dictionary<string, string> Aliases = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "shift", "LeftShift" }, { "ctrl", "LeftCtrl" }, { "control", "LeftCtrl" },
                { "alt", "LeftAlt" }, { "option", "LeftAlt" },
                { "meta", "LeftMeta" }, { "cmd", "LeftMeta" }, { "command", "LeftMeta" },
                { "win", "LeftMeta" }, { "windows", "LeftMeta" }, { "super", "LeftMeta" },
                { "esc", "Escape" }, { "return", "Enter" }, { "spacebar", "Space" }, { " ", "Space" },
                { "up", "UpArrow" }, { "down", "DownArrow" }, { "left", "LeftArrow" }, { "right", "RightArrow" },
                { "arrowup", "UpArrow" }, { "arrowdown", "DownArrow" }, { "arrowleft", "LeftArrow" }, { "arrowright", "RightArrow" },
                { "del", "Delete" }, { "ins", "Insert" }, { "pgup", "PageUp" }, { "pgdn", "PageDown" }, { "pgdown", "PageDown" },
                { "bksp", "Backspace" }, { "back", "Backspace" }, { "grave", "Backquote" }, { "tilde", "Backquote" },
                { "apostrophe", "Quote" }, { "dot", "Period" }, { "hyphen", "Minus" }, { "dash", "Minus" },
                { "equal", "Equals" }, { "menu", "ContextMenu" }, { "capslock", "CapsLock" }
            };
            for (int digit = 0; digit <= 9; digit++)
            {
                aliases[digit.ToString()] = "Digit" + digit;
                aliases["digit" + digit] = "Digit" + digit;
                aliases["num" + digit] = "Numpad" + digit;
                aliases["kp" + digit] = "Numpad" + digit;
            }
            return aliases;
        }

        /// <summary>Pure: maps the wire action names to the enum; the default is press. Throws ArgumentException on anything else.</summary>
        public static UapGameKeyAction ParseAction(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_");
            switch (normalized)
            {
                case "":
                case "press":
                case "tap":
                    return UapGameKeyAction.Press;
                case "down":
                case "keydown":
                case "key_down":
                case "hold":
                    return UapGameKeyAction.Down;
                case "up":
                case "keyup":
                case "key_up":
                case "release":
                    return UapGameKeyAction.Up;
                case "release_all":
                case "releaseall":
                    return UapGameKeyAction.ReleaseAll;
                default:
                    throw new ArgumentException("Unknown action '" + value + "'. Valid actions: press, down, up, release_all.");
            }
        }

        /// <summary>
        /// Pure: the canonical Key enum name for what the agent wrote, or
        /// null with an error that lists the closest known names.
        /// Case-insensitive, and the shorthand ("0", "shift", "esc", "up")
        /// is accepted, because an agent reads the game's code, which
        /// says `Key.Space` or "press W", not the enum's exact spelling.
        /// </summary>
        public static string NormalizeKeyName(string raw, out string error)
        {
            string trimmed = (raw ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "'key' is required (an Input System Key name such as W, Space, LeftShift, Digit1, F5).";
                return null;
            }
            string alias;
            if (Aliases.TryGetValue(trimmed, out alias))
            {
                return Found(alias, out error);
            }
            string compact = trimmed.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            if (Aliases.TryGetValue(compact, out alias))
            {
                return Found(alias, out error);
            }
            for (int i = 0; i < KnownKeys.Length; i++)
            {
                if (string.Equals(KnownKeys[i], compact, StringComparison.OrdinalIgnoreCase))
                {
                    return Found(KnownKeys[i], out error);
                }
            }
            error = "Unknown key '" + raw + "'." + Suggest(compact)
                + " Key names follow the Input System's Key enum (e.g. W, Space, Enter, LeftShift, Digit1, F5).";
            return null;
        }

        private static string Found(string canonical, out string error)
        {
            error = null;
            return canonical;
        }

        /// <summary>Pure: up to five known names containing the typed text, as a " Did you mean ...?" fragment, or empty.</summary>
        public static string Suggest(string typed)
        {
            if (string.IsNullOrEmpty(typed))
            {
                return string.Empty;
            }
            var hits = new List<string>();
            for (int i = 0; i < KnownKeys.Length && hits.Count < 5; i++)
            {
                if (KnownKeys[i].IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hits.Add(KnownKeys[i]);
                }
            }
            return hits.Count == 0 ? string.Empty : " Did you mean " + string.Join(", ", hits.ToArray()) + "?";
        }

        /// <summary>
        /// Pure: whether <paramref name="action"/> makes sense given
        /// whether this tool is already holding the key. A down on a held
        /// key and an up on a free key are refused rather than silently
        /// repeated, because either means the agent's model of the key
        /// state is wrong and the next call would be wrong too.
        /// </summary>
        public static bool CheckHeldState(UapGameKeyAction action, bool isHeld, string keyName, out string error)
        {
            switch (action)
            {
                case UapGameKeyAction.Press:
                case UapGameKeyAction.Down:
                    if (isHeld)
                    {
                        error = "Key '" + keyName + "' is already held down by this tool. Release it with action:up"
                            + " (or action:release_all) first.";
                        return false;
                    }
                    break;
                case UapGameKeyAction.Up:
                    if (!isHeld)
                    {
                        error = "Key '" + keyName + "' is not held by this tool, so there is nothing to release."
                            + " Hold it with action:down first; use action:release_all to reset every key.";
                        return false;
                    }
                    break;
            }
            error = null;
            return true;
        }
    }
}

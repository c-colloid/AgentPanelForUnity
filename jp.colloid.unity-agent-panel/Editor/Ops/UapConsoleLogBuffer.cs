using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The Unity half of the Console capture uap_console_logs reads:
    /// subscribes to Application.logMessageReceivedThreaded and hands each
    /// line to <see cref="UapConsoleLogStore"/> (which holds the ring and
    /// every rule worth testing).
    ///
    /// Why a capture of our own rather than the Console window's entries:
    /// Unity 2022.3 exposes no public API for them, and the internal
    /// `UnityEditor.LogEntries` reflection this package already uses for ONE
    /// integer (ConsoleWindowSync's error count, which degrades to
    /// "unavailable" when the reflection fails) is a far worse bet for the
    /// entries themselves -- their shape has changed across editor versions,
    /// and a tool that silently returns nothing is worse than no tool.
    /// `Application.logMessageReceivedThreaded` is public, fires in Play
    /// Mode and Edit Mode alike, and is already the source
    /// ConsoleErrorProvider trusts for the error chip.
    ///
    /// Why it is NOT hooked from [InitializeOnLoadMethod]: this package
    /// spent 2026-09-21 measuring what another package's always-on editor
    /// work costs its users (docs/design-notes/2026-09-21-uloop-always-
    /// loaded-cost.md), so adding an unconditional hook of its own would be
    /// poor form. <see cref="Install"/>/<see cref="Uninstall"/> run from
    /// UapOpsServer.EnsureStarted/Stop -- capture exists only while an agent
    /// session with UapOps enabled is live, and stops when it ends.
    /// </summary>
    public static class UapConsoleLogBuffer
    {
        /// <summary>Starts capturing. Idempotent -- UapOpsServer.EnsureStarted runs on every spawn.</summary>
        public static void Install()
        {
            if (!UapConsoleLogStore.SetCapturing(true))
            {
                return;
            }
            Application.logMessageReceivedThreaded -= OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        /// <summary>Stops capturing and drops what was captured. Idempotent.</summary>
        public static void Uninstall()
        {
            if (!UapConsoleLogStore.SetCapturing(false))
            {
                return;
            }
            Application.logMessageReceivedThreaded -= OnLogMessage;
            UapConsoleLogStore.Clear();
        }

        /// <summary>The wire name for a Unity log type. Unknown values map to "log" rather than throwing.</summary>
        public static string TypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                case LogType.Exception: return "exception";
                default: return "log";
            }
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            UapConsoleLogStore.Capture(TypeName(type), condition, stackTrace);
        }
    }
}

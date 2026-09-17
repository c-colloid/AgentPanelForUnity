using System;
using System.Collections.Generic;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Says whether a native modal dialog currently owns the Editor (design
    /// note 2026-09-17-modal-menu-and-base64-scan section 1.3). The menu
    /// table in <see cref="UapMenuDialogPolicy"/> can only list Unity's own
    /// items; a third-party [MenuItem] (or any tool) that opens a file
    /// picker or EditorUtility.DisplayDialog blocks the main thread the
    /// same way, and the dispatcher's timeout then read "still running ...
    /// will finish on its own" -- which is false: it finishes when a person
    /// clicks. This probe lets the timeout name the dialog instead.
    ///
    /// Called from HTTP WORKER threads (the main thread is the one that is
    /// stuck), so it touches no Unity API: plain user32 window enumeration,
    /// Windows only (null elsewhere). The signature is the one measured on
    /// 2022.3.22f1: a visible top-level "#32770" dialog window of this
    /// process while every Unity container window is disabled.
    /// </summary>
    public static class UapNativeModalProbe
    {
        /// <summary>The Win32 class of a native dialog box (file pickers, message boxes).</summary>
        public const string DialogWindowClass = "#32770";

        /// <summary>The Win32 class of every Unity Editor top-level window.</summary>
        public const string UnityContainerWindowClass = "UnityContainerWndClass";

        /// <summary>One visible top-level window of the Editor process, as the decision sees it.</summary>
        public struct WindowInfo
        {
            public string ClassName;
            public string Title;
            public bool Enabled;
        }

        /// <summary>
        /// Title of the blocking dialog ("" when it has none), or null when
        /// the windows do not show a modal state: no dialog window, or a
        /// Unity window that still accepts input (a dialog-classed window
        /// that disables nothing is not modal). Pure; pinned by tests.
        /// </summary>
        public static string FindBlockingDialog(IList<WindowInfo> visibleWindows)
        {
            if (visibleWindows == null)
            {
                return null;
            }
            string dialogTitle = null;
            bool sawUnityWindow = false;
            for (int i = 0; i < visibleWindows.Count; i++)
            {
                WindowInfo window = visibleWindows[i];
                if (string.Equals(window.ClassName, DialogWindowClass, StringComparison.Ordinal))
                {
                    if (dialogTitle == null)
                    {
                        dialogTitle = window.Title ?? string.Empty;
                    }
                }
                else if (string.Equals(window.ClassName, UnityContainerWindowClass, StringComparison.Ordinal))
                {
                    sawUnityWindow = true;
                    if (window.Enabled)
                    {
                        return null;
                    }
                }
            }
            return sawUnityWindow ? dialogTitle : null;
        }

        /// <summary>The sentence appended to a main-thread timeout; pure so the wording is testable.</summary>
        public static string DescribeBlockingDialog(string title)
        {
            return "NOTE: a native modal dialog" + (string.IsNullOrEmpty(title) ? string.Empty : " (\"" + title + "\")")
                + " is open in the Unity Editor and is what blocks its main thread. It ends only when a PERSON"
                + " closes it -- no uap_* tool can run or dismiss it. Tell the user to close that dialog, and do"
                + " not re-issue calls until uap_ping answers.";
        }

        /// <summary>
        /// <see cref="DescribeBlockingDialog"/> for the dialog blocking this
        /// Editor right now, or null (none, not Windows, or the probe
        /// failed -- it must never turn a timeout into a different error).
        /// </summary>
        public static string Describe()
        {
            try
            {
                string title = FindBlockingDialog(EnumerateVisibleWindows());
                return title == null ? null : DescribeBlockingDialog(title);
            }
            catch (Exception)
            {
                return null;
            }
        }

#if UNITY_EDITOR_WIN
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);

        // Not GetWindowText: for a window of the calling process that is a
        // SendMessage(WM_GETTEXT) with no timeout, i.e. a second hang if the
        // UI thread is stuck outside a message loop.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            StringBuilder lParam, uint flags, uint timeoutMillis, out IntPtr result);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        private const uint WmGetText = 0x000D;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint TitleTimeoutMillis = 200;

        private static List<WindowInfo> EnumerateVisibleWindows()
        {
            var windows = new List<WindowInfo>();
            uint self = GetCurrentProcessId();
            EnumWindows((hwnd, lParam) =>
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner != self || !IsWindowVisible(hwnd))
                {
                    return true;
                }
                var className = new StringBuilder(64);
                GetClassName(hwnd, className, className.Capacity);
                var info = new WindowInfo { ClassName = className.ToString(), Enabled = IsWindowEnabled(hwnd) };
                if (info.ClassName == DialogWindowClass)
                {
                    var title = new StringBuilder(256);
                    IntPtr ignored;
                    SendMessageTimeout(hwnd, WmGetText, (IntPtr)title.Capacity, title, SmtoAbortIfHung,
                        TitleTimeoutMillis, out ignored);
                    info.Title = title.ToString();
                }
                windows.Add(info);
                return true;
            }, IntPtr.Zero);
            return windows;
        }
#else
        private static List<WindowInfo> EnumerateVisibleWindows()
        {
            return null;
        }
#endif
    }
}

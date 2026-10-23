using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Brings the Unity Editor to the front (design note docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 5 -- uLoop's focus-window):
    /// EditorWindow.Focus() for the Editor's OWN window layout, plus an
    /// OS-level foreground request, because the two are independent.
    /// EditorWindow.Focus() only reorders which pane/tab is active WITHIN
    /// Unity; if the OS gave keyboard/mouse focus to a terminal or browser
    /// instead, Unity never sees the injected input uap_game_key/
    /// uap_game_pointer/uap_game_mouse queue for it (uap_project_info's
    /// Run In Background warning names this exact tool as the fix).
    ///
    /// NOT ReadOnly: taking OS-level foreground focus moves the user's
    /// keystrokes away from whatever they were typing into a moment ago --
    /// a real, visible side effect even though nothing in the project
    /// changes.
    ///
    /// Platform mechanism (Unity-free selection logic in
    /// UapEditorFocusPolicy):
    ///   Windows -- P/Invoke user32 SetForegroundWindow on
    ///              Process.GetCurrentProcess().MainWindowHandle. Windows'
    ///              foreground-lock heuristic refuses this call from a
    ///              process that is not itself already foreground unless
    ///              its input queue is attached to the current foreground
    ///              thread (AttachThreadInput) -- AllowSetForegroundWindow
    ///              does NOT help here, since that API grants permission
    ///              TO a pid and can only usefully be called BY the
    ///              process that currently owns the foreground, which is
    ///              whatever OTHER app the user was just using, not us.
    ///   macOS   -- osascript (System Events), by pid so several open
    ///              Unity windows are never ambiguous.
    ///   Linux   -- wmctrl -i -a <id> if wmctrl is on PATH, the id found
    ///              via `wmctrl -l` + a product-name title match; else
    ///              reported unsupported rather than guessed at.
    /// Every child process is capped at ~2 s and killed on timeout so this
    /// tool can never hang the dispatcher's main-thread tick.
    /// </summary>
    public sealed class UapEditorFocusTool : IUapTool
    {
        public const string ToolName = "uap_editor_focus";
        private const int ChildProcessTimeoutMs = 2000;

        // The Linux path runs TWO child processes back to back (`wmctrl -l`
        // then `wmctrl -i -a <id>`); giving each the full ChildProcessTimeoutMs
        // would let this tool block the main thread for up to ~4 s in the
        // worst case, which is what "never block more than ~2 s" (this
        // class's whole reason for capping child processes at all) is meant
        // to rule out. Halving the per-step budget keeps the WHOLE Linux
        // path within the same ~2 s ceiling every other platform gets in
        // one call. wmctrl itself replies in milliseconds in practice, so
        // this only ever matters on the already-degenerate "wmctrl hung"
        // path, where failing a bit sooner is strictly better.
        private const int LinuxWmctrlStepTimeoutMs = ChildProcessTimeoutMs / 2;

        private const int SwRestore = 9;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Brings the Unity Editor to the front: focuses the relevant pane inside the Editor"
                    + " AND requests OS-level foreground focus (Windows: SetForegroundWindow; macOS:"
                    + " osascript; Linux: wmctrl if installed, else reported unsupported). Call this before"
                    + " uap_game_key / uap_game_pointer / uap_game_mouse when the Editor does not currently"
                    + " have OS focus (see uap_project_info's Run In Background warning) -- injected input"
                    + " only reaches the running game while Unity is the foreground app, unless Run In"
                    + " Background is enabled in Player Settings. Best-effort and honestly reported: the"
                    + " OS-level step can fail (Windows' foreground-lock heuristic, a missing wmctrl), and"
                    + " the reply says which half succeeded. Takes no arguments.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject())
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string focusedWindowType = FocusEditorWindowInProcess();

            RuntimePlatform runtimePlatform = Application.platform;
            UapEditorFocusPlatform platform = UapEditorFocusPolicy.DetectPlatform(
                runtimePlatform == RuntimePlatform.WindowsEditor,
                runtimePlatform == RuntimePlatform.OSXEditor,
                runtimePlatform == RuntimePlatform.LinuxEditor);

            string osLevelDetail;
            bool osLevelFocused;
            switch (platform)
            {
                case UapEditorFocusPlatform.Windows:
                    osLevelFocused = TryFocusWindows(out osLevelDetail);
                    break;
                case UapEditorFocusPlatform.MacOS:
                    osLevelFocused = TryFocusMacOs(out osLevelDetail);
                    break;
                case UapEditorFocusPlatform.Linux:
                    osLevelFocused = TryFocusLinux(out osLevelDetail);
                    break;
                default:
                    osLevelFocused = false;
                    osLevelDetail = "OS-level foreground focus is not implemented for RuntimePlatform." + runtimePlatform + ".";
                    break;
            }

            JsonNode result = JsonNode.NewObject()
                .Set("platform", platform.ToString())
                .Set("editorWindowFocused", focusedWindowType != null)
                .Set("focusedWindowType", focusedWindowType ?? string.Empty)
                .Set("osLevelFocused", osLevelFocused)
                .Set("message", Describe(focusedWindowType, platform, osLevelFocused, osLevelDetail));
            if (!osLevelFocused && !string.IsNullOrEmpty(osLevelDetail))
            {
                result.Set("osLevelDetail", osLevelDetail);
            }
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>Best-effort in-Editor focus refresh; returns the focused window's type name, or null when none was focused.</summary>
        private static string FocusEditorWindowInProcess()
        {
            EditorWindow window = EditorWindow.focusedWindow;
            if (window == null)
            {
                return null;
            }
            window.Focus();
            return window.GetType().Name;
        }

        private static string Describe(string focusedWindowType, UapEditorFocusPlatform platform,
            bool osLevelFocused, string osLevelDetail)
        {
            string editorPart = focusedWindowType != null
                ? "Focused '" + focusedWindowType + "' in the Editor's own window layout"
                : "No EditorWindow currently had focus to refresh";
            string mechanism;
            switch (platform)
            {
                case UapEditorFocusPlatform.Windows: mechanism = "SetForegroundWindow"; break;
                case UapEditorFocusPlatform.MacOS: mechanism = "osascript"; break;
                case UapEditorFocusPlatform.Linux: mechanism = "wmctrl"; break;
                default: mechanism = null; break;
            }
            string osPart;
            if (mechanism == null)
            {
                osPart = "OS-level foreground focus is not implemented on this platform.";
            }
            else if (osLevelFocused)
            {
                osPart = "brought the Editor to the OS foreground (" + mechanism + ").";
            }
            else
            {
                osPart = "could NOT bring the Editor to the OS foreground"
                    + (string.IsNullOrEmpty(osLevelDetail) ? string.Empty : " (" + osLevelDetail + ")") + ".";
            }
            return editorPart + "; " + osPart;
        }

        // -- Windows: P/Invoke user32, no child process -----------------------

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();
        }

        private static bool TryFocusWindows(out string detail)
        {
            IntPtr targetWindow;
            try
            {
                // Fetched fresh, not cached: Process.MainWindowHandle is a
                // one-time heuristic computed when the Process object is
                // created, and can go stale across a long Editor session.
                targetWindow = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch (Exception ex)
            {
                detail = "Process.GetCurrentProcess().MainWindowHandle threw: " + ex.Message;
                return false;
            }
            if (targetWindow == IntPtr.Zero)
            {
                detail = "Process.MainWindowHandle is not available (no top-level window found for this process).";
                return false;
            }

            try
            {
                if (NativeMethods.IsIconic(targetWindow))
                {
                    NativeMethods.ShowWindow(targetWindow, SwRestore);
                }

                // Windows refuses SetForegroundWindow from a background
                // process unless its input queue is attached to whichever
                // thread currently owns the foreground window -- otherwise
                // the call just flashes the taskbar icon and returns
                // false. Attaching briefly makes Windows treat the request
                // as coming from the active app; always detached again in
                // the finally block, even on failure, so the Editor's
                // input processing is never left depending on another
                // app's thread staying alive.
                uint currentThreadId = NativeMethods.GetCurrentThreadId();
                IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
                uint foregroundThreadId = 0;
                bool attached = false;
                if (foregroundWindow != IntPtr.Zero)
                {
                    uint foregroundProcessId;
                    foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);
                    if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                    {
                        attached = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    }
                }
                try
                {
                    bool ok = NativeMethods.SetForegroundWindow(targetWindow);
                    detail = ok ? null : "SetForegroundWindow returned false (Windows' foreground-lock heuristic refused it).";
                    return ok;
                }
                finally
                {
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                }
            }
            catch (DllNotFoundException ex)
            {
                detail = ex.Message;
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        // -- macOS: osascript ---------------------------------------------------

        private static bool TryFocusMacOs(out string detail)
        {
            int pid = Process.GetCurrentProcess().Id;
            string[] args = UapEditorFocusPolicy.BuildOsascriptActivateArguments(pid);
            string stdout, stderr;
            ProcessOutcome outcome = RunProcess("/usr/bin/osascript", args, out stdout, out stderr);
            if (outcome == ProcessOutcome.Ok)
            {
                detail = null;
                return true;
            }
            detail = DescribeProcessFailure(outcome, "osascript", stderr);
            return false;
        }

        // -- Linux: wmctrl, best-effort ------------------------------------------

        private static bool TryFocusLinux(out string detail)
        {
            string listCommand = UapEditorFocusPolicy.BuildLinuxWmctrlShellCommand(
                UapEditorFocusPolicy.BuildWmctrlListArguments());
            string listStdout, listStderr;
            ProcessOutcome listOutcome = RunProcess("/bin/sh", new[] { "-c", listCommand },
                LinuxWmctrlStepTimeoutMs, out listStdout, out listStderr, out int listExitCode);
            if (listExitCode == UapEditorFocusPolicy.LinuxWmctrlMissingExitCode)
            {
                detail = "wmctrl is not on PATH (install it, e.g. 'apt install wmctrl', or use a window manager that supports EWMH).";
                return false;
            }
            if (listOutcome != ProcessOutcome.Ok)
            {
                detail = DescribeProcessFailure(listOutcome, "wmctrl -l", listStderr, LinuxWmctrlStepTimeoutMs);
                return false;
            }

            string title = PlayerSettings.productName;
            string windowId = UapEditorFocusPolicy.FindWindowId(listStdout, title);
            if (windowId == null)
            {
                detail = string.IsNullOrEmpty(title)
                    ? "No product name is set to match against `wmctrl -l` output."
                    : "No open window title contains '" + title + "' in `wmctrl -l` output.";
                return false;
            }

            string activateCommand = UapEditorFocusPolicy.BuildLinuxWmctrlShellCommand(
                UapEditorFocusPolicy.BuildWmctrlActivateArguments(windowId));
            string activateStdout, activateStderr;
            ProcessOutcome activateOutcome = RunProcess("/bin/sh", new[] { "-c", activateCommand },
                LinuxWmctrlStepTimeoutMs, out activateStdout, out activateStderr, out _);
            if (activateOutcome != ProcessOutcome.Ok)
            {
                detail = DescribeProcessFailure(activateOutcome, "wmctrl -i -a", activateStderr, LinuxWmctrlStepTimeoutMs);
                return false;
            }
            detail = null;
            return true;
        }

        // -- Shared child-process plumbing ---------------------------------------

        private enum ProcessOutcome
        {
            Ok,
            NotFound,
            NonZeroExit,
            TimedOut,
            StartFailed
        }

        private static ProcessOutcome RunProcess(string fileName, string[] args, out string stdout, out string stderr)
        {
            int exitCode;
            return RunProcess(fileName, args, ChildProcessTimeoutMs, out stdout, out stderr, out exitCode);
        }

        /// <summary>
        /// Runs a short child process capped at <paramref name="timeoutMs"/>
        /// (see <see cref="LinuxWmctrlStepTimeoutMs"/> for why a caller with
        /// more than one child process in sequence should pass less than
        /// <see cref="ChildProcessTimeoutMs"/>). WaitForExit is called
        /// BEFORE reading stdout/stderr, not after -- reading first would
        /// block on the child's own output buffering and could blow the
        /// time budget before this method ever reaches the timeout check.
        /// Every output this tool reads (one osascript line, a `wmctrl -l`
        /// listing) is small enough that the pipe never fills while nothing
        /// is draining it, so this ordering never itself causes a deadlock.
        /// Never throws.
        /// </summary>
        private static ProcessOutcome RunProcess(string fileName, string[] args, int timeoutMs,
            out string stdout, out string stderr, out int exitCode)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            exitCode = -1;
            var info = new ProcessStartInfo(fileName, UapEditorFocusPolicy.JoinArguments(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            Process process;
            try
            {
                process = Process.Start(info);
            }
            catch (Win32Exception)
            {
                return ProcessOutcome.NotFound;
            }
            catch (Exception)
            {
                return ProcessOutcome.StartFailed;
            }
            if (process == null)
            {
                return ProcessOutcome.StartFailed;
            }
            using (process)
            {
                bool exited;
                try
                {
                    exited = process.WaitForExit(timeoutMs);
                }
                catch (Exception)
                {
                    exited = false;
                }
                if (!exited)
                {
                    try { process.Kill(); } catch (Exception) { }
                    try { process.WaitForExit(200); } catch (Exception) { }
                    return ProcessOutcome.TimedOut;
                }
                try
                {
                    stdout = process.StandardOutput.ReadToEnd();
                    stderr = process.StandardError.ReadToEnd();
                    exitCode = process.ExitCode;
                }
                catch (Exception)
                {
                    // The process exited; a failure here just means we lost the
                    // diagnostic text, not the outcome.
                }
                return exitCode == 0 ? ProcessOutcome.Ok : ProcessOutcome.NonZeroExit;
            }
        }

        private static string DescribeProcessFailure(ProcessOutcome outcome, string commandDescription, string stderr)
        {
            return DescribeProcessFailure(outcome, commandDescription, stderr, ChildProcessTimeoutMs);
        }

        private static string DescribeProcessFailure(ProcessOutcome outcome, string commandDescription, string stderr, int timeoutMs)
        {
            switch (outcome)
            {
                case ProcessOutcome.NotFound:
                    return commandDescription + " could not be started (not found).";
                case ProcessOutcome.TimedOut:
                    return commandDescription + " did not finish within " + timeoutMs + " ms and was killed.";
                case ProcessOutcome.StartFailed:
                    return commandDescription + " could not be started.";
                case ProcessOutcome.NonZeroExit:
                    string trimmed = string.IsNullOrEmpty(stderr) ? string.Empty : stderr.Trim();
                    return commandDescription + " exited with an error" + (trimmed.Length > 0 ? ": " + trimmed : ".");
                default:
                    return null;
            }
        }
    }
}

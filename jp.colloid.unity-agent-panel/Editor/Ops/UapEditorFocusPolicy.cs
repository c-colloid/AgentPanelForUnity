using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Which OS-level focus mechanism uap_editor_focus uses. Unknown platforms simply get no OS-level step.</summary>
    public enum UapEditorFocusPlatform
    {
        Windows,
        MacOS,
        Linux,
        Unsupported
    }

    /// <summary>
    /// Unity-free half of uap_editor_focus (docs/design-notes/2026-09-22-
    /// uloop-phase1.md section 5): which platform maps to which mechanism,
    /// the exact argv for each child process, and the one piece of parsing
    /// worth getting right -- picking the correct window id out of
    /// `wmctrl -l` on Linux. Windows needs no child process (P/Invoke
    /// user32 directly from UapEditorFocusTool) so it appears here only as
    /// an enum value; macOS/Linux route through a short-lived process the
    /// tool caps at ~2 s.
    /// </summary>
    public static class UapEditorFocusPolicy
    {
        /// <summary>Maps Application.platform (passed in as three plain bools so this file stays Unity-free) onto the mechanism to use.</summary>
        public static UapEditorFocusPlatform DetectPlatform(bool isWindowsEditor, bool isOsxEditor, bool isLinuxEditor)
        {
            if (isWindowsEditor) { return UapEditorFocusPlatform.Windows; }
            if (isOsxEditor) { return UapEditorFocusPlatform.MacOS; }
            if (isLinuxEditor) { return UapEditorFocusPlatform.Linux; }
            return UapEditorFocusPlatform.Unsupported;
        }

        /// <summary>
        /// osascript argv that raises the Editor process (identified by
        /// its OS pid, not by title -- unambiguous even with several Unity
        /// Editor windows open) to frontmost on macOS. "-e" runs the
        /// AppleScript inline; no temp script file needed for one line.
        /// </summary>
        public static string[] BuildOsascriptActivateArguments(int editorProcessId)
        {
            return new[]
            {
                "-e",
                "tell application \"System Events\" to set frontmost of first process whose unix id is "
                    + editorProcessId + " to true"
            };
        }

        /// <summary>`wmctrl -l` argv: lists open windows as "&lt;id&gt; &lt;desktop&gt; &lt;host&gt; &lt;title...&gt;".</summary>
        public static string[] BuildWmctrlListArguments()
        {
            return new[] { "-l" };
        }

        /// <summary>
        /// `wmctrl -i -a &lt;id&gt;` argv: activates the EXACT window id
        /// found by <see cref="FindWindowId"/> rather than a title
        /// substring (plain `wmctrl -a TITLE` would also match, and could
        /// raise, an unrelated window such as "Unity Hub").
        /// </summary>
        public static string[] BuildWmctrlActivateArguments(string windowId)
        {
            return new[] { "-i", "-a", windowId };
        }

        // One `wmctrl -l` line: "0x02c00003  0 hostname  Title text here".
        // id/desktop/host are single tokens; the title is everything after
        // and may itself contain spaces, so it is captured greedily to the
        // end of the line rather than split on whitespace.
        private static readonly Regex WmctrlListLine =
            new Regex(@"^(0x[0-9a-fA-F]+)\s+-?\d+\s+\S+\s+(.*)$");

        /// <summary>
        /// Finds the hex window id of the first `wmctrl -l` line whose
        /// title contains <paramref name="titleContains"/> (case-
        /// insensitive), or null when none matched / wmctrl produced
        /// nothing usable. The caller's usual title is PlayerSettings.
        /// productName, since the Unity Editor's main window title starts
        /// with the project name.
        /// </summary>
        public static string FindWindowId(string wmctrlListOutput, string titleContains)
        {
            if (string.IsNullOrEmpty(wmctrlListOutput) || string.IsNullOrEmpty(titleContains))
            {
                return null;
            }
            string[] lines = wmctrlListOutput.Replace("\r", string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = WmctrlListLine.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }
                if (match.Groups[2].Value.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return match.Groups[1].Value;
                }
            }
            return null;
        }

        /// <summary>
        /// `sh -c` exit code the wrapper below uses when `command -v wmctrl`
        /// fails, so the Tool can tell "not on PATH" apart from "wmctrl ran
        /// and failed" without parsing stderr text.
        /// </summary>
        public const int LinuxWmctrlMissingExitCode = 3;

        /// <summary>
        /// Wraps a wmctrl argv in a `command -v` presence check, run
        /// through /bin/sh -c like every other Linux helper in this
        /// package (ClipboardImageReader's xclip/wl-paste script) so PATH
        /// resolution is the shell's job, not .NET's -- the same reason
        /// that file never asks Process.Start to find a bare command name
        /// itself. wmctrlArgs are argv tokens with no embedded whitespace
        /// (a flag or a "0x..." window id), so joining with a single space
        /// needs no quoting.
        /// </summary>
        public static string BuildLinuxWmctrlShellCommand(string[] wmctrlArgs)
        {
            return "if command -v wmctrl >/dev/null 2>&1; then wmctrl " + string.Join(" ", wmctrlArgs)
                + "; else exit " + LinuxWmctrlMissingExitCode + "; fi";
        }

        /// <summary>
        /// Win32/CRT command-line quoting (CommandLineToArgvW rules) for
        /// building a single ProcessStartInfo.Arguments string from argv
        /// tokens -- the same algorithm AgentClient.QuoteArg uses for the
        /// CLI's own argv, kept as an independent copy here (rather than a
        /// cross-file call into Integration/AgentClient.cs) so this Policy
        /// file has no dependency the smoke-test project would also need
        /// to add. .NET's ProcessStartInfo.Arguments parser follows these
        /// Windows conventions on every platform (it is parsed by the
        /// .NET runtime itself, not by the OS shell), so the same
        /// algorithm is correct for the osascript/wmctrl argv above too.
        /// </summary>
        public static string QuoteArgument(string value)
        {
            if (value == null)
            {
                value = string.Empty;
            }
            if (!NeedsQuoting(value))
            {
                return value;
            }
            var sb = new StringBuilder();
            sb.Append('"');
            int backslashRun = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    backslashRun++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashRun * 2 + 1);
                    sb.Append('"');
                    backslashRun = 0;
                    continue;
                }
                if (backslashRun > 0)
                {
                    sb.Append('\\', backslashRun);
                    backslashRun = 0;
                }
                sb.Append(c);
            }
            if (backslashRun > 0)
            {
                sb.Append('\\', backslashRun * 2);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Whether a token needs quoting to survive re-splitting: a space/tab, an embedded quote, or any other control character.</summary>
        public static bool NeedsQuoting(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == ' ' || c < ' ')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Joins argv tokens into one ProcessStartInfo.Arguments string, quoting each as needed.</summary>
        public static string JoinArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(QuoteArgument(args[i]));
            }
            return sb.ToString();
        }
    }
}

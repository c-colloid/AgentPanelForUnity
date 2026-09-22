using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The rules behind the "let the agent use uloop" switch
    /// (docs/design-notes/2026-09-21-uloop-always-loaded-cost.md section 5,
    /// option A). Unity-free, so the same rules run in the license-free
    /// smoke gate as well as under the Editor.
    ///
    /// What the switch is, and what it deliberately is NOT. OFF stops the
    /// AGENT from reaching for `uloop`: the steering text says the commands
    /// are refused, the spawn adds deny patterns for every shell tool, and
    /// the permission layer refuses a uloop command that slips past both.
    /// It does NOT stop uLoop itself -- that package's editor server, its
    /// 16 ms tick pump and its skill files keep running, because none of
    /// them is this panel's to switch off (section 4 of the same note).
    /// Naming the switch honestly is the whole reason it is not called
    /// "disable uLoop".
    ///
    /// Three layers rather than one, for a measured reason: the 2026-08-02
    /// script-gate work found that a `--disallowedTools` pattern can be
    /// accepted and then quietly ignored by the CLI (design section 8.7,
    /// case A1), so a deny list alone is not a guarantee. The steering line
    /// keeps the agent from trying in the first place, the deny patterns
    /// stop the common case at the CLI, and the can_use_tool refusal is the
    /// one layer this package controls end to end.
    /// </summary>
    public static class UloopAgentUsePolicy
    {
        /// <summary>The command this switch is about.</summary>
        public const string CommandName = "uloop";

        /// <summary>
        /// Shown to the agent when a uloop command is refused. Names the
        /// replacement tools and how a HUMAN turns the switch back on --
        /// never implies the agent could flip it itself.
        /// </summary>
        public const string DenyMessage =
            "uloop commands are turned off for the agent in this project (Unity Agent Panel > Settings >"
            + " uLoop integration). Use the uap_* tools instead: uap_play_mode to run the scene,"
            + " uap_console_logs for Console output, uap_scripts_commit for C# changes, and the scene /"
            + " component / property / asset tools for edits. If something genuinely needs uloop, say so"
            + " and let the user decide -- do not work around this with another shell.";

        /// <summary>
        /// Pure: true when <paramref name="command"/> invokes uloop in any
        /// of its segments. Reuses ScriptGate's shell splitter (quotes,
        /// pipes, ';', '&amp;', newlines) rather than growing a second
        /// parser, so `foo &amp;&amp; uloop compile`, `... | uloop`, and
        /// PowerShell's `&amp; uloop` are all caught.
        /// </summary>
        public static bool IsUloopInvocation(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return false;
            }
            foreach (string[] segment in ScriptGate.CommandSegments(command))
            {
                if (segment.Length > 0 && IsUloopExecutable(segment[0]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: true for the token shapes that name the uloop binary --
        /// "uloop", "uloop.exe", "./uloop", "/usr/local/bin/uloop",
        /// "C:\tools\uloop.cmd". Case-insensitive: Windows paths are, and a
        /// deny that "ULOOP" walks past would be worse than no deny.
        /// </summary>
        public static bool IsUloopExecutable(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            string name = token.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                string extension = name.Substring(dot);
                if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, dot);
                }
            }
            return string.Equals(name, CommandName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Pure: the deny entries added to `--disallowedTools` while the
        /// switch is off -- an exact form and a wildcard form per shell tool
        /// (Bash / PowerShell / Shell, single-sourced from ScriptGate). Both
        /// forms ship because the wildcard grammar is unmeasured, the same
        /// reason the uloop ALLOW preset writes both.
        /// </summary>
        public static List<string> BuildDisallowedPatterns()
        {
            var result = new List<string>();
            foreach (string shell in ScriptGate.ShellToolNameList)
            {
                result.Add(shell + "(" + CommandName + ")");
                result.Add(shell + "(" + CommandName + " *)");
            }
            return result;
        }

        /// <summary>
        /// Pure: the `--disallowedTools` list a spawn should carry. With the
        /// switch ON this is the user's list verbatim; with it OFF the deny
        /// patterns are appended, skipping any the user already wrote, and
        /// the user's own entries keep their order and position.
        /// Never mutates <paramref name="configured"/>: the switch is a
        /// spawn-time overlay, not an edit of the list the user owns.
        /// </summary>
        public static List<string> ComposeDisallowedTools(IEnumerable<string> configured,
            bool agentUseEnabled)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configured != null)
            {
                foreach (string entry in configured)
                {
                    if (entry == null)
                    {
                        continue;
                    }
                    result.Add(entry);
                    seen.Add(entry.Trim());
                }
            }
            if (agentUseEnabled)
            {
                return result;
            }
            foreach (string pattern in BuildDisallowedPatterns())
            {
                if (seen.Add(pattern))
                {
                    result.Add(pattern);
                }
            }
            return result;
        }

        /// <summary>Longest command text the transcript note carries; longer commands keep their head.</summary>
        public const int NoteCommandMaxChars = 120;

        /// <summary>
        /// Pure: the command as the transcript note should show it -- one
        /// line, trimmed, and cut at <see cref="NoteCommandMaxChars"/> so a
        /// pasted multi-line script cannot push the rest of the note off
        /// screen. Empty input yields an empty string rather than "null".
        /// </summary>
        public static string DescribeCommandForNote(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return string.Empty;
            }
            string single = command.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (single.Length <= NoteCommandMaxChars)
            {
                return single;
            }
            return single.Substring(0, NoteCommandMaxChars) + "...";
        }

        /// <summary>
        /// Pure: the steering sentence about uloop. ON keeps the 2026-08-02
        /// wording (a sanctioned but slower escape hatch); OFF says plainly
        /// that the commands are refused, because an agent that learns this
        /// from a denial has already burned a turn -- and one that does not
        /// learn it at all keeps trying.
        /// </summary>
        public static string SteeringLine(bool agentUseEnabled)
        {
            if (agentUseEnabled)
            {
                return "uloop or raw dynamic code is for what those tools cannot express"
                    + " -- it works, but is the slower, confirmation-heavy path.\n";
            }
            return "Do NOT call uloop: this project has turned it off for the agent, and the commands are"
                + " refused. Everything above is available without it; if a task truly needs uloop, say so"
                + " instead of trying another shell.\n";
        }
    }
}

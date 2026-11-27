using System;
using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The Unity-free decisions around request checkpoints (design note
    /// docs/design-notes/2026-11-23-request-checkpoints.md): which CLI
    /// tools the pre-write hook watches, where their target path sits in
    /// the tool input, which tools leave changes nothing can track, and
    /// the wording the model gets after a revert. Compiled verbatim into
    /// ci/SmokeTests.
    /// </summary>
    public static class UapCheckpointPolicy
    {
        /// <summary>
        /// Matcher of the PreToolUse hook the panel registers over the
        /// control protocol: the CLI's own file-writing tools. Measured
        /// 2026-11-23 against CLI 2.1.330 -- the callback arrives before
        /// the write in every permission mode, acceptEdits included, and
        /// the CLI waits for the answer.
        ///
        /// The shell tools are in the matcher for a different reason: a
        /// command line is not a file path, but its text arrives here
        /// before it runs, which is the moment to copy the files it names
        /// (<see cref="ShellTargets"/>) and to take the "before" picture
        /// of the project that the after-the-turn comparison needs.
        /// </summary>
        public const string HookMatcher = "Write|Edit|MultiEdit|NotebookEdit|Bash|PowerShell";

        /// <summary>callback_id the panel registers and expects back.</summary>
        public const string HookCallbackId = "uap_checkpoint";

        private const int TitleMaxChars = 80;

        /// <summary>
        /// The file a CLI file-writing tool is about to change, read from
        /// the hook's tool_input: file_path for Write / Edit / MultiEdit,
        /// notebook_path for NotebookEdit. Null for any other tool or a
        /// missing path.
        /// </summary>
        public static string FilePathOfToolInput(string toolName, JsonNode toolInput)
        {
            if (string.IsNullOrEmpty(toolName) || toolInput == null || !toolInput.IsObject)
            {
                return null;
            }
            string path;
            switch (toolName)
            {
                case "Write":
                case "Edit":
                case "MultiEdit":
                    path = toolInput["file_path"].AsString(null);
                    break;
                case "NotebookEdit":
                    path = toolInput["notebook_path"].AsString(null);
                    if (string.IsNullOrEmpty(path))
                    {
                        path = toolInput["file_path"].AsString(null);
                    }
                    break;
                default:
                    return null;
            }
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// True for the agents' shell tools: whatever a command line does
        /// to the disk is invisible to the checkpoint, so a request that
        /// ran one says so when it is reverted.
        /// </summary>
        public static bool IsShellTool(string wireToolName)
        {
            switch (wireToolName)
            {
                case "Bash":
                case "PowerShell":
                case "Shell":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The command line of a shell tool's input ("command", or
        /// "script" as some agents name it). Null for any other tool or
        /// an empty command.
        /// </summary>
        public static string ShellCommandOfToolInput(string toolName, JsonNode toolInput)
        {
            if (!IsShellTool(toolName) || toolInput == null || !toolInput.IsObject)
            {
                return null;
            }
            string command = toolInput["command"].AsString(null);
            if (string.IsNullOrEmpty(command))
            {
                command = toolInput["script"].AsString(null);
            }
            return string.IsNullOrEmpty(command) ? null : command;
        }

        private static readonly HashSet<string> RemoveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rm", "del", "erase", "unlink", "rmdir", "rd", "remove-item", "ri"
        };

        private static readonly HashSet<string> MoveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mv", "move", "move-item", "mi", "rename-item", "ren", "rni"
        };

        /// <summary>
        /// The files a shell command is likely to overwrite, move or
        /// delete, read off its text: ScriptGate's write-destination scan
        /// plus the arguments of the remove and move commands (a move
        /// takes its source away, which the gate has no reason to care
        /// about). Best effort by nature -- a path built from a variable,
        /// a script that writes on its own, or a command behind "cd" is
        /// not seen, and a candidate may not be a real path at all -- so
        /// the caller copies only candidates that exist and records
        /// nothing as "created" from this list. Wildcards are returned as
        /// written.
        /// </summary>
        public static List<string> ShellTargets(string command)
        {
            var targets = new List<string>();
            if (string.IsNullOrEmpty(command))
            {
                return targets;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string target in ScriptGate.ExtractShellWriteTargets(command))
            {
                AddTarget(targets, seen, target);
            }
            foreach (string[] tokens in ScriptGate.CommandSegments(command))
            {
                if (tokens.Length < 2)
                {
                    continue;
                }
                bool remove = RemoveCommands.Contains(tokens[0]);
                if (!remove && !MoveCommands.Contains(tokens[0]))
                {
                    continue;
                }
                for (int i = 1; i < tokens.Length; i++)
                {
                    string token = tokens[i];
                    // Flags: "-rf", "--force", "/q", "-Recurse", "-Path:x".
                    if (token.Length == 0 || token[0] == '-')
                    {
                        int colon = token.IndexOf(':');
                        if (colon > 0 && colon < token.Length - 1)
                        {
                            AddTarget(targets, seen, token.Substring(colon + 1));
                        }
                        continue;
                    }
                    if (token.Length == 2 && token[0] == '/')
                    {
                        continue;
                    }
                    AddTarget(targets, seen, token);
                }
            }
            return targets;
        }

        private static void AddTarget(List<string> targets, HashSet<string> seen, string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return;
            }
            target = target.Trim();
            // Not paths: a variable, a device, a file descriptor.
            if (target.Length == 0 || target[0] == '$' || target[0] == '&'
                || target == "/dev/null" || string.Equals(target, "nul", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (seen.Add(target))
            {
                targets.Add(target);
            }
        }

        /// <summary>
        /// True for the UapOps tools that run arbitrary editor code: their
        /// effects may or may not go through the asset pipeline the
        /// recorder watches, so they are named as untracked.
        /// </summary>
        public static bool IsArbitraryEditorTool(string uapToolName)
        {
            switch (uapToolName)
            {
                case "uap_execute_code":
                case "uap_editor_execute_menu":
                    return true;
                default:
                    return false;
            }
        }

        private const string MetaSuffix = ".meta";

        /// <summary>
        /// True when <paramref name="path"/> is something the user would
        /// call one of the request's files. Not a folder (a revert removes
        /// an emptied folder as a consequence, nobody asked about it), and
        /// not the .meta Unity keeps next to an asset that is itself in
        /// the checkpoint -- "Door.cs" and "Door.cs.meta" are one file to
        /// the person reading the card. A .meta on its own (only the
        /// import settings changed) does count.
        ///
        /// The ONE rule behind every number shown for a checkpoint: the
        /// confirmation card, the result note and what the model is told
        /// all go through here, so they cannot disagree (design note
        /// 2026-11-23-request-checkpoints.md section 11.3).
        /// </summary>
        public static bool IsDisplayFile(UapCheckpoint checkpoint, string path)
        {
            if (string.IsNullOrEmpty(path) || path[path.Length - 1] == '/')
            {
                return false;
            }
            if (checkpoint != null)
            {
                UapCheckpointEntry entry = checkpoint.Find(path);
                if (entry != null && entry.IsDirectory)
                {
                    return false;
                }
                if (path.Length > MetaSuffix.Length && path.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase)
                    && checkpoint.Find(path.Substring(0, path.Length - MetaSuffix.Length)) != null)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The entries of <paramref name="paths"/> that are <see cref="IsDisplayFile"/>, in order.</summary>
        public static List<string> DisplayFiles(UapCheckpoint checkpoint, IList<string> paths)
        {
            var result = new List<string>();
            if (paths == null)
            {
                return result;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                if (IsDisplayFile(checkpoint, paths[i]))
                {
                    result.Add(paths[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Splits a project-relative path for display: the file name is
        /// what tells two rows apart, so it is shown whole and first; the
        /// folder follows and may be cut short.
        /// </summary>
        public static void SplitPath(string path, out string name, out string directory)
        {
            path = path ?? string.Empty;
            int slash = path.LastIndexOf('/');
            name = slash >= 0 ? path.Substring(slash + 1) : path;
            directory = slash > 0 ? path.Substring(0, slash) : string.Empty;
        }

        /// <summary>A request title short enough to quote inside a one-line note (40 characters).</summary>
        public static string ShortTitle(string title)
        {
            const int Max = 40;
            title = title ?? string.Empty;
            return title.Length <= Max ? title : title.Substring(0, Max - 3) + "...";
        }

        /// <summary>The user's request as a one-line label: first line, trimmed, at most 80 characters.</summary>
        public static string ComposeTitle(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string trimmed = text.Trim();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0)
            {
                trimmed = trimmed.Substring(0, newline).TrimEnd();
            }
            if (trimmed.Length > TitleMaxChars)
            {
                trimmed = trimmed.Substring(0, TitleMaxChars - 3) + "...";
            }
            return trimmed;
        }

        /// <summary>"Bash x3, uap_execute_code" -- the untracked tools of a request, most used first.</summary>
        public static string DescribeUntracked(IDictionary<string, int> untracked)
        {
            if (untracked == null || untracked.Count == 0)
            {
                return string.Empty;
            }
            var pairs = new List<KeyValuePair<string, int>>(untracked);
            pairs.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            var sb = new StringBuilder();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(pairs[i].Key);
                if (pairs[i].Value > 1)
                {
                    sb.Append(" x").Append(pairs[i].Value);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// What the model is told, ahead of the user's next message, about
        /// a revert it did not see happen. Without it the model goes on
        /// believing its files and objects exist. English and ASCII on
        /// purpose: it is model input, not UI text.
        /// </summary>
        public static string ComposeModelNotice(string title, int restored, int removed,
            bool sceneUndone, bool sceneUndoUnavailable, int failed, string untracked)
        {
            var sb = new StringBuilder();
            sb.Append("[Unity Agent Panel] The user reverted your earlier request");
            if (!string.IsNullOrEmpty(title))
            {
                sb.Append(" (\"").Append(title.Replace("\"", "'")).Append("\")");
            }
            sb.Append(" with the panel's Revert button. ");
            sb.Append(restored).Append(" file(s) were put back to their content from before that request and ");
            sb.Append(removed).Append(" file(s) it created were removed.");
            if (sceneUndone)
            {
                sb.Append(" Its scene edits were undone through Unity's Undo.");
            }
            else if (sceneUndoUnavailable)
            {
                sb.Append(" Its scene edits could NOT be undone and are still in the scene.");
            }
            if (failed > 0)
            {
                sb.Append(" ").Append(failed).Append(" file(s) could not be put back.");
            }
            if (!string.IsNullOrEmpty(untracked))
            {
                sb.Append(" Changes made through these tools were not tracked and may remain: ").Append(untracked).Append(".");
            }
            sb.Append(" Do not assume the work of that request still exists: re-read files and query the scene before you rely on them.");
            return sb.ToString();
        }
    }
}

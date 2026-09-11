using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Pure helpers behind the composer's slash-command input (design note
    /// docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 1): parsing the CLI's command catalog, recognizing a typed
    /// command, and filtering the catalog for the suggestion popup. No
    /// Unity dependencies; every method is EditMode-tested directly.
    ///
    /// The wire contract this rests on: in --input-format stream-json the
    /// CLI treats a user message whose text is "/name args" as the slash
    /// command "name" (Agent SDK "Slash commands"); the panel therefore
    /// sends the typed text verbatim and never invents its own command
    /// syntax. Two commands get panel-side treatment on top:
    /// - "/clear" is handled LOCALLY (AgentHub.StartFresh, the same thing
    ///   the New chat button does): the CLI's own /clear would start a new
    ///   session id underneath a panel that still believes it is showing
    ///   the old one.
    /// - "/compact" goes to the CLI; the resulting system/compact_boundary
    ///   is what the panel reacts to (section 2).
    /// </summary>
    public static class SlashCommandCatalog
    {
        public const string ClearCommand = "clear";
        public const string CompactCommand = "compact";

        /// <summary>Upper bound on rows the suggestion popup shows.</summary>
        public const int MaxSuggestions = 8;

        // -- Catalog construction -----------------------------------------------

        /// <summary>
        /// Maps the initialize control_response commands[] array
        /// ([{name, description, argumentHint}, ...]). Tolerant: a
        /// non-array node, non-object items and items without a name are
        /// skipped; a leading slash in a name is stripped so "/compact" and
        /// "compact" are the same entry. Never returns null.
        /// </summary>
        public static List<SlashCommandEntry> ParseInitializeCommands(JsonNode commandsNode)
        {
            var result = new List<SlashCommandEntry>();
            if (commandsNode == null || !commandsNode.IsArray)
            {
                return result;
            }
            foreach (JsonNode item in commandsNode.Items)
            {
                if (!item.IsObject)
                {
                    continue;
                }
                string name = NormalizeName(item["name"].AsString(string.Empty));
                if (name.Length == 0)
                {
                    continue;
                }
                result.Add(new SlashCommandEntry
                {
                    name = name,
                    description = item["description"].AsString(string.Empty) ?? string.Empty,
                    argumentHint = item["argumentHint"].AsString(string.Empty) ?? string.Empty
                });
            }
            return result;
        }

        /// <summary>
        /// Combines the rich initialize catalog with system/init's
        /// names-only slash_commands[]: rich entries win (they carry the
        /// description), names the rich list does not know are appended
        /// with empty metadata. Duplicates (ordinal-ignore-case) collapse.
        /// Either input may be null. Never returns null.
        /// </summary>
        public static List<SlashCommandEntry> Merge(IList<SlashCommandEntry> rich, string[] names)
        {
            var result = new List<SlashCommandEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rich != null)
            {
                for (int i = 0; i < rich.Count; i++)
                {
                    SlashCommandEntry entry = rich[i];
                    if (entry == null)
                    {
                        continue;
                    }
                    string name = NormalizeName(entry.name);
                    if (name.Length == 0 || !seen.Add(name))
                    {
                        continue;
                    }
                    result.Add(new SlashCommandEntry
                    {
                        name = name,
                        description = entry.description ?? string.Empty,
                        argumentHint = entry.argumentHint ?? string.Empty
                    });
                }
            }
            if (names != null)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    string name = NormalizeName(names[i]);
                    if (name.Length == 0 || !seen.Add(name))
                    {
                        continue;
                    }
                    result.Add(new SlashCommandEntry { name = name });
                }
            }
            return result;
        }

        /// <summary>
        /// The catalog the composer actually offers: the cached CLI list
        /// plus the two commands the panel guarantees regardless of what
        /// the CLI reported ("/compact" and "/clear" are supported by every
        /// SDK-mode CLI this panel targets, and "/clear" is handled locally
        /// anyway). "/clear" ALWAYS carries the panel's own description --
        /// it is the panel's New chat, not the CLI's -- while "/compact"
        /// keeps the CLI's description when one was reported. The
        /// guaranteed two are inserted at the front, in that order, so a
        /// bare "/" shows them first. Never returns null and never mutates
        /// its input.
        /// </summary>
        public static List<SlashCommandEntry> WithBuiltins(IList<SlashCommandEntry> catalog,
            string compactDescription, string clearDescription)
        {
            var result = new List<SlashCommandEntry>();
            SlashCommandEntry compact = Find(catalog, CompactCommand);
            result.Add(new SlashCommandEntry
            {
                name = CompactCommand,
                description = compact != null && !string.IsNullOrEmpty(compact.description)
                    ? compact.description : (compactDescription ?? string.Empty),
                argumentHint = compact != null ? (compact.argumentHint ?? string.Empty) : string.Empty
            });
            result.Add(new SlashCommandEntry
            {
                name = ClearCommand,
                description = clearDescription ?? string.Empty
            });
            if (catalog != null)
            {
                for (int i = 0; i < catalog.Count; i++)
                {
                    SlashCommandEntry entry = catalog[i];
                    if (entry == null || string.IsNullOrEmpty(entry.name))
                    {
                        continue;
                    }
                    if (IsName(entry.name, CompactCommand) || IsName(entry.name, ClearCommand))
                    {
                        continue;
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        // -- Typed-text recognition -------------------------------------------------

        /// <summary>
        /// True when <paramref name="text"/> is a slash-command message:
        /// after leading whitespace, a '/' immediately followed by at least
        /// one command character. <paramref name="name"/> is the command
        /// (no slash), <paramref name="args"/> the trimmed remainder (may
        /// be empty; may span lines). "/" alone, "/ foo", "//", a path like
        /// "/usr/bin" ('/' is not a command character) and ordinary prose
        /// are NOT commands, so a message that merely starts with a slash
        /// for another reason still goes out as plain text.
        /// </summary>
        public static bool TryParse(string text, out string name, out string args)
        {
            name = string.Empty;
            args = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            if (i >= text.Length || text[i] != '/')
            {
                return false;
            }
            int start = i + 1;
            int end = start;
            while (end < text.Length && IsCommandChar(text[end]))
            {
                end++;
            }
            if (end == start)
            {
                return false;
            }
            // The name must be followed by whitespace or the end of the
            // text: "/compact:x" or "/usr/bin" is not a command.
            if (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                return false;
            }
            name = text.Substring(start, end - start);
            args = text.Substring(end).Trim();
            return true;
        }

        /// <summary>
        /// The suggestion popup's trigger: true while the WHOLE text is a
        /// slash followed by zero or more command characters and nothing
        /// else ("/", "/co", "/compact") -- i.e. the user is still typing
        /// the command name. A space, a newline, or any other character
        /// after the name ends the prefix mode (the user is typing
        /// arguments now). <paramref name="prefix"/> is the name typed so
        /// far, without the slash (empty for a bare "/").
        /// </summary>
        public static bool TryGetTypedPrefix(string text, out string prefix)
        {
            prefix = string.Empty;
            if (string.IsNullOrEmpty(text) || text[0] != '/')
            {
                return false;
            }
            for (int i = 1; i < text.Length; i++)
            {
                if (!IsCommandChar(text[i]))
                {
                    return false;
                }
            }
            prefix = text.Substring(1);
            return true;
        }

        /// <summary>
        /// Catalog rows for the popup: names starting with
        /// <paramref name="prefix"/> first (catalog order), then names
        /// merely containing it, ordinal-ignore-case, capped at
        /// <see cref="MaxSuggestions"/>. An empty prefix lists the catalog
        /// head. Never returns null.
        /// </summary>
        public static List<SlashCommandEntry> Filter(IList<SlashCommandEntry> catalog, string prefix)
        {
            var result = new List<SlashCommandEntry>();
            if (catalog == null)
            {
                return result;
            }
            prefix = prefix ?? string.Empty;
            for (int i = 0; i < catalog.Count && result.Count < MaxSuggestions; i++)
            {
                SlashCommandEntry entry = catalog[i];
                if (entry != null && !string.IsNullOrEmpty(entry.name)
                    && entry.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(entry);
                }
            }
            if (prefix.Length == 0)
            {
                return result;
            }
            for (int i = 0; i < catalog.Count && result.Count < MaxSuggestions; i++)
            {
                SlashCommandEntry entry = catalog[i];
                if (entry != null && !string.IsNullOrEmpty(entry.name)
                    && !result.Contains(entry)
                    && entry.name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        /// The field text after accepting <paramref name="entry"/>:
        /// "/name " -- with a trailing space so the caret is ready for
        /// arguments and the popup (prefix mode) closes. Sending trims it
        /// back to "/name".
        /// </summary>
        public static string CompleteText(SlashCommandEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name))
            {
                return string.Empty;
            }
            return "/" + entry.name + " ";
        }

        /// <summary>
        /// Enter-on-popup rule: Enter COMPLETES the highlighted entry
        /// (instead of sending) only while the typed prefix is not already
        /// exactly that entry's name -- once the user has typed (or
        /// completed) the full name, Enter sends it. Keeps a no-argument
        /// command at two keystrokes ("/comp" Enter Enter) without ever
        /// sending something the user has not seen spelled out.
        /// </summary>
        public static bool ShouldCompleteOnEnter(string typedPrefix, SlashCommandEntry selected)
        {
            if (selected == null || string.IsNullOrEmpty(selected.name))
            {
                return false;
            }
            return !string.Equals(typedPrefix, selected.name, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsClear(string name)
        {
            return IsName(name, ClearCommand);
        }

        public static bool IsCompact(string name)
        {
            return IsName(name, CompactCommand);
        }

        // -- Internals -----------------------------------------------------------------

        private static bool IsName(string name, string expected)
        {
            return string.Equals(NormalizeName(name), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static SlashCommandEntry Find(IList<SlashCommandEntry> catalog, string name)
        {
            if (catalog == null)
            {
                return null;
            }
            for (int i = 0; i < catalog.Count; i++)
            {
                if (catalog[i] != null && IsName(catalog[i].name, name))
                {
                    return catalog[i];
                }
            }
            return null;
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            name = name.Trim();
            if (name.StartsWith("/", StringComparison.Ordinal))
            {
                name = name.Substring(1);
            }
            return name;
        }

        /// <summary>
        /// Characters a command name may contain: letters, digits, '-',
        /// '_' and ':' (namespaced skill commands like "plugin:skill").
        /// </summary>
        private static bool IsCommandChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':';
        }
    }
}

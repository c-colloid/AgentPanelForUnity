using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure, Unity-free text helpers behind the compact tools/list gateway
    /// (design note docs/design-notes/2026-09-22-uloop-phase1.md section
    /// 2.8 A'+B): reducing a tool's full <see cref="IUapTool.Description"/>
    /// to a one-line summary for <c>uap_tools</c>' search results, and
    /// matching that summary (plus the tool's name/module) against a search
    /// query. No UnityEngine/UnityEditor reference on purpose, so both are
    /// compiled verbatim into ci/SmokeTests (see UapToolSummarySmoke) --
    /// the same split UapGameViewSizePolicy/UapPlayModePolicy already use
    /// for the rest of this package's argument/text rules.
    /// </summary>
    public static class UapToolSummary
    {
        /// <summary>
        /// Approximate cap (design section 2.8 B): long enough that most
        /// tool descriptions' real first sentence fits whole (the shortest
        /// ones are one clause, the longest single sentences in this
        /// package run to a few hundred characters), short enough that a
        /// uap_tools search result list of a dozen matches stays skimmable.
        /// "Approximate" because the cut point still snaps to a word
        /// boundary rather than a hard character count.
        /// </summary>
        public const int MaxChars = 160;

        /// <summary>
        /// Word endings that carry a '.' without ending a sentence, matched
        /// case-insensitively against the token immediately before a
        /// candidate terminator. Deliberately short -- this package's own
        /// tool descriptions only ever use "e.g." and "i.e." this way, and
        /// "etc." appears at the end of a clause often enough (", etc.)")
        /// that treating it as non-terminating too is the safer default.
        /// </summary>
        private static readonly string[] NonTerminatingAbbreviations = { "e.g", "i.e", "etc" };

        /// <summary>
        /// The first sentence of <paramref name="description"/>, trimmed to
        /// at most <see cref="MaxChars"/> characters. Pure string scanning,
        /// no regex (this package's convention for hot, small helpers):
        /// <list type="bullet">
        /// <item>Runs of whitespace (this package's descriptions are built
        /// from several C# string literals concatenated with '+', which
        /// sometimes leaves doubled spaces at the seams) collapse to one
        /// space first.</item>
        /// <item>A '.', '!' or '?' ends the sentence only when followed by
        /// whitespace or the end of the string -- so it never fires inside
        /// a dotted identifier like "EditorApplication.isPlaying" or a path
        /// like "Assets/**/*.cs", neither of which is followed by a
        /// space.</item>
        /// <item>A '.' that WOULD otherwise end the sentence still does not
        /// when the word immediately before it is one of
        /// <see cref="NonTerminatingAbbreviations"/> ("e.g. the Game
        /// View..." reads on) or when it sits between two digits ("Unity
        /// 2022.3" is a version number, not two sentences).</item>
        /// <item>No terminator found at all: the whole (whitespace-
        /// collapsed) string is treated as "the first sentence" -- the
        /// length cap below still applies.</item>
        /// </list>
        /// The result is then capped at <see cref="MaxChars"/>, cutting at
        /// the last word boundary at or before the limit (never mid-word)
        /// and trimming a trailing "--" or lone "-" left dangling by the
        /// cut before appending "..." -- this package's descriptions use
        /// " -- " for asides constantly, and a summary ending in a single
        /// hyphen reads as a cut string, not a sentence.
        /// </summary>
        public static string FirstSentence(string description)
        {
            return FirstSentence(description, MaxChars);
        }

        /// <summary>
        /// <see cref="FirstSentence(string)"/> with the length cap chosen by
        /// the caller (a field description in a lean schema is held to a
        /// shorter line than a tool's uap_tools summary -- see
        /// <see cref="UapLeanSchema"/>). Every other rule is identical; a
        /// <paramref name="maxChars"/> below 8 is raised to 8 so the cut
        /// (a word plus the "..." ellipsis) always has room to work.
        /// </summary>
        public static string FirstSentence(string description, int maxChars)
        {
            return LeadCore(description, maxChars, false);
        }

        /// <summary>
        /// The one-line form a lean input schema gives a FIELD description
        /// (<see cref="UapLeanSchema"/>): <see cref="FirstSentence(string, int)"/>
        /// with two refinements, both for text that sits next to a field name
        /// rather than standing alone. (1) A first sentence that is only a
        /// label -- one word ("Required."), a scope tag ("start only.",
        /// "action:set, optional.", "1D only.") -- explains nothing, so the
        /// following sentence is appended (repeatedly, while the last one
        /// added is still such a label). (2) When the length cap cuts inside
        /// an open "(", "[", "{" or quote, the cut backs up to just before
        /// that opener (when at least <see cref="MinKeepChars"/> remain), so
        /// the line never ends in a dangling "(e.g...." or half a quoted
        /// value.
        /// </summary>
        public static string LeadText(string description, int maxChars)
        {
            return LeadCore(description, maxChars, true);
        }

        private static string LeadCore(string description, int maxChars, bool field)
        {
            if (string.IsNullOrEmpty(description))
            {
                return string.Empty;
            }
            string collapsed = CollapseWhitespace(description.Trim());
            if (collapsed.Length == 0)
            {
                return string.Empty;
            }

            int end = SentenceEnd(collapsed, 0);
            if (field)
            {
                int start = 0;
                // At most three extensions: a run of labels is not worth chasing.
                for (int step = 0; step < 3 && end < collapsed.Length
                    && IsLabelSentence(collapsed.Substring(start, end - start)); step++)
                {
                    start = end + 1;
                    end = SentenceEnd(collapsed, start);
                }
            }

            return CapLength(collapsed.Substring(0, end), maxChars < MinCapChars ? MinCapChars : maxChars, field);
        }

        /// <summary>
        /// The exclusive end index of the sentence that starts at
        /// <paramref name="from"/> in whitespace-collapsed
        /// <paramref name="collapsed"/> -- just past its terminator, or the
        /// text length when none is found. The terminator rules are those
        /// documented on <see cref="FirstSentence(string)"/>.
        /// </summary>
        private static int SentenceEnd(string collapsed, int from)
        {
            for (int i = from; i < collapsed.Length; i++)
            {
                char c = collapsed[i];
                if (c != '.' && c != '!' && c != '?')
                {
                    continue;
                }
                bool followedByBoundary = i + 1 >= collapsed.Length || char.IsWhiteSpace(collapsed[i + 1]);
                if (!followedByBoundary)
                {
                    continue;
                }
                if (c == '.' && IsDecimalPoint(collapsed, i))
                {
                    continue;
                }
                if (c == '.' && EndsWithAbbreviation(collapsed, i))
                {
                    continue;
                }
                return i + 1;
            }
            return collapsed.Length;
        }

        /// <summary>
        /// True when <paramref name="sentence"/> is a bare label: a single
        /// word, or one whose last word is "only", "required" or "optional"
        /// ("start only.", "action:set, optional."). Such a sentence says
        /// WHEN a field applies, never WHAT it is.
        /// </summary>
        private static bool IsLabelSentence(string sentence)
        {
            string text = sentence.Trim().TrimEnd('.', '!', '?', ':', ';', ',').TrimEnd();
            if (text.Length == 0)
            {
                return false;
            }
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                return true;
            }
            string last = text.Substring(lastSpace + 1);
            return string.Equals(last, "only", StringComparison.OrdinalIgnoreCase)
                || string.Equals(last, "required", StringComparison.OrdinalIgnoreCase)
                || string.Equals(last, "optional", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="tool"/>'s name, module or
        /// <see cref="FirstSentence"/> summary contains
        /// <paramref name="query"/> (case-insensitive substring, uap_tools'
        /// own matching rule -- design section 2.8 B). A null/empty query
        /// matches everything, so a caller can filter by module alone.
        /// </summary>
        public static bool MatchesQuery(string name, string module, string description, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }
            return ContainsIgnoreCase(name, query)
                || ContainsIgnoreCase(module, query)
                || ContainsIgnoreCase(FirstSentence(description), query);
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDecimalPoint(string text, int dotIndex)
        {
            return dotIndex > 0 && dotIndex + 1 < text.Length
                && char.IsDigit(text[dotIndex - 1]) && char.IsDigit(text[dotIndex + 1]);
        }

        private static bool EndsWithAbbreviation(string text, int dotIndex)
        {
            // The word immediately before the dot: walk back to the last
            // whitespace/'(' /'"' so "(e.g." and "\"e.g." still match.
            int wordStart = dotIndex;
            while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1])
                && text[wordStart - 1] != '(' && text[wordStart - 1] != '"')
            {
                wordStart--;
            }
            string word = text.Substring(wordStart, dotIndex - wordStart);
            for (int i = 0; i < NonTerminatingAbbreviations.Length; i++)
            {
                if (string.Equals(word, NonTerminatingAbbreviations[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CollapseWhitespace(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                    }
                    lastWasSpace = true;
                }
                else
                {
                    builder.Append(c);
                    lastWasSpace = false;
                }
            }
            return builder.ToString();
        }

        /// <summary>Appended when a sentence is cut short -- reserved out of <see cref="MaxChars"/> up front so the RESULT (word + ellipsis) never exceeds it.</summary>
        private const string Ellipsis = "...";

        /// <summary>Smallest cap <see cref="FirstSentence(string, int)"/> honours -- room for a short word plus <see cref="Ellipsis"/>.</summary>
        private const int MinCapChars = 8;

        /// <summary>Fewest characters <see cref="LeadText"/> keeps when it backs a cut up to before an open bracket or quote; a shorter remainder would explain nothing, so the plain cut stays.</summary>
        private const int MinKeepChars = 24;

        private static string CapLength(string sentence, int maxChars, bool balanceCut)
        {
            if (sentence.Length <= maxChars)
            {
                return sentence;
            }
            int budget = maxChars - Ellipsis.Length;
            int cut = sentence.LastIndexOf(' ', budget - 1);
            if (cut <= 0)
            {
                cut = budget;
            }
            string trimmed = sentence.Substring(0, cut).TrimEnd();
            if (balanceCut)
            {
                int open = FirstUnclosedOpener(trimmed);
                if (open >= MinKeepChars)
                {
                    trimmed = trimmed.Substring(0, open).TrimEnd(',', ':', ';', ' ');
                }
            }
            trimmed = trimmed.TrimEnd('-', ' ');
            return trimmed + Ellipsis;
        }

        /// <summary>
        /// Index of the earliest "(", "[", "{" or '"' in
        /// <paramref name="text"/> that is still open at its end, or -1 when
        /// everything is closed. A closer that does not match the innermost
        /// open one is ignored; apostrophes are never quotes (they are
        /// possessives far more often).
        /// </summary>
        private static int FirstUnclosedOpener(string text)
        {
            var chars = new System.Collections.Generic.List<char>();
            var positions = new System.Collections.Generic.List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char top = chars.Count > 0 ? chars[chars.Count - 1] : '\0';
                if (c == '"')
                {
                    if (top == '"')
                    {
                        chars.RemoveAt(chars.Count - 1);
                        positions.RemoveAt(positions.Count - 1);
                    }
                    else
                    {
                        chars.Add(c);
                        positions.Add(i);
                    }
                }
                else if (top != '"' && (c == '(' || c == '[' || c == '{'))
                {
                    chars.Add(c);
                    positions.Add(i);
                }
                else if (top != '"' && ((c == ')' && top == '(') || (c == ']' && top == '[') || (c == '}' && top == '{')))
                {
                    chars.RemoveAt(chars.Count - 1);
                    positions.RemoveAt(positions.Count - 1);
                }
            }
            return positions.Count > 0 ? positions[0] : -1;
        }
    }
}

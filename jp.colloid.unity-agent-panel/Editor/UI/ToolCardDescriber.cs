using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Tool name + input JSON -&gt; icon name + human one-line summary
    /// (the describeCommand table from R01, Unity edition). Pure static
    /// and Unity-API-free so the table is unit-testable; icon names are
    /// resolved (with silent fallback to the glyph) by IconLoader at
    /// render time, so unverified names are safe.
    /// </summary>
    public static class ToolCardDescriber
    {
        /// <summary>Result triple for one tool call.</summary>
        public struct Description
        {
            /// <summary>Built-in editor icon name (IconLoader.Find input).</summary>
            public string IconName;
            /// <summary>Text glyph used when the icon name does not resolve.</summary>
            public string FallbackGlyph;
            /// <summary>One-line human summary of the input.</summary>
            public string Summary;
        }

        private const int SummaryMaxChars = 60;
        private const int ArgHintMaxChars = 40;

        // Non-ASCII glyphs come from IconLoader -- the single guarded
        // registry of runtime-constructed UI glyphs (GlyphAuditTests).
        private static readonly string GlyphFile = IconLoader.GlyphFile;
        private static readonly string GlyphSearch = IconLoader.GlyphSearch;
        private static readonly string GlyphWeb = "@";
        private static readonly string GlyphShell = "$";
        private static readonly string GlyphTask = IconLoader.GlyphTask;
        private static readonly string GlyphDefault = IconLoader.GlyphBullet;
        private static readonly string GlyphQuestion = "?";
        private static readonly string GlyphSkill = IconLoader.GlyphSpark;
        private static readonly string GlyphMcp = IconLoader.GlyphGear;

        /// <summary>Builds the icon + summary for one tool_use record.</summary>
        public static Description Describe(string toolName, string inputJson)
        {
            return Describe(toolName, ParseInput(inputJson));
        }

        /// <summary>
        /// Same table, taking an already-parsed input node -- for callers
        /// that hold the request's JsonNode (PermissionCard) so the summary
        /// never round-trips through re-serialize + re-parse. Null or
        /// non-object nodes behave exactly like unparseable JSON in the
        /// string overload.
        /// </summary>
        public static Description Describe(string toolName, JsonNode inputNode)
        {
            string name = toolName ?? string.Empty;
            JsonNode input = inputNode ?? JsonNode.Null;

            if (Is(name, "Read") || Is(name, "Write") || Is(name, "Edit")
                || Is(name, "MultiEdit") || Is(name, "NotebookEdit"))
            {
                string path = FirstString(input, "file_path", "path", "notebook_path");
                return Make("d_TextAsset Icon", GlyphFile,
                    path != null ? FileName(path) : name);
            }
            if (Is(name, "Bash") || Is(name, "PowerShell") || Is(name, "Shell"))
            {
                string command = FirstString(input, "command", "script");
                return Make("d_UnityEditor.ConsoleWindow", GlyphShell,
                    command != null ? CommandSummary(command) : name);
            }
            if (Is(name, "Grep") || Is(name, "Glob"))
            {
                string pattern = FirstString(input, "pattern", "query");
                return Make("d_Search Icon", GlyphSearch, pattern ?? name);
            }
            if (Is(name, "WebFetch"))
            {
                string url = FirstString(input, "url");
                return Make("d_BuildSettings.Web.Small", GlyphWeb,
                    url != null ? HostOf(url) : name);
            }
            if (Is(name, "WebSearch"))
            {
                string query = FirstString(input, "query");
                return Make("d_Search Icon", GlyphWeb, query ?? name);
            }
            if (Is(name, "Task"))
            {
                string description = FirstString(input, "description", "prompt");
                return Make("d_UnityEditor.HierarchyWindow", GlyphTask,
                    description ?? name);
            }
            if (Is(name, "AskUserQuestion"))
            {
                return DescribeAskUserQuestion(input, name);
            }
            if (Is(name, "Skill"))
            {
                return DescribeSkill(input, name);
            }

            // uap_web_fetch (2026-09-17): the permission card must show
            // WHERE the request goes -- host first, then the path shortened
            // -- because the URL is the only safeguard against an agent
            // smuggling project data out in it (design note
            // 2026-09-17-web-fetch-tool.md section 5.1).
            if (name.EndsWith("uap_web_fetch", System.StringComparison.OrdinalIgnoreCase))
            {
                string url = FirstString(input, "url");
                return Make("d_BuildSettings.Web.Small", GlyphWeb,
                    url != null ? WebFetchSummary(url) : ShortenToolDisplayName(name));
            }

            if (name.EndsWith("uap_web_search", System.StringComparison.OrdinalIgnoreCase))
            {
                string query = FirstString(input, "query");
                return Make("d_Search Icon", GlyphWeb, query != null ? Truncate(query.Trim(), ArgHintMaxChars) : ShortenToolDisplayName(name));
            }

            // Unknown / MCP tools: generic row with whatever key is useful.
            // The name echo uses the SAME shortened form ToolActivityCard
            // puts in its header label (ShortenToolDisplayName): before,
            // an MCP call with no useful key echoed the raw wire name
            // ("mcp__unity-ops__uap_scripts_compile") while the header
            // showed "uap_scripts_compile", so the card's "don't repeat the
            // name" check never matched and the row rendered the plumbing
            // prefix a second time as if it were a summary.
            string generic = FirstString(input,
                "file_path", "path", "command", "pattern", "query", "url", "description");
            bool isMcp = IsMcpName(name);
            string echo = string.IsNullOrEmpty(name)
                ? L10n.S.ToolCardDefaultName
                : ShortenToolDisplayName(name);
            return Make(isMcp ? "d_SettingsIcon" : null, isMcp ? GlyphMcp : GlyphDefault,
                generic ?? echo);
        }

        /// <summary>
        /// Skill tool: input is {"skill": "&lt;name&gt;", "args": "..."}
        /// (the CLI's slash-command invocation). Without this branch the
        /// generic fallback found no useful key, echoed "Skill" as the
        /// summary (which the card then hid as redundant) and left a row
        /// that said nothing about WHICH skill ran. Shows "skill-name" or
        /// "skill-name args", args truncated like a Bash argument hint.
        /// </summary>
        private static Description DescribeSkill(JsonNode input, string name)
        {
            string skill = FirstString(input, "skill", "name", "command");
            if (string.IsNullOrEmpty(skill))
            {
                return Make("d_Preset.Context", GlyphSkill, name);
            }
            string args = FirstString(input, "args", "arguments");
            string summary = string.IsNullOrEmpty(args)
                ? skill.Trim()
                : skill.Trim() + " " + Truncate(args.Trim(), ArgHintMaxChars);
            return Make("d_Preset.Context", GlyphSkill, summary);
        }

        // -- Summary helpers ----------------------------------------------------

        /// <summary>"git commit -m ..." -&gt; "git commit -m ..." (first word + hint).</summary>
        private static string CommandSummary(string command)
        {
            string flat = command.Replace('\n', ' ').Replace('\r', ' ').Trim();
            int space = flat.IndexOf(' ');
            if (space < 0)
            {
                return Truncate(flat, SummaryMaxChars);
            }
            string first = flat.Substring(0, space);
            string rest = flat.Substring(space + 1).Trim();
            return first + " " + Truncate(rest, ArgHintMaxChars);
        }

        /// <summary>Last path segment ("Assets/Scripts/Player.cs" -&gt; "Player.cs").</summary>
        private static string FileName(string path)
        {
            string flat = path.Trim();
            int cut = flat.LastIndexOfAny(new[] { '/', '\\' });
            string name = cut >= 0 && cut < flat.Length - 1 ? flat.Substring(cut + 1) : flat;
            return Truncate(name, SummaryMaxChars);
        }

        /// <summary>"https://docs.unity3d.com/x/y" -&gt; "docs.unity3d.com".</summary>
        /// <summary>
        /// "host/path" with the scheme dropped and the path cut to keep the
        /// host readable: "docs.unity3d.com/2022.3/Documentation/Manual/cla...".
        /// Pure and internal for tests.
        /// </summary>
        internal static string WebFetchSummary(string url)
        {
            string host = HostOf(url);
            string s = url.Trim();
            int scheme = s.IndexOf("://", System.StringComparison.Ordinal);
            if (scheme >= 0)
            {
                s = s.Substring(scheme + 3);
            }
            int slash = s.IndexOf('/');
            string path = slash >= 0 ? s.Substring(slash) : string.Empty;
            if (path.Length <= 1)
            {
                return host;
            }
            return host + Truncate(path, ArgHintMaxChars);
        }

        private static string HostOf(string url)
        {
            string s = url.Trim();
            int scheme = s.IndexOf("://", System.StringComparison.Ordinal);
            if (scheme >= 0)
            {
                s = s.Substring(scheme + 3);
            }
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                s = s.Substring(0, slash);
            }
            return Truncate(s, SummaryMaxChars);
        }

        /// <summary>
        /// AskUserQuestion summary (design note 2026-08-14-ui-polish-
        /// audit.md contract item 4): the first question's own text
        /// (input.questions[0].question -- the same field
        /// AskUserQuestionInput.FromInput reads as the verified answers
        /// key), which is what actually tells a reader what Claude asked
        /// once the card has finished and collapsed. Falls back to a
        /// question-count phrasing when that text is missing/empty but at
        /// least one question is present -- reusing
        /// PermQuestionTitleSingle/Plural, the SAME already-localized
        /// strings the live permission card's own title uses for this
        /// exact tool, rather than inventing a new catalog entry (this
        /// stream does not own UiStrings.cs/UiStringsJa.cs). Absent/
        /// malformed input (no "questions" array at all) falls back to the
        /// bare tool name, same as every other branch above when it has
        /// nothing useful to show.
        /// </summary>
        private static Description DescribeAskUserQuestion(JsonNode input, string name)
        {
            JsonNode questions = input != null && input.IsObject
                ? input["questions"] : JsonNode.Null;
            int count = questions.IsArray ? questions.Count : 0;
            string text = count > 0 ? questions[0]["question"].AsString(null) : null;
            if (!string.IsNullOrEmpty(text))
            {
                return Make(null, GlyphQuestion, text.Replace('\n', ' ').Replace('\r', ' '));
            }
            if (count > 0)
            {
                string fallback = count == 1
                    ? L10n.A(L10n.S.PermQuestionTitleSingle) : L10n.A(L10n.S.PermQuestionTitlePlural);
                return Make(null, GlyphQuestion, fallback);
            }
            return Make(null, GlyphQuestion, name);
        }

        // -- Plumbing --------------------------------------------------------------

        private static bool Is(string toolName, string candidate)
        {
            return string.Equals(toolName, candidate, System.StringComparison.OrdinalIgnoreCase);
        }

        private static JsonNode ParseInput(string inputJson)
        {
            if (string.IsNullOrEmpty(inputJson))
            {
                return JsonNode.Null;
            }
            JsonNode node;
            string error;
            if (JsonParser.TryParse(inputJson, out node, out error) && node.IsObject)
            {
                return node;
            }
            return JsonNode.Null;
        }

        private static string FirstString(JsonNode input, params string[] keys)
        {
            if (input == null || !input.IsObject)
            {
                return null;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                string value = input[keys[i]].AsString(null);
                if (!string.IsNullOrEmpty(value))
                {
                    return value.Replace('\n', ' ').Replace('\r', ' ');
                }
            }
            return null;
        }

        private static Description Make(string iconName, string glyph, string summary)
        {
            return new Description
            {
                IconName = iconName,
                FallbackGlyph = glyph,
                Summary = Truncate(summary ?? string.Empty, SummaryMaxChars)
            };
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxChars - 3) + "...";
        }

        // -- Display-name shortening ----------------------------------------------

        /// <summary>Wire-protocol prefix every MCP tool name carries ahead of
        /// the server id (e.g. "mcp__unity-ops__uap_ping").</summary>
        private const string McpPrefix = "mcp__";

        /// <summary>Double underscore: the wire format's ONLY separator between
        /// "mcp", the server id and the tool id. A server id is assumed never
        /// to contain "__" itself (that would make the split ambiguous); a
        /// TOOL id frequently does (UapOps tool ids are snake_case and some
        /// happen to contain a doubled underscore), which is exactly why this
        /// method only ever looks for the FIRST "__" after the prefix and
        /// takes everything after it as the tool id, untouched.</summary>
        private const string Separator = "__";

        /// <summary>
        /// Shared parser behind BOTH display transforms below
        /// (<see cref="ShortenToolDisplayName"/>, which drops the server id
        /// entirely, and <see cref="FormatMcpNameWithServer"/>, which keeps
        /// it) so the wire-shape rule -- including every malformed-input
        /// decision -- has exactly ONE implementation. Returns false (with
        /// <paramref name="server"/>/<paramref name="tool"/> left null) for
        /// anything that does not cleanly match
        /// "mcp__&lt;server&gt;__&lt;tool&gt;":
        /// - does not start with "mcp__" at all;
        /// - no second "__" anywhere after the prefix ("mcp__", "mcp__server");
        /// - the second "__" begins immediately after the prefix, i.e. an
        ///   EMPTY server id ("mcp____tool" = "mcp__" + "__tool");
        /// - an EMPTY tool id ("mcp__server__").
        /// Only the FIRST "__" after the prefix is ever treated as the
        /// separator: a tool id that itself contains "__" (UapOps ids are
        /// snake_case and some legitimately have a doubled underscore, e.g.
        /// wire name "mcp__server__sub__tool") is returned whole as
        /// "sub__tool" in <paramref name="tool"/>, never re-split. The
        /// guiding principle for every malformed case is "only split when
        /// BOTH halves are non-empty" -- an empty half means the input does
        /// not actually match the shape, so failing outright (false, caller
        /// falls back to the untouched original) is safer than fabricating
        /// a label from a half-parsed wire name.
        /// </summary>
        private static bool TryParseMcpName(string wireName, out string server, out string tool)
        {
            server = null;
            tool = null;
            if (string.IsNullOrEmpty(wireName)
                || !wireName.StartsWith(McpPrefix, System.StringComparison.Ordinal))
            {
                return false;
            }

            string afterPrefix = wireName.Substring(McpPrefix.Length);
            int separatorIndex = afterPrefix.IndexOf(Separator, System.StringComparison.Ordinal);
            // separatorIndex < 0: no second "__" at all ("mcp__x", "mcp__").
            // separatorIndex == 0: the remainder starts with "__" itself, so
            // the server id ahead of it is empty ("mcp____tool"). Both are
            // "does not match the shape".
            if (separatorIndex <= 0)
            {
                return false;
            }

            string toolId = afterPrefix.Substring(separatorIndex + Separator.Length);
            if (string.IsNullOrEmpty(toolId))
            {
                // "mcp__server__" -- nothing after the separator.
                return false;
            }

            server = afterPrefix.Substring(0, separatorIndex);
            tool = toolId;
            return true;
        }

        /// <summary>True when the wire name cleanly matches
        /// "mcp__&lt;server&gt;__&lt;tool&gt;" (same rule as the display
        /// transforms below).</summary>
        public static bool IsMcpName(string wireName)
        {
            string server;
            string tool;
            return TryParseMcpName(wireName, out server, out tool);
        }

        /// <summary>
        /// Strips the "mcp__&lt;server&gt;__" plumbing prefix from a raw
        /// tool_use wire name so a UI label can show just the part that
        /// says what the tool actually DOES, e.g.
        /// "mcp__unity-ops__uap_query_component_types" -&gt;
        /// "uap_query_component_types".
        ///
        /// USE THIS ONLY FOR INFORMATIONAL, AFTER-THE-FACT SURFACES --
        /// ToolActivityCard's .uap-toolcard-name and SubagentCard's
        /// .uap-subcard-type, which report what already ran. It DELIBERATELY
        /// throws away which server answered the call, because those cards
        /// are read-only history: the server id is protocol plumbing there,
        /// not information the user needs on every row. Do NOT reuse this
        /// for the permission-prompt card (PermissionCard) -- see
        /// <see cref="FormatMcpNameWithServer"/> for why that surface must
        /// keep the server id instead of discarding it.
        ///
        /// Why this exists (measured live in the editor, not a tidiness
        /// pass): this package ships its own in-editor MCP server (UapOps),
        /// so its own tool names constantly appear in the panel's own
        /// activity feed. Two real wire names captured on screen --
        /// "mcp__unity-ops__uap_query_component_types" (41 chars) and
        /// "mcp__uap-ops__uap_prefab_revert_added_gameobject" (48 chars) --
        /// were measured with MeasureTextSize against the live
        /// .uap-toolcard-name label style (fontSize 11): the 48-char name
        /// needs 300.5 px, against a content column of roughly 300 px. The
        /// USS side (.uap-toolcard-name / .uap-subcard-type) now makes the
        /// label shrinkable and caps it at 50% width with
        /// text-overflow: ellipsis so an oversized name can never again
        /// starve the sibling summary/time/chevron columns out of a
        /// clipping container -- but UI Toolkit's ellipsis only truncates
        /// at the END of the string, so a capped raw wire name would render
        /// as "mcp__uap-ops__uap_pref..." and hide the only part of the
        /// string that means anything to the user. Stripping the
        /// "mcp__server__" plumbing FIRST means the ellipsis (if it still
        /// triggers at all) eats into the tool id itself rather than into
        /// text the user never needed to see.
        ///
        /// Rules (see <see cref="TryParseMcpName"/> for the exact shared
        /// parsing/malformed-input rule):
        /// - "mcp__&lt;server&gt;__&lt;tool&gt;" -&gt; "&lt;tool&gt;".
        /// - Any name that does not start with "mcp__" is returned
        ///   completely unchanged ("Read", "Bash", "TodoWrite", ...):
        ///   this method only ever removes the MCP plumbing prefix, never
        ///   truncates, lowercases or otherwise reformats -- truncation for
        ///   display is the USS layer's job (text-overflow: ellipsis).
        /// - Malformed shapes are never thrown on; each returns the ORIGINAL
        ///   string unchanged rather than guessing: "mcp__" -&gt; "mcp__";
        ///   "mcp__server__" -&gt; "mcp__server__" (empty tool id);
        ///   "mcp____tool" -&gt; "mcp____tool" (empty server id).
        /// </summary>
        public static string ShortenToolDisplayName(string wireName)
        {
            string server;
            string tool;
            if (!TryParseMcpName(wireName, out server, out tool))
            {
                return wireName ?? string.Empty;
            }
            return tool;
        }

        /// <summary>
        /// Reformats a raw tool_use wire name as "&lt;server&gt;: &lt;tool&gt;",
        /// e.g. "mcp__unity-ops__uap_ping" -&gt; "unity-ops: uap_ping" --
        /// removing the "mcp__"/"__" protocol punctuation while KEEPING the
        /// server id, unlike <see cref="ShortenToolDisplayName"/> which
        /// drops it.
        ///
        /// USE THIS ONLY FOR THE AUTHORISATION SURFACE -- PermissionCard's
        /// summary title, where the user is deciding whether to ALLOW a
        /// call, not just reading a history row. Extension Profiles let this
        /// panel talk to more than one third-party MCP server at once (a
        /// designed, non-hypothetical scenario, not just this package's own
        /// UapOps server), and two different servers are free to expose a
        /// same-named tool id -- e.g. two servers both defining
        /// "delete_all". <see cref="ShortenToolDisplayName"/>'s output would
        /// render both prompts as the identical "Allow delete_all?" with no
        /// way to tell which server is asking; that is fine for an
        /// after-the-fact activity row but NOT fine for a prompt the user
        /// must affirmatively approve. Keeping "&lt;server&gt;: " ahead of the
        /// tool id preserves exactly the provenance an approval decision
        /// needs, while still dropping the "mcp__"/double-underscore
        /// plumbing that is meaningless to a human either way. Do NOT
        /// "simplify" PermissionCard to reuse ShortenToolDisplayName instead
        /// -- that would silently reintroduce the ambiguity described here.
        ///
        /// Same shared parser, same all-or-nothing malformed-input contract
        /// as ShortenToolDisplayName (see <see cref="TryParseMcpName"/>):
        /// non-MCP names and every malformed shape ("mcp__", "mcp__server",
        /// "mcp__server__", "mcp____tool") are returned completely
        /// unchanged, never partially reformatted, and this never throws.
        /// </summary>
        public static string FormatMcpNameWithServer(string wireName)
        {
            string server;
            string tool;
            if (!TryParseMcpName(wireName, out server, out tool))
            {
                return wireName ?? string.Empty;
            }
            return server + ": " + tool;
        }
    }
}

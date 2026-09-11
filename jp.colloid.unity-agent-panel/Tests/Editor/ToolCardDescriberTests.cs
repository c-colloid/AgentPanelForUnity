using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Table tests for the tool name + input -&gt; icon/summary translation
    /// (ToolCardDescriber). Pure string logic, no VisualElements. Pins
    /// English via L10n.OverrideForTests since the unknown-tool fallback
    /// now reads L10n.S.ToolCardDefaultName
    /// (docs/design-notes/2026-08-01-i18n.md) and this machine's OS
    /// language is Japanese.
    /// </summary>
    public class ToolCardDescriberTests
    {
        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(PanelLanguage.English);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
        }

        [Test]
        public void Read_UsesFileName()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Read",
                "{\"file_path\":\"C:/proj/Assets/Scripts/Player.cs\"}");
            Assert.AreEqual("Player.cs", d.Summary);
            Assert.AreEqual("d_TextAsset Icon", d.IconName);
        }

        [Test]
        public void Edit_UsesFileName_BackslashPaths()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Edit",
                "{\"file_path\":\"C:\\\\proj\\\\Assets\\\\A.md\"}");
            Assert.AreEqual("A.md", d.Summary);
        }

        [Test]
        public void Write_WithoutPath_FallsBackToToolName()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Write", "{}");
            Assert.AreEqual("Write", d.Summary);
        }

        [Test]
        public void Bash_UsesFirstCommandWordPlusArgHint()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Bash",
                "{\"command\":\"git commit -m \\\"message\\\"\"}");
            StringAssert.StartsWith("git ", d.Summary);
            StringAssert.Contains("commit", d.Summary);
        }

        [Test]
        public void Bash_LongCommand_TruncatesArgHint()
        {
            string longArgs = new string('x', 200);
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Bash",
                "{\"command\":\"dotnet " + longArgs + "\"}");
            StringAssert.StartsWith("dotnet ", d.Summary);
            Assert.LessOrEqual(d.Summary.Length, "dotnet ".Length + 40);
            StringAssert.EndsWith("...", d.Summary);
        }

        [Test]
        public void Bash_MultilineCommand_IsFlattened()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Bash",
                "{\"command\":\"ls\\n-la\"}");
            StringAssert.DoesNotContain("\n", d.Summary);
        }

        [Test]
        public void Grep_UsesPattern()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Grep",
                "{\"pattern\":\"TODO|FIXME\"}");
            Assert.AreEqual("TODO|FIXME", d.Summary);
            Assert.AreEqual("d_Search Icon", d.IconName);
        }

        [Test]
        public void Glob_UsesPattern()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Glob",
                "{\"pattern\":\"**/*.cs\"}");
            Assert.AreEqual("**/*.cs", d.Summary);
        }

        [Test]
        public void WebFetch_UsesHost()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("WebFetch",
                "{\"url\":\"https://docs.unity3d.com/2022.3/Documentation/x.html\"}");
            Assert.AreEqual("docs.unity3d.com", d.Summary);
        }

        [Test]
        public void WebSearch_UsesQuery()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("WebSearch",
                "{\"query\":\"unity uitk link tag\"}");
            Assert.AreEqual("unity uitk link tag", d.Summary);
        }

        [Test]
        public void Task_UsesDescription()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Task",
                "{\"description\":\"Explore the codebase\",\"prompt\":\"...\"}");
            Assert.AreEqual("Explore the codebase", d.Summary);
        }

        // -- AskUserQuestion ---------------------------------------------------
        // design note 2026-08-14-ui-polish-audit.md contract item 4: the
        // first question's own text as the summary, falling back to a
        // question-count phrasing (the SAME PermQuestionTitleSingle/Plural
        // strings the live permission card's title already uses for this
        // tool) when that text is missing, and to the bare tool name when
        // the input carries no questions at all.

        [Test]
        public void AskUserQuestion_UsesFirstQuestionText()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("AskUserQuestion",
                "{\"questions\":[{\"question\":\"Which color do you prefer?\","
                    + "\"header\":\"Color\",\"options\":[]}]}");
            Assert.AreEqual("Which color do you prefer?", d.Summary);
        }

        [Test]
        public void AskUserQuestion_MultipleQuestions_UsesTheFirstOnesText()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("AskUserQuestion",
                "{\"questions\":[{\"question\":\"First?\"},{\"question\":\"Second?\"}]}");
            Assert.AreEqual("First?", d.Summary);
        }

        [Test]
        public void AskUserQuestion_EmptyQuestionText_FallsBackToSingularCountPhrasing()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("AskUserQuestion",
                "{\"questions\":[{\"question\":\"\"}]}");
            Assert.AreEqual(L10n.A(L10n.S.PermQuestionTitleSingle), d.Summary);
        }

        [Test]
        public void AskUserQuestion_MissingQuestionText_MultipleQuestions_FallsBackToPluralCountPhrasing()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("AskUserQuestion",
                "{\"questions\":[{\"header\":\"A\"},{\"header\":\"B\"}]}");
            Assert.AreEqual(L10n.A(L10n.S.PermQuestionTitlePlural), d.Summary);
        }

        [Test]
        public void AskUserQuestion_AbsentInput_FallsBackToToolName()
        {
            ToolCardDescriber.Description d1 = ToolCardDescriber.Describe("AskUserQuestion", "{}");
            Assert.AreEqual("AskUserQuestion", d1.Summary);

            // (string)null: a bare null is ambiguous since the JsonNode
            // overload arrived (CS0121); this test means the JSON-string
            // path.
            ToolCardDescriber.Description d2 = ToolCardDescriber.Describe("AskUserQuestion", (string)null);
            Assert.AreEqual("AskUserQuestion", d2.Summary);
        }

        [Test]
        public void UnknownTool_FallsBackToUsefulKeyOrName()
        {
            ToolCardDescriber.Description d1 = ToolCardDescriber.Describe(
                "mcp__custom__thing", "{\"path\":\"a/b.txt\"}");
            Assert.AreEqual("a/b.txt", d1.Summary);

            // No useful key: the echo is the SHORTENED display name (what
            // ToolActivityCard's header shows), never the raw wire name --
            // the card hides an echo only when the two strings match.
            ToolCardDescriber.Description d2 = ToolCardDescriber.Describe(
                "mcp__custom__thing", "{\"weird\":1}");
            Assert.AreEqual("thing", d2.Summary);
        }

        [Test]
        public void McpTool_GetsAnIconInsteadOfTheGenericBullet()
        {
            ToolCardDescriber.Description mcp = ToolCardDescriber.Describe(
                "mcp__unity-ops__uap_scripts_compile", "{}");
            Assert.IsFalse(string.IsNullOrEmpty(mcp.IconName));
            Assert.AreEqual(IconLoader.GlyphGear, mcp.FallbackGlyph);
            Assert.AreEqual("uap_scripts_compile", mcp.Summary);

            ToolCardDescriber.Description plain = ToolCardDescriber.Describe(
                "SomeUnknownTool", "{}");
            Assert.IsNull(plain.IconName);
            Assert.AreEqual(IconLoader.GlyphBullet, plain.FallbackGlyph);
            Assert.AreEqual("SomeUnknownTool", plain.Summary);
        }

        // -- Skill ---------------------------------------------------------------

        [Test]
        public void Skill_UsesSkillName()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Skill",
                "{\"skill\":\"run\"}");
            Assert.AreEqual("run", d.Summary);
            Assert.AreEqual("d_Preset.Context", d.IconName);
            Assert.AreEqual(IconLoader.GlyphSpark, d.FallbackGlyph);
        }

        [Test]
        public void Skill_WithArgs_AppendsTruncatedArgHint()
        {
            string longArgs = new string('y', 200);
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Skill",
                "{\"skill\":\"code-review\",\"args\":\"" + longArgs + "\"}");
            StringAssert.StartsWith("code-review ", d.Summary);
            Assert.LessOrEqual(d.Summary.Length, "code-review ".Length + 40);
            StringAssert.EndsWith("...", d.Summary);
        }

        [Test]
        public void Skill_WithoutSkillKey_FallsBackToToolName()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("Skill", "{}");
            Assert.AreEqual("Skill", d.Summary);
        }

        [Test]
        public void NullOrEmptyInput_IsSafe()
        {
            ToolCardDescriber.Description d1 = ToolCardDescriber.Describe(null, (string)null);
            Assert.AreEqual(L10n.S.ToolCardDefaultName, d1.Summary);

            ToolCardDescriber.Description d2 = ToolCardDescriber.Describe("Read", "not json");
            Assert.AreEqual("Read", d2.Summary);
        }

        [Test]
        public void ToolNames_MatchCaseInsensitively()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("bash",
                "{\"command\":\"echo hi\"}");
            StringAssert.StartsWith("echo ", d.Summary);
        }

        [Test]
        public void SummaryNeverNull_AndFallbackGlyphAlwaysSet()
        {
            ToolCardDescriber.Description d = ToolCardDescriber.Describe("", "");
            Assert.IsNotNull(d.Summary);
            Assert.IsFalse(string.IsNullOrEmpty(d.FallbackGlyph));
        }

        // -- ShortenToolDisplayName -------------------------------------------------
        // Card-header display-name shortening (measured live defect): the
        // 48-char raw wire name "mcp__uap-ops__uap_prefab_revert_added_gameobject"
        // needs 300.5 px at the card label's fontSize 11, against a content
        // column of about 300 px. See ToolCardDescriber.ShortenToolDisplayName's
        // doc comment for the full rationale and the malformed-input rules
        // pinned by the tests below.

        [Test]
        public void ShortenToolDisplayName_McpToolName_StripsServerPrefix()
        {
            // The 41-char name on screen at the time this defect was measured.
            Assert.AreEqual("uap_query_component_types",
                ToolCardDescriber.ShortenToolDisplayName(
                    "mcp__unity-ops__uap_query_component_types"));
        }

        [Test]
        public void ShortenToolDisplayName_LongerMcpToolName_StripsServerPrefix()
        {
            // The 48-char name that measured 300.5 px against a ~300 px column.
            Assert.AreEqual("uap_prefab_revert_added_gameobject",
                ToolCardDescriber.ShortenToolDisplayName(
                    "mcp__uap-ops__uap_prefab_revert_added_gameobject"));
        }

        [Test]
        public void ShortenToolDisplayName_ToolIdContainsItsOwnDoubleUnderscore_KeptWhole()
        {
            // Only the FIRST "__" after "mcp__" is the server/tool separator;
            // a tool id with its own "__" must never be re-split.
            Assert.AreEqual("sub__tool",
                ToolCardDescriber.ShortenToolDisplayName("mcp__server__sub__tool"));
        }

        [Test]
        public void ShortenToolDisplayName_NonMcpNames_AreReturnedUnchanged()
        {
            Assert.AreEqual("Read", ToolCardDescriber.ShortenToolDisplayName("Read"));
            Assert.AreEqual("Bash", ToolCardDescriber.ShortenToolDisplayName("Bash"));
            Assert.AreEqual("TodoWrite", ToolCardDescriber.ShortenToolDisplayName("TodoWrite"));
        }

        [Test]
        public void ShortenToolDisplayName_WrongCasePrefix_IsNotMatched()
        {
            // The wire prefix is a protocol literal, always exactly "mcp__";
            // this is not the case-insensitive tool-name matching Is() does
            // for known first-party tools.
            Assert.AreEqual("MCP__server__tool",
                ToolCardDescriber.ShortenToolDisplayName("MCP__server__tool"));
        }

        [Test]
        public void ShortenToolDisplayName_JustThePrefix_IsReturnedUnchanged()
        {
            Assert.AreEqual("mcp__", ToolCardDescriber.ShortenToolDisplayName("mcp__"));
        }

        [Test]
        public void ShortenToolDisplayName_PrefixWithNoSeparator_IsReturnedUnchanged()
        {
            Assert.AreEqual("mcp__server", ToolCardDescriber.ShortenToolDisplayName("mcp__server"));
        }

        [Test]
        public void ShortenToolDisplayName_EmptyToolId_IsReturnedUnchanged()
        {
            // "mcp__server__" -- nothing after the separator to shorten to.
            Assert.AreEqual("mcp__server__", ToolCardDescriber.ShortenToolDisplayName("mcp__server__"));
        }

        [Test]
        public void ShortenToolDisplayName_EmptyServerId_IsReturnedUnchanged()
        {
            // "mcp____tool" = "mcp__" prefix + "__tool": the first "__" after
            // the prefix is itself the start of a second "__", so the server
            // id ahead of it is empty and the whole name is left untouched.
            Assert.AreEqual("mcp____tool", ToolCardDescriber.ShortenToolDisplayName("mcp____tool"));
        }

        [Test]
        public void ShortenToolDisplayName_NullOrEmpty_IsSafe()
        {
            Assert.AreEqual(string.Empty, ToolCardDescriber.ShortenToolDisplayName(null));
            Assert.AreEqual(string.Empty, ToolCardDescriber.ShortenToolDisplayName(string.Empty));
        }

        // -- FormatMcpNameWithServer --------------------------------------------
        // The authorisation-surface counterpart to ShortenToolDisplayName
        // (PermissionCard's summary title): KEEPS the server id instead of
        // dropping it, because Extension Profiles make more than one MCP
        // server a designed scenario and two servers can expose the same
        // tool id (e.g. both "delete_all") -- an approval prompt must be
        // able to tell them apart. Same shared parser and the same
        // all-or-nothing malformed-input contract as ShortenToolDisplayName
        // (see ToolCardDescriber.TryParseMcpName), so this mirrors that
        // method's case matrix exactly.

        [Test]
        public void FormatMcpNameWithServer_McpToolName_KeepsServerId()
        {
            Assert.AreEqual("unity-ops: uap_ping",
                ToolCardDescriber.FormatMcpNameWithServer("mcp__unity-ops__uap_ping"));
        }

        [Test]
        public void FormatMcpNameWithServer_ToolIdContainsItsOwnDoubleUnderscore_KeptWhole()
        {
            // Same "only the FIRST __ after the prefix is the separator"
            // rule as ShortenToolDisplayName: "mcp__a__b__c" -> server "a",
            // tool id "b__c" kept whole, never re-split.
            Assert.AreEqual("a: b__c",
                ToolCardDescriber.FormatMcpNameWithServer("mcp__a__b__c"));
        }

        [Test]
        public void FormatMcpNameWithServer_NonMcpNames_AreReturnedUnchanged()
        {
            Assert.AreEqual("Read", ToolCardDescriber.FormatMcpNameWithServer("Read"));
            Assert.AreEqual("Bash", ToolCardDescriber.FormatMcpNameWithServer("Bash"));
            Assert.AreEqual("TodoWrite", ToolCardDescriber.FormatMcpNameWithServer("TodoWrite"));
        }

        [Test]
        public void FormatMcpNameWithServer_WrongCasePrefix_IsNotMatched()
        {
            Assert.AreEqual("MCP__server__tool",
                ToolCardDescriber.FormatMcpNameWithServer("MCP__server__tool"));
        }

        [Test]
        public void FormatMcpNameWithServer_JustThePrefix_IsReturnedUnchanged()
        {
            Assert.AreEqual("mcp__", ToolCardDescriber.FormatMcpNameWithServer("mcp__"));
        }

        [Test]
        public void FormatMcpNameWithServer_PrefixWithNoSeparator_IsReturnedUnchanged()
        {
            Assert.AreEqual("mcp__server", ToolCardDescriber.FormatMcpNameWithServer("mcp__server"));
        }

        [Test]
        public void FormatMcpNameWithServer_EmptyToolId_IsReturnedUnchanged()
        {
            // "mcp__server__" -- nothing after the separator to show.
            Assert.AreEqual("mcp__server__", ToolCardDescriber.FormatMcpNameWithServer("mcp__server__"));
        }

        [Test]
        public void FormatMcpNameWithServer_EmptyServerId_IsReturnedUnchanged()
        {
            // "mcp____tool" = "mcp__" prefix + "__tool": the server id ahead
            // of the second "__" is empty, so the whole name is untouched.
            Assert.AreEqual("mcp____tool", ToolCardDescriber.FormatMcpNameWithServer("mcp____tool"));
        }

        [Test]
        public void FormatMcpNameWithServer_NullOrEmpty_IsSafe()
        {
            Assert.AreEqual(string.Empty, ToolCardDescriber.FormatMcpNameWithServer(null));
            Assert.AreEqual(string.Empty, ToolCardDescriber.FormatMcpNameWithServer(string.Empty));
        }

        // ------------------------------------------------------------------
        // UXA-1: JsonNode overload -- same table, no re-serialize/re-parse.
        // ------------------------------------------------------------------

        [Test]
        public void Describe_JsonNodeOverload_MatchesStringOverload()
        {
            string[][] cases =
            {
                new[] { "Read", "{\"file_path\":\"Assets/Scripts/Player.cs\"}" },
                new[] { "Bash", "{\"command\":\"git commit -m message\"}" },
                new[] { "Grep", "{\"pattern\":\"TODO\"}" },
                new[] { "WebFetch", "{\"url\":\"https://docs.unity3d.com/x/y\"}" },
                new[] { "Write", "{}" },
                new[] { "SomeUnknownTool", "{\"path\":\"a/b.txt\"}" }
            };
            foreach (string[] c in cases)
            {
                ToolCardDescriber.Description fromString =
                    ToolCardDescriber.Describe(c[0], c[1]);
                Core.Json.JsonNode node;
                string error;
                Assert.IsTrue(Core.Json.JsonParser.TryParse(c[1], out node, out error), error);
                ToolCardDescriber.Description fromNode =
                    ToolCardDescriber.Describe(c[0], node);

                Assert.AreEqual(fromString.Summary, fromNode.Summary, c[0]);
                Assert.AreEqual(fromString.IconName, fromNode.IconName, c[0]);
                Assert.AreEqual(fromString.FallbackGlyph, fromNode.FallbackGlyph, c[0]);
            }
        }

        [Test]
        public void Describe_JsonNodeOverload_NullNode_BehavesLikeUnparseableJson()
        {
            ToolCardDescriber.Description fromNull =
                ToolCardDescriber.Describe("Write", (Core.Json.JsonNode)null);
            ToolCardDescriber.Description fromBadJson =
                ToolCardDescriber.Describe("Write", "not json");

            Assert.AreEqual(fromBadJson.Summary, fromNull.Summary);
        }
    }
}

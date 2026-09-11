using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure-logic pins for the composer's slash-command support (design
    /// note docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 1): catalog parsing from the initialize response, the
    /// typed-text grammar, popup filtering, and the Enter-completes rule.
    /// </summary>
    public class SlashCommandCatalogTests
    {
        private static SlashCommandEntry Entry(string name, string description = "", string argumentHint = "")
        {
            return new SlashCommandEntry { name = name, description = description, argumentHint = argumentHint };
        }

        // -- ParseInitializeCommands / Merge ---------------------------------------------

        [Test]
        public void ParseInitializeCommands_ReadsNameDescriptionAndArgumentHint()
        {
            JsonNode node = JsonParser.Parse(
                "[{\"name\":\"compact\",\"description\":\"Clear history but keep a summary\","
                + "\"argumentHint\":\"[instructions]\"},{\"name\":\"/review\",\"description\":\"Review\"},"
                + "{\"description\":\"no name -> skipped\"},\"not an object\"]");
            List<SlashCommandEntry> parsed = SlashCommandCatalog.ParseInitializeCommands(node);

            Assert.AreEqual(2, parsed.Count);
            Assert.AreEqual("compact", parsed[0].name);
            Assert.AreEqual("Clear history but keep a summary", parsed[0].description);
            Assert.AreEqual("[instructions]", parsed[0].argumentHint);
            Assert.AreEqual("review", parsed[1].name, "a leading slash in the CLI's name is stripped");
            Assert.AreEqual(string.Empty, parsed[1].argumentHint);
        }

        [Test]
        public void ParseInitializeCommands_FixtureInitializeResponse_HasCompact()
        {
            // The real initialize response captured in out_bidi.jsonl.
            List<string> lines = FixtureLoader.ReadLines("out_bidi.jsonl");
            List<SlashCommandEntry> parsed = null;
            foreach (string line in lines)
            {
                JsonNode node = JsonParser.Parse(line);
                if (node["type"].AsString() == "control_response")
                {
                    parsed = SlashCommandCatalog.ParseInitializeCommands(
                        node["response"]["response"]["commands"]);
                    break;
                }
            }
            Assert.IsNotNull(parsed, "the fixture must contain the initialize control_response");
            Assert.Greater(parsed.Count, 0);
            bool hasCompact = false;
            foreach (SlashCommandEntry entry in parsed)
            {
                if (entry.name == "compact")
                {
                    hasCompact = true;
                    Assert.IsNotEmpty(entry.description);
                }
            }
            Assert.IsTrue(hasCompact, "the CLI's own catalog lists /compact");
        }

        [Test]
        public void ParseInitializeCommands_NonArray_ReturnsEmptyNeverNull()
        {
            Assert.IsEmpty(SlashCommandCatalog.ParseInitializeCommands(null));
            Assert.IsEmpty(SlashCommandCatalog.ParseInitializeCommands(JsonNode.Null));
            Assert.IsEmpty(SlashCommandCatalog.ParseInitializeCommands(JsonParser.Parse("{\"a\":1}")));
        }

        [Test]
        public void Merge_RichEntriesWin_NamesOnlyAppended_DuplicatesCollapse()
        {
            var rich = new List<SlashCommandEntry> { Entry("compact", "rich desc"), Entry("Review") };
            List<SlashCommandEntry> merged = SlashCommandCatalog.Merge(rich,
                new[] { "compact", "review", "init", "/init", "", null });

            Assert.AreEqual(3, merged.Count);
            Assert.AreEqual("compact", merged[0].name);
            Assert.AreEqual("rich desc", merged[0].description);
            Assert.AreEqual("Review", merged[1].name);
            Assert.AreEqual("init", merged[2].name);
            Assert.AreEqual(string.Empty, merged[2].description);
        }

        [Test]
        public void Merge_BothInputsNull_ReturnsEmpty()
        {
            Assert.IsEmpty(SlashCommandCatalog.Merge(null, null));
        }

        // -- WithBuiltins -----------------------------------------------------------------------

        [Test]
        public void WithBuiltins_EmptyCatalog_OffersCompactAndClearWithPanelDescriptions()
        {
            List<SlashCommandEntry> offered = SlashCommandCatalog.WithBuiltins(
                new List<SlashCommandEntry>(), "compact desc", "clear desc");

            Assert.AreEqual(2, offered.Count);
            Assert.AreEqual("compact", offered[0].name);
            Assert.AreEqual("compact desc", offered[0].description);
            Assert.AreEqual("clear", offered[1].name);
            Assert.AreEqual("clear desc", offered[1].description);
        }

        [Test]
        public void WithBuiltins_KeepsCliCompactDescription_ButOverridesClear()
        {
            var catalog = new List<SlashCommandEntry>
            {
                Entry("review", "Review code"),
                Entry("compact", "CLI compact desc", "[instructions]"),
                Entry("clear", "CLI clear desc")
            };
            List<SlashCommandEntry> offered = SlashCommandCatalog.WithBuiltins(catalog, "panel compact", "panel clear");

            Assert.AreEqual(3, offered.Count);
            Assert.AreEqual("compact", offered[0].name);
            Assert.AreEqual("CLI compact desc", offered[0].description);
            Assert.AreEqual("[instructions]", offered[0].argumentHint);
            Assert.AreEqual("clear", offered[1].name);
            Assert.AreEqual("panel clear", offered[1].description,
                "/clear is the panel's New chat, so its description is the panel's");
            Assert.AreEqual("review", offered[2].name);
            Assert.AreEqual(3, catalog.Count, "the input list must not be mutated");
        }

        // -- TryParse (the sent-text grammar) --------------------------------------------------

        [TestCase("/compact", "compact", "")]
        [TestCase("/compact focus on the shader work", "compact", "focus on the shader work")]
        [TestCase("  /clear  ", "clear", "")]
        [TestCase("/plugin:skill arg1\nline two", "plugin:skill", "arg1\nline two")]
        [TestCase("/my-cmd_2", "my-cmd_2", "")]
        public void TryParse_Commands(string text, string expectedName, string expectedArgs)
        {
            string name;
            string args;
            Assert.IsTrue(SlashCommandCatalog.TryParse(text, out name, out args));
            Assert.AreEqual(expectedName, name);
            Assert.AreEqual(expectedArgs, args);
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("/")]
        [TestCase("/ compact")]
        [TestCase("//")]
        [TestCase("/usr/bin/claude")]
        [TestCase("fix /compact please")]
        [TestCase("1/2 of the scene")]
        public void TryParse_NotCommands(string text)
        {
            string name;
            string args;
            Assert.IsFalse(SlashCommandCatalog.TryParse(text, out name, out args), text);
        }

        // -- TryGetTypedPrefix (the popup trigger) -------------------------------------------

        [TestCase("/", "")]
        [TestCase("/c", "c")]
        [TestCase("/compact", "compact")]
        [TestCase("/plugin:sk", "plugin:sk")]
        public void TryGetTypedPrefix_PrefixMode(string text, string expectedPrefix)
        {
            string prefix;
            Assert.IsTrue(SlashCommandCatalog.TryGetTypedPrefix(text, out prefix));
            Assert.AreEqual(expectedPrefix, prefix);
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("compact")]
        [TestCase(" /compact")]
        [TestCase("/compact ")]
        [TestCase("/compact\n")]
        [TestCase("/compact focus")]
        [TestCase("/usr/bin")]
        public void TryGetTypedPrefix_NotPrefixMode(string text)
        {
            string prefix;
            Assert.IsFalse(SlashCommandCatalog.TryGetTypedPrefix(text, out prefix), text ?? "<null>");
        }

        // -- Filter -----------------------------------------------------------------------------------

        [Test]
        public void Filter_PrefixMatchesFirst_ThenContains_CaseInsensitive()
        {
            var catalog = new List<SlashCommandEntry>
            {
                Entry("compact"), Entry("clear"), Entry("review"), Entry("recompile"), Entry("Context")
            };
            List<SlashCommandEntry> matches = SlashCommandCatalog.Filter(catalog, "co");

            Assert.AreEqual(3, matches.Count);
            Assert.AreEqual("compact", matches[0].name);
            Assert.AreEqual("Context", matches[1].name, "prefix matches keep catalog order and ignore case");
            Assert.AreEqual("recompile", matches[2].name, "a contains-match trails the prefix matches");
        }

        [Test]
        public void Filter_EmptyPrefix_ListsCatalogHead_CappedAtMaxSuggestions()
        {
            var catalog = new List<SlashCommandEntry>();
            for (int i = 0; i < SlashCommandCatalog.MaxSuggestions + 5; i++)
            {
                catalog.Add(Entry("cmd" + i));
            }
            List<SlashCommandEntry> matches = SlashCommandCatalog.Filter(catalog, string.Empty);
            Assert.AreEqual(SlashCommandCatalog.MaxSuggestions, matches.Count);
            Assert.AreEqual("cmd0", matches[0].name);
        }

        [Test]
        public void Filter_NoMatch_ReturnsEmpty_NullCatalogTolerated()
        {
            Assert.IsEmpty(SlashCommandCatalog.Filter(new List<SlashCommandEntry> { Entry("compact") }, "zzz"));
            Assert.IsEmpty(SlashCommandCatalog.Filter(null, "c"));
        }

        // -- CompleteText / ShouldCompleteOnEnter ------------------------------------------------

        [Test]
        public void CompleteText_AddsSlashAndTrailingSpace()
        {
            Assert.AreEqual("/compact ", SlashCommandCatalog.CompleteText(Entry("compact")));
            Assert.AreEqual(string.Empty, SlashCommandCatalog.CompleteText(null));
        }

        [Test]
        public void ShouldCompleteOnEnter_OnlyWhilePrefixIsNotTheFullName()
        {
            SlashCommandEntry compact = Entry("compact");
            Assert.IsTrue(SlashCommandCatalog.ShouldCompleteOnEnter("", compact));
            Assert.IsTrue(SlashCommandCatalog.ShouldCompleteOnEnter("comp", compact));
            Assert.IsFalse(SlashCommandCatalog.ShouldCompleteOnEnter("compact", compact),
                "the full name typed: Enter sends");
            Assert.IsFalse(SlashCommandCatalog.ShouldCompleteOnEnter("COMPACT", compact));
            Assert.IsFalse(SlashCommandCatalog.ShouldCompleteOnEnter("comp", null));
        }

        [Test]
        public void IsClear_IsCompact_NormalizeSlashAndCase()
        {
            Assert.IsTrue(SlashCommandCatalog.IsClear("clear"));
            Assert.IsTrue(SlashCommandCatalog.IsClear("/Clear"));
            Assert.IsFalse(SlashCommandCatalog.IsClear("clearx"));
            Assert.IsTrue(SlashCommandCatalog.IsCompact("compact"));
            Assert.IsFalse(SlashCommandCatalog.IsCompact(null));
        }
    }
}

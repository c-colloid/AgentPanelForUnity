using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// TranscriptUsage / TranscriptLoader token-accounting regression tests.
    ///
    /// Most of these run the REAL path -- TranscriptLoader.Load(path, out
    /// usage) over Tests/Editor/Fixtures/session_usage_disk.jsonl, a
    /// sanitized copy of an actual on-disk transcript (ids, models, usage
    /// numbers and timestamps are byte-for-byte real; only human text was
    /// replaced) -- rather than exercising TranscriptUsage in isolation,
    /// because the bug this file exists to pin (see below) lives in how
    /// TranscriptLoader feeds lines into TranscriptUsage.Accumulate, not in
    /// TranscriptUsage's arithmetic alone.
    ///
    /// THE central fact under test: one API response is written to the
    /// on-disk JSONL as SEVERAL lines (one per content block -- text,
    /// thinking, tool_use), each repeating the SAME message.id and the SAME
    /// usage object. Summing usage per LINE instead of per distinct
    /// message.id therefore roughly doubles every total. For this fixture:
    /// 24 assistant-typed lines (22 non-synthetic, spanning 15 distinct
    /// message.id values) sum to 6679 output tokens per line, versus 3147
    /// when deduped by id -- a 2.1x inflation were the dedup ever removed.
    /// Ground-truth totals below were computed by hand directly from the
    /// fixture file and cross-checked both ways (per-line and per-id) to
    /// confirm the 6679-vs-3147 relationship before being pinned here.
    ///
    /// The Accumulate(...)-level tests at the bottom exercise
    /// TranscriptUsage in isolation (no file, no TranscriptLoader) only for
    /// argument-validation edge cases that would otherwise require
    /// constructing awkward synthetic fixture lines to reach.
    ///
    /// All source literals are strict ASCII (see TranscriptLoaderTests for
    /// the same convention and the reason: escaping non-ASCII through
    /// tooling has repeatedly failed in this repo).
    /// </summary>
    public class TranscriptUsageTests
    {
        /// <summary>Name of the real captured fixture this class centers on.</summary>
        private const string UsageFixtureName = "session_usage_disk.jsonl";

        /// <summary>
        /// What summing per JSONL LINE instead of per distinct message.id
        /// would produce for TotalOutputTokens on the ground-truth fixture
        /// -- named here so the totals assertion below can explain itself
        /// if this regresses, without anyone having to recompute it.
        /// </summary>
        private const long NaivePerLineOutputTokenSum = 6679;

        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelTranscriptUsageTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private string WriteFile(params string[] lines)
        {
            string path = Path.Combine(_dir, "session.jsonl");
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return path;
        }

        // ---------------------------------------------------------------
        // Ground-truth totals over the real fixture
        // ---------------------------------------------------------------

        [Test]
        public void Load_RealFixture_SessionUsageDisk_TotalsMatchGroundTruth()
        {
            TranscriptUsage usage;
            TranscriptLoader.Load(FixtureLoader.GetPath(UsageFixtureName), out usage);

            Assert.AreEqual(29, usage.TotalInputTokens,
                "TotalInputTokens must equal the ground truth computed by hand from the fixture");
            Assert.AreEqual(3147, usage.TotalOutputTokens,
                "TotalOutputTokens must be deduped by message.id (3147), NOT summed per JSONL line "
                + "(" + NaivePerLineOutputTokenSum + ") -- one API response is written as several "
                + "lines that each repeat the same usage object, so a naive per-line sum is 2.1x "
                + "too high");
            Assert.AreEqual(476769, usage.TotalCacheReadInputTokens,
                "TotalCacheReadInputTokens must equal the ground truth computed by hand from the fixture");
            Assert.AreEqual(26070, usage.TotalCacheCreationInputTokens,
                "TotalCacheCreationInputTokens must equal the ground truth computed by hand from the fixture");
        }

        [Test]
        public void Load_RealFixture_SessionUsageDisk_LastTurnModelUsage_ContainsOnlyFinalTurnOpusEntry()
        {
            // The fixture's final turn (after the third and last genuine
            // user prompt) contains exactly one non-synthetic assistant
            // line, model claude-opus-5 with output_tokens 52. A trailing
            // "<synthetic>" model line (a locally-generated placeholder,
            // never sent to the API) follows it and must be excluded
            // entirely rather than appearing as a meaningless extra row.
            TranscriptUsage usage;
            TranscriptLoader.Load(FixtureLoader.GetPath(UsageFixtureName), out usage);

            Assert.AreEqual(1, usage.LastTurnModelUsage.Count,
                "the last turn must contain exactly one model row");
            Assert.IsTrue(usage.LastTurnModelUsage.ContainsKey("claude-opus-5"));
            Assert.AreEqual(52, usage.LastTurnModelUsage["claude-opus-5"].OutputTokens);
            Assert.IsFalse(usage.LastTurnModelUsage.ContainsKey("<synthetic>"),
                "a synthetic placeholder response was never sent to the API and must not appear");
        }

        [Test]
        public void Load_RealFixture_SessionUsageDisk_LastTurnModelUsage_NeverCarriesContextWindowOrCost()
        {
            // Neither field exists anywhere in an on-disk transcript
            // (both are stream-only, see the TranscriptUsage class doc
            // comment) -- any nonzero value here would be invented data,
            // not something recovered from the file.
            TranscriptUsage usage;
            TranscriptLoader.Load(FixtureLoader.GetPath(UsageFixtureName), out usage);

            Assert.Greater(usage.LastTurnModelUsage.Count, 0,
                "the fixture must actually produce at least one row for this check to be meaningful");
            foreach (KeyValuePair<string, ModelUsage> entry in usage.LastTurnModelUsage)
            {
                Assert.AreEqual(0, entry.Value.ContextWindow,
                    "ContextWindow does not exist on disk for model " + entry.Key);
                Assert.AreEqual(0.0, entry.Value.CostUsd,
                    "CostUsd does not exist on disk for model " + entry.Key);
            }
        }

        [Test]
        public void Load_RealFixture_SessionUsageDisk_UserPromptCount_ExcludesToolResultUserLines()
        {
            // The fixture has 3 genuine user-authored text prompts and many
            // more role:"user" lines that are actually tool_result envelopes
            // (the CLI records tool results under role "user" on disk) --
            // those must not inflate the prompt count.
            TranscriptUsage usage;
            TranscriptLoader.Load(FixtureLoader.GetPath(UsageFixtureName), out usage);

            Assert.AreEqual(3, usage.UserPromptCount);
        }

        [Test]
        public void Load_RealFixture_SessionUsageDisk_MaxMessagesTrim_DoesNotAffectTotals()
        {
            // The totals cover the WHOLE file even when maxMessages trims
            // the returned message list -- the scan happens before the
            // trim (TranscriptLoader.Load doc comment). Prove both halves
            // of that claim: the list actually shrinks, and the totals
            // computed alongside the trimmed list are identical to the
            // untrimmed ones.
            string path = FixtureLoader.GetPath(UsageFixtureName);

            TranscriptUsage fullUsage;
            List<ChatMessage> fullMessages = TranscriptLoader.Load(path, out fullUsage);

            TranscriptUsage trimmedUsage;
            List<ChatMessage> trimmedMessages = TranscriptLoader.Load(path, out trimmedUsage, 1);

            Assert.Less(trimmedMessages.Count, fullMessages.Count,
                "maxMessages:1 must actually shrink the returned message list for this fixture");
            Assert.AreEqual(1, trimmedMessages.Count);

            Assert.AreEqual(29, trimmedUsage.TotalInputTokens);
            Assert.AreEqual(3147, trimmedUsage.TotalOutputTokens,
                "totals must be computed over the whole file, not just the " + trimmedMessages.Count
                + " message(s) that survived the maxMessages trim");
            Assert.AreEqual(476769, trimmedUsage.TotalCacheReadInputTokens);
            Assert.AreEqual(26070, trimmedUsage.TotalCacheCreationInputTokens);
        }

        // ---------------------------------------------------------------
        // Degrade paths: out usage must never be null, never throw
        // ---------------------------------------------------------------

        [Test]
        public void Load_EmptyPath_UsageNonNull_EmptyMessagesAndZeroTotals()
        {
            TranscriptUsage usage = null;
            List<ChatMessage> messages = null;
            Assert.DoesNotThrow(delegate
            {
                messages = TranscriptLoader.Load(string.Empty, out usage);
            });

            Assert.IsNotNull(usage, "out usage must never be null, even on the empty-path degrade path");
            Assert.IsNotNull(messages);
            Assert.IsEmpty(messages);
            AssertAllZero(usage);
        }

        [Test]
        public void Load_MissingFile_UsageNonNull_EmptyMessagesAndZeroTotals()
        {
            TranscriptUsage usage = null;
            List<ChatMessage> messages = null;
            string path = Path.Combine(_dir, "does-not-exist.jsonl");
            Assert.DoesNotThrow(delegate
            {
                messages = TranscriptLoader.Load(path, out usage);
            });

            Assert.IsNotNull(usage, "out usage must never be null, even on the missing-file degrade path");
            Assert.IsNotNull(messages);
            Assert.IsEmpty(messages);
            AssertAllZero(usage);
        }

        [Test]
        public void Load_GarbageTruncatedJson_UsageNonNull_EmptyMessagesAndZeroTotals()
        {
            string path = WriteFile(
                "not json at all",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\"",
                "{{{{",
                "");

            TranscriptUsage usage = null;
            List<ChatMessage> messages = null;
            Assert.DoesNotThrow(delegate
            {
                messages = TranscriptLoader.Load(path, out usage);
            });

            Assert.IsNotNull(usage, "out usage must never be null, even when every line fails to parse");
            Assert.IsNotNull(messages);
            Assert.IsEmpty(messages);
            AssertAllZero(usage);
        }

        private static void AssertAllZero(TranscriptUsage usage)
        {
            Assert.AreEqual(0, usage.TotalInputTokens);
            Assert.AreEqual(0, usage.TotalOutputTokens);
            Assert.AreEqual(0, usage.TotalCacheReadInputTokens);
            Assert.AreEqual(0, usage.TotalCacheCreationInputTokens);
            Assert.AreEqual(0, usage.UserPromptCount);
            Assert.AreEqual(0, usage.LastTurnModelUsage.Count);
        }

        // ---------------------------------------------------------------
        // TranscriptUsage.Accumulate: argument-validation edge cases
        // ---------------------------------------------------------------

        [Test]
        public void Accumulate_NullMessage_ReturnsFalse_AndChangesNothing()
        {
            TranscriptUsage usage = new TranscriptUsage();
            bool primed = usage.Accumulate(
                BuildAssistantMessage("msg-1", "claude-opus-5", 10, 20, 30, 40));
            Assert.IsTrue(primed, "the priming call must itself succeed for this test to be meaningful");

            bool result = usage.Accumulate(null);

            Assert.IsFalse(result);
            AssertTotals(usage, 10, 20, 30, 40);
            Assert.AreEqual(1, usage.LastTurnModelUsage.Count);
            Assert.AreEqual(20, usage.LastTurnModelUsage["claude-opus-5"].OutputTokens);
        }

        [Test]
        public void Accumulate_MessageWithNoUsageObject_ReturnsFalse_AndChangesNothing()
        {
            TranscriptUsage usage = new TranscriptUsage();
            bool primed = usage.Accumulate(
                BuildAssistantMessage("msg-1", "claude-opus-5", 10, 20, 30, 40));
            Assert.IsTrue(primed, "the priming call must itself succeed for this test to be meaningful");

            // A message object that has an id and a model but no "usage"
            // key at all (as opposed to an empty or malformed one).
            JsonNode noUsageMessage = JsonNode.NewObject()
                .Set("id", "msg-2")
                .Set("model", "claude-opus-5");

            bool result = usage.Accumulate(noUsageMessage);

            Assert.IsFalse(result);
            AssertTotals(usage, 10, 20, 30, 40);
            Assert.AreEqual(1, usage.LastTurnModelUsage.Count);
            Assert.AreEqual(20, usage.LastTurnModelUsage["claude-opus-5"].OutputTokens);
        }

        [Test]
        public void Accumulate_RepeatMessageId_ReturnsFalse_AndChangesNothing()
        {
            // Same message.id as an already-counted line, but with
            // DIFFERENT usage numbers -- if dedup ever broke, the totals
            // below would visibly move instead of silently staying right
            // by coincidence.
            TranscriptUsage usage = new TranscriptUsage();
            bool primed = usage.Accumulate(
                BuildAssistantMessage("msg-1", "claude-opus-5", 10, 20, 30, 40));
            Assert.IsTrue(primed, "the priming call must itself succeed for this test to be meaningful");

            bool result = usage.Accumulate(
                BuildAssistantMessage("msg-1", "claude-opus-5", 999, 999, 999, 999));

            Assert.IsFalse(result);
            AssertTotals(usage, 10, 20, 30, 40);
            Assert.AreEqual(1, usage.LastTurnModelUsage.Count);
            Assert.AreEqual(20, usage.LastTurnModelUsage["claude-opus-5"].OutputTokens);
        }

        private static void AssertTotals(TranscriptUsage usage, long input, long output,
            long cacheRead, long cacheCreate)
        {
            Assert.AreEqual(input, usage.TotalInputTokens);
            Assert.AreEqual(output, usage.TotalOutputTokens);
            Assert.AreEqual(cacheRead, usage.TotalCacheReadInputTokens);
            Assert.AreEqual(cacheCreate, usage.TotalCacheCreationInputTokens);
        }

        private static JsonNode BuildAssistantMessage(string id, string model, long inputTokens,
            long outputTokens, long cacheReadTokens, long cacheCreateTokens)
        {
            JsonNode usageNode = JsonNode.NewObject()
                .Set("input_tokens", inputTokens)
                .Set("output_tokens", outputTokens)
                .Set("cache_read_input_tokens", cacheReadTokens)
                .Set("cache_creation_input_tokens", cacheCreateTokens);
            return JsonNode.NewObject()
                .Set("id", id)
                .Set("model", model)
                .Set("usage", usageNode);
        }
    }
}

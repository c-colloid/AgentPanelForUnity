using System.IO;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The boot-time transcript backfill
    /// (docs/design-notes/2026-08-05-boot-model-usage.md section 6).
    ///
    /// Origin: after v0.20.1 persisted the per-model snapshot, a cache
    /// written by an OLDER version still booted with an empty usage popover
    /// -- and the user pointed out that switching to another History entry
    /// and back filled it in. That workaround works because SwitchToSession
    /// rebuilds the snapshot from the CLI transcript; these tests pin that
    /// the boot path now performs the same rebuild when (and only when) the
    /// cache has no snapshot to offer.
    ///
    /// Only the explicit-path half (BackfillModelUsageFromTranscript) is
    /// exercised: the discovery half resolves the REAL project root, which
    /// an EditMode test cannot control. All fixture literals are strict
    /// ASCII, per this suite's convention.
    /// </summary>
    public class AgentHubModelUsageBackfillTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            AgentHub.ResetForTests();
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelBackfillTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            AgentHub.ResetForTests();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        private string WriteTranscript(params string[] lines)
        {
            string path = Path.Combine(_dir, "fixture-session.jsonl");
            File.WriteAllLines(path, lines);
            return path;
        }

        private static string AssistantLine(string id, string model, long output)
        {
            return "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"model\":\"" + model
                + "\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],"
                + "\"usage\":{\"input_tokens\":10,\"output_tokens\":" + output
                + ",\"cache_read_input_tokens\":5,\"cache_creation_input_tokens\":0}}}";
        }

        private static ChatSession SessionWithHistory()
        {
            return new ChatSession
            {
                sessionId = "fixture-session",
                totalInputTokens = 10,
                totalOutputTokens = 20,
                completedTurns = 1,
            };
        }

        [Test]
        public void Backfill_TranscriptWithUsage_PopulatesLastModelUsage_AndReportsTrue()
        {
            string path = WriteTranscript(AssistantLine("m1", "claude-opus-5", 42));

            bool applied = AgentHub.BackfillModelUsageFromTranscript(SessionWithHistory(), path);

            Assert.IsTrue(applied);
            Assert.AreEqual(1, AgentHub.LastModelUsage.Count);
            Assert.AreEqual(42, AgentHub.LastModelUsage["claude-opus-5"].OutputTokens,
                "the snapshot must carry the transcript's numbers, exactly like SwitchToSession's");
        }

        [Test]
        public void Backfill_MissingTranscript_ReportsFalse_AndTouchesNothing()
        {
            bool applied = AgentHub.BackfillModelUsageFromTranscript(
                SessionWithHistory(), Path.Combine(_dir, "no-such-file.jsonl"));

            Assert.IsFalse(applied);
            Assert.AreEqual(0, AgentHub.LastModelUsage.Count);
        }

        [Test]
        public void Backfill_TranscriptWithNoUsageLines_ReportsFalse()
        {
            // A transcript can legitimately contain no per-model usage at
            // all (e.g. only synthetic/error lines survived). The backfill
            // must degrade to the empty state rather than fabricate one --
            // reporting an outcome that was not observed is this project's
            // dominant defect class.
            string path = WriteTranscript("{\"type\":\"user\",\"message\":{\"role\":\"user\","
                + "\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}");

            bool applied = AgentHub.BackfillModelUsageFromTranscript(SessionWithHistory(), path);

            Assert.IsFalse(applied);
            Assert.AreEqual(0, AgentHub.LastModelUsage.Count);
        }
    }
}

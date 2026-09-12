using System;
using System.IO;
using System.Threading;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The 2026-09-12 defect, end to end (design note
    /// docs/design-notes/2026-09-12-session-cache-transient-read-failure.md).
    ///
    /// Reported as "every compile switches the panel to a new session and
    /// the conversation history is gone". The compile itself was innocent:
    /// the pre-reload save rewrites SessionCache.json, an antivirus/indexer
    /// opens the freshly replaced file, and the post-reload read fails with
    /// "Sharing violation" (six such failures in one measured editor
    /// session, one per reload). The Session getter folded that failure into
    /// `loaded ?? new ChatSession()`, and the next save wrote that empty
    /// session over the real transcript -- a lock lasting milliseconds
    /// turned into permanent data loss.
    ///
    /// These tests hold a real exclusive lock on a real temp cache file, so
    /// they exercise the production Load/Save paths rather than a stand-in.
    /// All fixture literals are strict ASCII, per this suite's convention.
    /// </summary>
    public class AgentHubSessionCacheRecoveryTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            AgentHub.ResetForTests();
            _dir = Path.Combine(Path.GetTempPath(),
                "AgentPanelCacheRecoveryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "SessionCache.json");
            AgentHub.SetSessionCacheForTests(new SessionCacheFile(_path));
        }

        [TearDown]
        public void TearDown()
        {
            // Restore the real project cache for every other fixture in the
            // run (AgentHub statics are shared), then clear the session.
            AgentHub.SetSessionCacheForTests(null);
            AgentHub.ResetForTests();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        /// <summary>Plants a cache holding one user message, as a real session would.</summary>
        private void PlantTranscript()
        {
            var session = new ChatSession { sessionId = "planted-session", title = "planted" };
            var message = new ChatMessage { role = ChatMessage.RoleUser };
            message.Add(ChatMessageBlock.MakeText("the conversation the user must not lose"));
            session.AddMessage(message);
            session.completedTurns = 3;
            session.totalInputTokens = 100;
            new SessionCacheFile(_path).Save(session);
            Assert.IsTrue(File.Exists(_path), "precondition: a cache to lose");
        }

        private static FileStream LockExclusively(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        [Test]
        public void LockedCache_IsNeverOverwrittenByTheEmptyStandInSession()
        {
            // THE regression. Read fails -> panel shows an empty session ->
            // the next save must NOT persist it.
            PlantTranscript();
            string before = File.ReadAllText(_path);
            using (LockExclusively(_path))
            {
                Assert.AreEqual(0, AgentHub.Session.messages.Count,
                    "the stand-in session is empty -- that part is unavoidable");
                Assert.IsTrue(AgentHub.SessionCacheUnreadableForTests,
                    "the failed read must be remembered, not silently forgotten");
                AgentHub.SaveSessionCache();
            }
            Assert.AreEqual(before, File.ReadAllText(_path),
                "saving over a cache we failed to read is what destroyed the transcript");
        }

        [Test]
        public void OnceTheLockClears_TheTranscriptComesBack_AndSavingResumes()
        {
            PlantTranscript();
            using (LockExclusively(_path))
            {
                Assert.AreEqual(0, AgentHub.Session.messages.Count);
            }
            // The retry is throttled so a locked read cannot stutter every
            // repaint; wait out one interval, then touch the getter again.
            Thread.Sleep(300);

            ChatSession recovered = AgentHub.Session;

            Assert.AreEqual(1, recovered.messages.Count, "the planted transcript is back");
            Assert.AreEqual("planted-session", recovered.sessionId);
            Assert.AreEqual(3, recovered.completedTurns);
            Assert.IsFalse(AgentHub.SessionCacheUnreadableForTests,
                "a readable cache must unblock saving");
        }

        [Test]
        public void RecoveryKeepsWhateverArrivedWhileTheCacheWasUnreadable()
        {
            // A resumed CLI keeps talking while the cache is locked. Those
            // messages land on the stand-in session, so the recovery has to
            // splice rather than replace -- or the fix would trade one kind
            // of message loss for another.
            PlantTranscript();
            using (LockExclusively(_path))
            {
                var arrived = new ChatMessage { role = ChatMessage.RoleAssistant };
                arrived.Add(ChatMessageBlock.MakeText("reply that arrived during the lock"));
                AgentHub.Session.AddMessage(arrived);
            }
            Thread.Sleep(300);

            ChatSession recovered = AgentHub.Session;

            Assert.AreEqual(2, recovered.messages.Count);
            Assert.AreEqual(ChatMessage.RoleUser, recovered.messages[0].role,
                "the restored transcript comes first...");
            Assert.AreEqual(ChatMessage.RoleAssistant, recovered.messages[1].role,
                "...and what arrived during the lock is appended after it");
        }

        [Test]
        public void MergeRecoveredCache_SumsTheStandInsDeltas_WithoutDoubleCounting()
        {
            // The stand-in starts at zero, so its counters ARE the deltas
            // accrued since the failed read -- except cost, which is the
            // CLI's own running total (ChatSession.AccumulateTurn's
            // monotonic rule) and is taken as a maximum, not a sum.
            var restored = new ChatSession
            {
                sessionId = "old",
                title = "restored",
                totalInputTokens = 100,
                totalOutputTokens = 200,
                completedTurns = 3,
                totalCostUsd = 1.5
            };
            var live = new ChatSession
            {
                sessionId = "resumed",
                agentBackend = 1,
                totalInputTokens = 10,
                totalOutputTokens = 20,
                completedTurns = 1,
                totalCostUsd = 0.4,
                lastActivityTimestamp = "2026-09-12T00:00:00.0000000Z"
            };

            ChatSession merged = AgentHub.MergeRecoveredCache(restored, live);

            Assert.AreEqual(110, merged.totalInputTokens);
            Assert.AreEqual(220, merged.totalOutputTokens);
            Assert.AreEqual(4, merged.completedTurns);
            Assert.AreEqual(1.5, merged.totalCostUsd, 0.0001, "cost is monotonic, never summed");
            Assert.AreEqual("resumed", merged.sessionId,
                "the live id is the one the post-reload --resume reconnected to");
            Assert.AreEqual(1, merged.agentBackend);
            Assert.AreEqual("restored", merged.title, "an existing title is not clobbered");
            Assert.AreEqual("2026-09-12T00:00:00.0000000Z", merged.lastActivityTimestamp);
        }

        [Test]
        public void MergeRecoveredCache_NullSides_ReturnTheOtherOne()
        {
            var session = new ChatSession { sessionId = "only" };
            Assert.AreSame(session, AgentHub.MergeRecoveredCache(null, session));
            Assert.AreSame(session, AgentHub.MergeRecoveredCache(session, null));
        }

        [Test]
        public void NoCacheAtAll_StillSavesNormally()
        {
            // The other null: nothing on disk is not a failed read, so the
            // block must never arm for it (otherwise a brand-new project
            // would never persist its first conversation).
            Assert.AreEqual(0, AgentHub.Session.messages.Count);
            Assert.IsFalse(AgentHub.SessionCacheUnreadableForTests);

            AgentHub.SaveSessionCache();

            Assert.IsTrue(File.Exists(_path), "the first save must go through");
        }
    }
}

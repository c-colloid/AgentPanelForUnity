using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The shared compaction wording (CompactionNote) and the status bar's
    /// "compacted" gate (StatusBarView.ShowCompactedState) -- design note
    /// docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// sections 2.2 / 2.3.
    /// </summary>
    public class CompactionNoteTests
    {
        [Test]
        public void Describe_Manual_WithTokens_UsesTheShortTokenForm()
        {
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedManualFmt, "84.2k"),
                CompactionNote.Describe("manual", 84213));
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedManualFmt, "1.2M"),
                CompactionNote.Describe("manual", 1200000));
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedManualFmt, "512"),
                CompactionNote.Describe("manual", 512));
        }

        [Test]
        public void Describe_Auto_WithTokens()
        {
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedAutoFmt, "190.0k"),
                CompactionNote.Describe("auto", 190000));
        }

        [Test]
        public void Describe_NoTokens_OmitsTheClause()
        {
            Assert.AreEqual(L10n.S.HubCompactedManual, CompactionNote.Describe("manual", -1));
            Assert.AreEqual(L10n.S.HubCompactedAuto, CompactionNote.Describe("auto", -1));
        }

        [Test]
        public void Describe_UnknownTrigger_ReadsAsAuto()
        {
            // An unrecognized (or missing) trigger still means the CLI did
            // it, not the user -- never claim a /compact the user did not type.
            Assert.AreEqual(L10n.S.HubCompactedAuto, CompactionNote.Describe(string.Empty, -1));
            Assert.AreEqual(L10n.F(L10n.S.HubCompactedAutoFmt, "10.0k"), CompactionNote.Describe(null, 10000));
        }

        [Test]
        public void ShowCompactedState_OnlyWhileFlaggedAndNoReading()
        {
            Assert.IsTrue(StatusBarView.ShowCompactedState(true, -1));
            Assert.IsFalse(StatusBarView.ShowCompactedState(true, 12000), "a real reading always wins");
            Assert.IsFalse(StatusBarView.ShowCompactedState(false, -1));
            Assert.IsFalse(StatusBarView.ShowCompactedState(false, 12000));
        }

        [Test]
        public void TokenCountFormat_Short()
        {
            Assert.AreEqual("0", TokenCountFormat.Short(0));
            Assert.AreEqual("999", TokenCountFormat.Short(999));
            Assert.AreEqual("1.0k", TokenCountFormat.Short(1000));
            Assert.AreEqual("28.3k", TokenCountFormat.Short(28299));
            Assert.AreEqual("1.0M", TokenCountFormat.Short(1000000));
        }
    }
}

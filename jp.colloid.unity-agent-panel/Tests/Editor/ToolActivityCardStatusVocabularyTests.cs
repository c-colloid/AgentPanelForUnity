using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-05 UI redesign, D3: a tool the user DENIED and a tool that
    /// FAILED are two different facts and must not share a glyph class or
    /// an accent bar. Before this, ToolActivityCard.CreateStatusIcon mapped
    /// both to the same red cross and CreateGroupRow folded a failing
    /// child into a group card with no accent at all.
    /// </summary>
    [TestFixture]
    public class ToolActivityCardStatusVocabularyTests
    {
        [SetUp]
        public void SetUp()
        {
            ToolActivityCard.ResetExpandedStateForTests();
        }

        [TearDown]
        public void TearDown()
        {
            ToolActivityCard.ResetExpandedStateForTests();
        }

        private static ToolCallRecord Make(string id, ToolCallStatus status)
        {
            return new ToolCallRecord
            {
                toolUseId = id,
                toolName = "Bash",
                status = status,
                inputJson = "{\"command\":\"ls\"}",
                resultSummary = "done"
            };
        }

        /// <summary>
        /// Identity of a status icon regardless of environment: on a real
        /// editor IconLoader.CreateIcon resolves the built-in texture and
        /// returns an Image (the texture name is the key); when the name
        /// does not resolve it returns the fallback Label (its class list
        /// is the key). Denied and failed must yield DIFFERENT keys in
        /// both worlds -- that is the whole point of D3.
        /// </summary>
        private static string StatusIconKey(VisualElement header)
        {
            foreach (VisualElement child in header.Children())
            {
                var image = child as Image;
                if (image != null && image.ClassListContains("uap-tool-icon"))
                {
                    return "image:" + (image.image != null ? image.image.name : "null");
                }
                var label = child as Label;
                if (label != null && label.ClassListContains("uap-tool-glyph"))
                {
                    return "glyph:" + string.Join(" ", label.GetClasses());
                }
            }
            return null;
        }

        private static string CardIconKey(ToolActivityCard card)
        {
            VisualElement header = card.Q<VisualElement>(className: "uap-toolcard-header");
            Assert.IsNotNull(header, "card must render a header row");
            return StatusIconKey(header);
        }

        [Test]
        public void DeniedCard_UsesTheDeniedAccent_NotTheFailedOne()
        {
            var card = new ToolActivityCard(Make("toolu_denied", ToolCallStatus.Denied));

            Assert.IsTrue(card.ClassListContains("uap-toolcard--denied"));
            Assert.IsFalse(card.ClassListContains("uap-toolcard--failed"));
        }

        [Test]
        public void FailedCard_KeepsTheFailedAccent()
        {
            var card = new ToolActivityCard(Make("toolu_failed", ToolCallStatus.Failed));

            Assert.IsTrue(card.ClassListContains("uap-toolcard--failed"));
            Assert.IsFalse(card.ClassListContains("uap-toolcard--denied"));
        }

        [Test]
        public void DeniedAndFailedCards_RenderDifferentStatusIcons()
        {
            string denied = CardIconKey(new ToolActivityCard(Make("toolu_d", ToolCallStatus.Denied)));
            string failed = CardIconKey(new ToolActivityCard(Make("toolu_f", ToolCallStatus.Failed)));
            string ok = CardIconKey(new ToolActivityCard(Make("toolu_ok", ToolCallStatus.Succeeded)));

            Assert.IsNotNull(denied);
            Assert.IsNotNull(failed);
            Assert.AreNotEqual(failed, denied, "denied must not reuse the failed icon");
            Assert.AreNotEqual(ok, denied, "denied must not reuse the success icon");
        }

        [TestCase(false, false, null)]
        [TestCase(false, true, "uap-toolgroup--denied")]
        [TestCase(true, false, "uap-toolgroup--failed")]
        [TestCase(true, true, "uap-toolgroup--failed")]
        public void ResolveGroupModifier_FailedOutranksDenied(bool anyFailed, bool anyDenied, string expected)
        {
            Assert.AreEqual(expected, ToolActivityCard.ResolveGroupModifier(anyFailed, anyDenied));
        }

        [Test]
        public void GroupRow_CarriesTheWorstChildsAccentAndGlyph()
        {
            var records = new List<ToolCallRecord>
            {
                Make("g1", ToolCallStatus.Succeeded),
                Make("g2", ToolCallStatus.Denied),
                Make("g3", ToolCallStatus.Succeeded)
            };

            VisualElement group = ToolActivityCard.CreateGroupRow(records);

            Assert.IsTrue(group.ClassListContains("uap-toolgroup--denied"));
            Assert.IsFalse(group.ClassListContains("uap-toolgroup--failed"));
            VisualElement header = group.Q<VisualElement>(className: "uap-toolgroup-header");
            Assert.IsNotNull(header);
            string deniedKey = StatusIconKey(header);

            var failing = new List<ToolCallRecord>
            {
                Make("f1", ToolCallStatus.Succeeded),
                Make("f2", ToolCallStatus.Failed)
            };
            VisualElement failedGroup = ToolActivityCard.CreateGroupRow(failing);
            Assert.IsTrue(failedGroup.ClassListContains("uap-toolgroup--failed"));
            string failedKey = StatusIconKey(
                failedGroup.Q<VisualElement>(className: "uap-toolgroup-header"));

            Assert.IsNotNull(deniedKey);
            Assert.AreNotEqual(failedKey, deniedKey,
                "a denied-only group must not wear the failed group's icon");
        }

        [Test]
        public void GroupRow_AllSucceeded_HasNoAccentModifier()
        {
            var records = new List<ToolCallRecord>
            {
                Make("ok1", ToolCallStatus.Succeeded),
                Make("ok2", ToolCallStatus.Succeeded)
            };

            VisualElement group = ToolActivityCard.CreateGroupRow(records);

            Assert.IsFalse(group.ClassListContains("uap-toolgroup--denied"));
            Assert.IsFalse(group.ClassListContains("uap-toolgroup--failed"));
        }
    }
}

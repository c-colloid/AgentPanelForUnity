using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The composer's slash-command popup driven through its real
    /// value-changed and OnKeyDown paths (the ComposerEnterActionTests
    /// seams; panel-less, so caret placement is skipped) -- design note
    /// docs/design-notes/2026-09-07-slash-commands-and-compaction.md
    /// section 1.3.
    /// </summary>
    public class ComposerSlashCommandTests
    {
        private List<SlashCommandEntry> _savedCatalog;
        private bool _savedCtrlEnter;
        private string _savedDraft;

        [SetUp]
        public void SetUp()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            _savedCatalog = settings.slashCommandCatalog;
            _savedCtrlEnter = settings.ctrlEnterToSend;
            _savedDraft = SessionStateBridge.InputDraft;
            settings.slashCommandCatalog = new List<SlashCommandEntry>
            {
                new SlashCommandEntry { name = "compact", description = "Summarize", argumentHint = "[instructions]" },
                new SlashCommandEntry { name = "review", description = "Review code" },
                new SlashCommandEntry { name = "context", description = "Show context" }
            };
            settings.ctrlEnterToSend = false; // Enter sends
            SessionStateBridge.InputDraft = string.Empty;
        }

        [TearDown]
        public void TearDown()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.slashCommandCatalog = _savedCatalog;
            settings.ctrlEnterToSend = _savedCtrlEnter;
            SessionStateBridge.InputDraft = _savedDraft;
        }

        private static void Key(ComposerView composer, KeyCode code, char character = '\0')
        {
            using (KeyDownEvent evt = KeyDownEvent.GetPooled(character, code, EventModifiers.None))
            {
                composer.SimulateKeyDownForTests(evt);
            }
        }

        private static List<string> Names(IList<SlashCommandEntry> entries)
        {
            var names = new List<string>();
            foreach (SlashCommandEntry entry in entries)
            {
                names.Add(entry.name);
            }
            return names;
        }

        [Test]
        public void TypingSlash_OpensThePopup_WithBuiltinsFirst()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            Assert.IsFalse(composer.SlashPopupVisibleForTests);

            composer.SetFieldTextForTests("/");
            Assert.IsTrue(composer.SlashPopupVisibleForTests);
            List<string> names = Names(composer.SlashMatchesForTests);
            Assert.AreEqual("compact", names[0]);
            Assert.AreEqual("clear", names[1], "/clear is always offered, even when the CLI list lacks it");
            Assert.Contains("review", names);
            Assert.AreEqual(0, composer.SlashSelectedForTests);
            Assert.AreEqual(L10n.S.ComposerSlashHint, composer.HintTextForTests);
        }

        [Test]
        public void Prefix_FiltersThePopup_AndASpaceClosesIt()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);

            composer.SetFieldTextForTests("/co");
            Assert.IsTrue(composer.SlashPopupVisibleForTests);
            CollectionAssert.AreEqual(new[] { "compact", "context" }, Names(composer.SlashMatchesForTests));

            composer.SetFieldTextForTests("/compact ");
            Assert.IsFalse(composer.SlashPopupVisibleForTests, "arguments mode: the popup is gone");
            Assert.AreNotEqual(L10n.S.ComposerSlashHint, composer.HintTextForTests);

            composer.SetFieldTextForTests("plain prose");
            Assert.IsFalse(composer.SlashPopupVisibleForTests);
        }

        [Test]
        public void NoMatch_KeepsThePopupOpen_WithNoSelection()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/zzz");
            Assert.IsTrue(composer.SlashPopupVisibleForTests);
            Assert.AreEqual(0, composer.SlashMatchesForTests.Count);
            Assert.AreEqual(-1, composer.SlashSelectedForTests);
        }

        [Test]
        public void ArrowKeys_MoveTheSelection_AndWrap()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/co");

            Key(composer, KeyCode.DownArrow);
            Assert.AreEqual(1, composer.SlashSelectedForTests);
            Key(composer, KeyCode.DownArrow);
            Assert.AreEqual(0, composer.SlashSelectedForTests, "wraps to the top");
            Key(composer, KeyCode.UpArrow);
            Assert.AreEqual(1, composer.SlashSelectedForTests, "wraps to the bottom");
            Assert.AreEqual("/co", composer.FieldTextForTests, "arrows never touch the text");
        }

        [Test]
        public void Tab_CompletesTheSelection_AndSwallowsThePairedTabCharacter()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/co");
            Key(composer, KeyCode.DownArrow); // -> context

            Key(composer, KeyCode.Tab);
            Assert.AreEqual("/context ", composer.FieldTextForTests);
            Assert.IsFalse(composer.SlashPopupVisibleForTests, "completion leaves prefix mode");

            // The paired '\t' character event must not reach the field.
            Key(composer, KeyCode.None, '\t');
            Assert.AreEqual("/context ", composer.FieldTextForTests);
        }

        [Test]
        public void Enter_OnAPartialPrefix_Completes_InsteadOfSending()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/comp");

            Key(composer, KeyCode.Return);
            Assert.AreEqual("/compact ", composer.FieldTextForTests,
                "Enter on a highlighted suggestion completes it; nothing was sent");
            Assert.AreEqual(ComposerView.EnterAction.Ignore, composer.PendingEnterActionForTests,
                "the paired character event must neither send nor insert a newline");

            Key(composer, KeyCode.None, '\n');
            Assert.AreEqual("/compact ", composer.FieldTextForTests, "no newline landed in the field");
            Assert.AreEqual(ComposerView.EnterAction.Unknown, composer.PendingEnterActionForTests);
        }

        [Test]
        public void Escape_ClosesThePopup_WithoutTouchingTheText()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/co");
            Assert.IsTrue(composer.SlashPopupVisibleForTests);

            Key(composer, KeyCode.Escape);
            Assert.IsFalse(composer.SlashPopupVisibleForTests);
            Assert.AreEqual("/co", composer.FieldTextForTests);
        }

        [Test]
        public void DisabledInput_NeverShowsThePopup()
        {
            var composer = new ComposerView();
            composer.Refresh(null, true);
            composer.SetFieldTextForTests("/co");
            Assert.IsTrue(composer.SlashPopupVisibleForTests);

            composer.Refresh(null, false);
            Assert.IsFalse(composer.SlashPopupVisibleForTests);
        }

        [Test]
        public void RestoredDraft_StartingWithSlash_OpensThePopupOnConstruction()
        {
            SessionStateBridge.InputDraft = "/rev";
            var composer = new ComposerView();
            Assert.IsTrue(composer.SlashPopupVisibleForTests);
            CollectionAssert.AreEqual(new[] { "review" }, Names(composer.SlashMatchesForTests));
        }
    }
}

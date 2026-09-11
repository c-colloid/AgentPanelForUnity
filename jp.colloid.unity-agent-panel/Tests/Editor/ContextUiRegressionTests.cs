using System.Reflection;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression suite for the context-strip / empty-state review
    /// findings:
    /// - Chip labels carry asset/GameObject names, which are
    ///   model-writable via tools; they bypass the InlineMarkupConverter
    ///   chokepoint, so every chip-strip Label must keep
    ///   enableRichText = false (TextElement defaults it to TRUE).
    /// - EmptyStateView.Detach must reset the visibility latch so a
    ///   deactivate/reactivate cycle re-subscribes the editor events
    ///   (otherwise the dynamic suggestion chips freeze).
    /// </summary>
    public class ContextUiRegressionTests
    {
        [Test]
        public void ContextChip_Labels_NeverEnableRichText()
        {
            var bar = new ContextBarView();
            var asset = new TextAsset("payload");
            try
            {
                // A name the agent could give an asset via tools: without
                // enableRichText = false this would render as live rich
                // text in the chip.
                asset.name = "<color=#00ff00>Evil.cs";
                bar.AddObjectChips(new Object[] { asset });

                var labels = bar.Root.Query<Label>(
                    className: "uap-ctx-chip-label").ToList();
                // Dropped-object chip label + console-error chip label.
                Assert.GreaterOrEqual(labels.Count, 2,
                    "expected the dropped chip and the error chip labels");
                for (int i = 0; i < labels.Count; i++)
                {
                    Assert.IsFalse(labels[i].enableRichText,
                        "chip label must stay plain text: " + labels[i].text);
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ContextChip_DroppedObjectLabel_KeepsRawName()
        {
            var bar = new ContextBarView();
            var asset = new TextAsset("payload");
            try
            {
                asset.name = "<b>Safe.cs";
                bar.AddObjectChips(new Object[] { asset });
                Label label = bar.Root.Q<Label>(className: "uap-ctx-chip-label");
                Assert.IsNotNull(label);
                // The error chip label is empty until errors exist, so the
                // first non-empty one is the dropped chip.
                var labels = bar.Root.Query<Label>(
                    className: "uap-ctx-chip-label").ToList();
                bool found = false;
                for (int i = 0; i < labels.Count; i++)
                {
                    if (labels[i].text == "<b>Safe.cs")
                    {
                        found = true;
                        Assert.IsFalse(labels[i].enableRichText);
                    }
                }
                Assert.IsTrue(found, "dropped chip label should show the raw name");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ContextAttachmentBlock_RendersCollapsed_WithPlainLabels()
        {
            var block = Colloid.AgentPanel.Model.ChatMessageBlock.MakeContextAttachment(
                "<b>GameObject: Evil", "payload with <color=red>tags</color>");
            VisualElement element = MessageBlockFactory.CreateContextAttachmentBlock(block);

            // Collapsed by default: no payload box exists until expanded.
            Assert.IsNull(element.Q(className: "uap-attach-scroll"));

            // Title/glyph labels never enable rich text (titles carry
            // model-writable object names outside the chokepoint).
            var labels = element.Query<Label>().ToList();
            Assert.GreaterOrEqual(labels.Count, 2);
            for (int i = 0; i < labels.Count; i++)
            {
                Assert.IsFalse(labels[i].enableRichText,
                    "attachment label must stay plain text: " + labels[i].text);
            }
            Label title = element.Q<Label>(className: "uap-attach-title");
            Assert.IsNotNull(title);
            Assert.AreEqual("<b>GameObject: Evil", title.text);
        }

        [Test]
        public void UserMessage_WithAttachmentBlocks_DoesNotRenderWireDelimiters()
        {
            var message = new Colloid.AgentPanel.Model.ChatMessage
            {
                role = Colloid.AgentPanel.Model.ChatMessage.RoleUser
            };
            message.Add(Colloid.AgentPanel.Model.ChatMessageBlock.MakeText("fix this"));
            message.Add(Colloid.AgentPanel.Model.ChatMessageBlock.MakeContextAttachment(
                "Console errors (2)", "E1\nE2"));
            VisualElement element = MessageBlockFactory.CreateMessageElement(message, null);

            Assert.IsNotNull(element.Q(className: "uap-attach"),
                "the attachment chip row must render");
            // The wire delimiter text must never appear anywhere in the
            // rendered user message (display is decoupled from the wire).
            var allLabels = element.Query<Label>().ToList();
            for (int i = 0; i < allLabels.Count; i++)
            {
                StringAssert.DoesNotContain("ATTACHED UNITY EDITOR CONTEXT",
                    allLabels[i].text ?? string.Empty);
            }
        }

        /// <summary>
        /// UICODE-9: quick actions live on disk and only change via
        /// Settings, yet every Selection/Console change while the empty
        /// state was visible re-read QuickActions.json. Reloads now happen
        /// on construction and on each show; editor-state churn rebuilds
        /// the chips from the CACHE.
        /// </summary>
        [Test]
        public void EmptyState_EditorStateChurn_DoesNotRereadQuickActionsFromDisk()
        {
            var view = new EmptyStateView(delegate { });
            view.SetVisible(true);
            int reloadsAfterShow = view.QuickActionReloadCountForTests;

            for (int i = 0; i < 5; i++)
            {
                FireEditorStateChanged(view);
            }
            Assert.AreEqual(reloadsAfterShow, view.QuickActionReloadCountForTests,
                "selection/console churn must rebuild chips from the cache, not from disk");

            view.SetVisible(false);
            view.SetVisible(true);
            Assert.AreEqual(reloadsAfterShow + 1, view.QuickActionReloadCountForTests,
                "each SHOW reloads once, picking up Settings edits made meanwhile");
        }

        private static void FireEditorStateChanged(EmptyStateView view)
        {
            typeof(EmptyStateView)
                .GetMethod("OnEditorStateChanged",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(view, null);
        }

        [Test]
        public void EmptyState_DetachThenShow_Resubscribes()
        {
            var view = new EmptyStateView(delegate { });
            view.SetVisible(true);
            Assert.IsTrue(IsSubscribed(view), "SetVisible(true) subscribes");

            // OnDeactivate path.
            view.Detach();
            Assert.IsFalse(IsSubscribed(view), "Detach unsubscribes");

            // OnActivate -> Refresh -> SetVisible(true) on the SAME
            // instance: without the latch reset in Detach this hit the
            // equality early-return and never re-subscribed.
            view.SetVisible(true);
            Assert.IsTrue(IsSubscribed(view),
                "Detach must reset the visibility latch so the next"
                + " SetVisible(true) re-subscribes");
        }

        private static bool IsSubscribed(EmptyStateView view)
        {
            FieldInfo field = typeof(EmptyStateView).GetField("_subscribed",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "_subscribed field moved or renamed");
            return (bool)field.GetValue(view);
        }
    }
}

using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Review defect fix (docs/design-notes/2026-07-31-subagent-display.md
    /// section 7b): ComputeSignature used to hash subagent.progressLine's
    /// Length and subagent.totalTokens. AgentHub mutates both on every
    /// task_progress / task_updated event -- far more often than ChatView's
    /// 60ms Refresh tick -- so a running SubagentCard's row was torn down
    /// and rebuilt on almost every refresh, and a freshly-built SubagentCard
    /// always starts collapsed (SubagentCard._expanded defaults false): a
    /// user could never keep a running subagent's card open.
    ///
    /// The fix drops progressLine/totalTokens (and lastToolName, which was
    /// never hashed at all and so was already stale between unrelated
    /// rebuilds) from the signature entirely -- SubagentCard now live-
    /// updates those fields itself via its own scheduler (SubagentCard.
    /// StartLiveUpdate) -- and keeps only structural fields: status,
    /// blocks.Count, droppedBlockCount.
    ///
    /// Uses MessageListController.ComputeSignatureForTests, an internal
    /// seam visible via the existing InternalsVisibleTo("Colloid.AgentPanel.
    /// Editor.Tests") (Editor/AssemblyInfo.cs, same *ForTests idiom as
    /// AgentHub.ResetForTests / WireClientForTests) since ComputeSignature
    /// itself stays private.
    /// </summary>
    public class MessageListControllerSignatureTests
    {
        private static ChatMessage BuildSubagentMessage(SubagentRecord subagent)
        {
            var message = new ChatMessage { role = ChatMessage.RoleAssistant };
            var toolCall = new ToolCallRecord
            {
                toolUseId = "toolu_sig_test",
                toolName = "Agent",
                status = ToolCallStatus.Running,
                subagent = subagent
            };
            message.Add(ChatMessageBlock.MakeToolCall(toolCall));
            return message;
        }

        [Test]
        public void HotFields_ProgressLineLastToolNameTotalTokens_DoNotChangeSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            int before = MessageListController.ComputeSignatureForTests(message);

            subagent.progressLine = "Reading files across the repository...";
            subagent.lastToolName = "Read";
            subagent.totalTokens = 12345;

            int after = MessageListController.ComputeSignatureForTests(message);

            Assert.AreEqual(before, after,
                "progressLine/lastToolName/totalTokens must never affect the row's rebuild "
                    + "signature -- SubagentCard live-updates them in place instead");
        }

        [Test]
        public void RepeatedProgressLineMutation_NeverChangesSignature()
        {
            // The exact failure mode from the review: many task_progress
            // events in a row, each with a longer/different description.
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            int baseline = MessageListController.ComputeSignatureForTests(message);

            string[] descriptions =
            {
                "Scanning files",
                "Scanning files in src/",
                "Scanning files in src/ and tests/",
                ""
            };
            foreach (string description in descriptions)
            {
                subagent.progressLine = description;
                subagent.totalTokens += 500;
                Assert.AreEqual(baseline, MessageListController.ComputeSignatureForTests(message),
                    "signature drifted after progressLine/totalTokens mutation: \"" + description + "\"");
            }
        }

        [Test]
        public void StatusTransition_ChangesSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            int before = MessageListController.ComputeSignatureForTests(message);

            subagent.status = "completed";

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after, "a status transition must still trigger a row rebuild");
        }

        [Test]
        public void NestedBlockAppended_ChangesSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            int before = MessageListController.ComputeSignatureForTests(message);

            subagent.AddBlock(ChatMessageBlock.MakeText("nested subagent output"));

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after,
                "appending a nested block must still trigger a row rebuild (so the expanded "
                    + "details panel actually shows the new block)");
        }

        [Test]
        public void DroppedBlockCountIncrease_ChangesSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            int before = MessageListController.ComputeSignatureForTests(message);

            subagent.droppedBlockCount++;

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after,
                "a bump in dropped-block count must still trigger a row rebuild (so the "
                    + "\"N earlier steps omitted\" note updates)");
        }

        // -- Nested tool-call status (design note 2026-08-03-subagent-ux-and-
        // midturn-input.md section 1.2: "a nested tool call completing does
        // not trigger a rebuild at all") -----------------------------------

        private static ToolCallRecord MakeNestedToolCall(string toolUseId, ToolCallStatus status)
        {
            return new ToolCallRecord
            {
                toolUseId = toolUseId,
                toolName = "Read",
                status = status
            };
        }

        [Test]
        public void NestedToolCall_RunningToCompleted_ChangesSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            ToolCallRecord nested = MakeNestedToolCall("toolu_nested", ToolCallStatus.Running);
            subagent.AddBlock(ChatMessageBlock.MakeToolCall(nested));

            int before = MessageListController.ComputeSignatureForTests(message);

            // Nothing about the OUTER subagent record changes here -- status,
            // blocks.Count and droppedBlockCount all stay put. Only the
            // nested tool call's own status transitions, exactly the case
            // the three existing fields could never observe.
            nested.status = ToolCallStatus.Succeeded;

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after,
                "a nested tool call finishing (Running -> Succeeded) must trigger a row "
                    + "rebuild so its ToolActivityCard stops spinning");
        }

        [Test]
        public void NestedToolCall_RunningToFailed_ChangesSignature()
        {
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            ToolCallRecord nested = MakeNestedToolCall("toolu_nested_fail", ToolCallStatus.Running);
            subagent.AddBlock(ChatMessageBlock.MakeToolCall(nested));

            int before = MessageListController.ComputeSignatureForTests(message);

            nested.status = ToolCallStatus.Failed;

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after,
                "a nested tool call failing (Running -> Failed) must also trigger a row rebuild");
        }

        [Test]
        public void NestedToolCall_ProgressOnlyChange_StillDoesNotChangeSignature()
        {
            // Pins that the fix for the nested-status gap does not
            // accidentally widen the signature to cover the outer
            // subagent's hot fields too -- the deliberate exclusion from
            // HotFields_ProgressLineLastToolNameTotalTokens_DoNotChangeSignature
            // above must still hold with a nested tool call present.
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            ToolCallRecord nested = MakeNestedToolCall("toolu_nested_progress", ToolCallStatus.Running);
            subagent.AddBlock(ChatMessageBlock.MakeToolCall(nested));

            int before = MessageListController.ComputeSignatureForTests(message);

            subagent.progressLine = "Reading nested_file.txt";
            subagent.lastToolName = "Read";
            subagent.totalTokens = 999;

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreEqual(before, after,
                "outer progressLine/lastToolName/totalTokens must still be excluded even "
                    + "when a nested tool call is present");
        }

        [Test]
        public void NestedNonToolBlock_DoesNotThrow_AndNestedToolStatusStillTracked()
        {
            // subagent.blocks can also contain plain text/thinking blocks
            // (no toolCall) -- the new loop must skip those instead of
            // throwing on a null toolCall, while still catching a real
            // nested tool call's status change alongside it.
            var subagent = new SubagentRecord { status = "running" };
            ChatMessage message = BuildSubagentMessage(subagent);
            subagent.AddBlock(ChatMessageBlock.MakeText("nested narration"));
            ToolCallRecord nested = MakeNestedToolCall("toolu_nested_mixed", ToolCallStatus.Running);
            subagent.AddBlock(ChatMessageBlock.MakeToolCall(nested));

            int before = 0;
            Assert.DoesNotThrow(() => before = MessageListController.ComputeSignatureForTests(message));

            nested.status = ToolCallStatus.Succeeded;

            int after = MessageListController.ComputeSignatureForTests(message);
            Assert.AreNotEqual(before, after,
                "a nested tool call's status change must still be observed alongside a "
                    + "plain nested text block");
        }

        // -- MessageBlockFactory.SettingsGeneration (docs/design-notes/
        // 2026-08-01-settings-enrichment.md #2: showThinking toggle) -------------------

        [Test]
        public void SettingsGenerationBump_ChangesSignature_ForAnOrdinaryMessage()
        {
            int originalGeneration = MessageBlockFactory.SettingsGeneration;
            try
            {
                var message = new ChatMessage { role = ChatMessage.RoleAssistant };
                message.Add(ChatMessageBlock.MakeText("hello"));

                int before = MessageListController.ComputeSignatureForTests(message);
                MessageBlockFactory.SettingsGeneration++;
                int after = MessageListController.ComputeSignatureForTests(message);

                Assert.AreNotEqual(before, after,
                    "bumping SettingsGeneration (e.g. the showThinking toggle) must "
                        + "force every cached row to rebuild on the next Refresh, even "
                        + "for a message with no Thinking block of its own");
            }
            finally
            {
                MessageBlockFactory.SettingsGeneration = originalGeneration;
            }
        }
    }
}

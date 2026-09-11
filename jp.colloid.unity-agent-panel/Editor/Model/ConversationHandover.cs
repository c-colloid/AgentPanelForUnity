using System.Collections.Generic;
using System.Text;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Builds the text block that carries the conversation so far over to
    /// a DIFFERENT agent after a mid-conversation backend switch (design
    /// note docs/design-notes/2026-09-10-backend-switch-session.md
    /// section 5). The new agent cannot resume the old agent's session, so
    /// the panel prepends this transcript to the first message it sends;
    /// the chat bubble shows only what the user typed. Pure: no Unity API.
    /// </summary>
    public static class ConversationHandover
    {
        public const string Header = "===== CONVERSATION HANDOVER =====";
        public const string Footer = "===== END OF CONVERSATION HANDOVER =====";
        /// <summary>Upper bound of the transcript part; the OLDEST messages are dropped first.</summary>
        public const int DefaultMaxChars = 24000;
        public const string OmittedMarker = "(earlier messages omitted)";

        /// <summary>
        /// The handover block, or null when the session holds no user or
        /// assistant text worth handing over. <paramref name="messages"/>
        /// are read in order; system notes, thinking and permission cards
        /// are skipped, tool calls are reduced to one line each.
        /// </summary>
        public static string Build(IList<ChatMessage> messages, string previousAgent, string newAgent,
            int maxChars = DefaultMaxChars)
        {
            var entries = new List<string>();
            if (messages != null)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    string entry = RenderMessage(messages[i]);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            if (entries.Count == 0)
            {
                return null;
            }
            // Keep the tail: the most recent exchange matters most.
            int total = 0;
            int first = entries.Count;
            while (first > 0 && total + entries[first - 1].Length + 2 <= maxChars)
            {
                first--;
                total += entries[first].Length + 2;
            }
            if (first == entries.Count)
            {
                // Even the last entry is over budget: keep its tail.
                string last = entries[entries.Count - 1];
                entries = new List<string> { last.Substring(last.Length - System.Math.Min(last.Length, maxChars)) };
                first = 0;
            }
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("The user was talking to ").Append(previousAgent ?? "another agent")
                .Append(" in this panel and has switched to you (").Append(newAgent ?? "this agent")
                .Append(") mid-conversation. The transcript so far follows; continue from it")
                .Append(" without repeating or summarizing it back.\n\n");
            if (first > 0)
            {
                sb.Append(OmittedMarker).Append("\n\n");
            }
            for (int i = first; i < entries.Count; i++)
            {
                sb.Append(entries[i]).Append("\n\n");
            }
            sb.Append(Footer);
            return sb.ToString();
        }

        /// <summary>The wire text for the first message after a switch: handover first, then what the user typed.</summary>
        public static string Compose(string handover, string userText)
        {
            if (string.IsNullOrEmpty(handover))
            {
                return userText ?? string.Empty;
            }
            return string.IsNullOrEmpty(userText) ? handover : handover + "\n\n" + userText;
        }

        private static string RenderMessage(ChatMessage message)
        {
            if (message == null || message.blocks == null)
            {
                return null;
            }
            bool isUser = message.role == ChatMessage.RoleUser;
            bool isAssistant = message.role == ChatMessage.RoleAssistant;
            if (!isUser && !isAssistant)
            {
                return null;
            }
            var body = new StringBuilder();
            for (int i = 0; i < message.blocks.Count; i++)
            {
                ChatMessageBlock block = message.blocks[i];
                if (block == null)
                {
                    continue;
                }
                switch (block.kind)
                {
                    case ChatBlockKind.Text:
                        AppendLine(body, block.text);
                        break;
                    case ChatBlockKind.ContextAttachment:
                        AppendLine(body, "[attached: " + block.title + "]");
                        break;
                    case ChatBlockKind.ToolCall:
                        if (block.toolCall != null && !string.IsNullOrEmpty(block.toolCall.toolName))
                        {
                            string summary = block.toolCall.inputSummary ?? string.Empty;
                            AppendLine(body, "[tool " + block.toolCall.toolName
                                + (summary.Length > 0 ? ": " + summary : string.Empty) + "]");
                        }
                        break;
                    default:
                        break;
                }
            }
            if (body.Length == 0)
            {
                return null;
            }
            return (isUser ? "User: " : "Assistant: ") + body.ToString().TrimEnd();
        }

        private static void AppendLine(StringBuilder sb, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(text.Trim());
        }
    }
}

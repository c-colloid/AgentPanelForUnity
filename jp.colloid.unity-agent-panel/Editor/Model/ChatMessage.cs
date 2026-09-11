using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>Kind of one block inside a ChatMessage.</summary>
    [Serializable]
    public enum ChatBlockKind
    {
        Text,
        Thinking,
        ToolCall,
        SystemNote,
        Permission,
        Error,
        /// <summary>
        /// Editor context attached to a user message (selection/scene/
        /// dropped object/console errors). Display-only: the wire text the
        /// CLI receives is composed separately by ContextBlockFormatter.
        /// title = chip label, text = the block payload WITHOUT the wire
        /// delimiters.
        /// </summary>
        ContextAttachment,
        /// <summary>
        /// An image the user attached (design note 2026-09-07 section
        /// 2.3.1): text = absolute path of the encoded file under
        /// Library/AgentPanel/Attachments, title = the source name shown
        /// as the caption. The transcript renders a thumbnail; the bytes
        /// themselves were sent as an image content block.
        /// </summary>
        Image
    }

    /// <summary>
    /// One renderable block of a chat message. Unity-serializable (public
    /// fields, no polymorphism): the kind decides which fields matter.
    /// </summary>
    [Serializable]
    public class ChatMessageBlock
    {
        public ChatBlockKind kind;
        /// <summary>Text / Thinking / SystemNote / Permission / Error body.
        /// For ContextAttachment: the attachment payload.</summary>
        public string text = string.Empty;
        /// <summary>ContextAttachment only: the chip label shown collapsed
        /// (e.g. "GameObject: Main Camera"). Empty for other kinds.</summary>
        public string title = string.Empty;
        /// <summary>True while this block is still receiving streaming deltas.</summary>
        public bool streaming;
        /// <summary>ToolCall payload (null-object pattern: empty when unused).</summary>
        public ToolCallRecord toolCall;
        /// <summary>
        /// Thinking blocks only: cumulative estimated thinking-token count
        /// (design note 2026-08-01-thinking-content-loss.md section 5 point
        /// 1). The CLI's headless stream-json never sends thinking body
        /// text -- only this running estimate (system/thinking_tokens) plus
        /// a signature on the finalized block -- so this is the one piece
        /// of live information the compact indicator (MessageBlockFactory)
        /// has to show. Zero means "no estimate yet/unknown", which the
        /// renderer treats as "omit the token count" rather than "0
        /// tokens". Additive field: SessionCacheFile round-trips it but
        /// defaults to 0 for every other block kind and for caches written
        /// before this field existed.
        /// </summary>
        public long thinkingTokens;
        /// <summary>
        /// True for a redacted_thinking block (design note 2026-08-01-
        /// thinking-content-loss.md section 6): the model flagged this
        /// reasoning as safety-sensitive, so the API sent only encrypted
        /// data, never readable text. Reuses ChatBlockKind.Thinking rather
        /// than adding a new kind -- the least invasive representation,
        /// since every consumer that already switches on ChatBlockKind
        /// (showThinking gating, SessionCacheFile, MessageListController's
        /// signature) keeps working unchanged; only MessageBlockFactory
        /// needs to branch on this flag to swap in the redacted note.
        /// text stays empty and streaming stays false: redacted blocks
        /// arrive complete, never via thinking_delta.
        /// </summary>
        public bool thinkingRedacted;
        /// <summary>
        /// SystemNote only (design note 2026-08-14-ui-polish-audit.md
        /// contract item 2): true for a note that reports something going
        /// WRONG or interrupted (a dead CLI process reconnecting) rather
        /// than routine bookkeeping (a denied permission, an auto-continue
        /// status line). MessageBlockFactory adds the warn-colored
        /// uap-note--warn class when this is set. Defaults to false so
        /// every pre-existing MakeSystemNote call site keeps rendering the
        /// plain .uap-note style unchanged.
        /// </summary>
        public bool warning;

        public static ChatMessageBlock MakeText(string text, bool streaming = false)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.Text,
                text = text ?? string.Empty,
                streaming = streaming
            };
        }

        public static ChatMessageBlock MakeThinking(string text, bool streaming = false)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.Thinking,
                text = text ?? string.Empty,
                streaming = streaming
            };
        }

        /// <summary>One redacted_thinking block (see the field's doc comment above).</summary>
        public static ChatMessageBlock MakeRedactedThinking()
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.Thinking,
                text = string.Empty,
                thinkingRedacted = true
            };
        }

        public static ChatMessageBlock MakeToolCall(ToolCallRecord record)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.ToolCall,
                toolCall = record
            };
        }

        /// <summary>One attached image (path of the encoded file + caption).</summary>
        public static ChatMessageBlock MakeImage(string path, string sourceName)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.Image,
                text = path ?? string.Empty,
                title = sourceName ?? string.Empty
            };
        }

        public static ChatMessageBlock MakeContextAttachment(string title, string payload)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.ContextAttachment,
                title = title ?? string.Empty,
                text = payload ?? string.Empty
            };
        }

        /// <summary>
        /// <paramref name="warning"/> defaults to false so every pre-
        /// existing call site keeps compiling and rendering unchanged (see
        /// the <see cref="warning"/> field's own doc comment); AgentHub's
        /// process-exited/reconnecting note is the one call site that
        /// passes true.
        /// </summary>
        public static ChatMessageBlock MakeSystemNote(string text, bool warning = false)
        {
            return new ChatMessageBlock
            {
                kind = ChatBlockKind.SystemNote,
                text = text ?? string.Empty,
                warning = warning
            };
        }

        public static ChatMessageBlock MakeError(string text)
        {
            return new ChatMessageBlock { kind = ChatBlockKind.Error, text = text ?? string.Empty };
        }
    }

    /// <summary>
    /// One transcript entry: a role plus an ordered list of blocks
    /// (text/thinking/toolCall/systemNote/permission/error). This is the
    /// panel's display cache model -- the canonical transcript lives in the
    /// CLI session JSONL (ARCHITECTURE.md D5).
    /// </summary>
    [Serializable]
    public class ChatMessage
    {
        public const string RoleUser = "user";
        public const string RoleAssistant = "assistant";
        public const string RoleSystem = "system";

        /// <summary>Panel-local id (not the API message id).</summary>
        public string id = Guid.NewGuid().ToString("N");
        /// <summary>"user" | "assistant" | "system".</summary>
        public string role = RoleUser;
        public List<ChatMessageBlock> blocks = new List<ChatMessageBlock>();
        /// <summary>ISO-8601 timestamp (from the wire when available).</summary>
        public string timestamp = string.Empty;
        /// <summary>Turn id this message belongs to (0 = unknown/restored).</summary>
        public int turnId;
        /// <summary>User messages: set when the isReplay echo confirmed delivery.</summary>
        public bool delivered;

        /// <summary>Appends a block and returns it (builder convenience).</summary>
        public ChatMessageBlock Add(ChatMessageBlock block)
        {
            if (block != null)
            {
                blocks.Add(block);
            }
            return block;
        }

        /// <summary>The last block, or null when empty.</summary>
        public ChatMessageBlock LastBlock()
        {
            return blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
        }

        /// <summary>Concatenated text of all Text blocks (title/summary source).</summary>
        public string PlainText()
        {
            var parts = new List<string>();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].kind == ChatBlockKind.Text && !string.IsNullOrEmpty(blocks[i].text))
                {
                    parts.Add(blocks[i].text);
                }
            }
            return string.Join("\n", parts);
        }
    }
}

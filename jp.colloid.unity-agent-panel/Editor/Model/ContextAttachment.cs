using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One editor-context attachment travelling with an outgoing user
    /// message: a short human-readable title (the chip label, e.g.
    /// "GameObject: Main Camera" or "Console errors (3)") plus the block
    /// payload WITHOUT wire delimiters. The wire text the CLI receives is
    /// composed separately (ContextBlockFormatter.Compose over the
    /// payloads); this type only feeds the transcript display and the
    /// CompileGate queue, keeping wire and display decoupled.
    /// </summary>
    [Serializable]
    public sealed class ContextAttachment
    {
        public string title = string.Empty;
        public string payload = string.Empty;

        public ContextAttachment()
        {
        }

        public ContextAttachment(string title, string payload)
        {
            this.title = title ?? string.Empty;
            this.payload = payload ?? string.Empty;
        }
    }
}

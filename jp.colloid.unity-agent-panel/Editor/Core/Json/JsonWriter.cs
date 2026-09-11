using System.Globalization;
using System.Text;

namespace Colloid.AgentPanel.Core.Json
{
    /// <summary>
    /// Serializes a <see cref="JsonNode"/> tree to EXACTLY one line of JSON.
    /// Writing a malformed or multi-line JSON line to the CLI stdin kills the
    /// process (exit 1, no output), so this writer is the single serialization
    /// authority for all outbound payloads:
    /// - Escapes '"', '\\', and every control character below 0x20
    ///   (\n \r \t \b \f named, the rest as \u00XX).
    /// - Never emits raw CR/LF.
    /// - Passes all non-ASCII text (CJK etc.) through unescaped; the byte-level
    ///   UTF-8 stdin write preserves it.
    /// </summary>
    public static class JsonWriter
    {
        /// <summary>Serializes the node to a single-line JSON string (no trailing newline).</summary>
        public static string Write(JsonNode node)
        {
            var sb = new StringBuilder(256);
            WriteNode(node ?? JsonNode.Null, sb);
            return sb.ToString();
        }

        /// <summary>Escapes a raw string for embedding inside a JSON string literal (no quotes added).</summary>
        public static string EscapeString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(value.Length + 8);
            AppendEscaped(value, sb);
            return sb.ToString();
        }

        // ---------------------------------------------------------------

        private static void WriteNode(JsonNode node, StringBuilder sb)
        {
            switch (node.Type)
            {
                case JsonNodeType.Null:
                    sb.Append("null");
                    break;
                case JsonNodeType.Bool:
                    sb.Append(node.BoolValue ? "true" : "false");
                    break;
                case JsonNodeType.Number:
                    WriteNumber(node, sb);
                    break;
                case JsonNodeType.String:
                    sb.Append('"');
                    AppendEscaped(node.StringValue ?? string.Empty, sb);
                    sb.Append('"');
                    break;
                case JsonNodeType.Array:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var item in node.Items)
                    {
                        if (!firstItem)
                        {
                            sb.Append(',');
                        }
                        firstItem = false;
                        WriteNode(item, sb);
                    }
                    sb.Append(']');
                    break;
                case JsonNodeType.Object:
                    sb.Append('{');
                    bool firstProp = true;
                    foreach (var pair in node.Properties)
                    {
                        if (!firstProp)
                        {
                            sb.Append(',');
                        }
                        firstProp = false;
                        sb.Append('"');
                        AppendEscaped(pair.Key, sb);
                        sb.Append("\":");
                        WriteNode(pair.Value, sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteNumber(JsonNode node, StringBuilder sb)
        {
            // Round-trip the exact raw text when the node came from the parser
            // or an integer factory; otherwise format invariant.
            string raw = node.RawNumberText;
            if (!string.IsNullOrEmpty(raw))
            {
                sb.Append(raw);
                return;
            }
            double value = node.NumberValue;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null"); // JSON has no NaN/Infinity; degrade safely.
                return;
            }
            long asLong = (long)value;
            if (asLong == value)
            {
                sb.Append(asLong.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static void AppendEscaped(string value, StringBuilder sb)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(c))
                        {
                            // CORE-8: a PAIRED surrogate (emoji etc.) passes
                            // through raw -- Encoding.UTF8 turns it into the
                            // correct 4-byte sequence at the stdin write. A
                            // LONE half (split emoji, corrupted paste) would
                            // instead become U+FFFD there, silently mangling
                            // the user's text -- so it is \uXXXX-escaped,
                            // which JSON permits and round-trips losslessly.
                            if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                            {
                                sb.Append(c);
                                sb.Append(value[i + 1]);
                                i++;
                            }
                            else
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                        }
                        else if (char.IsLowSurrogate(c))
                        {
                            // Reachable only for a low half with no preceding
                            // high half (the paired case consumed both above).
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // All other non-ASCII (CJK etc.): pass through as
                            // UTF-16; UTF-8 encoding happens at the byte-level
                            // stdin write.
                            sb.Append(c);
                        }
                        break;
                }
            }
        }
    }
}

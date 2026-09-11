using System;
using System.Globalization;
using System.Text;

namespace Colloid.AgentPanel.Core.Json
{
    /// <summary>
    /// Thrown by <see cref="JsonParser"/> only for truly malformed input
    /// (unterminated strings, unbalanced brackets, garbage tokens).
    /// Tolerated without exception: UTF-8 BOM, surrounding whitespace,
    /// trailing commas, unknown escape sequences (kept literally),
    /// lone surrogates, arbitrarily long input.
    /// </summary>
    public class JsonParseException : Exception
    {
        public int Position { get; private set; }

        public JsonParseException(string message, int position)
            : base(message + " (at index " + position + ")")
        {
            Position = position;
        }
    }

    /// <summary>
    /// Tolerant recursive-descent JSON parser producing a <see cref="JsonNode"/> DOM.
    /// Pure C#, no Unity dependencies, no line-length limit (operates on the
    /// full string; CLI initialize responses can be hundreds of KB on one line).
    /// </summary>
    public static class JsonParser
    {
        /// <summary>Parses a complete JSON document (one stream-json line).</summary>
        /// <exception cref="JsonParseException">The input is not valid JSON.</exception>
        public static JsonNode Parse(string text)
        {
            if (text == null)
            {
                throw new JsonParseException("Input is null", 0);
            }

            int pos = 0;
            SkipBomAndWhitespace(text, ref pos);
            if (pos >= text.Length)
            {
                throw new JsonParseException("Input is empty", pos);
            }

            JsonNode root = ParseValue(text, ref pos, 0);

            SkipWhitespace(text, ref pos);
            if (pos < text.Length)
            {
                throw new JsonParseException("Unexpected trailing content", pos);
            }
            return root;
        }

        /// <summary>Non-throwing variant. Returns false and a null node on malformed input.</summary>
        public static bool TryParse(string text, out JsonNode node, out string error)
        {
            try
            {
                node = Parse(text);
                error = null;
                return true;
            }
            catch (JsonParseException ex)
            {
                node = null;
                error = ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------

        private const int MaxDepth = 256;

        private static JsonNode ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonParseException("Nesting too deep", pos);
            }
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                throw new JsonParseException("Unexpected end of input", pos);
            }

            char c = s[pos];
            switch (c)
            {
                case '{':
                    return ParseObject(s, ref pos, depth);
                case '[':
                    return ParseArray(s, ref pos, depth);
                case '"':
                    return JsonNode.Of(ParseString(s, ref pos));
                case 't':
                    ExpectLiteral(s, ref pos, "true");
                    return JsonNode.Of(true);
                case 'f':
                    ExpectLiteral(s, ref pos, "false");
                    return JsonNode.Of(false);
                case 'n':
                    ExpectLiteral(s, ref pos, "null");
                    return JsonNode.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ParseNumber(s, ref pos);
                    }
                    throw new JsonParseException("Unexpected character '" + c + "'", pos);
            }
        }

        private static JsonNode ParseObject(string s, ref int pos, int depth)
        {
            pos++; // consume '{'
            var obj = JsonNode.NewObject();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}')
            {
                pos++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw new JsonParseException("Unterminated object", pos);
                }
                if (s[pos] == '}')
                {
                    // Tolerate trailing comma: {"a":1,}
                    pos++;
                    return obj;
                }
                if (s[pos] != '"')
                {
                    throw new JsonParseException("Expected string key in object", pos);
                }
                string key = ParseString(s, ref pos);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                {
                    throw new JsonParseException("Expected ':' after object key", pos);
                }
                pos++;

                JsonNode value = ParseValue(s, ref pos, depth + 1);
                obj.Set(key, value);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw new JsonParseException("Unterminated object", pos);
                }
                if (s[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (s[pos] == '}')
                {
                    pos++;
                    return obj;
                }
                throw new JsonParseException("Expected ',' or '}' in object", pos);
            }
        }

        private static JsonNode ParseArray(string s, ref int pos, int depth)
        {
            pos++; // consume '['
            var arr = JsonNode.NewArray();
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']')
            {
                pos++;
                return arr;
            }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw new JsonParseException("Unterminated array", pos);
                }
                if (s[pos] == ']')
                {
                    // Tolerate trailing comma: [1,2,]
                    pos++;
                    return arr;
                }

                JsonNode value = ParseValue(s, ref pos, depth + 1);
                arr.Add(value);

                SkipWhitespace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw new JsonParseException("Unterminated array", pos);
                }
                if (s[pos] == ',')
                {
                    pos++;
                    continue;
                }
                if (s[pos] == ']')
                {
                    pos++;
                    return arr;
                }
                throw new JsonParseException("Expected ',' or ']' in array", pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            // s[pos] == '"'
            pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length)
                {
                    throw new JsonParseException("Unterminated string", pos);
                }
                char c = s[pos];
                if (c == '"')
                {
                    pos++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    pos++;
                    if (pos >= s.Length)
                    {
                        throw new JsonParseException("Unterminated escape sequence", pos);
                    }
                    char e = s[pos];
                    switch (e)
                    {
                        case '"': sb.Append('"'); pos++; break;
                        case '\\': sb.Append('\\'); pos++; break;
                        case '/': sb.Append('/'); pos++; break;
                        case 'b': sb.Append('\b'); pos++; break;
                        case 'f': sb.Append('\f'); pos++; break;
                        case 'n': sb.Append('\n'); pos++; break;
                        case 'r': sb.Append('\r'); pos++; break;
                        case 't': sb.Append('\t'); pos++; break;
                        case 'u':
                            pos++;
                            sb.Append(ParseUnicodeEscape(s, ref pos));
                            break;
                        default:
                            // Tolerant: keep unknown escapes literally instead of failing.
                            sb.Append(e);
                            pos++;
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
        }

        /// <summary>
        /// Parses the 4 hex digits after "\u". Surrogate pairs arrive as two
        /// consecutive \uXXXX escapes; each half is appended as a UTF-16 unit,
        /// so pairs recombine naturally in the resulting string.
        /// </summary>
        private static char ParseUnicodeEscape(string s, ref int pos)
        {
            if (pos + 4 > s.Length)
            {
                throw new JsonParseException("Truncated \\u escape", pos);
            }
            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                char h = s[pos + i];
                int digit;
                if (h >= '0' && h <= '9') { digit = h - '0'; }
                else if (h >= 'a' && h <= 'f') { digit = h - 'a' + 10; }
                else if (h >= 'A' && h <= 'F') { digit = h - 'A' + 10; }
                else
                {
                    throw new JsonParseException("Invalid hex digit in \\u escape", pos + i);
                }
                code = (code << 4) | digit;
            }
            pos += 4;
            return (char)code;
        }

        private static JsonNode ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && s[pos] == '-')
            {
                pos++;
            }
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
            {
                pos++;
            }
            if (pos < s.Length && s[pos] == '.')
            {
                pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
                {
                    pos++;
                }
            }
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
            {
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
                {
                    pos++;
                }
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9')
                {
                    pos++;
                }
            }

            string raw = s.Substring(start, pos - start);
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new JsonParseException("Invalid number '" + raw + "'", start);
            }
            return JsonNode.OfNumberRaw(value, raw);
        }

        private static void ExpectLiteral(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length ||
                string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
            {
                throw new JsonParseException("Invalid literal, expected '" + literal + "'", pos);
            }
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }
        }

        private static void SkipBomAndWhitespace(string s, ref int pos)
        {
            // Captured fixture files (and defensive reading of CLI output)
            // may start with a UTF-8 BOM decoded as U+FEFF.
            while (pos < s.Length && s[pos] == (char)0xFEFF)
            {
                pos++;
            }
            SkipWhitespace(s, ref pos);
        }
    }
}

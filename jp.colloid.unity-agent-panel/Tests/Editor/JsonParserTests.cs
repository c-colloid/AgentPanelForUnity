using System.Text;
using Colloid.AgentPanel.Core.Json;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression tests for the dependency-free JSON layer, driven by real
    /// captured CLI v2.1.218 output. All source literals are strict ASCII;
    /// CJK / emoji test data is built from code points at runtime.
    /// </summary>
    public class JsonParserTests
    {
        // Builds a string from Unicode code points (keeps this source ASCII).
        private static string U(params int[] codePoints)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < codePoints.Length; i++)
            {
                sb.Append(char.ConvertFromUtf32(codePoints[i]));
            }
            return sb.ToString();
        }

        // "Japanese" written in kanji: U+65E5 U+672C U+8A9E.
        private static string Cjk()
        {
            return U(0x65E5, 0x672C, 0x8A9E);
        }

        // ---------------------------------------------------------------
        // Fixture round-trips
        // ---------------------------------------------------------------

        [Test]
        public void AllFixtureLines_ParseWithoutException()
        {
            foreach (string fixture in FixtureLoader.AllFixtureNames)
            {
                var lines = FixtureLoader.ReadLines(fixture);
                Assert.IsNotEmpty(lines, fixture + " should contain at least one line");
                for (int i = 0; i < lines.Count; i++)
                {
                    JsonNode node = null;
                    Assert.DoesNotThrow(
                        delegate { node = JsonParser.Parse(lines[i]); },
                        fixture + " line " + (i + 1) + " should parse");
                    Assert.IsNotNull(node);
                    Assert.IsTrue(node.IsObject,
                        fixture + " line " + (i + 1) + " should be a JSON object");
                    Assert.IsNotNull(node["type"].AsString(),
                        fixture + " line " + (i + 1) + " should carry a type field");
                }
            }
        }

        [Test]
        public void FixtureLine_WithBom_Parses()
        {
            // out1.jsonl was captured with a UTF-8 BOM; feed the raw first line
            // including a forced BOM prefix through the parser.
            string line = FixtureLoader.ReadLines("out1.jsonl")[0];
            string withBom = ((char)0xFEFF) + line;
            JsonNode node = JsonParser.Parse(withBom);
            Assert.AreEqual("system", node["type"].AsString());
        }

        [Test]
        public void FixtureRoundTrip_ReparsesToSameStructure()
        {
            // Write(Parse(line)) must itself be valid JSON with identical
            // scalar values at spot-checked paths.
            string line = FixtureLoader.ReadLines("out1.jsonl")[0];
            JsonNode first = JsonParser.Parse(line);
            string rewritten = JsonWriter.Write(first);
            JsonNode second = JsonParser.Parse(rewritten);

            Assert.AreEqual(first["session_id"].AsString(), second["session_id"].AsString());
            Assert.AreEqual(first["tools"].Count, second["tools"].Count);
            Assert.AreEqual(first["cwd"].AsString(), second["cwd"].AsString());
        }

        // ---------------------------------------------------------------
        // Escaping and round-trips
        // ---------------------------------------------------------------

        [Test]
        public void EscapeRoundTrip_CjkAndControlChars()
        {
            string nasty = Cjk() + " line1\nline2\r\ttab \"quoted\" back\\slash "
                + (char)0x01 + (char)0x1F + U(0x1F600); // emoji U+1F600

            var node = JsonNode.NewObject().Set("text", nasty);
            string json = JsonWriter.Write(node);

            StringAssert.DoesNotContain("\n", json, "writer must emit a single line");
            StringAssert.DoesNotContain("\r", json, "writer must emit a single line");

            JsonNode parsed = JsonParser.Parse(json);
            Assert.AreEqual(nasty, parsed["text"].AsString());
        }

        [Test]
        public void Writer_PassesCjkThroughUnescaped()
        {
            string json = JsonWriter.Write(JsonNode.NewObject().Set("t", Cjk()));
            StringAssert.Contains(Cjk(), json, "CJK must not be \\u-escaped");
        }

        /// <summary>
        /// CORE-8: a LONE surrogate half (split emoji, corrupted paste)
        /// passed through raw becomes U+FFFD at the UTF-8 stdin write --
        /// silent text corruption. The writer \uXXXX-escapes lone halves
        /// (JSON permits an unpaired \uXXXX), keeping the byte stream clean
        /// and the round-trip lossless.
        /// </summary>
        [Test]
        public void Writer_LoneHighSurrogate_IsEscaped_AndRoundTrips()
        {
            string text = "a" + (char)0xD83D + "b";
            string json = JsonWriter.Write(JsonNode.NewObject().Set("t", text));

            StringAssert.Contains("\\ud83d", json);
            Assert.AreEqual(text, JsonParser.Parse(json)["t"].AsString());

            string decoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json));
            Assert.IsTrue(decoded.IndexOf((char)0xFFFD) < 0,
                "the emitted JSON itself must survive UTF-8 encoding without replacement chars");
        }

        [Test]
        public void Writer_LoneLowSurrogate_IsEscaped_AndRoundTrips()
        {
            string text = (char)0xDE00 + "tail";
            string json = JsonWriter.Write(JsonNode.NewObject().Set("t", text));

            StringAssert.Contains("\\ude00", json);
            Assert.AreEqual(text, JsonParser.Parse(json)["t"].AsString());
        }

        [Test]
        public void Writer_ValidSurrogatePair_StillPassesThroughRaw()
        {
            // The paired case (real emoji) must KEEP the raw pass-through --
            // UTF-8 turns the pair into its correct 4-byte sequence.
            string json = JsonWriter.Write(JsonNode.NewObject().Set("t", U(0x1F600)));
            StringAssert.Contains(U(0x1F600), json);
            StringAssert.DoesNotContain("\\ud83d", json);
        }

        [Test]
        public void Writer_EscapesControlCharsBelow0x20()
        {
            string json = JsonWriter.Write(JsonNode.NewObject().Set("t", "" + (char)0x01));
            StringAssert.Contains("\\u0001", json);
        }

        [Test]
        public void Writer_EscapesQuotesBackslashesAndNamedControls()
        {
            string json = JsonWriter.Write(JsonNode.NewObject()
                .Set("t", "a\"b\\c\nd\re\tf\bg\fh"));
            StringAssert.Contains("\\\"", json);
            StringAssert.Contains("\\\\", json);
            StringAssert.Contains("\\n", json);
            StringAssert.Contains("\\r", json);
            StringAssert.Contains("\\t", json);
            StringAssert.Contains("\\b", json);
            StringAssert.Contains("\\f", json);
        }

        [Test]
        public void Parser_DecodesSurrogatePairEscapes()
        {
            // The escaped surrogate pair D83D/DE00 decodes to U+1F600 (emoji).
            JsonNode node = JsonParser.Parse("{\"e\":\"\\uD83D\\uDE00\"}");
            Assert.AreEqual(U(0x1F600), node["e"].AsString());
        }

        [Test]
        public void Parser_DecodesBmpUnicodeEscapes()
        {
            // The escapes 65E5/672C/8A9E decode to the CJK sample string.
            JsonNode node = JsonParser.Parse("{\"e\":\"\\u65E5\\u672C\\u8A9E\"}");
            Assert.AreEqual(Cjk(), node["e"].AsString());
        }

        [Test]
        public void Parser_KeepsUnknownEscapesLiterally()
        {
            // Tolerant behavior: "\x" is not valid JSON but must not throw.
            JsonNode node = JsonParser.Parse("{\"e\":\"a\\xb\"}");
            Assert.AreEqual("axb", node["e"].AsString());
        }

        [Test]
        public void Parser_ToleratesTrailingCommas()
        {
            JsonNode node = JsonParser.Parse("{\"a\":[1,2,],\"b\":{\"c\":true,},}");
            Assert.AreEqual(2, node["a"].Count);
            Assert.IsTrue(node["b"]["c"].AsBool());
        }

        // ---------------------------------------------------------------
        // Malformed input => JsonParseException (and only then)
        // ---------------------------------------------------------------

        [TestCase("garbage")]
        [TestCase("{")]
        [TestCase("{\"a\":}")]
        [TestCase("[1,")]
        [TestCase("\"unterminated")]
        [TestCase("{\"a\":1} extra")]
        [TestCase("")]
        [TestCase("{\"a\" 1}")]
        [TestCase("{a:1}")]
        public void Parser_ThrowsOnMalformedInput(string input)
        {
            Assert.Throws<JsonParseException>(delegate { JsonParser.Parse(input); });
        }

        [Test]
        public void TryParse_ReturnsFalseWithReason_OnMalformedInput()
        {
            JsonNode node;
            string error;
            Assert.IsFalse(JsonParser.TryParse("not json", out node, out error));
            Assert.IsNull(node);
            Assert.IsNotNull(error);
        }

        // ---------------------------------------------------------------
        // DOM semantics
        // ---------------------------------------------------------------

        [Test]
        public void MissingKeys_ReturnNullNode_AndChainSafely()
        {
            JsonNode node = JsonParser.Parse("{\"a\":1}");
            Assert.IsTrue(node["nope"].IsNull);
            Assert.IsTrue(node["nope"]["deeper"][42].IsNull);
            Assert.AreEqual("fallback", node["nope"]["deeper"].AsString("fallback"));
            Assert.AreEqual(7, node["nope"].AsInt(7));
            Assert.IsTrue(node["nope"].AsBool(true));
        }

        [Test]
        public void Numbers_PreserveInt64Precision()
        {
            JsonNode node = JsonParser.Parse(
                "{\"big\":9007199254740993,\"neg\":-42,\"pi\":3.5,\"exp\":1e3}");
            Assert.AreEqual(9007199254740993L, node["big"].AsLong());
            Assert.AreEqual(-42, node["neg"].AsInt());
            Assert.AreEqual(3.5, node["pi"].AsDouble(), 1e-12);
            Assert.AreEqual(1000.0, node["exp"].AsDouble(), 1e-12);
        }

        [Test]
        public void Numbers_RoundTripRawText()
        {
            string json = JsonWriter.Write(JsonParser.Parse("{\"big\":9007199254740993}"));
            StringAssert.Contains("9007199254740993", json);
        }

        [Test]
        public void LongLines_Parse()
        {
            // The initialize response is one line of tens/hundreds of KB;
            // out_bidi.jsonl line 1 is the real capture (about 17 KB).
            string line = FixtureLoader.ReadLines("out_bidi.jsonl")[0];
            Assert.Greater(line.Length, 10000, "capture should be a long single line");
            JsonNode node = JsonParser.Parse(line);
            Assert.AreEqual("control_response", node["type"].AsString());
        }

        [Test]
        public void NullNode_IsImmutable()
        {
            Assert.Throws<System.InvalidOperationException>(
                delegate { JsonNode.Null.Set("a", 1); });
            Assert.Throws<System.InvalidOperationException>(
                delegate { JsonNode.Null.Add(1); });
        }

        [Test]
        public void Builder_ProducesExpectedShapes()
        {
            var node = JsonNode.NewObject()
                .Set("s", "v")
                .Set("n", 12)
                .Set("b", true)
                .Set("arr", JsonNode.NewArray().Add("x").Add(1.5))
                .Set("nil", JsonNode.Null);
            string json = JsonWriter.Write(node);
            JsonNode parsed = JsonParser.Parse(json);
            Assert.AreEqual("v", parsed["s"].AsString());
            Assert.AreEqual(12, parsed["n"].AsInt());
            Assert.IsTrue(parsed["b"].AsBool());
            Assert.AreEqual(2, parsed["arr"].Count);
            Assert.AreEqual(1.5, parsed["arr"][1].AsDouble(), 1e-12);
            Assert.IsTrue(parsed["nil"].IsNull);
        }
    }
}

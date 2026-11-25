using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapLeanSchema.Lean (design note docs/design-notes/2026-10-02-session-
    /// context-cost.md section 4): the one-line-descriptions form of an
    /// input schema that the compact tools/list serves. Pure JsonNode
    /// rules, mirrored in the license-free smoke gate (LeanSchemaSmoke).
    /// </summary>
    [TestFixture]
    public class UapLeanSchemaTests
    {
        private const string LongDescription =
            "Sets the thing to the given value, which must already exist in the scene and be enabled."
            + " A second sentence that only the full schema carries.";

        private static JsonNode SampleSchema()
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("description", LongDescription)
                .Set("title", "Sample")
                .Set("properties", JsonNode.NewObject()
                    .Set("mode", JsonNode.NewObject().Set("type", "string")
                        .Set("enum", JsonNode.NewArray().Add("a").Add("b"))
                        .Set("default", "a")
                        .Set("description", "Which mode to use. Longer story here.")
                        .Set("examples", JsonNode.NewArray().Add("a")))
                    .Set("count", JsonNode.NewObject().Set("type", "integer")
                        .Set("minimum", 0).Set("maximum", 10))
                    .Set("pos", JsonNode.NewObject().Set("type", "array")
                        .Set("items", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "One axis. More."))
                        .Set("minItems", 3).Set("maxItems", 3))
                    .Set("ops", JsonNode.NewObject().Set("type", "array")
                        .Set("items", JsonNode.NewObject().Set("type", "object")
                            .Set("properties", JsonNode.NewObject()
                                .Set("op", JsonNode.NewObject().Set("type", "string")
                                    .Set("description", "The operation. Details after."))
                                .Set("deep", JsonNode.NewObject().Set("type", "object")
                                    .Set("properties", JsonNode.NewObject()
                                        .Set("x", JsonNode.NewObject().Set("type", "number")
                                            .Set("description", "The X value. Y.")))))
                            .Set("required", JsonNode.NewArray().Add("op"))
                            .Set("additionalProperties", false))))
                .Set("required", JsonNode.NewArray().Add("mode"))
                .Set("additionalProperties", false);
        }

        [Test]
        public void Lean_KeepsStructuralKeywordsVerbatim_AtEveryLevel()
        {
            JsonNode lean = UapLeanSchema.Lean(SampleSchema());

            Assert.AreEqual("object", lean["type"].AsString());
            Assert.IsFalse(lean["additionalProperties"].AsBool(true));
            Assert.AreEqual("mode", lean["required"][0].AsString());

            JsonNode mode = lean["properties"]["mode"];
            Assert.AreEqual("string", mode["type"].AsString());
            Assert.AreEqual(2, mode["enum"].Count);
            Assert.AreEqual("b", mode["enum"][1].AsString());
            Assert.AreEqual("a", mode["default"].AsString());

            JsonNode count = lean["properties"]["count"];
            Assert.AreEqual(0, count["minimum"].AsInt(-1));
            Assert.AreEqual(10, count["maximum"].AsInt(-1));

            JsonNode pos = lean["properties"]["pos"];
            Assert.AreEqual("number", pos["items"]["type"].AsString());
            Assert.AreEqual(3, pos["minItems"].AsInt(-1));
            Assert.AreEqual(3, pos["maxItems"].AsInt(-1));

            JsonNode item = lean["properties"]["ops"]["items"];
            Assert.AreEqual("op", item["required"][0].AsString());
            Assert.IsFalse(item["additionalProperties"].AsBool(true));
            Assert.AreEqual("number", item["properties"]["deep"]["properties"]["x"]["type"].AsString());
        }

        [Test]
        public void Lean_ReducesEveryDescriptionToItsFirstSentence_AtEveryLevel()
        {
            JsonNode lean = UapLeanSchema.Lean(SampleSchema());

            Assert.AreEqual(
                UapToolSummary.FirstSentence(LongDescription, UapLeanSchema.DefaultMaxDescriptionChars),
                lean["description"].AsString());
            StringAssert.DoesNotContain("second sentence", lean["description"].AsString());
            Assert.AreEqual("Which mode to use.", lean["properties"]["mode"]["description"].AsString());
            Assert.AreEqual("One axis.", lean["properties"]["pos"]["items"]["description"].AsString());
            JsonNode item = lean["properties"]["ops"]["items"];
            Assert.AreEqual("The operation.", item["properties"]["op"]["description"].AsString());
            Assert.AreEqual("The X value.", item["properties"]["deep"]["properties"]["x"]["description"].AsString());
        }

        [Test]
        public void Lean_CapsDescriptions_AtTheDefaultAndAtAConfiguredLimit()
        {
            string words = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon";
            JsonNode schema = JsonNode.NewObject().Set("type", "string").Set("description", words);

            string byDefault = UapLeanSchema.Lean(schema)["description"].AsString();
            Assert.LessOrEqual(byDefault.Length, UapLeanSchema.DefaultMaxDescriptionChars);
            StringAssert.EndsWith("...", byDefault);

            string tight = UapLeanSchema.Lean(schema, 40)["description"].AsString();
            Assert.LessOrEqual(tight.Length, 40);
            StringAssert.EndsWith("...", tight);

            string roomy = UapLeanSchema.Lean(schema, UapToolSummary.MaxChars)["description"].AsString();
            Assert.AreEqual(words, roomy);
        }

        [Test]
        public void Lean_DropsExamplesTitleAndUnknownKeys()
        {
            JsonNode lean = UapLeanSchema.Lean(SampleSchema());
            Assert.IsFalse(lean.HasKey("title"));
            Assert.IsFalse(lean["properties"]["mode"].HasKey("examples"));

            JsonNode odd = JsonNode.NewObject().Set("type", "string").Set("x-vendor", "y").Set("$comment", "z");
            JsonNode leanOdd = UapLeanSchema.Lean(odd);
            Assert.AreEqual(1, leanOdd.Count);
            Assert.AreEqual("string", leanOdd["type"].AsString());
        }

        [Test]
        public void Lean_APropertyNamedLikeAKeyword_IsAPropertyNotAKeyword()
        {
            JsonNode schema = JsonNode.NewObject().Set("type", "object")
                .Set("properties", JsonNode.NewObject()
                    .Set("description", JsonNode.NewObject().Set("type", "string")
                        .Set("description", "Free text. More."))
                    .Set("examples", JsonNode.NewObject().Set("type", "array")
                        .Set("items", JsonNode.NewObject().Set("type", "string")))
                    .Set("default", JsonNode.NewObject().Set("type", "boolean")));
            JsonNode lean = UapLeanSchema.Lean(schema);
            JsonNode props = lean["properties"];
            Assert.AreEqual(3, props.Count);
            Assert.AreEqual("string", props["description"]["type"].AsString());
            Assert.AreEqual("Free text.", props["description"]["description"].AsString());
            Assert.AreEqual("array", props["examples"]["type"].AsString());
            Assert.AreEqual("boolean", props["default"]["type"].AsString());
        }

        [Test]
        public void Lean_DefaultAndEnumValuesAreCopiedWhole_NotLeaned()
        {
            JsonNode schema = JsonNode.NewObject().Set("type", "object")
                .Set("default", JsonNode.NewObject().Set("description", "kept as data. Really.").Set("examples", 1))
                .Set("enum", JsonNode.NewArray().Add(JsonNode.NewObject().Set("title", "t")));
            JsonNode lean = UapLeanSchema.Lean(schema);
            Assert.AreEqual("kept as data. Really.", lean["default"]["description"].AsString());
            Assert.AreEqual(1, lean["default"]["examples"].AsInt(-1));
            Assert.AreEqual("t", lean["enum"][0]["title"].AsString());
        }

        [Test]
        public void Lean_IsADeepCopy_InputIsNotModified_AndSharesNoContainers()
        {
            JsonNode schema = SampleSchema();
            string before = JsonWriter.Write(schema);
            JsonNode lean = UapLeanSchema.Lean(schema);
            Assert.AreEqual(before, JsonWriter.Write(schema));
            Assert.AreNotSame(schema, lean);
            Assert.AreNotSame(schema["properties"], lean["properties"]);
            Assert.AreNotSame(schema["required"], lean["required"]);
            // Mutating the copy never reaches the original.
            lean["properties"].Set("added", JsonNode.NewObject().Set("type", "string"));
            Assert.IsFalse(schema["properties"].HasKey("added"));
        }

        [Test]
        public void Lean_IsIdempotent()
        {
            JsonNode once = UapLeanSchema.Lean(SampleSchema());
            JsonNode twice = UapLeanSchema.Lean(once);
            Assert.AreEqual(JsonWriter.Write(once), JsonWriter.Write(twice));

            // A description that was cut with "..." must survive a second pass unchanged.
            JsonNode capped = UapLeanSchema.Lean(JsonNode.NewObject().Set("description", new string('w', 10) + " "
                + "word word word word word word word word word word word word word word word word word word word word word"));
            Assert.AreEqual(JsonWriter.Write(capped), JsonWriter.Write(UapLeanSchema.Lean(capped)));
        }

        [Test]
        public void Lean_KeepsPropertyOrder()
        {
            JsonNode lean = UapLeanSchema.Lean(SampleSchema());
            var keys = new System.Collections.Generic.List<string>(lean["properties"].Keys);
            CollectionAssert.AreEqual(new[] { "mode", "count", "pos", "ops" }, keys);
        }

        [Test]
        public void Lean_RecursesIntoCombinators_AndKeepsTheirStructure()
        {
            JsonNode schema = JsonNode.NewObject()
                .Set("oneOf", JsonNode.NewArray()
                    .Add(JsonNode.NewObject().Set("type", "string").Set("description", "Text value. B."))
                    .Add(JsonNode.NewObject().Set("type", "integer")));
            JsonNode lean = UapLeanSchema.Lean(schema);
            Assert.AreEqual(2, lean["oneOf"].Count);
            Assert.AreEqual("Text value.", lean["oneOf"][0]["description"].AsString());
            Assert.AreEqual("integer", lean["oneOf"][1]["type"].AsString());
        }

        [Test]
        public void Lean_OddShapes_NeverThrow()
        {
            Assert.IsTrue(UapLeanSchema.Lean(null).IsNull);
            Assert.IsTrue(UapLeanSchema.Lean(JsonNode.Null).IsNull);
            Assert.AreEqual("text", UapLeanSchema.Lean(JsonNode.Of("text")).AsString());
            Assert.AreEqual(5, UapLeanSchema.Lean(JsonNode.Of(5)).AsInt(-1));
            Assert.IsTrue(UapLeanSchema.Lean(JsonNode.NewArray()).IsArray);
            Assert.AreEqual(0, UapLeanSchema.Lean(JsonNode.NewObject()).Count);

            JsonNode arrayOfSchemas = JsonNode.NewArray()
                .Add(JsonNode.NewObject().Set("type", "string").Set("description", "Item one. Two."));
            JsonNode leanArray = UapLeanSchema.Lean(arrayOfSchemas);
            Assert.AreEqual("Item one.", leanArray[0]["description"].AsString());

            // Wrong-typed keyword values: a non-string description is dropped, a
            // non-object "properties" and a non-array "oneOf" are copied as they are.
            JsonNode odd = JsonNode.NewObject()
                .Set("description", 42)
                .Set("properties", "nope")
                .Set("oneOf", JsonNode.NewObject().Set("k", "v"))
                .Set("items", JsonNode.Null)
                .Set("required", "name");
            JsonNode leanOdd = UapLeanSchema.Lean(odd);
            Assert.IsFalse(leanOdd.HasKey("description"));
            Assert.AreEqual("nope", leanOdd["properties"].AsString());
            Assert.AreEqual("v", leanOdd["oneOf"]["k"].AsString());
            Assert.IsTrue(leanOdd["items"].IsNull);
            Assert.AreEqual("name", leanOdd["required"].AsString());
        }

        [Test]
        public void Lean_ABareLabelFirstSentence_IsExtendedWithTheNextOne()
        {
            Assert.AreEqual("start only. Frames per second, 1 to 60 (default 30).", LeanDescription("start only. Frames per second, 1 to 60 (default 30). Extra."));
            Assert.AreEqual("action:set, optional. A condition.", LeanDescription("action:set, optional. A condition. More."));
            Assert.AreEqual("Required. The state holding the tree.", LeanDescription("Required. The state holding the tree. More."));
            // A real two-word sentence is a description, not a label.
            Assert.AreEqual("Scene name or path.", LeanDescription("Scene name or path. Omit for the active scene."));
            Assert.AreEqual("Marker shape.", LeanDescription("Marker shape. Default point."));
            // A lone label with nothing after it stays as it is, and a run of labels stops.
            Assert.AreEqual("Required.", LeanDescription("Required."));
            Assert.AreEqual("a only. b only. c only. d only.", LeanDescription("a only. b only. c only. d only. Real text. More."));
        }

        [Test]
        public void Lean_ACutInsideABracketOrQuote_BacksUpToBeforeTheOpener()
        {
            string text = "Which axis to rotate around and how far, in degrees (e.g. 90 for a quarter turn, -90 for the other way round, 180 to flip).";
            string lean = LeanDescription(text);
            Assert.AreEqual("Which axis to rotate around and how far, in degrees...", lean);

            string quoted = "Pick the state to switch to, exactly as the controller shows it: \"Base Layer.Some Very Long State Name.Sub State Machine.Idle Variant Two\" or similar.";
            Assert.AreEqual("Pick the state to switch to, exactly as the controller shows it...", LeanDescription(quoted));

            // Too little would remain: the plain word-boundary cut stays.
            string early = "Pairs (alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau).";
            StringAssert.StartsWith("Pairs (alpha beta", LeanDescription(early));
            Assert.LessOrEqual(LeanDescription(early).Length, UapLeanSchema.DefaultMaxDescriptionChars);

            // A bracket that closes before the cut is not an opener.
            string closed = "Offset (x, y) in pixels from the anchor, positive to the right and up, negative to the left and down always.";
            StringAssert.StartsWith("Offset (x, y) in pixels from the anchor", LeanDescription(closed));
        }

        [Test]
        public void Lean_LabelExtensionAndBracketBackoff_AreIdempotent()
        {
            JsonNode schema = JsonNode.NewObject().Set("type", "object").Set("properties", JsonNode.NewObject()
                .Set("a", JsonNode.NewObject().Set("type", "number").Set("description", "start only. Frames per second, 1 to 60 (default 30)."))
                .Set("b", JsonNode.NewObject().Set("type", "string").Set("description",
                    "Which axis to rotate around and how far, in degrees (e.g. 90 for a quarter turn, -90 for the other way round, 180 to flip)."))
                .Set("c", JsonNode.NewObject().Set("type", "string").Set("description", "Required.")));
            JsonNode once = UapLeanSchema.Lean(schema);
            Assert.AreEqual(JsonWriter.Write(once),
                JsonWriter.Write(UapLeanSchema.Lean(once)));
        }

        private static string LeanDescription(string text)
        {
            return UapLeanSchema.Lean(JsonNode.NewObject().Set("type", "string").Set("description", text))["description"].AsString();
        }

        [Test]
        public void Lean_EmptyOrBlankDescription_IsDropped()
        {
            JsonNode lean = UapLeanSchema.Lean(JsonNode.NewObject().Set("type", "string").Set("description", "   "));
            Assert.IsFalse(lean.HasKey("description"));
            Assert.AreEqual("string", lean["type"].AsString());
        }

        [Test]
        public void Lean_SelfReferencingTree_IsCutOff_NotAStackOverflow()
        {
            JsonNode loop = JsonNode.NewObject().Set("type", "object");
            loop.Set("items", loop);
            JsonNode lean = UapLeanSchema.Lean(loop);
            Assert.AreEqual("object", lean["type"].AsString());
        }

        [Test]
        public void Lean_EveryRegisteredTool_KeepsTheStructureOfItsSchema()
        {
            // The structural skeleton (types, enums, required, nesting) of the
            // real schemas -- Core's and, when installed, Pro's -- must come
            // through: compare with descriptions and any key outside the
            // structural set stripped from BOTH sides.
            ToolRegistry registry = ToolRegistry.CreateDefault();
            foreach (string name in registry.AllNames())
            {
                IUapTool tool = registry.Find(name);
                JsonNode full = tool.InputSchema;
                JsonNode lean = UapLeanSchema.Lean(full);
                Assert.AreEqual(Skeleton(full), Skeleton(lean), tool.Name);
                Assert.LessOrEqual(JsonWriter.Write(lean).Length, JsonWriter.Write(full).Length, tool.Name);
                Assert.AreEqual(JsonWriter.Write(lean), JsonWriter.Write(UapLeanSchema.Lean(lean)), tool.Name + " (idempotent)");
            }
        }

        [Test]
        public void CoreModuleSchemas_DeclareTheShapeOfEveryObjectAndArray_InTheSchemaItself()
        {
            // Lean keeps only the first sentence of a description. A field whose
            // shape ("each item: {a, b}") lives ONLY in its description would
            // lose it in the compact list, so every core-module object needs
            // "properties" and every array an "items" that says what it holds.
            ToolRegistry registry = ToolRegistry.CreateDefault();
            foreach (IUapTool tool in registry.ListEnabled(new[] { UapToolGateway.DirectlyListedModule }))
            {
                AssertShapeDeclared(tool.InputSchema, tool.Name);
            }
        }

        private static void AssertShapeDeclared(JsonNode node, string path)
        {
            if (node == null || !node.IsObject) { return; }
            bool hasEnum = node.HasKey("enum");
            string type = node.HasKey("type") && node["type"].IsString ? node["type"].AsString() : null;
            if (type == "object" && !hasEnum)
            {
                Assert.IsTrue(node.HasKey("properties"), path + " is an object without properties");
            }
            if (type == "array")
            {
                Assert.IsTrue(node.HasKey("items") && node["items"].IsObject
                    && (node["items"].HasKey("type") || node["items"].HasKey("enum")), path + " is an array without a typed items");
                AssertShapeDeclared(node["items"], path + ".items");
            }
            if (node.HasKey("properties") && node["properties"].IsObject)
            {
                foreach (var pair in node["properties"].Properties)
                {
                    AssertShapeDeclared(pair.Value, path + "." + pair.Key);
                }
            }
        }

        private static readonly System.Collections.Generic.HashSet<string> StructuralKeys =
            new System.Collections.Generic.HashSet<string>
            {
                "type", "enum", "default", "required", "properties", "items", "additionalProperties",
                "minimum", "maximum", "minItems", "maxItems"
            };

        /// <summary>The schema reduced to its structural keys only (recursively), as compact JSON.</summary>
        private static string Skeleton(JsonNode schema)
        {
            return JsonWriter.Write(Strip(schema));
        }

        private static JsonNode Strip(JsonNode node)
        {
            if (node.IsArray)
            {
                JsonNode list = JsonNode.NewArray();
                foreach (JsonNode item in node.Items) { list.Add(Strip(item)); }
                return list;
            }
            if (!node.IsObject) { return node; }
            JsonNode result = JsonNode.NewObject();
            foreach (var pair in node.Properties)
            {
                if (!StructuralKeys.Contains(pair.Key)) { continue; }
                if (pair.Key == "properties")
                {
                    JsonNode props = JsonNode.NewObject();
                    foreach (var p in pair.Value.Properties) { props.Set(p.Key, Strip(p.Value)); }
                    result.Set(pair.Key, props);
                }
                else if (pair.Key == "enum" || pair.Key == "default" || pair.Key == "required"
                    || pair.Key == "type")
                {
                    result.Set(pair.Key, pair.Value);
                }
                else
                {
                    result.Set(pair.Key, Strip(pair.Value));
                }
            }
            return result;
        }
    }
}

using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Item 2.8 A'+B (design note docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.8): the compact tools/list, the uap_tools/
    /// uap_call gateway, uap_help, and the ResolveEffectiveTool resolution
    /// permission decisions/displays use. Drives UapOpsRequestHandler
    /// directly without HTTP, the same way UapOpsRequestHandlerTests does
    /// -- StubUapTool (ToolRegistryTests.cs, same assembly/namespace) is
    /// reused rather than redefined.
    /// </summary>
    [TestFixture]
    public class UapToolGatewayTests
    {
        private const string Token = "gw-token";
        private const string McpPath = "/mcp";
        private const string McpCompactPath = "/mcp/compact";

        private ToolRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new ToolRegistry();
            _registry.Register(new StubUapTool
            {
                Name = "core_tool",
                Module = "core",
                Description = "Does the core thing -- e.g. a small example. Rest of the story.",
                ReadOnly = true
            });
            _registry.Register(new StubUapTool
            {
                Name = "editor_tool",
                Module = "editor",
                Description = "Moves an editor window around the screen.",
                Undoable = false
            });
        }

        [TearDown]
        public void TearDown()
        {
            UapOpsServer.ResetForTests();
        }

        private UapOpsRequestHandler NewHandler(string[] enabledModules)
        {
            return new UapOpsRequestHandler(_registry, () => Token, () => enabledModules,
                new DirectUapToolExecutor());
        }

        private static UapOpsHttpRequest Post(string path, string body)
        {
            return new UapOpsHttpRequest
            {
                Method = "POST",
                Path = path,
                AuthorizationHeader = "Bearer " + Token,
                Body = body
            };
        }

        private static string BuildRequest(long id, string method, JsonNode paramsNode = null)
        {
            JsonNode obj = JsonNode.NewObject().Set("jsonrpc", "2.0").Set("id", id).Set("method", method);
            if (paramsNode != null)
            {
                obj.Set("params", paramsNode);
            }
            return JsonWriter.Write(obj);
        }

        private static JsonNode CallParams(string name, JsonNode arguments = null)
        {
            JsonNode node = JsonNode.NewObject().Set("name", name);
            if (arguments != null)
            {
                node.Set("arguments", arguments);
            }
            return node;
        }

        // -----------------------------------------------------------------
        // tools/list: compact vs full
        // -----------------------------------------------------------------

        [Test]
        public void ToolsList_Full_ListsEveryEnabledTool_NoGatewayEntries()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];

            var names = new List<string>();
            foreach (JsonNode tool in tools.Items)
            {
                names.Add(tool["name"].AsString());
            }
            CollectionAssert.Contains(names, "core_tool");
            CollectionAssert.Contains(names, "editor_tool");
            CollectionAssert.DoesNotContain(names, UapToolGateway.ToolsToolName);
            CollectionAssert.DoesNotContain(names, UapToolGateway.CallToolName);
        }

        [Test]
        public void ToolsList_Compact_ListsOnlyCoreDirectly_PlusGatewayEntries()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpCompactPath, BuildRequest(1, "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];

            var names = new List<string>();
            foreach (JsonNode tool in tools.Items)
            {
                names.Add(tool["name"].AsString());
            }
            CollectionAssert.Contains(names, "core_tool");
            CollectionAssert.DoesNotContain(names, "editor_tool");
            CollectionAssert.Contains(names, UapToolGateway.ToolsToolName);
            CollectionAssert.Contains(names, UapToolGateway.CallToolName);
        }

        // -----------------------------------------------------------------
        // Lean schemas (design note 2026-10-02-session-context-cost.md
        // section 4): the compact list carries lean schemas, full mode and
        // every "names" detail path keep the tool's own schema.
        // -----------------------------------------------------------------

        private const string FieldDescription =
            "Where to put it, in world units. A long second sentence that only the full schema should carry.";

        private static JsonNode RichSchema()
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("properties", JsonNode.NewObject()
                    .Set("position", JsonNode.NewObject().Set("type", "array")
                        .Set("items", JsonNode.NewObject().Set("type", "number"))
                        .Set("minItems", 3).Set("maxItems", 3)
                        .Set("description", FieldDescription))
                    .Set("mode", JsonNode.NewObject().Set("type", "string")
                        .Set("enum", JsonNode.NewArray().Add("a").Add("b"))
                        .Set("description", "Which mode. More words follow here."))
                    .Set("name", JsonNode.NewObject().Set("type", "string")))
                .Set("required", JsonNode.NewArray().Add("mode"))
                .Set("additionalProperties", false);
        }

        private void RegisterRichTools()
        {
            _registry.Register(new StubUapTool
            {
                Name = "core_rich",
                Module = "core",
                Description = "Rich core tool. Second sentence of the description stays.",
                Schema = RichSchema()
            });
            _registry.Register(new StubUapTool
            {
                Name = "editor_rich",
                Module = "editor",
                Description = "Rich editor tool.",
                Schema = RichSchema()
            });
        }

        private static JsonNode ListedTool(JsonNode tools, string name)
        {
            foreach (JsonNode tool in tools.Items)
            {
                if (tool["name"].AsString() == name)
                {
                    return tool;
                }
            }
            return JsonNode.Null;
        }

        [Test]
        public void ToolsList_Compact_ListedToolSchemas_AreLean_DescriptionsUntouched()
        {
            RegisterRichTools();
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpCompactPath, BuildRequest(1, "tools/list")));
            JsonNode listed = ListedTool(JsonParser.Parse(response.Body)["result"]["tools"], "core_rich");

            Assert.IsTrue(listed.IsObject);
            Assert.AreEqual("Rich core tool. Second sentence of the description stays.", listed["description"].AsString());
            JsonNode schema = listed["inputSchema"];
            Assert.AreEqual(JsonWriter.Write(UapLeanSchema.Lean(RichSchema())), JsonWriter.Write(schema));
            Assert.AreEqual("Where to put it, in world units.", schema["properties"]["position"]["description"].AsString());
            Assert.AreEqual("Which mode.", schema["properties"]["mode"]["description"].AsString());
            // Everything a client needs to form a valid call is still there.
            Assert.AreEqual(3, schema["properties"]["position"]["minItems"].AsInt(-1));
            Assert.AreEqual("b", schema["properties"]["mode"]["enum"][1].AsString());
            Assert.AreEqual("mode", schema["required"][0].AsString());
            Assert.IsFalse(schema["additionalProperties"].AsBool(true));
            Assert.Less(JsonWriter.Write(schema).Length, JsonWriter.Write(RichSchema()).Length);
        }

        [Test]
        public void ToolsList_Compact_GatewayEntries_CarryLeanSchemas()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpCompactPath, BuildRequest(1, "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];

            JsonNode toolsEntry = ListedTool(tools, UapToolGateway.ToolsToolName);
            JsonNode callEntry = ListedTool(tools, UapToolGateway.CallToolName);
            Assert.AreEqual(JsonWriter.Write(UapLeanSchema.Lean(UapToolGateway.ToolsInputSchema())),
                JsonWriter.Write(toolsEntry["inputSchema"]));
            Assert.AreEqual(JsonWriter.Write(UapLeanSchema.Lean(UapToolGateway.CallInputSchema())),
                JsonWriter.Write(callEntry["inputSchema"]));
            Assert.AreEqual("name", callEntry["inputSchema"]["required"][0].AsString());
            // Descriptions of the gateway tools are not trimmed.
            Assert.AreEqual(UapToolGateway.ToolsDescription, toolsEntry["description"].AsString());
            Assert.AreEqual(UapToolGateway.CallDescription, callEntry["description"].AsString());
        }

        [Test]
        public void ToolsList_Compact_GatewayAndHelpDescriptions_SayListedSchemasAreLean()
        {
            StringAssert.Contains("one-line field descriptions", UapToolGateway.ToolsDescription);
            StringAssert.Contains("uap_help names:[...] returns the full schema", UapToolGateway.ToolsDescription);
            StringAssert.Contains("one-line field descriptions", UapToolGateway.CallDescription);
            StringAssert.Contains("uap_help names:[...] returns the full schema", UapToolGateway.CallDescription);
            StringAssert.Contains("one-line field descriptions", new UapHelpTool().Description);
        }

        [Test]
        public void ToolsList_Full_SchemasAndDescriptions_AreTheToolsOwn_ByteForByte()
        {
            RegisterRichTools();
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];

            foreach (string name in new[] { "core_rich", "editor_rich" })
            {
                JsonNode listed = ListedTool(tools, name);
                IUapTool own = _registry.Find(name);
                Assert.AreEqual(JsonWriter.Write(own.InputSchema), JsonWriter.Write(listed["inputSchema"]), name);
                Assert.AreEqual(own.Description, listed["description"].AsString(), name);
                Assert.AreEqual(FieldDescription,
                    listed["inputSchema"]["properties"]["position"]["description"].AsString(), name);
            }
        }

        [Test]
        public void UapTools_Names_ReturnsTheFullSchema_EvenWhenAskedOnTheCompactPath()
        {
            RegisterRichTools();
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode args = JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("editor_rich").Add("core_rich"));
            UapOpsHttpResponse response = handler.Handle(
                Post(McpCompactPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName, args))));
            JsonNode body = JsonParser.Parse(response.Body);
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            Assert.AreEqual(2, results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                Assert.AreEqual(JsonWriter.Write(RichSchema()), JsonWriter.Write(results[i]["inputSchema"]));
                Assert.AreEqual(FieldDescription,
                    results[i]["inputSchema"]["properties"]["position"]["description"].AsString());
            }
            Assert.AreEqual("Rich editor tool.", results[0]["description"].AsString());
        }

        [Test]
        public void UapHelp_Names_ReturnsTheFullSchema_NotTheLeanOne()
        {
            RegisterRichTools();
            JsonNode input = JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("core_rich"));
            JsonNode result = new UapHelpTool().Execute(input, _registry, new[] { "core", "editor" },
                UapPlaybookCatalog.CreateBuiltIn(), true);
            JsonNode tool = JsonParser.Parse(result[0]["text"].AsString())["tools"][0];
            Assert.IsTrue(tool["found"].AsBool());
            Assert.AreEqual(JsonWriter.Write(RichSchema()), JsonWriter.Write(tool["inputSchema"]));
            Assert.AreEqual(FieldDescription,
                tool["inputSchema"]["properties"]["position"]["description"].AsString());
        }

        [Test]
        public void ToolsList_CompactListedSchemas_AreNeverLargerThanFull_ForTheRealRegistry()
        {
            // Every real core tool: lean never grows, and its structure matches
            // (UapLeanSchemaTests checks the skeleton; this checks the wiring).
            ToolRegistry real = ToolRegistry.CreateDefault();
            var handler = new UapOpsRequestHandler(real, () => Token, () => new[] { "core" }, new DirectUapToolExecutor());
            JsonNode compactTools = JsonParser.Parse(
                handler.Handle(Post(McpCompactPath, BuildRequest(1, "tools/list"))).Body)["result"]["tools"];
            int checkedTools = 0;
            foreach (JsonNode listed in compactTools.Items)
            {
                IUapTool own = real.Find(listed["name"].AsString());
                if (own == null) { continue; } // the two gateway entries
                checkedTools++;
                Assert.AreEqual(own.Description, listed["description"].AsString(), own.Name);
                Assert.AreEqual(JsonWriter.Write(UapLeanSchema.Lean(own.InputSchema)),
                    JsonWriter.Write(listed["inputSchema"]), own.Name);
                Assert.LessOrEqual(JsonWriter.Write(listed["inputSchema"]).Length,
                    JsonWriter.Write(own.InputSchema).Length, own.Name);
            }
            Assert.Greater(checkedTools, 0);
        }

        [Test]
        public void ToolsList_Compact_WithoutCoreEnabled_ListsOnlyTheGatewayEntries()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "editor" });
            UapOpsHttpResponse response = handler.Handle(Post(McpCompactPath, BuildRequest(1, "tools/list")));
            JsonNode tools = JsonParser.Parse(response.Body)["result"]["tools"];
            Assert.AreEqual(2, tools.Count);
        }

        [Test]
        public void UnknownPath_Returns404_EvenWithAValidMethodAndBody()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" });
            UapOpsHttpResponse response = handler.Handle(Post("/mcp/nope", BuildRequest(1, "tools/list")));
            Assert.AreEqual(404, response.StatusCode);
        }

        // -----------------------------------------------------------------
        // tools/call: a direct call by name keeps working on BOTH paths
        // -----------------------------------------------------------------

        [Test]
        public void ToolsCall_DirectByName_WorksOnCompactPath_ForAToolNotListedThere()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            UapOpsHttpResponse response = handler.Handle(
                Post(McpCompactPath, BuildRequest(1, "tools/call", CallParams("editor_tool"))));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsFalse(body["result"]["isError"].AsBool(true));
        }

        // -----------------------------------------------------------------
        // uap_call unwrapping
        // -----------------------------------------------------------------

        [Test]
        public void UapCall_UnwrapsToTheInnerTool_AndPassesItsArguments()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode innerArgs = JsonNode.NewObject().Set("x", 1);
            JsonNode wrapped = CallParams(UapToolGateway.CallToolName,
                JsonNode.NewObject().Set("name", "editor_tool").Set("arguments", innerArgs));
            handler.Handle(Post(McpPath, BuildRequest(1, "tools/call", wrapped)));

            StubUapTool editorTool = (StubUapTool)_registry.Find("editor_tool");
            Assert.AreEqual(1, editorTool.ExecuteCallCount);
            Assert.AreEqual(1, editorTool.LastInput["x"].AsInt());
        }

        [Test]
        public void UapCall_InnerToolFromADisabledModule_IsRefused_SameMessageAsADirectCall()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" }); // "editor" NOT enabled
            JsonNode wrapped = CallParams(UapToolGateway.CallToolName,
                JsonNode.NewObject().Set("name", "editor_tool"));
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/call", wrapped)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
            StringAssert.Contains("module 'editor' is disabled", body["result"]["content"][0]["text"].AsString());

            StubUapTool editorTool = (StubUapTool)_registry.Find("editor_tool");
            Assert.AreEqual(0, editorTool.ExecuteCallCount);
        }

        [Test]
        public void UapCall_CallingItselfByName_IsRefusedAsUnknownTool_NeverRecurses()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode wrapped = CallParams(UapToolGateway.CallToolName,
                JsonNode.NewObject().Set("name", UapToolGateway.CallToolName));
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/call", wrapped)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
            StringAssert.Contains("Unknown tool: " + UapToolGateway.CallToolName,
                body["result"]["content"][0]["text"].AsString());
        }

        [Test]
        public void UapCall_NamingUapTools_IsRefusedAsUnknownTool()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode wrapped = CallParams(UapToolGateway.CallToolName,
                JsonNode.NewObject().Set("name", UapToolGateway.ToolsToolName));
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/call", wrapped)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
        }

        [Test]
        public void UapCall_MissingInnerName_ReturnsAHelpfulError_NotAnUnknownToolMessage()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" });
            JsonNode wrapped = CallParams(UapToolGateway.CallToolName, JsonNode.NewObject());
            UapOpsHttpResponse response = handler.Handle(Post(McpPath, BuildRequest(1, "tools/call", wrapped)));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsTrue(body["result"]["isError"].AsBool());
            StringAssert.Contains("requires 'name'", body["result"]["content"][0]["text"].AsString());
        }

        // -----------------------------------------------------------------
        // uap_tools: search and detail
        // -----------------------------------------------------------------

        [Test]
        public void UapTools_Search_MatchesByModule_AcrossEnabledNonCoreModules()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode args = JsonNode.NewObject().Set("module", "editor");
            UapOpsHttpResponse response = handler.Handle(
                Post(McpPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName, args))));
            JsonNode body = JsonParser.Parse(response.Body);
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("editor_tool", results[0]["name"].AsString());
            Assert.AreEqual("editor", results[0]["module"].AsString());
            Assert.IsFalse(string.IsNullOrEmpty(results[0]["summary"].AsString()));
        }

        [Test]
        public void UapTools_Search_ExcludesModulesThatAreNotEnabled()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" }); // "editor" off
            UapOpsHttpResponse response = handler.Handle(
                Post(McpPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName))));
            JsonNode body = JsonParser.Parse(response.Body);
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            var names = new List<string>();
            foreach (JsonNode r in results.Items) { names.Add(r["name"].AsString()); }
            CollectionAssert.Contains(names, "core_tool");
            CollectionAssert.DoesNotContain(names, "editor_tool");
        }

        [Test]
        public void UapTools_Detail_ForAnEnabledName_ReturnsFullDescriptionAndSchema()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core", "editor" });
            JsonNode args = JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("editor_tool"));
            UapOpsHttpResponse response = handler.Handle(
                Post(McpPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName, args))));
            JsonNode body = JsonParser.Parse(response.Body);
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0]["found"].AsBool());
            Assert.AreEqual("Moves an editor window around the screen.", results[0]["description"].AsString());
            Assert.IsTrue(results[0]["inputSchema"].IsObject);
        }

        [Test]
        public void UapTools_Detail_ForAnUnknownName_ReportsFoundFalse_NotAnError()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" });
            JsonNode args = JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("does_not_exist"));
            UapOpsHttpResponse response = handler.Handle(
                Post(McpPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName, args))));
            JsonNode body = JsonParser.Parse(response.Body);
            Assert.IsFalse(body["result"]["isError"].AsBool(true));
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            Assert.IsFalse(results[0]["found"].AsBool(true));
            StringAssert.Contains("unknown", results[0]["reason"].AsString());
        }

        [Test]
        public void UapTools_Detail_ForANameInADisabledModule_ReportsFoundFalse_WithTheModuleReason()
        {
            UapOpsRequestHandler handler = NewHandler(new[] { "core" }); // "editor" off
            JsonNode args = JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("editor_tool"));
            UapOpsHttpResponse response = handler.Handle(
                Post(McpPath, BuildRequest(1, "tools/call", CallParams(UapToolGateway.ToolsToolName, args))));
            JsonNode body = JsonParser.Parse(response.Body);
            JsonNode results = JsonParser.Parse(body["result"]["content"][0]["text"].AsString())["tools"];
            Assert.IsFalse(results[0]["found"].AsBool(true));
            StringAssert.Contains("disabled", results[0]["reason"].AsString());
        }

        // -----------------------------------------------------------------
        // uap_help
        // -----------------------------------------------------------------

        [Test]
        public void UapHelp_Metadata_IsCoreModule_ReadOnly_NotUndoable()
        {
            var tool = new UapHelpTool();
            Assert.AreEqual("core", tool.Module);
            Assert.IsTrue(tool.ReadOnly);
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
            Assert.AreEqual("object", tool.InputSchema["type"].AsString());
        }

        [Test]
        public void UapHelp_NoNamesAndNoName_ThrowsArgumentException()
        {
            var tool = new UapHelpTool();
            Assert.Throws<System.ArgumentException>(delegate { tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void UapHelp_SingleNameConvenience_AndNamesArray_BothResolve()
        {
            UapOpsServer.ResetForTests();
            UapOpsServer.SetEnabledModules(new[] { "core" });
            var tool = new UapHelpTool();

            JsonNode viaName = tool.Execute(JsonNode.NewObject().Set("name", "uap_ping"));
            JsonNode viaNameParsed = JsonParser.Parse(viaName[0]["text"].AsString())["tools"];
            Assert.IsTrue(viaNameParsed[0]["found"].AsBool());
            Assert.AreEqual("uap_ping", viaNameParsed[0]["name"].AsString());

            JsonNode viaNames = tool.Execute(
                JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("uap_ping")));
            JsonNode viaNamesParsed = JsonParser.Parse(viaNames[0]["text"].AsString())["tools"];
            Assert.IsTrue(viaNamesParsed[0]["found"].AsBool());
        }

        [Test]
        public void UapHelp_UnknownName_ReportsFoundFalse_NotAnException()
        {
            UapOpsServer.ResetForTests();
            UapOpsServer.SetEnabledModules(new[] { "core" });
            var tool = new UapHelpTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("name", "totally_not_a_tool"));
            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString())["tools"];
            Assert.IsFalse(parsed[0]["found"].AsBool(true));
        }

        // -----------------------------------------------------------------
        // ResolveEffectiveTool: the permission-decision/display resolution
        // (design section 2.8 item 4)
        // -----------------------------------------------------------------

        [Test]
        public void ResolveEffectiveTool_PlainWireName_ResolvesDirectly_InputUnchanged()
        {
            JsonNode input = JsonNode.NewObject().Set("path", "Foo");
            JsonNode effective;
            IUapTool tool = UapOpsServer.ResolveEffectiveTool(
                "mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create", input, out effective);
            Assert.IsNotNull(tool);
            Assert.AreEqual("uap_asset_create", tool.Name);
            Assert.AreSame(input, effective);
        }

        [Test]
        public void ResolveEffectiveTool_UapCall_ResolvesTheInnerTool_AndItsArguments()
        {
            JsonNode innerArgs = JsonNode.NewObject().Set("path", "Foo");
            JsonNode outerInput = JsonNode.NewObject()
                .Set("name", "uap_asset_create").Set("arguments", innerArgs);
            JsonNode effective;
            IUapTool tool = UapOpsServer.ResolveEffectiveTool(
                "mcp__" + UapOpsMcpConfig.ServerName + "__" + UapToolGateway.CallToolName,
                outerInput, out effective);
            Assert.IsNotNull(tool);
            Assert.AreEqual("uap_asset_create", tool.Name);
            Assert.AreSame(innerArgs, effective);
        }

        [Test]
        public void ResolveEffectiveTool_MalformedUapCall_ResolvesNull()
        {
            IUapTool tool = UapOpsServer.ResolveEffectiveTool(
                "mcp__" + UapOpsMcpConfig.ServerName + "__" + UapToolGateway.CallToolName,
                JsonNode.NewObject(), out _);
            Assert.IsNull(tool);
        }

        [Test]
        public void ResolveEffectiveTool_NonUapOpsWireName_ResolvesNull()
        {
            IUapTool tool = UapOpsServer.ResolveEffectiveTool("Bash", JsonNode.NewObject(), out _);
            Assert.IsNull(tool);
        }
    }
}

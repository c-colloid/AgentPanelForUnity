using System.Collections.Generic;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_help topic:"&lt;id&gt;" playbooks (design note docs/design-notes/
    /// 2026-10-02-uap-help-playbooks.md): serving a topic, the "unavailable"
    /// rule that follows module toggles, the index line in the steering text,
    /// provider merging with a fake provider, and a guard that no built-in
    /// body names a tool that does not exist. The Unity-free catalog logic is
    /// also covered by the license-free smoke harness (PlaybookSmoke).
    /// </summary>
    [TestFixture]
    public class UapHelpPlaybookTests
    {
        private ToolRegistry _registry;
        private UapPlaybookCatalog _catalog;

        [SetUp]
        public void SetUp()
        {
            // The real Core registry: the playbooks must be checked against the
            // tools that actually exist, not a stub list that can drift.
            _registry = ToolRegistry.CreateDefault();
            _catalog = UapPlaybookCatalog.CreateBuiltIn();
        }

        [TearDown]
        public void TearDown()
        {
            UapOpsServer.ResetForTests();
        }

        private string Help(JsonNode input, string[] modules, bool compact = false, UapPlaybookCatalog catalog = null,
            ToolRegistry registry = null)
        {
            JsonNode result = new UapHelpTool().Execute(input, registry ?? _registry, modules, catalog ?? _catalog, compact);
            return result[0]["text"].AsString();
        }

        private static JsonNode Topic(string id)
        {
            return JsonNode.NewObject().Set("topic", id);
        }

        // -- uap_help topic ----------------------------------------------------

        [Test]
        public void Topic_Found_ReturnsTitleSummaryRequiredToolsAndBody()
        {
            string text = Help(Topic("ui-click"), new[] { "core", "editor" });

            StringAssert.Contains("Playbook \"ui-click\"", text);
            StringAssert.Contains(_catalog.Find("ui-click").Title, text);
            StringAssert.Contains(_catalog.Find("ui-click").Summary, text);
            StringAssert.Contains("Required tools: uap_editor_screenshot, uap_game_pointer, uap_play_mode", text);
            StringAssert.Contains(_catalog.Find("ui-click").Body, text);
        }

        [Test]
        public void Topic_UnavailableWhenARequiredToolsModuleIsDisabled_ListsWhatIsAvailable()
        {
            // verify-gameplay needs the editor module's tools; with only core on it
            // is unavailable, and the answer is the available list, not an exception.
            string text = Help(Topic("verify-gameplay"), new[] { "core" });

            StringAssert.Contains("No playbook \"verify-gameplay\" is available", text);
            StringAssert.Contains("- script-change:", text);
            StringAssert.DoesNotContain("- verify-gameplay:", text);
            StringAssert.DoesNotContain("1. uap_project_info", text, "the body must not be served for an unavailable topic");
        }

        [Test]
        public void Topic_Unknown_ListsAvailableIds_NotAnException()
        {
            string text = null;
            Assert.DoesNotThrow(delegate { text = Help(Topic("no-such-playbook"), new[] { "core", "editor", "code" }); });

            StringAssert.Contains("No playbook \"no-such-playbook\" is available", text);
            StringAssert.Contains("- verify-gameplay:", text);
            StringAssert.Contains("- ui-click:", text);
            StringAssert.Contains("- script-change:", text);
            StringAssert.Contains("- execute-code:", text);
            StringAssert.DoesNotContain("- compact-mode:", text, "needs the gateway tools, which a full-list connection does not have");
        }

        [Test]
        public void Topic_WithNoModulesEnabled_SaysNothingIsAvailable()
        {
            string text = Help(Topic("verify-gameplay"), new string[0]);
            StringAssert.Contains("no playbooks are available at all", text);
        }

        [Test]
        public void Topic_CompactMode_IsOnlyOfferedOnACompactConnection()
        {
            string full = Help(Topic("compact-mode"), new[] { "core" }, false);
            string compact = Help(Topic("compact-mode"), new[] { "core" }, true);

            StringAssert.Contains("No playbook \"compact-mode\" is available", full);
            StringAssert.Contains("Playbook \"compact-mode\"", compact);
            StringAssert.Contains("uap_call", compact);
        }

        [Test]
        public void GatewayToolNames_MatchTheCatalogsPrivateLiterals()
        {
            // UapPlaybookCatalog repeats the two names (it must stay Unity-free); this
            // is the test that keeps the copies honest, same idea as
            // UapToolListModePolicyTests.SteeringSentence_TheThreeDuplicatedLiterals.
            System.Func<string, string> noRegistry = delegate (string name) { return null; };
            System.Func<string, bool> compact = UapPlaybookCatalog.AvailabilityFor(new string[0], noRegistry, true);
            System.Func<string, bool> full = UapPlaybookCatalog.AvailabilityFor(new string[0], noRegistry, false);

            Assert.IsTrue(compact(UapToolGateway.ToolsToolName));
            Assert.IsTrue(compact(UapToolGateway.CallToolName));
            Assert.IsFalse(full(UapToolGateway.ToolsToolName));
            Assert.IsFalse(full(UapToolGateway.CallToolName));
        }

        [Test]
        public void NoNamesAndNoTopic_StillThrows_AndTheMessageNamesTheTopics()
        {
            var tool = new UapHelpTool();
            var ex = Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject(), _registry, new[] { "core" }, _catalog, false);
            });

            StringAssert.Contains("'topic'", ex.Message);
            StringAssert.Contains("Playbooks (uap_help topic:\"<id>\"): script-change", ex.Message);
        }

        [Test]
        public void Names_WithoutATopic_AreByteCompatibleWithTheOldReply()
        {
            string[] modules = { "core" };
            JsonNode expected = JsonNode.NewObject().Set("tools", JsonNode.NewArray()
                .Add(UapToolGateway.ResolveToolDetail(_registry, modules, "uap_ping"))
                .Add(UapToolGateway.ResolveToolDetail(_registry, modules, "totally_not_a_tool")));

            string actual = Help(JsonNode.NewObject().Set("names", JsonNode.NewArray().Add("uap_ping").Add("totally_not_a_tool")), modules);

            Assert.AreEqual(JsonWriter.Write(expected), actual);
        }

        [Test]
        public void NamesAndTopicTogether_ReturnBoth()
        {
            string text = Help(JsonNode.NewObject().Set("name", "uap_ping").Set("topic", "script-change"), new[] { "core" });

            StringAssert.StartsWith("{\"tools\":", text);
            StringAssert.Contains("\"uap_ping\"", text);
            StringAssert.Contains("Playbook \"script-change\"", text);
        }

        [Test]
        public void Metadata_StaysReadOnly_AndTheDescriptionAndSchemaCarryTheTopic()
        {
            var tool = new UapHelpTool();
            Assert.IsTrue(tool.ReadOnly);
            Assert.IsFalse(tool.Undoable);
            Assert.AreEqual("core", tool.Module);
            Assert.IsTrue(tool.InputSchema["properties"].HasKey("topic"));
            Assert.AreEqual("string", tool.InputSchema["properties"]["topic"]["type"].AsString());

            // uap_tools shows only the first sentence on a compact list: it has to name both jobs.
            string summary = UapToolSummary.FirstSentence(tool.Description);
            StringAssert.Contains("inputSchema", summary);
            StringAssert.Contains("playbook", summary);
        }

        // -- steering index ----------------------------------------------------

        private string Steering(string[] modules, bool compact = false, UapPlaybookCatalog catalog = null, ToolRegistry registry = null)
        {
            return AgentHub.ComposeUapOpsSteeringSection(true, modules, false, true, compact, catalog ?? _catalog, registry ?? _registry);
        }

        [Test]
        public void Steering_EndsWithTheIndexOfTheAvailablePlaybooks()
        {
            string section = Steering(new[] { "core", "editor", "code" });

            string lastLine = section.Substring(section.LastIndexOf('\n') + 1);
            Assert.AreEqual("Playbooks (uap_help topic:\"<id>\"): verify-gameplay, ui-click, script-change, reproduce-input, execute-code",
                lastLine);
        }

        [Test]
        public void Steering_IndexFollowsTheEnabledModules()
        {
            string coreOnly = Steering(new[] { "core" });
            StringAssert.Contains("script-change", coreOnly);
            StringAssert.DoesNotContain("verify-gameplay", coreOnly);
            StringAssert.DoesNotContain("execute-code", coreOnly);

            string withoutCode = Steering(new[] { "core", "editor" });
            StringAssert.Contains("verify-gameplay", withoutCode);
            StringAssert.DoesNotContain("execute-code", withoutCode);
        }

        [Test]
        public void Steering_CompactIndexAddsCompactMode_FullDoesNot()
        {
            StringAssert.Contains("compact-mode", Steering(new[] { "core" }, true));
            StringAssert.DoesNotContain("compact-mode", Steering(new[] { "core" }, false));
        }

        [Test]
        public void Steering_NoAvailablePlaybook_AddsNoLine()
        {
            // prefab alone enables a family but none of the playbooks' tools.
            string section = Steering(new[] { "prefab" });
            StringAssert.DoesNotContain("Playbooks (", section);
            Assert.IsFalse(section.EndsWith("\n"));
        }

        [Test]
        public void Steering_StillKeepsTheSafetyStatements()
        {
            string section = Steering(new[] { "core", "editor", "code" });

            StringAssert.Contains("prefer the typed uap_* tools", section);
            StringAssert.Contains("is never auto-approved", section);
            StringAssert.Contains("playbook verify-gameplay", section);
        }

        // -- providers ---------------------------------------------------------

        private sealed class FakePlaybookProvider : IUapPlaybookProvider
        {
            public readonly List<UapPlaybook> Items = new List<UapPlaybook>();

            public IEnumerable<UapPlaybook> CreatePlaybooks()
            {
                return Items;
            }
        }

        private static UapPlaybook Fake(string id, params string[] tools)
        {
            return new UapPlaybook(id, "Fake " + id, "Fake summary of " + id + ".", tools, "1. Call fake_tool.\n2. Done.");
        }

        private ToolRegistry RegistryWithFakeModule()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "fake_tool", Module = "fake" });
            registry.Register(new StubUapTool { Name = "uap_ping", Module = "core" });
            return registry;
        }

        [Test]
        public void Provider_Merged_ServedByHelpAndNamedInSteering_OnlyWhileItsModuleIsOn()
        {
            var provider = new FakePlaybookProvider();
            provider.Items.Add(Fake("fake-flow", "fake_tool"));
            UapPlaybookCatalog catalog = UapPlaybookCatalog.Create(new IUapPlaybookProvider[] { provider }, null);
            ToolRegistry registry = RegistryWithFakeModule();

            string on = Help(Topic("fake-flow"), new[] { "core", "fake" }, false, catalog, registry);
            StringAssert.Contains("Playbook \"fake-flow\"", on);
            StringAssert.Contains("1. Call fake_tool.", on);
            StringAssert.Contains("fake-flow", Steering(new[] { "core", "fake" }, false, catalog, registry));

            string off = Help(Topic("fake-flow"), new[] { "core" }, false, catalog, registry);
            StringAssert.Contains("No playbook \"fake-flow\" is available", off);
            StringAssert.DoesNotContain("fake-flow", Steering(new[] { "core" }, false, catalog, registry));
        }

        [Test]
        public void Provider_ToolThatIsNotRegistered_MakesThePlaybookUnavailable()
        {
            var provider = new FakePlaybookProvider();
            provider.Items.Add(Fake("ghost-flow", "ghost_tool"));
            UapPlaybookCatalog catalog = UapPlaybookCatalog.Create(new IUapPlaybookProvider[] { provider }, null);

            string text = Help(Topic("ghost-flow"), new[] { "core", "fake" }, false, catalog, RegistryWithFakeModule());

            StringAssert.Contains("No playbook \"ghost-flow\" is available", text);
        }

        [Test]
        public void Provider_DuplicateId_IsRejectedWithALog_CoreWins()
        {
            var provider = new FakePlaybookProvider();
            provider.Items.Add(Fake("verify-gameplay", "fake_tool"));
            provider.Items.Add(Fake("fresh-one", "fake_tool"));
            var log = new List<string>();

            UapPlaybookCatalog catalog = UapPlaybookCatalog.Create(
                new IUapPlaybookProvider[] { provider }, delegate (string message) { log.Add(message); });

            Assert.AreEqual(_catalog.Count + 1, catalog.Count);
            Assert.AreEqual(_catalog.Find("verify-gameplay").Body, catalog.Find("verify-gameplay").Body);
            Assert.IsNotNull(catalog.Find("fresh-one"));
            Assert.AreEqual(1, log.Count);
            StringAssert.Contains("verify-gameplay", log[0]);
            StringAssert.Contains("already taken", log[0]);
        }

        [Test]
        public void Discovery_IgnoresNonPublicProviders_AndTheEditorCatalogHoldsTheBuiltIns()
        {
            // FakePlaybookProvider above is a PRIVATE nested class: like the tool-provider
            // stubs, it must never reach production discovery.
            foreach (IUapPlaybookProvider provider in UapPlaybookProviderDiscovery.Discover())
            {
                Assert.AreNotEqual(typeof(FakePlaybookProvider), provider.GetType());
            }
            Assert.GreaterOrEqual(UapOpsServer.Playbooks.Count, 6);
            Assert.IsNotNull(UapOpsServer.Playbooks.Find("verify-gameplay"));
        }

        // -- the built-in bodies against the real tools --------------------------

        [Test]
        public void BuiltInPlaybooks_NameOnlyToolsThatExist()
        {
            var known = new HashSet<string>(_registry.AllNames());
            known.Add(UapToolGateway.ToolsToolName);
            known.Add(UapToolGateway.CallToolName);

            foreach (UapPlaybook playbook in _catalog.All)
            {
                foreach (string required in playbook.RequiredTools)
                {
                    Assert.IsTrue(known.Contains(required), playbook.Id + " requires unknown tool " + required);
                }
                foreach (Match match in Regex.Matches(playbook.Body, "uap_[a-z0-9_]+"))
                {
                    Assert.IsTrue(known.Contains(match.Value), playbook.Id + " names unknown tool " + match.Value);
                }
            }
        }

        [Test]
        public void BuiltInPlaybooks_EveryRequiredToolIsNamedInTheBody_AndTheBodyIsASensibleSize()
        {
            foreach (UapPlaybook playbook in _catalog.All)
            {
                foreach (string required in playbook.RequiredTools)
                {
                    StringAssert.Contains(required, playbook.Body, playbook.Id);
                }
                Assert.GreaterOrEqual(playbook.Body.Length, 600, playbook.Id);
                Assert.LessOrEqual(playbook.Body.Length, 1700, playbook.Id);
            }
        }

        [Test]
        public void BuiltInPlaybooks_ArgumentsNamedInTheBody_ExistInTheToolsSchema()
        {
            // "tool_name key:value" -- every key written right after a tool's name in
            // a step must be an input property of that tool, so a body can never teach
            // an argument that does not exist.
            foreach (UapPlaybook playbook in _catalog.All)
            {
                foreach (string line in playbook.Body.Split('\n'))
                {
                    foreach (Match toolMatch in Regex.Matches(line, "(uap_[a-z0-9_]+)((?: [a-z_A-Z]+:\\S*)+)"))
                    {
                        IUapTool tool = _registry.Find(toolMatch.Groups[1].Value);
                        if (tool == null)
                        {
                            continue;
                        }
                        foreach (Match arg in Regex.Matches(toolMatch.Groups[2].Value, "([a-z_A-Z]+):"))
                        {
                            Assert.IsTrue(tool.InputSchema["properties"].HasKey(arg.Groups[1].Value),
                                playbook.Id + ": " + tool.Name + " has no argument '" + arg.Groups[1].Value + "'");
                        }
                    }
                }
            }
        }
    }
}

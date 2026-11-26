using System;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The PreToolUse hook answered over the control protocol (design note
    /// docs/design-notes/2026-11-23-request-checkpoints.md section 2): the
    /// handshake registers it only when asked, and every hook_callback is
    /// answered -- the CLI holds the tool until the answer arrives, so an
    /// unanswered one is a hung turn. The wire shapes here are the ones
    /// measured against CLI 2.1.330.
    /// </summary>
    [TestFixture]
    public class AgentClientHookCallbackTests
    {
        private const string HookLine =
            "{\"type\":\"control_request\",\"request_id\":\"hook_1\",\"request\":{\"subtype\":\"hook_callback\","
            + "\"callback_id\":\"uap_checkpoint\",\"tool_use_id\":\"toolu_1\",\"input\":{\"hook_event_name\":\"PreToolUse\","
            + "\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:/fake/project/Assets/Foo.cs\",\"old_string\":\"a\",\"new_string\":\"b\"}}}}";

        private FakeCliProcess _fake;
        private AgentClient _client;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeCliProcess();
            _client = new AgentClient(_fake);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        private void PumpAll()
        {
            while (_client.Pump(50, 50.0) > 0)
            {
            }
        }

        private void Start(bool withHook)
        {
            _client.Start(new AgentClientOptions
            {
                CliPath = "C:/fake/claude.exe",
                WorkingDirectory = "C:/fake/project",
                PreToolUseHookMatcher = withHook ? UapCheckpointPolicy.HookMatcher : null,
                PreToolUseHookCallbackId = UapCheckpointPolicy.HookCallbackId
            });
            _fake.ScriptLine(
                "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"C:/fake/project\",\"session_id\":\"s1\"}");
            PumpAll();
        }

        private JsonNode LastWritten()
        {
            Assert.Greater(_fake.WrittenLines.Count, 0);
            return JsonParser.Parse(_fake.WrittenLines[_fake.WrittenLines.Count - 1]);
        }

        [Test]
        public void Handshake_RegistersTheHook_WhenAMatcherIsGiven()
        {
            Start(true);

            JsonNode request = JsonParser.Parse(_fake.WrittenLines[0])["request"];
            Assert.AreEqual("initialize", request["subtype"].AsString());
            JsonNode hook = request["hooks"]["PreToolUse"][0];
            Assert.AreEqual(UapCheckpointPolicy.HookMatcher, hook["matcher"].AsString());
            StringAssert.Contains("Write", hook["matcher"].AsString());
            StringAssert.Contains("Bash", hook["matcher"].AsString());
            Assert.AreEqual("uap_checkpoint", hook["hookCallbackIds"][0].AsString());
        }

        [Test]
        public void Handshake_IsUnchanged_WithoutAMatcher()
        {
            Start(false);

            JsonNode request = JsonParser.Parse(_fake.WrittenLines[0])["request"];
            Assert.AreEqual("initialize", request["subtype"].AsString());
            Assert.IsFalse(request.HasKey("hooks"),
                "no matcher must leave the handshake exactly as it was before hooks existed");
        }

        [Test]
        public void HookCallback_RaisesTheEventFirst_ThenAnswersWithAnEmptyResult()
        {
            Start(true);
            int linesWhenRaised = -1;
            string seenPath = null;
            _client.HookCallbackReceived += delegate (ControlRequestMessage message)
            {
                linesWhenRaised = _fake.WrittenLines.Count;
                JsonNode input = message.Request["input"];
                seenPath = UapCheckpointPolicy.FilePathOfToolInput(
                    input["tool_name"].AsString(null), input["tool_input"]);
            };
            int before = _fake.WrittenLines.Count;

            _fake.ScriptLine(HookLine);
            PumpAll();

            Assert.AreEqual("C:/fake/project/Assets/Foo.cs", seenPath);
            Assert.AreEqual(before, linesWhenRaised,
                "the handler must run BEFORE the answer is written: the CLI writes the file as soon as it has the answer");
            Assert.AreEqual(before + 1, _fake.WrittenLines.Count);
            JsonNode response = LastWritten();
            Assert.AreEqual("control_response", response["type"].AsString());
            Assert.AreEqual("success", response["response"]["subtype"].AsString());
            Assert.AreEqual("hook_1", response["response"]["request_id"].AsString());
            Assert.IsTrue(response["response"]["response"].IsObject);
            Assert.AreEqual(0, response["response"]["response"].Count, "an empty hook result: no decision, the tool proceeds");
        }

        [Test]
        public void HookCallback_IsAnswered_WithNoSubscriber()
        {
            Start(true);
            int before = _fake.WrittenLines.Count;

            _fake.ScriptLine(HookLine);
            PumpAll();

            Assert.AreEqual(before + 1, _fake.WrittenLines.Count);
            Assert.AreEqual("hook_1", LastWritten()["response"]["request_id"].AsString());
        }

        [Test]
        public void HookCallback_IsAnswered_EvenWhenTheHandlerThrows()
        {
            Start(true);
            _client.HookCallbackReceived += delegate { throw new InvalidOperationException("boom"); };
            int before = _fake.WrittenLines.Count;

            _fake.ScriptLine(HookLine);
            Assert.DoesNotThrow(PumpAll);

            Assert.AreEqual(before + 1, _fake.WrittenLines.Count,
                "a failing recorder must never leave the CLI waiting on its hook");
            Assert.AreEqual("hook_1", LastWritten()["response"]["request_id"].AsString());
        }

        [Test]
        public void HookCallback_DoesNotCountAsAPermissionPrompt()
        {
            Start(true);

            _fake.ScriptLine(HookLine);
            PumpAll();

            Assert.IsNull(_client.PendingPermissionRequestId);
            Assert.AreEqual(0, _client.QueuedPermissionCount);
        }
    }
}

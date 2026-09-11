using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// system/init `plugins[]` / `plugin_errors[]` mapping (design note
    /// section 1.4). The one-entry case replays the real 2.1.267 capture
    /// (init_plugins.json); the empty case replays the 2.1.218-era
    /// captures that already carried `"plugins":[]`.
    /// </summary>
    [TestFixture]
    public class SystemInitMessagePluginsTests
    {
        private static SystemInitMessage Parse(string json)
        {
            return SystemInitMessage.FromJson(JsonParser.Parse(json), null);
        }

        [Test]
        public void MeasuredCapture_OnePluginEntry_AllFourFields()
        {
            var msg = Parse(FixtureLoader.ReadAllText("init_plugins.json"));
            Assert.IsNotNull(msg);
            Assert.AreEqual("2.1.267", msg.ClaudeCodeVersion);
            Assert.AreEqual(1, msg.Plugins.Length);
            Assert.AreEqual("unity", msg.Plugins[0].Name);
            Assert.AreEqual("unity@unity-agent-plugin", msg.Plugins[0].Source);
            Assert.AreEqual("0.1.2-beta", msg.Plugins[0].Version);
            Assert.AreEqual("/root/.claude/plugins/cache/unity-agent-plugin/unity/0.1.2-beta", msg.Plugins[0].Path);
            Assert.AreEqual(0, msg.PluginErrors.Length, "the key is omitted when there are no errors");
            Assert.Contains("unity:ui", msg.SlashCommands);
            Assert.Contains("Skill", msg.Tools);
        }

        [Test]
        public void EraCaptures_EmptyPluginsArray_ParsesToEmpty()
        {
            var msg = Parse(FixtureLoader.ReadLines("init_flags.json")[0]);
            Assert.IsNotNull(msg);
            Assert.AreEqual(0, msg.Plugins.Length);
            Assert.AreEqual(0, msg.PluginErrors.Length);
        }

        [Test]
        public void MissingKeys_NeverNull()
        {
            var msg = Parse("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}");
            Assert.IsNotNull(msg.Plugins);
            Assert.IsNotNull(msg.PluginErrors);
            Assert.AreEqual(0, msg.Plugins.Length);
            Assert.AreEqual(0, msg.PluginErrors.Length);
        }

        [Test]
        public void PluginErrors_DocumentedShape_Mapped_NonObjectsSkipped()
        {
            var msg = Parse("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\","
                + "\"plugins\":[\"not-an-object\",{\"name\":\"x\"}],"
                + "\"plugin_errors\":[{\"plugin\":\"unity\",\"type\":\"invalid_manifest\",\"message\":\"bad\"},7]}");
            Assert.AreEqual(1, msg.Plugins.Length);
            Assert.AreEqual("x", msg.Plugins[0].Name);
            Assert.IsNull(msg.Plugins[0].Source);
            Assert.AreEqual(1, msg.PluginErrors.Length);
            Assert.AreEqual("unity", msg.PluginErrors[0].Plugin);
            Assert.AreEqual("invalid_manifest", msg.PluginErrors[0].ErrorType);
            Assert.AreEqual("bad", msg.PluginErrors[0].Message);
        }
    }
}

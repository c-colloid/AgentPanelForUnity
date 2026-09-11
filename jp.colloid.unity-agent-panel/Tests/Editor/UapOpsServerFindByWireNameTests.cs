using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UapOpsServer.FindByWireName tests (design section 8.2 B2): resolves
    /// a CLI wire tool_use/can_use_tool name back to the registered
    /// IUapTool, the lookup the permission card's "not undoable" badge and
    /// the turn-level non-undoable warning both depend on. Pure Registry lookup
    /// -- never starts the HTTP listener (UapOpsServer.Registry is lazily
    /// built without EnsureStarted).
    /// </summary>
    [TestFixture]
    public class UapOpsServerFindByWireNameTests
    {
        [TearDown]
        public void TearDown()
        {
            UapOpsServer.ResetForTests();
        }

        [Test]
        public void FindByWireName_KnownNonUndoableTool_ResolvesIt()
        {
            IUapTool tool = UapOpsServer.FindByWireName(
                "mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create");
            Assert.IsNotNull(tool);
            Assert.AreEqual("uap_asset_create", tool.Name);
            Assert.IsFalse(tool.Undoable);
        }

        [Test]
        public void FindByWireName_KnownUndoableTool_ResolvesIt()
        {
            IUapTool tool = UapOpsServer.FindByWireName(
                "mcp__" + UapOpsMcpConfig.ServerName + "__uap_scene_create_object");
            Assert.IsNotNull(tool);
            Assert.IsTrue(tool.Undoable);
        }

        [Test]
        public void FindByWireName_NonUapOpsToolName_ReturnsNull()
        {
            Assert.IsNull(UapOpsServer.FindByWireName("Write"));
            Assert.IsNull(UapOpsServer.FindByWireName("Bash"));
        }

        [Test]
        public void FindByWireName_UnknownToolUnderOurPrefix_ReturnsNull()
        {
            Assert.IsNull(UapOpsServer.FindByWireName(
                "mcp__" + UapOpsMcpConfig.ServerName + "__uap_does_not_exist"));
        }

        [Test]
        public void FindByWireName_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(UapOpsServer.FindByWireName(null));
            Assert.IsNull(UapOpsServer.FindByWireName(string.Empty));
        }
    }
}

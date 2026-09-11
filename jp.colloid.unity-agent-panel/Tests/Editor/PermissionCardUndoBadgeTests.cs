using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design section 8.2 B2(b): the permission card must show a
    /// "not undoable" badge for a UapOps tool whose IUapTool.Undoable is
    /// false, and hide it for every other tool. Constructs a real
    /// PermissionCard (no attached panel/window needed -- BuildSkeleton
    /// only builds a VisualElement tree) and inspects the inline style the
    /// production code sets, the same way BuildToolVariant itself reads it
    /// back on the next Refresh.
    /// </summary>
    [TestFixture]
    public class PermissionCardUndoBadgeTests
    {
        [TearDown]
        public void TearDown()
        {
            UapOpsServer.ResetForTests();
        }

        private static ControlRequestMessage BuildCanUseToolRequest(string requestId, string toolName)
        {
            string json = "{\"request_id\":\"" + requestId + "\",\"request\":{\"subtype\":\"can_use_tool\""
                + ",\"tool_name\":\"" + toolName + "\",\"input\":{}}}";
            JsonNode node = JsonParser.Parse(json);
            return ControlRequestMessage.FromJson(node, null);
        }

        private static Label FindUndoBadge(PermissionCard card)
        {
            return card.Root.Q<Label>(className: "uap-perm-undo-badge");
        }

        [Test]
        public void Refresh_NonUndoableUapOpsTool_ShowsUndoBadge()
        {
            var card = new PermissionCard();
            card.Refresh(BuildCanUseToolRequest("r1", "mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create"));

            Label badge = FindUndoBadge(card);
            Assert.IsNotNull(badge);
            Assert.AreEqual(DisplayStyle.Flex, badge.style.display.value);
        }

        [Test]
        public void UndoBadge_CarriesTheExplanatoryTooltip()
        {
            // UXA-5: the badge alone says "not undoable" but nothing about
            // the consequence. The tooltip is set once in BuildSkeleton, so
            // it must exist regardless of the badge's display state.
            var card = new PermissionCard();
            card.Refresh(BuildCanUseToolRequest("r1t", "mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create"));

            Assert.AreEqual(L10n.S.PermUndoNotSupportedTooltip, FindUndoBadge(card).tooltip);
        }

        [Test]
        public void Refresh_UndoableUapOpsTool_HidesUndoBadge()
        {
            var card = new PermissionCard();
            card.Refresh(BuildCanUseToolRequest("r2", "mcp__" + UapOpsMcpConfig.ServerName + "__uap_scene_create_object"));

            Assert.AreEqual(DisplayStyle.None, FindUndoBadge(card).style.display.value);
        }

        [Test]
        public void Refresh_NonUapOpsTool_HidesUndoBadge()
        {
            var card = new PermissionCard();
            card.Refresh(BuildCanUseToolRequest("r3", "Write"));

            Assert.AreEqual(DisplayStyle.None, FindUndoBadge(card).style.display.value);
        }

        [Test]
        public void Refresh_SwitchingFromNonUndoableToUndoableRequest_HidesTheBadgeAgain()
        {
            var card = new PermissionCard();
            card.Refresh(BuildCanUseToolRequest("r4", "mcp__" + UapOpsMcpConfig.ServerName + "__uap_asset_create"));
            Assert.AreEqual(DisplayStyle.Flex, FindUndoBadge(card).style.display.value);

            card.Refresh(BuildCanUseToolRequest("r5", "mcp__" + UapOpsMcpConfig.ServerName + "__uap_scene_create_object"));
            Assert.AreEqual(DisplayStyle.None, FindUndoBadge(card).style.display.value);
        }
    }
}

using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Adversarial-review regression (docs/design-notes/2026-09-28-uloop-
    /// phase3.md): the permission card's dedicated uap_execute_code detail
    /// (the full snippet in a monospace block) must render the real code
    /// EVEN WHEN the call is reached through uap_call, not just on a direct
    /// call. Before this test's fix, PermissionCard resolved the effective
    /// tool via UapOpsServer.ResolveEffectiveTool (correctly unwrapping
    /// uap_call for the undo badge) but then built the code detail from
    /// the OUTER {"name":...,"arguments":{...}} envelope instead of the
    /// unwrapped inner arguments -- so a uap_call-wrapped uap_execute_code
    /// card silently fell back to showing only "action: run" and hid the
    /// snippet, the usings and every risk flag from the approver. This is
    /// exactly the "card that does not show the actual code" failure mode
    /// a security review of this tool has to rule out.
    /// </summary>
    [TestFixture]
    public class PermissionCardExecuteCodeDetailTests
    {
        [TearDown]
        public void TearDown()
        {
            UapOpsServer.ResetForTests();
        }

        private static Label FindCodeLabel(PermissionCard card)
        {
            return card.Root.Q<Label>(className: "uap-perm-preview");
        }

        [Test]
        public void Refresh_DirectExecuteCodeCall_ShowsTheFullCodeInTheDetail()
        {
            string toolName = "mcp__" + UapOpsMcpConfig.ServerName + "__" + UapExecuteCodeTool.ToolName;
            string json = "{\"request_id\":\"d1\",\"request\":{\"subtype\":\"can_use_tool\""
                + ",\"tool_name\":\"" + toolName + "\""
                + ",\"input\":{\"action\":\"run\",\"code\":\"int marker__direct = 1; return marker__direct;\"}}}";
            var request = ControlRequestMessage.FromJson(JsonParser.Parse(json), null);

            var card = new PermissionCard();
            card.Refresh(request);

            Label codeLabel = FindCodeLabel(card);
            Assert.IsNotNull(codeLabel, "a direct uap_execute_code call must render the code detail label.");
            StringAssert.Contains("marker__direct", codeLabel.text);
        }

        [Test]
        public void Refresh_ExecuteCodeReachedThroughUapCall_StillShowsTheFullCodeInTheDetail()
        {
            // The wrapping uap_call itself is never Pro-gated/renamed here --
            // only its OWN wire name needs the mcp__ prefix; the inner
            // "name"/"arguments" are the wire protocol's own shape
            // (UapToolGateway.TryUnwrapCall), not an MCP-prefixed name.
            string outerToolName = "mcp__" + UapOpsMcpConfig.ServerName + "__" + UapToolGateway.CallToolName;
            string json = "{\"request_id\":\"d2\",\"request\":{\"subtype\":\"can_use_tool\""
                + ",\"tool_name\":\"" + outerToolName + "\""
                + ",\"input\":{\"name\":\"" + UapExecuteCodeTool.ToolName + "\""
                + ",\"arguments\":{\"action\":\"run\",\"code\":\"int marker__viaCall = 2; return marker__viaCall;\"}}}}";
            var request = ControlRequestMessage.FromJson(JsonParser.Parse(json), null);

            var card = new PermissionCard();
            card.Refresh(request);

            Label codeLabel = FindCodeLabel(card);
            Assert.IsNotNull(codeLabel,
                "uap_execute_code reached through uap_call must still render the code detail label, "
                + "not silently fall back to the codeless 'action: run' branch.");
            StringAssert.Contains("marker__viaCall", codeLabel.text);
        }
    }
}

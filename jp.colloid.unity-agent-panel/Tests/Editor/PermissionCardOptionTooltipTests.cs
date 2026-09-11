using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// AskUserQuestion option.Description is model-controlled and used to
    /// assign straight to optionButton.tooltip, bypassing
    /// IconLoader.SanitizeForDisplay, while the sibling Label built from
    /// the identical string one line later went through PlainLabel (which
    /// does sanitize) -- an inconsistent bypass of the sanitize chokepoint
    /// installed everywhere else in PermissionCard.cs.
    /// </summary>
    [TestFixture]
    public class PermissionCardOptionTooltipTests
    {
        private static ControlRequestMessage BuildAskUserQuestionRequest(string description)
        {
            string json = "{\"request_id\":\"tt1\",\"request\":{"
                + "\"subtype\":\"can_use_tool\","
                + "\"tool_name\":\"AskUserQuestion\","
                + "\"requires_user_interaction\":true,"
                + "\"input\":{\"questions\":[{"
                + "\"question\":\"Pick one\",\"header\":\"H\",\"multiSelect\":false,"
                + "\"options\":[{\"label\":\"A\",\"description\":\"" + description + "\"}]"
                + "}]}}}";
            JsonNode node = JsonParser.Parse(json);
            return ControlRequestMessage.FromJson(node, null);
        }

        [Test]
        public void OptionDescription_WithUnsafeEmoji_TooltipIsSanitized()
        {
            string grinningFace = char.ConvertFromUtf32(0x1F600); // uncurated pictograph
            string rawDescription = "careful " + grinningFace + " read only";

            var card = new PermissionCard();
            card.Refresh(BuildAskUserQuestionRequest(rawDescription));

            Button optionButton = card.Root.Q<Button>(className: "uap-perm-option");
            Assert.IsNotNull(optionButton, "AskUserQuestion option button must render");
            Assert.AreEqual(IconLoader.SanitizeForDisplay(rawDescription), optionButton.tooltip);
            Assert.AreNotEqual(rawDescription, optionButton.tooltip,
                "the emoji must not survive verbatim in the tooltip");
        }

        [Test]
        public void OptionDescription_TooltipAndLabel_StaySanitizedTheSame()
        {
            // Same untrusted string renders through two paths (tooltip and
            // the sibling Label) -- both must agree on the sanitized text.
            string sparkles = char.ConvertFromUtf32(0x2728); // curated -> HEAVY ASTERISK
            string rawDescription = "shiny " + sparkles + " option";

            var card = new PermissionCard();
            card.Refresh(BuildAskUserQuestionRequest(rawDescription));

            Button optionButton = card.Root.Q<Button>(className: "uap-perm-option");
            Label descLabel = optionButton.Q<Label>(className: "uap-perm-option-desc");
            Assert.IsNotNull(descLabel);
            Assert.AreEqual(descLabel.text, optionButton.tooltip);
        }
    }
}

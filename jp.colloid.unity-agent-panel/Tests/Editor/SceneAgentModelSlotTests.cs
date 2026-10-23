using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    public class SceneAgentModelSlotTests
    {
        [TearDown]
        public void TearDown()
        {
            SceneAgentToolsExtensions.RegisterModelElement(null);
        }

        [Test]
        public void Slot_WithoutARegisteredElement_ShowsTheDimProWord()
        {
            SceneAgentToolsExtensions.RegisterModelElement(null);
            var slot = new SceneAgentModelSlot();
            Assert.AreEqual(1, slot.childCount);
            var badge = slot[0] as Label;
            Assert.IsNotNull(badge);
            Assert.AreEqual("Pro", badge.text);
            Assert.IsTrue(badge.ClassListContains(SceneAgentModelSlot.BadgeClass));
            Assert.IsNotEmpty(badge.tooltip, "the tooltip says what the slot would hold");
        }

        [Test]
        public void Slot_WithARegisteredElement_HoldsIt_AndRebuildsOnChange()
        {
            var slot = new SceneAgentModelSlot();
            var mine = new Label("model tools");
            SceneAgentToolsExtensions.RegisterModelElement(() => mine);
            slot.Build();
            Assert.AreEqual(1, slot.childCount);
            Assert.AreSame(mine, slot[0]);
            SceneAgentToolsExtensions.RegisterModelElement(null);
            slot.Build();
            Assert.AreEqual("Pro", ((Label)slot[0]).text, "clearing the factory puts the word back");
        }
    }
}

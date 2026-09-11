using NUnit.Framework;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Narrow-width mode (docs/design-notes/2026-09-05-ui-redesign.md,
    /// D5): AgentPanelWindow toggles `uap-narrow` on its root from its own
    /// measured width so AgentPanel.uss can fold secondary chrome below
    /// 340px. The decision is a pure function; these pin its boundary and
    /// its "no measurement yet" behaviour, plus the class application on
    /// a detached element (no window needed).
    /// </summary>
    [TestFixture]
    public class AgentPanelWindowWidthClassTests
    {
        private const float T = UI.AgentPanelWindow.NarrowWidthThresholdPx;

        [Test]
        public void Threshold_IsTheSpecsNarrowLine()
        {
            Assert.AreEqual(340f, T);
        }

        [TestCase(339f, true)]
        [TestCase(340f, false)]
        [TestCase(341f, false)]
        [TestCase(300f, true)]
        [TestCase(1200f, false)]
        public void ResolveIsNarrow_Boundary(float width, bool expected)
        {
            Assert.AreEqual(expected, UI.AgentPanelWindow.ResolveIsNarrow(width, T));
        }

        [Test]
        public void ResolveIsNarrow_NoRealMeasurement_IsNotNarrow()
        {
            // The first GeometryChangedEvent after docking can report 0,
            // and resolvedStyle.width is NaN before the first layout; a
            // phantom measurement must never fold the chrome.
            Assert.IsFalse(UI.AgentPanelWindow.ResolveIsNarrow(0f, T));
            Assert.IsFalse(UI.AgentPanelWindow.ResolveIsNarrow(-1f, T));
            Assert.IsFalse(UI.AgentPanelWindow.ResolveIsNarrow(float.NaN, T));
        }

        [Test]
        public void ApplyWidthClass_TogglesTheRootClassBothWays()
        {
            var root = new VisualElement();

            UI.AgentPanelWindow.ApplyWidthClass(root, 300f);
            Assert.IsTrue(root.ClassListContains(UI.AgentPanelWindow.NarrowClassName));

            UI.AgentPanelWindow.ApplyWidthClass(root, 600f);
            Assert.IsFalse(root.ClassListContains(UI.AgentPanelWindow.NarrowClassName),
                "widening back past the threshold must remove the class, not leave it sticky");
        }
    }
}

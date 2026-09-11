using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Phase 5a stream B default-value guards on a fresh PanelSettings instance.</summary>
    [TestFixture]
    public class Phase5aPanelSettingsDefaultsTests
    {
        [Test]
        public void UapScriptGateEnabled_DefaultsToTrue()
        {
            Assert.IsTrue(new PanelSettings().uapScriptGateEnabled);
        }

        [Test]
        public void UapOpsModules_DefaultsToCorePrefabEditor()
        {
            // Phase 5b stream A: "prefab"/"editor" join "core" as default-ON
            // modules (design-notes kickoff section A1/A2).
            // 2026-09-07: "markers" (Scene-view 3D markers) joined as generation 2.
            CollectionAssert.AreEqual(new List<string> { "core", "prefab", "editor", "markers" }, new PanelSettings().uapOpsModules);
        }

        [Test]
        public void UapOpsEnabled_DefaultsToTrue()
        {
            Assert.IsTrue(new PanelSettings().uapOpsEnabled);
        }
    }
}

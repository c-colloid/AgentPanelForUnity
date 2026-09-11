using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards the Settings permission-mode dropdown &lt;-&gt; wire-string
    /// translation (ARCHITECTURE.md D3: --permission-mode /
    /// set_permission_mode take "default"/"plan"/"acceptEdits").
    /// </summary>
    public class PermissionModeMappingTests
    {
        [Test]
        public void ToCliValue_MapsEveryOption()
        {
            Assert.AreEqual("default", PermissionModeMapping.ToCliValue(PermissionModeOption.Default));
            Assert.AreEqual("plan", PermissionModeMapping.ToCliValue(PermissionModeOption.Plan));
            Assert.AreEqual("acceptEdits", PermissionModeMapping.ToCliValue(PermissionModeOption.AcceptEdits));
        }

        [Test]
        public void FromCliValue_MapsKnownValues_CaseInsensitively()
        {
            Assert.AreEqual(PermissionModeOption.Plan, PermissionModeMapping.FromCliValue("plan"));
            Assert.AreEqual(PermissionModeOption.Plan, PermissionModeMapping.FromCliValue("PLAN"));
            Assert.AreEqual(PermissionModeOption.AcceptEdits,
                PermissionModeMapping.FromCliValue("acceptEdits"));
            Assert.AreEqual(PermissionModeOption.AcceptEdits,
                PermissionModeMapping.FromCliValue("ACCEPTEDITS"));
            Assert.AreEqual(PermissionModeOption.Default, PermissionModeMapping.FromCliValue("default"));
        }

        [Test]
        public void FromCliValue_UnknownOrEmpty_FallsBackToDefault_WithoutThrowing()
        {
            Assert.AreEqual(PermissionModeOption.Default, PermissionModeMapping.FromCliValue(null));
            Assert.AreEqual(PermissionModeOption.Default, PermissionModeMapping.FromCliValue(string.Empty));
            Assert.AreEqual(PermissionModeOption.Default,
                PermissionModeMapping.FromCliValue("some-future-cli-mode"));
        }

        [Test]
        public void RoundTrip_EveryOption()
        {
            foreach (PermissionModeOption option in new[]
            {
                PermissionModeOption.Default, PermissionModeOption.Plan, PermissionModeOption.AcceptEdits
            })
            {
                string wire = PermissionModeMapping.ToCliValue(option);
                Assert.AreEqual(option, PermissionModeMapping.FromCliValue(wire));
            }
        }

    }
}

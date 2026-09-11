using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UXIA-7: the permission-mode dropdown labels, resolved in the UI
    /// layer from the L10n catalog (they were English literals in
    /// Model-layer PermissionModeMapping before -- unlocalizable without a
    /// D9 violation). Same test shape as AutoApproveLevelLabelsTests.
    /// </summary>
    [TestFixture]
    public class PermissionModeLabelsTests
    {
        [Test]
        public void Describe_EachOption_ReturnsItsOwnCatalogString()
        {
            Assert.AreEqual(L10n.S.SettingsPermissionModeOptionDefault,
                PermissionModeLabels.Describe(PermissionModeOption.Default));
            Assert.AreEqual(L10n.S.SettingsPermissionModeOptionPlan,
                PermissionModeLabels.Describe(PermissionModeOption.Plan));
            Assert.AreEqual(L10n.S.SettingsPermissionModeOptionAcceptEdits,
                PermissionModeLabels.Describe(PermissionModeOption.AcceptEdits));
        }

        [Test]
        public void Describe_UnknownValue_FallsBackToTheDefaultLabel()
        {
            // A settings asset from a future build: the safest reading is
            // the most-asking one, never an empty string.
            Assert.AreEqual(L10n.S.SettingsPermissionModeOptionDefault,
                PermissionModeLabels.Describe((PermissionModeOption)999));
        }

        [Test]
        public void Describe_NoTwoOptions_ShareALabel()
        {
            string d = PermissionModeLabels.Describe(PermissionModeOption.Default);
            string p = PermissionModeLabels.Describe(PermissionModeOption.Plan);
            string a = PermissionModeLabels.Describe(PermissionModeOption.AcceptEdits);
            Assert.AreNotEqual(d, p);
            Assert.AreNotEqual(d, a);
            Assert.AreNotEqual(p, a);
        }
    }
}

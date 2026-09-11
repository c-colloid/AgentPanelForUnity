using Colloid.AgentPanel.UI;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// UXA-7: a permission request arriving while the user is editing in
    /// Settings/History must NOT yank the whole panel to Chat -- the
    /// view-independent floating window carries the request instead. The
    /// policy is pure; AgentPanelWindow.RefreshIfDirty just obeys it.
    /// </summary>
    [TestFixture]
    public class PermissionArrivalPolicyTests
    {
        [TestCase(PanelViewKind.Settings)]
        [TestCase(PanelViewKind.History)]
        public void NonChatView_OpensTheFloatingWindow_NeverSwitchesViews(PanelViewKind view)
        {
            Assert.AreEqual(PermissionArrivalPolicy.Arrival.OpenFloatingWindow,
                PermissionArrivalPolicy.Decide(view, false));
        }

        [Test]
        public void ChatActive_BadgeOnly_ChatViewsOwnHostingDecides()
        {
            Assert.AreEqual(PermissionArrivalPolicy.Arrival.BadgeOnly,
                PermissionArrivalPolicy.Decide(PanelViewKind.Chat, false));
        }

        [Test]
        public void WindowAlreadyOpen_BadgeOnly_TheRequestIsAlreadyReachable()
        {
            Assert.AreEqual(PermissionArrivalPolicy.Arrival.BadgeOnly,
                PermissionArrivalPolicy.Decide(PanelViewKind.Settings, true));
            Assert.AreEqual(PermissionArrivalPolicy.Arrival.BadgeOnly,
                PermissionArrivalPolicy.Decide(PanelViewKind.Chat, true));
        }
    }
}

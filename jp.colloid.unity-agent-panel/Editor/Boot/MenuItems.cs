using Colloid.AgentPanel.UI;
using UnityEditor;

namespace Colloid.AgentPanel.Boot
{
    /// <summary>Menu entry points (ARCHITECTURE.md module tree, Boot/).</summary>
    public static class MenuItems
    {
        [MenuItem("Window/Agent Panel")]
        public static void OpenAgentPanel()
        {
            AgentPanelWindow.Open();
        }
    }
}

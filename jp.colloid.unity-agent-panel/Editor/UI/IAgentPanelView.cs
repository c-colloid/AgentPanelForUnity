using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// The views AgentPanelWindow's ViewContainer can show (R05 section
    /// 6.1). Phase 3 wires Chat and Settings; History is a stub
    /// VisualElement this stage (a later stage fills it in behind the same
    /// switching mechanics, so no AgentPanelWindow change is needed when
    /// that happens).
    /// </summary>
    public enum PanelViewKind
    {
        Chat,
        History,
        Settings
    }

    /// <summary>
    /// Contract for a view hosted inside the panel's ViewContainer region
    /// (R05 section 6.1 view-registry seam). Phase 1-2 shipped only
    /// ChatView; Phase 3 adds SettingsView and HistoryView behind the same
    /// interface -- AgentPanelWindow builds every registered view's root
    /// once and keeps it alive for the window's lifetime, toggling display
    /// and OnActivate/OnDeactivate on switch (see
    /// docs/design-notes/2026-07-31-view-container-switching.md for why:
    /// switching away from Chat must never restart the CLI client or
    /// rebuild the transcript DOM).
    /// </summary>
    public interface IAgentPanelView
    {
        /// <summary>Short display name (future tab label).</summary>
        string Title { get; }

        /// <summary>Builds and returns the view's root element (called once).</summary>
        VisualElement CreateGUI();

        /// <summary>Called when the view becomes visible (subscribe events here).</summary>
        void OnActivate();

        /// <summary>Called when the view is hidden or the window closes (unsubscribe).</summary>
        void OnDeactivate();

        /// <summary>Persist transient UI state (scroll offset, drafts) to SessionState.</summary>
        void SerializeState();
    }
}

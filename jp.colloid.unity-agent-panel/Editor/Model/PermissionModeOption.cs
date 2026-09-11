namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// The three --permission-mode values the Settings UI exposes
    /// (ARCHITECTURE.md D3 / R05 section 2.5's "[Manual/Plan/Auto-edit]"
    /// indicator). PanelSettings.permissionMode stores the raw CLI string
    /// (so an unrecognized value from a future CLI version round-trips
    /// through the asset unharmed); this enum plus PermissionModeMapping is
    /// only the UI-facing dropdown &lt;-&gt; wire-string translation layer,
    /// kept as a pure, Unity-API-free mapping so it is directly EditMode
    /// testable without building a VisualElement.
    /// </summary>
    public enum PermissionModeOption
    {
        Default,
        Plan,
        AcceptEdits
    }
}

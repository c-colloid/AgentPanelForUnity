namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Platform-specific resolution of the claude CLI executable path
    /// (ARCHITECTURE.md D1). Implementations are pure C# (no Unity APIs);
    /// the user-configured manual path is injected by the caller instead of
    /// being read from EditorPrefs here.
    /// </summary>
    public interface ICliPathProbe
    {
        /// <summary>
        /// Returns the full path of the CLI executable, or null when no
        /// candidate exists. Never throws.
        /// </summary>
        string Resolve();

        /// <summary>
        /// The ordered candidate locations this probe checks (for the
        /// first-run diagnostics UI). Paths are returned whether or not
        /// they exist.
        /// </summary>
        string[] DescribeCandidates();
    }
}

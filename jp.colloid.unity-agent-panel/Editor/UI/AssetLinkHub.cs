namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Fixed rendering/context seam (shared contract -- do not extend):
    /// the markdown renderer raises LinkClicked with the raw
    /// project-relative path ("Assets/..." / "Packages/...") and consults
    /// PathExists (assigned by the Integration side) before rendering a
    /// path as a link. Null-safe: while PathExists is unassigned no path
    /// is ever linkified.
    /// </summary>
    public static class AssetLinkHub
    {
        /// <summary>Raised by the renderer with the raw project-relative path.</summary>
        public static event System.Action<string> LinkClicked;

        /// <summary>Set by the context side; the renderer treats null as "false".</summary>
        public static System.Func<string, bool> PathExists;

        public static void RaiseLinkClicked(string path)
        {
            System.Action<string> handler = LinkClicked;
            if (handler != null && !string.IsNullOrEmpty(path))
            {
                handler(path);
            }
        }
    }
}

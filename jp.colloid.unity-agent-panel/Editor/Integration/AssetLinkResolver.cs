using UnityEditor;
using UnityEngine;
using Colloid.AgentPanel.UI;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Context-side implementation of the AssetLinkHub contract
    /// (R05 section 4.3): supplies the existence check the renderer runs
    /// before linkifying an Assets/Packages path, and handles clicks --
    /// single click pings the asset in the Project window, a second click
    /// on the same path within the double-click window opens it
    /// (AssetDatabase.OpenAsset, honoring a trailing #L42 line marker).
    /// Non-existent paths are never linkified (hallucination guard: the
    /// presence of a link is itself verification). Main thread only.
    /// </summary>
    public static class AssetLinkResolver
    {
        /// <summary>Two clicks on the same path within this window = open.</summary>
        private const double DoubleClickSeconds = 0.4;

        private static string _lastClickedPath;
        private static double _lastClickTime;

        [InitializeOnLoadMethod]
        private static void Install()
        {
            // Statics reset on domain reload, so a fresh domain starts
            // unsubscribed; the -=/+= pair keeps a repeated Install within
            // one domain from double-firing PingObject per click.
            AssetLinkHub.PathExists = IsProjectAssetPath;
            AssetLinkHub.LinkClicked -= OnLinkClicked;
            AssetLinkHub.LinkClicked += OnLinkClicked;
        }

        /// <summary>
        /// True when the raw path (optionally carrying a #L42 suffix)
        /// resolves to a real asset under Assets/ or Packages/.
        /// </summary>
        internal static bool IsProjectAssetPath(string rawPath)
        {
            string path;
            int line;
            if (!TryNormalize(rawPath, out path, out line))
            {
                return false;
            }
            return AssetDatabase.GetMainAssetTypeAtPath(path) != null;
        }

        private static void OnLinkClicked(string rawPath)
        {
            string path;
            int line;
            if (!TryNormalize(rawPath, out path, out line))
            {
                return;
            }
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            bool secondClick = path == _lastClickedPath
                && now - _lastClickTime <= DoubleClickSeconds;
            _lastClickedPath = path;
            _lastClickTime = now;

            if (secondClick)
            {
                // Reset so a triple click does not open twice.
                _lastClickedPath = null;
                if (line > 0)
                {
                    AssetDatabase.OpenAsset(asset, line);
                }
                else
                {
                    AssetDatabase.OpenAsset(asset);
                }
            }
            else
            {
                EditorGUIUtility.PingObject(asset);
            }
        }

        /// <summary>
        /// Normalizes a raw link path: trims, converts backslashes,
        /// extracts a trailing "#L42" line marker, and requires an
        /// Assets/ or Packages/ prefix. Pure string work (testable).
        /// </summary>
        internal static bool TryNormalize(string rawPath, out string path, out int line)
        {
            path = null;
            line = -1;
            if (string.IsNullOrEmpty(rawPath))
            {
                return false;
            }
            string candidate = rawPath.Trim().Replace('\\', '/');

            int marker = candidate.LastIndexOf("#L", System.StringComparison.Ordinal);
            if (marker > 0)
            {
                int parsed;
                if (int.TryParse(candidate.Substring(marker + 2),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed)
                    && parsed > 0)
                {
                    line = parsed;
                    candidate = candidate.Substring(0, marker);
                }
            }

            if (!candidate.StartsWith("Assets/", System.StringComparison.Ordinal)
                && !candidate.StartsWith("Packages/", System.StringComparison.Ordinal))
            {
                return false;
            }
            path = candidate;
            return true;
        }
    }
}

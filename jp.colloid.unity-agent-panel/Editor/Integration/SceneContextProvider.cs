using System.Text;
using Colloid.AgentPanel.UI;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Summarizes the active scene for a context chip (R05 section 4.1):
    /// scene name/path plus root object names, capped so a huge scene
    /// cannot bloat the outgoing message. Work happens only on demand
    /// (chip attach / message send) -- GetRootGameObjects allocates, so
    /// this must never run on a per-frame tick. Main thread only.
    /// </summary>
    public static class SceneContextProvider
    {
        private const int MaxRootNames = 40;

        /// <summary>Active scene name for the chip label ("(untitled)" fallback).</summary>
        public static string ActiveSceneName
        {
            get
            {
                Scene scene = SceneManager.GetActiveScene();
                return string.IsNullOrEmpty(scene.name) ? L10n.S.CtxSceneUntitledFallback : scene.name;
            }
        }

        /// <summary>
        /// Context block for the active scene, or null when no valid
        /// scene is loaded. Root names beyond the cap collapse into a
        /// count line; the whole block obeys the 2 KB cap.
        /// </summary>
        public static string BuildSummary()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            var sb = new StringBuilder(256);
            sb.Append(L10n.F(L10n.S.CtxSceneSummaryHeaderFmt,
                string.IsNullOrEmpty(scene.name) ? L10n.S.CtxSceneUntitledFallback : scene.name));
            if (!string.IsNullOrEmpty(scene.path))
            {
                sb.Append(L10n.F(L10n.S.CtxSceneSummaryPathFmt, scene.path));
            }
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxSceneLoadedLabelFmt,
                scene.isLoaded ? L10n.S.CtxSelectionYes : L10n.S.CtxSelectionNo));
            sb.Append("\n  ").Append(L10n.F(L10n.S.CtxSceneRootObjectsLabelFmt, scene.rootCount));

            if (scene.isLoaded && scene.rootCount > 0)
            {
                UnityEngine.GameObject[] roots = scene.GetRootGameObjects();
                int listed = 0;
                for (int i = 0; i < roots.Length && listed < MaxRootNames; i++)
                {
                    if (roots[i] == null)
                    {
                        continue;
                    }
                    sb.Append("\n    - ").Append(roots[i].name);
                    if (!roots[i].activeSelf)
                    {
                        sb.Append(L10n.S.CtxSceneRootInactiveSuffix);
                    }
                    listed++;
                }
                if (roots.Length > listed)
                {
                    sb.Append("\n    ").Append(
                        L10n.F(L10n.S.CtxSceneMoreRootsFmt, roots.Length - listed));
                }
            }
            return ContextBlockFormatter.TruncateBlock(
                sb.ToString(), ContextBlockFormatter.MaxBlockChars);
        }
    }
}

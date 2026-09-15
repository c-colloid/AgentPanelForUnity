using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The prefab-stage guard every write tool runs before mutating
    /// anything (design section 1.2/8.2 B2): when a prefab stage is open
    /// and the resolved target is NOT inside it, fail with a structured
    /// error instead of silently touching the wrong virtual scene (R09
    /// section 9.2's documented "aktive scene" trap).
    /// </summary>
    public static class UapPrefabStageGuard
    {
        /// <summary>
        /// True when no prefab stage is open, or the given scene IS the
        /// open stage's scene. False (with an explanatory
        /// <paramref name="error"/>) when a stage is open and the caller
        /// asked for a DIFFERENT scene.
        /// </summary>
        public static bool CheckScene(Scene scene, out string error)
        {
            error = null;
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return true;
            }
            if (scene == stage.scene)
            {
                return true;
            }
            error = "A prefab stage ('" + stage.assetPath + "') is currently open, and the requested"
                + " scene is not the prefab stage's scene. Omit 'scene' to target the open prefab, or"
                + " close prefab mode first -- Agent Panel Pro's uap_prefab_stage op:\"close\" does that"
                + " without leaving the panel.";
            return false;
        }

        /// <summary>
        /// Same check for a resolved GameObject target: true when no stage
        /// is open, the target is null (nothing scene-scoped to guard), or
        /// the target lives inside the open stage's scene.
        /// </summary>
        public static bool Check(GameObject target, out string error)
        {
            error = null;
            if (target == null)
            {
                return true;
            }
            return CheckScene(target.scene, out error);
        }
    }
}

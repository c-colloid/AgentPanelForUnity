using System.IO;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Shared "create the Assets/ folder chain if missing" helper (R09
    /// section 4.1: no single-call AssetDatabase "mkdir -p"). UapAssetCreateTool
    /// and UapPrefabCreateTool each carry their own private copy from Phase 5a/
    /// stream A -- left untouched here to avoid an unrelated diff. Phase 5b
    /// stream B's anim-module tools (uap_anim_create_clip, uap_animator_edit)
    /// are the third/fourth need for this exact logic, so it is extracted
    /// once here instead of adding yet more copies.
    /// </summary>
    internal static class UapAssetFolderHelper
    {
        public static void EnsureFolderExists(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }
            string parent = Path.GetDirectoryName(folder);
            if (parent != null)
            {
                parent = parent.Replace('\\', '/');
            }
            string leaf = Path.GetFileName(folder);
            EnsureFolderExists(parent);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            {
                AssetDatabase.CreateFolder(parent, leaf);
            }
        }
    }
}

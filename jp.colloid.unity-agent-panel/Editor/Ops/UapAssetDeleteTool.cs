using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Deletes an asset by moving it to the OS trash (design section 8.2 B2:
    /// the honest, RESTORABLE counterpart to uap_asset_create's
    /// non-undoability -- restorable via the trash, not via Ctrl+Z, so
    /// Undoable stays false here too).
    ///
    /// Destructive (design note 2026-09-09-jobs-and-destructive-confirm
    /// section 2): nothing in the Editor takes a trashed asset back, and a
    /// folder path takes everything under it, so the call refuses without
    /// confirm:true and reports what it would trash (asset type, or the
    /// number of assets inside a folder); dry_run:true asks for that
    /// report alone.
    /// </summary>
    public sealed class UapAssetDeleteTool : UapDestructiveToolBase
    {
        public override string Name
        {
            get { return "uap_asset_delete"; }
        }

        public override string Description
        {
            get
            {
                return "Deletes an asset by moving it to the OS trash (restorable from there,"
                    + " but NOT undoable with Ctrl+Z). Destructive: refuses without confirm:true"
                    + " and reports what would be trashed (a folder path takes every asset inside);"
                    + " dry_run:true previews only.";
            }
        }

        public override string Module
        {
            get { return "core"; }
        }

        public override bool Undoable
        {
            get { return false; }
        }

        protected override JsonNode BuildInputSchema()
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("properties", JsonNode.NewObject()
                    .Set("path", JsonNode.NewObject().Set("type", "string")
                        .Set("description", "Project path of the asset to delete, e.g. 'Assets/Materials/Foo.mat'.")))
                .Set("required", JsonNode.NewArray().Add("path"))
                .Set("additionalProperties", false);
        }

        public override string Preview(JsonNode input)
        {
            string path = ResolvePath(input);
            if (AssetDatabase.IsValidFolder(path))
            {
                int inside = AssetDatabase.FindAssets(string.Empty, new[] { path }).Length;
                return "Would move the FOLDER '" + path + "' and the " + inside
                    + " asset(s) inside it (subfolders included) to the OS trash.";
            }
            Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            return "Would move the " + (mainType != null ? mainType.Name : "asset") + " '" + path
                + "' to the OS trash. Anything referencing it will lose the reference (missing asset).";
        }

        protected override JsonNode Apply(JsonNode input)
        {
            string path = ResolvePath(input);
            if (!AssetDatabase.MoveAssetToTrash(path))
            {
                throw new InvalidOperationException("Failed to move asset to trash: " + path);
            }
            return UapToolResults.Text("Moved to trash: " + path + ".");
        }

        /// <summary>Validates and normalizes the path exactly as the delete does, so a preview refuses the same inputs.</summary>
        private static string ResolvePath(JsonNode input)
        {
            string path = input["path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            // OPS-L2 (SR-asset-path): deletion gets the same scope guard as
            // creation -- without it, Packages/ or ProjectSettings/ paths
            // (or an "Assets/../..." escape) could be trashed.
            string pathError;
            string normalized = UapAssetPath.NormalizeUnderAssets(path, out pathError);
            if (normalized == null)
            {
                throw new ArgumentException(pathError);
            }
            path = normalized;
            if (!AssetDatabase.IsValidFolder(path))
            {
                string error;
                if (UapAddressing.ResolveAsset(path, out error) == null)
                {
                    throw new InvalidOperationException(error);
                }
            }
            return path;
        }
    }
}

using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Creates a new asset -- a Material, or an instance of any already-
    /// compiled ScriptableObject subclass -- at a project path (design
    /// section 1.2, R09 section 4.1). Deliberately Undoable=false (R09
    /// section 9.1: AssetDatabase.CreateAsset is outside Unity's Undo
    /// system); uap_asset_delete is its honest, restorable (Trash, not
    /// Ctrl+Z) counterpart per design section 8.2 B2.
    /// </summary>
    public sealed class UapAssetCreateTool : IUapTool
    {
        public string Name
        {
            get { return "uap_asset_create"; }
        }

        public string Description
        {
            get
            {
                return "Creates a new asset (a Material, or an instance of any already-compiled"
                    + " ScriptableObject type) at a project path. NOT undoable with Ctrl+Z --"
                    + " use uap_asset_delete (moves to the OS trash, restorable) to remove it.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("assetType", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "\"Material\", or a ScriptableObject type name (short or fully-qualified)."))
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Desired project path under 'Assets/', e.g. 'Assets/Materials/Foo.mat'. Uniquified on collision."))
                        .Set("shader", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Shader name for a Material (default \"Standard\").")))
                    .Set("required", JsonNode.NewArray().Add("assetType").Add("path"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string assetType = input["assetType"].AsString(null);
            string path = input["path"].AsString(null);
            if (string.IsNullOrEmpty(assetType))
            {
                throw new ArgumentException("'assetType' is required.");
            }
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            // OPS-3 (SR-asset-path): dot segments collapse BEFORE the
            // Assets/ check -- a bare StartsWith let "Assets/../Evil.mat"
            // escape the folder.
            string pathError;
            string normalizedPath = UapAssetPath.NormalizeUnderAssets(path, out pathError);
            if (normalizedPath == null)
            {
                throw new ArgumentException(pathError);
            }

            UnityEngine.Object asset;
            if (string.Equals(assetType, "Material", StringComparison.OrdinalIgnoreCase))
            {
                string shaderName = input["shader"].AsString("Standard");
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    throw new InvalidOperationException("Shader not found: " + shaderName);
                }
                asset = new Material(shader);
            }
            else
            {
                string error;
                Type type = UapComponentTypeResolver.ResolveScriptableObjectType(assetType, out error);
                if (type == null)
                {
                    throw new InvalidOperationException(error);
                }
                asset = ScriptableObject.CreateInstance(type);
            }

            string folder = Path.GetDirectoryName(normalizedPath);
            if (folder != null)
            {
                folder = folder.Replace('\\', '/');
            }
            EnsureFolderExists(folder);

            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(normalizedPath);
            AssetDatabase.CreateAsset(asset, uniquePath);
            // OPS-9 (SR-save-if-dirty): scoped save; see UapPropertySetTool.
            AssetDatabase.SaveAssetIfDirty(asset);
            // OPS-3: CreateAsset can fail without throwing; never report
            // "Created" for a path that holds nothing.
            if (AssetDatabase.LoadMainAssetAtPath(uniquePath) == null)
            {
                throw new InvalidOperationException(
                    "Unity did not create an asset at '" + uniquePath + "'. No changes were made.");
            }

            return UapToolResults.Text("Created asset at " + uniquePath + ".");
        }

        /// <summary>Recursively creates parent folders (R09 section 4.1: no single-call "mkdir -p" for AssetDatabase folders).</summary>
        private static void EnsureFolderExists(string folder)
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

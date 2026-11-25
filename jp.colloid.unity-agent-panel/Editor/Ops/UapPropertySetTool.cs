using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Generic SerializedObject/SerializedProperty-based property setter
    /// (design section 1.2/3.2 -- "the heart of Phase 5"): works against
    /// any component on a scene GameObject OR any asset (Material,
    /// ScriptableObject, ...) by property path, without the tool needing
    /// to know the target's C# type ahead of time. Exactly one of 'path'
    /// (+ optional 'componentType'/'componentIndex') or 'assetPath' must be
    /// given.
    /// </summary>
    public sealed class UapPropertySetTool : IUapTool
    {
        public string Name
        {
            get { return "uap_property_set"; }
        }

        public string Description
        {
            get
            {
                return "Sets any serialized property on a component (Transform, RectTransform, Camera,"
                    + " Light, TextMeshPro, Renderer, custom scripts) or on an asset by SerializedProperty"
                    + " path. Common paths: Transform m_LocalPosition / m_LocalRotation (given as Euler"
                    + " {x,y,z}) / m_LocalScale (or use uap_transform_set); RectTransform"
                    + " m_AnchoredPosition / m_SizeDelta / m_AnchorMin / m_AnchorMax / m_Pivot (or use"
                    + " uap_rect_transform_set, which does all of them in one call); Camera 'field of"
                    + " view' / 'near clip plane' / 'far clip plane' / orthographic / 'orthographic size'"
                    + " / m_ClearFlags / m_BackGroundColor / m_CullingMask / m_Depth; TextMeshProUGUI and"
                    + " TextMeshPro m_text / m_fontSize / m_fontColor / m_fontAsset /"
                    + " m_HorizontalAlignment / m_VerticalAlignment / m_fontStyle / m_enableAutoSizing."
                    + " Use uap_object_inspect to list every path with its current value. Object"
                    + " references take an asset path, or 'path#subAssetName' for a sub-asset; enums take"
                    + " the value name; LayerMask takes an int, a layer name, or an array of names."
                    + " componentType \"GameObject\" addresses the GameObject itself for m_IsActive (bool),"
                    + " m_Layer (int or layer name), m_TagString (string) and m_StaticEditorFlags (int or a"
                    + " comma-separated list of StaticEditorFlags names)."
                    + " For Material shader values (_Color, _Fluo, ...) do not use m_SavedProperties paths;"
                    + " use uap_material_set.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return true; }
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
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of a scene GameObject. Mutually exclusive with 'assetPath'."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path, used with 'path'. Omit to use the active scene (or the open prefab stage)."))
                        .Set("componentType", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Component type on the GameObject named by 'path' (required when 'path' is given). Pass \"GameObject\" to edit the GameObject itself: m_IsActive, m_Layer, m_TagString, m_StaticEditorFlags."))
                        .Set("componentIndex", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Which component when several of the same type exist (default 0). This is the PER-TYPE ordinal -- uap_component_list reports it as typeIndex; do not pass that tool's all-components index."))
                        .Set("assetPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Project-relative asset path, e.g. 'Assets/Materials/Foo.mat'. Mutually exclusive with 'path'."))
                        .Set("propertyPath", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "SerializedProperty path, e.g. 'm_Speed' or 'items.Array.data[0].name'."))
                        .Set("value", JsonNode.NewObject()
                            .Set("description", "New value; shape depends on the property's type (number/bool/string, {x,y,z} or [x,y,z], {r,g,b,a} or [r,g,b,a], an enum name matched ignoring case/spaces, a LayerMask as an int/layer name/array of names, or an object reference as an asset path or 'path#subAssetName' for a sub-asset). Prefer sending the JSON object/array itself; a JSON-encoded string such as \"{\\\"x\\\": 1, \\\"y\\\": 2}\" is parsed and accepted too."))
                        .Set("valueIsNull", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Set true to clear an object-reference property instead of assigning 'value'.")))
                    .Set("required", JsonNode.NewArray().Add("propertyPath"))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string propertyPath = input["propertyPath"].AsString(null);
            if (string.IsNullOrEmpty(propertyPath))
            {
                throw new ArgumentException("'propertyPath' is required.");
            }
            string hierarchyPath = input["path"].AsString(null);
            string assetPath = input["assetPath"].AsString(null);
            bool hasHierarchy = !string.IsNullOrEmpty(hierarchyPath);
            bool hasAsset = !string.IsNullOrEmpty(assetPath);
            if (hasHierarchy == hasAsset)
            {
                throw new ArgumentException("Specify exactly one of 'path' or 'assetPath'.");
            }

            string error;
            UnityEngine.Object target;
            GameObject sceneGuardTarget = null;
            string targetDescription;

            if (hasHierarchy)
            {
                string sceneQuery = input["scene"].AsString(null);
                GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, hierarchyPath, out error);
                if (go == null)
                {
                    throw new InvalidOperationException(error);
                }
                sceneGuardTarget = go;
                if (!UapPrefabStageGuard.Check(go, out error))
                {
                    throw new InvalidOperationException(error);
                }

                string componentTypeName = input["componentType"].AsString(null);
                if (string.IsNullOrEmpty(componentTypeName))
                {
                    throw new ArgumentException("'componentType' is required when 'path' is given.");
                }
                if (string.Equals(componentTypeName, "GameObject", StringComparison.OrdinalIgnoreCase))
                {
                    return SetGameObjectProperty(go, hierarchyPath, propertyPath, input["value"]);
                }
                Type type = UapComponentTypeResolver.ResolveComponentType(componentTypeName, out error);
                if (type == null)
                {
                    throw new InvalidOperationException(error);
                }
                int componentIndex = input["componentIndex"].AsInt(0);
                Component[] comps = go.GetComponents(type);
                if (componentIndex < 0 || componentIndex >= comps.Length)
                {
                    throw new InvalidOperationException("No component of type '" + componentTypeName
                        + "' at index " + componentIndex + " on '" + hierarchyPath + "' (found " + comps.Length + ").");
                }
                target = comps[componentIndex];
                targetDescription = hierarchyPath + " [" + type.Name + "]";
            }
            else
            {
                target = UapAddressing.ResolveAsset(assetPath, out error);
                if (target == null)
                {
                    throw new InvalidOperationException(error);
                }
                targetDescription = assetPath;
            }

            if (target is Material && propertyPath.StartsWith("m_SavedProperties", StringComparison.Ordinal))
            {
                throw new ArgumentException("Do not use m_SavedProperties paths for Material shader values;"
                    + " use uap_material_set instead.");
            }

            var so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(propertyPath);
            if (prop == null)
            {
                throw new InvalidOperationException("Property not found: '" + propertyPath + "' on " + targetDescription);
            }

            JsonNode value = input["value"];
            bool valueIsNull = input["valueIsNull"].AsBool(false);
            UapPropertyValueWriter.Write(prop, value, valueIsNull);
            so.ApplyModifiedProperties();

            if (sceneGuardTarget != null)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(sceneGuardTarget.scene);
            }
            else
            {
                EditorUtility.SetDirty(target);
                // OPS-9 (SR-save-if-dirty): save THIS asset only -- the
                // argument-less SaveAssets() flushed every dirty asset in
                // the session, including edits other code left deliberately
                // unsaved.
                AssetDatabase.SaveAssetIfDirty(target);
            }

            return UapToolResults.Text("Set " + propertyPath + " on " + targetDescription + ".");
        }

        private static readonly string[] GameObjectPaths =
        {
            "m_IsActive", "m_Layer", "m_TagString", "m_StaticEditorFlags"
        };

        /// <summary>
        /// componentType "GameObject": edits the GameObject's own serialized
        /// fields. m_IsActive and static flags are additionally applied
        /// through the Unity API so the hierarchy refreshes immediately.
        /// </summary>
        private static JsonNode SetGameObjectProperty(GameObject go, string hierarchyPath, string propertyPath,
            JsonNode value)
        {
            if (Array.IndexOf(GameObjectPaths, propertyPath) < 0)
            {
                throw new InvalidOperationException("Property not found: '" + propertyPath
                    + "' on the GameObject itself. Supported: " + string.Join(", ", GameObjectPaths) + ".");
            }
            if (value == null || value.IsNull)
            {
                throw new ArgumentException("'value' is required.");
            }
            switch (propertyPath)
            {
                case "m_IsActive":
                {
                    bool active = value.AsBool(go.activeSelf);
                    Undo.RecordObject(go, "Set active");
                    go.SetActive(active);
                    break;
                }
                case "m_Layer":
                {
                    int layer;
                    string layerName = value.AsString(null);
                    int parsed;
                    if (!string.IsNullOrEmpty(layerName)
                        && !int.TryParse(layerName, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        layer = LayerMask.NameToLayer(layerName);
                        if (layer < 0)
                        {
                            throw new ArgumentException("Unknown layer name: " + layerName);
                        }
                    }
                    else
                    {
                        layer = value.AsInt(go.layer);
                    }
                    if (layer < 0 || layer > 31)
                    {
                        throw new ArgumentException("Layer must be 0..31 (or a layer name).");
                    }
                    Undo.RecordObject(go, "Set layer");
                    go.layer = layer;
                    break;
                }
                case "m_TagString":
                {
                    string tag = value.AsString(null);
                    if (string.IsNullOrEmpty(tag))
                    {
                        throw new ArgumentException("'value' must be a tag name.");
                    }
                    Undo.RecordObject(go, "Set tag");
                    go.tag = tag;
                    break;
                }
                default:
                {
                    StaticEditorFlags flags = ParseStaticFlags(value);
                    Undo.RecordObject(go, "Set static flags");
                    GameObjectUtility.SetStaticEditorFlags(go, flags);
                    break;
                }
            }
            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            return UapToolResults.Text("Set " + propertyPath + " on " + hierarchyPath + " [GameObject].");
        }

        private static StaticEditorFlags ParseStaticFlags(JsonNode value)
        {
            string text = value.AsString(null);
            int number;
            if (string.IsNullOrEmpty(text)
                || int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                return (StaticEditorFlags)value.AsInt(0);
            }
            int combined = 0;
            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                {
                    continue;
                }
                object parsedFlag;
                try
                {
                    parsedFlag = Enum.Parse(typeof(StaticEditorFlags), part, true);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException("Unknown StaticEditorFlags name: " + part);
                }
                combined |= (int)parsedFlag;
            }
            return (StaticEditorFlags)combined;
        }
    }
}

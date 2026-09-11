using System;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Read-only: dumps every visible SerializedProperty (path/type/value)
    /// of one component on a GameObject (R09 section 8.2's "inspect a
    /// type-unknown component" pattern) -- the read-side counterpart of
    /// uap_property_set, letting the agent discover a property path/value
    /// before writing to it.
    /// </summary>
    public sealed class UapObjectInspectTool : IUapTool
    {
        public string Name
        {
            get { return "uap_object_inspect"; }
        }

        public string Description
        {
            get
            {
                return "Lists every serialized property path with its current value for any component"
                    + " (Transform, RectTransform, Camera, TextMeshPro, custom scripts) on a GameObject"
                    + " -- array elements appear as name.Array.data[i]; use uap_component_list first to find"
                    + " the component type. Read-only.";
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
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the target GameObject."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("componentType", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Component type to inspect (short or fully-qualified name)."))
                        .Set("componentIndex", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Which component when several of the same type exist (default 0). This is the PER-TYPE ordinal -- uap_component_list reports it as typeIndex; do not pass that tool's all-components index.")))
                    .Set("required", JsonNode.NewArray().Add("path").Add("componentType"))
                    .Set("additionalProperties", false);
            }
        }

        /// <summary>OPS defect #4: caps total emitted property rows so a huge array (or many of them) cannot blow up the response.</summary>
        private const int MaxEmittedEntries = 500;

        /// <summary>Per-array element cap -- see <see cref="AppendGenericProperty"/>.</summary>
        private const int MaxArrayElements = 32;

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            string componentTypeName = input["componentType"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }
            if (string.IsNullOrEmpty(componentTypeName))
            {
                throw new ArgumentException("'componentType' is required.");
            }
            string sceneQuery = input["scene"].AsString(null);
            string error;
            GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, path, out error);
            if (go == null)
            {
                throw new InvalidOperationException(error);
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
                    + "' at index " + componentIndex + " on '" + path + "' (found " + comps.Length + ").");
            }
            Component target = comps[componentIndex];

            var so = new SerializedObject(target);
            JsonNode props = JsonNode.NewArray();
            SerializedProperty it = so.GetIterator();
            bool enterChildren = true;
            int emitted = 0;
            while (emitted < MaxEmittedEntries && it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyType == SerializedPropertyType.Generic)
                {
                    // OPS defect #4: a Generic property (an array like
                    // m_Materials, or a nested serializable struct) used to
                    // print as the single opaque row "<Generic>" with no
                    // children at all -- an agent could never discover the
                    // "m_Materials.Array.data[0]" path uap_property_set
                    // needs. Expand it instead of describing it as one row.
                    emitted += AppendGenericProperty(props, it, MaxEmittedEntries - emitted);
                    continue;
                }
                props.Add(JsonNode.NewObject()
                    .Set("path", it.propertyPath)
                    .Set("type", it.propertyType.ToString())
                    .Set("value", DescribeValue(it)));
                emitted++;
            }

            JsonNode result = JsonNode.NewObject()
                .Set("gameObjectPath", path)
                .Set("component", target.GetType().FullName)
                .Set("properties", props);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static string DescribeValue(SerializedProperty it)
        {
            switch (it.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return it.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return it.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return FormatFloat(it.floatValue);
                case SerializedPropertyType.String:
                    return it.stringValue;
                case SerializedPropertyType.Enum:
                    return it.enumValueIndex >= 0 && it.enumDisplayNames != null
                        && it.enumValueIndex < it.enumDisplayNames.Length
                        ? it.enumDisplayNames[it.enumValueIndex]
                        : it.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference:
                    return DescribeObjectReference(it.objectReferenceValue);
                case SerializedPropertyType.Vector2:
                    return FormatVector2(it.vector2Value);
                case SerializedPropertyType.Vector3:
                    return FormatVector3(it.vector3Value);
                case SerializedPropertyType.Vector4:
                    return FormatVector4(it.vector4Value);
                case SerializedPropertyType.Color:
                    return FormatColor(it.colorValue);
                case SerializedPropertyType.Rect:
                    return FormatRect(it.rectValue);
                case SerializedPropertyType.Quaternion:
                    return FormatVector3(it.quaternionValue.eulerAngles);
                case SerializedPropertyType.ArraySize:
                    return it.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.LayerMask:
                    return DescribeLayerMask(it.intValue);
                default:
                    return "<" + it.propertyType + ">";
            }
        }

        /// <summary>
        /// OPS defect #1's inspect-side counterpart: when the referenced
        /// object is a SUB-asset (a Material inside a font asset, a Mesh
        /// inside an FBX), printing only its containing asset path throws
        /// away which sub-asset it actually is -- an agent copying that
        /// path back into uap_property_set would land on the wrong object
        /// (the main asset). Print "path#name" so inspect output round-trips.
        /// </summary>
        private static string DescribeObjectReference(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "null";
            }
            string assetPath = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(assetPath))
            {
                return value.name;
            }
            if (!AssetDatabase.IsMainAsset(value))
            {
                return assetPath + "#" + value.name;
            }
            return assetPath;
        }

        /// <summary>
        /// OPS defect #2's inspect-side counterpart: the raw int mask alone
        /// forces the agent to decode bits by hand, so the layer names it
        /// contains are appended, e.g. "5 [Default, TransparentFX]".
        /// </summary>
        private static string DescribeLayerMask(int mask)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0)
                {
                    continue;
                }
                string name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
            return mask.ToString(CultureInfo.InvariantCulture) + " [" + string.Join(", ", names) + "]";
        }

        /// <summary>
        /// OPS defect #4: expands a Generic property that the flat
        /// NextVisible(false) walk would otherwise print as a single opaque
        /// "<Generic>" row. An array (prop.isArray) gets an
        /// "path.Array.size" entry plus one "path.Array.data[i]" entry per
        /// element (up to <see cref="MaxArrayElements"/>, with a truncation
        /// note past that); a non-array Generic (a nested serializable
        /// struct) gets its direct children expanded one level via the
        /// standard Copy()/GetEndProperty()/NextVisible idiom -- ONE level
        /// only, so a struct nested inside this struct still shows as its
        /// own "<Generic>" row rather than recursing arbitrarily deep.
        /// Returns how many rows were appended, respecting <paramref name="budget"/>.
        /// </summary>
        private static int AppendGenericProperty(JsonNode props, SerializedProperty it, int budget)
        {
            if (budget <= 0)
            {
                return 0;
            }

            if (it.isArray)
            {
                string arrayPath = it.propertyPath;
                int count = it.arraySize;
                props.Add(JsonNode.NewObject()
                    .Set("path", arrayPath + ".Array.size")
                    .Set("type", "ArraySize")
                    .Set("value", count.ToString(CultureInfo.InvariantCulture)));
                int emitted = 1;

                int shown = Math.Min(count, MaxArrayElements);
                for (int i = 0; i < shown && emitted < budget; i++)
                {
                    SerializedProperty element = it.GetArrayElementAtIndex(i);
                    props.Add(JsonNode.NewObject()
                        .Set("path", element.propertyPath)
                        .Set("type", element.propertyType.ToString())
                        .Set("value", DescribeValue(element)));
                    emitted++;
                }
                if (count > shown && emitted < budget)
                {
                    props.Add(JsonNode.NewObject()
                        .Set("path", arrayPath + ".Array")
                        .Set("type", "Note")
                        .Set("value", "... " + (count - shown) + " more element(s) truncated"));
                    emitted++;
                }
                return emitted;
            }

            SerializedProperty child = it.Copy();
            SerializedProperty end = it.GetEndProperty();
            bool enterChildren = true;
            int childEmitted = 0;
            while (childEmitted < budget && child.NextVisible(enterChildren) && !SerializedProperty.EqualContents(child, end))
            {
                enterChildren = false;
                if (child.propertyType == SerializedPropertyType.Generic)
                {
                    props.Add(JsonNode.NewObject()
                        .Set("path", child.propertyPath)
                        .Set("type", child.isArray ? "Generic (array)" : "Generic")
                        .Set("value", "<Generic>"));
                }
                else
                {
                    props.Add(JsonNode.NewObject()
                        .Set("path", child.propertyPath)
                        .Set("type", child.propertyType.ToString())
                        .Set("value", DescribeValue(child)));
                }
                childEmitted++;
            }
            return childEmitted;
        }

        /// <summary>OPS defect #4: round-trip precision (was ToString()'s 2-decimal rounding -- 0.005 printed as 0.01) via "R", InvariantCulture.</summary>
        private static string FormatFloat(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatVector2(Vector2 v)
        {
            return "(" + FormatFloat(v.x) + ", " + FormatFloat(v.y) + ")";
        }

        private static string FormatVector3(Vector3 v)
        {
            return "(" + FormatFloat(v.x) + ", " + FormatFloat(v.y) + ", " + FormatFloat(v.z) + ")";
        }

        private static string FormatVector4(Vector4 v)
        {
            return "(" + FormatFloat(v.x) + ", " + FormatFloat(v.y) + ", " + FormatFloat(v.z) + ", " + FormatFloat(v.w) + ")";
        }

        private static string FormatColor(Color c)
        {
            return "RGBA(" + FormatFloat(c.r) + ", " + FormatFloat(c.g) + ", " + FormatFloat(c.b) + ", " + FormatFloat(c.a) + ")";
        }

        private static string FormatRect(Rect r)
        {
            return "(x:" + FormatFloat(r.x) + ", y:" + FormatFloat(r.y)
                + ", width:" + FormatFloat(r.width) + ", height:" + FormatFloat(r.height) + ")";
        }
    }
}

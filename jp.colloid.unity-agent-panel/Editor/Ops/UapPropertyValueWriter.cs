using System;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Writes a JsonNode value into a SerializedProperty, dispatching on the
    /// property's OWN reported type (R09 section 3.3's type table) rather
    /// than guessing from the JSON shape -- this is what lets uap_property_set
    /// work generically against any component/asset without a per-type
    /// tool. Caller is responsible for SerializedObject.ApplyModifiedProperties()
    /// afterward (this class only ever touches the one SerializedProperty).
    /// </summary>
    public static class UapPropertyValueWriter
    {
        /// <summary>
        /// Writes <paramref name="value"/> (or clears to null when
        /// <paramref name="valueIsNull"/>) into <paramref name="prop"/>.
        /// Throws InvalidOperationException/ArgumentException with a
        /// descriptive message for an unsupported propertyType or a
        /// malformed value -- callers surface that as the tool error.
        /// </summary>
        public static void Write(SerializedProperty prop, JsonNode value, bool valueIsNull)
        {
            if (prop == null)
            {
                throw new ArgumentNullException("prop");
            }
            if (valueIsNull)
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                {
                    throw new InvalidOperationException(
                        "valueIsNull is only valid for an ObjectReference property (was " + prop.propertyType + ").");
                }
                prop.objectReferenceValue = null;
                return;
            }
            if (value == null || value.IsNull)
            {
                throw new ArgumentException("'value' is required (pass valueIsNull:true to clear an object reference).");
            }

            // OPS-7 (SR-strict-coercion): the enum branch below learned to
            // refuse wrong-shaped values; these branches used the lenient
            // As* accessors directly, so a bool/object on an int property
            // silently wrote the default 0, and a scalar on a Vector/Color
            // property resolved every component to its CURRENT value -- a
            // no-op reported as success. Same policy everywhere now: check
            // the shape, refuse with DescribeJsonType, THEN read.
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = ReadInteger(value, prop);
                    return;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = value.AsBool();
                    return;
                case SerializedPropertyType.Float:
                    RequireNumber(value, prop);
                    prop.floatValue = (float)value.AsDouble();
                    return;
                case SerializedPropertyType.String:
                    prop.stringValue = value.AsString(string.Empty);
                    return;
                case SerializedPropertyType.ArraySize:
                    RequireInteger(value, prop);
                    prop.arraySize = (int)value.AsLong();
                    return;
                case SerializedPropertyType.Enum:
                    WriteEnum(prop, value);
                    return;
                case SerializedPropertyType.Color:
                    RequireObject(value, prop);
                    prop.colorValue = ReadColor(value, prop.colorValue);
                    return;
                case SerializedPropertyType.Vector2:
                    RequireObject(value, prop);
                    prop.vector2Value = ReadVector(value, prop.vector2Value.x, prop.vector2Value.y, 0f, 0f);
                    return;
                case SerializedPropertyType.Vector3:
                    RequireObject(value, prop);
                    prop.vector3Value = ReadVector3(value, prop.vector3Value);
                    return;
                case SerializedPropertyType.Vector4:
                    RequireObject(value, prop);
                    prop.vector4Value = ReadVector4(value, prop.vector4Value);
                    return;
                case SerializedPropertyType.Quaternion:
                    RequireObject(value, prop);
                    Vector3 euler = ReadVector3(value, prop.quaternionValue.eulerAngles);
                    prop.quaternionValue = Quaternion.Euler(euler);
                    return;
                case SerializedPropertyType.Rect:
                    RequireObject(value, prop);
                    prop.rectValue = ReadRect(value, prop.rectValue);
                    return;
                case SerializedPropertyType.ObjectReference:
                    WriteObjectReference(prop, value);
                    return;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = ReadLayerMask(value, prop);
                    return;
                default:
                    throw new InvalidOperationException(
                        "uap_property_set does not support propertyType " + prop.propertyType
                        + " yet (property '" + prop.propertyPath + "').");
            }
        }

        private static void WriteEnum(SerializedProperty prop, JsonNode value)
        {
            if (value.IsString)
            {
                string name = value.AsString(string.Empty);
                string[] names = prop.enumNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        prop.enumValueIndex = i;
                        return;
                    }
                }
                // prop.enumNames carries the DISPLAY name ("Solid Color",
                // "Don't Clear"), not the C# enum member name
                // ("SolidColor", "DontClear") -- the exact/OrdinalIgnoreCase
                // check above rejects the C# name outright even though it
                // unambiguously names the same value. Fall back to comparing
                // both sides with every non-alphanumeric character removed
                // (so "SolidColor" == "Solid Color" and "depth_only" ==
                // "Depth only") before giving up.
                string normalizedName = NormalizeEnumToken(name);
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(NormalizeEnumToken(names[i]), normalizedName, StringComparison.Ordinal))
                    {
                        prop.enumValueIndex = i;
                        return;
                    }
                }
                throw new ArgumentException("Unknown enum value '" + name + "' for '" + prop.propertyPath
                    + "'. Valid values: " + string.Join(", ", names));
            }
            // The string branch above validates against enumNames and throws
            // with the valid list. This branch used to be a bare
            // `prop.enumValueIndex = (int)value.AsLong();` -- no validation of
            // any kind, in the same method, three lines apart. Two ways that
            // went wrong:
            //
            //   * enumValueIndex is an INDEX into enumNames, not the enum's
            //     underlying value. An out-of-range index does not throw; the
            //     inspector simply shows a blank popup and the serialized
            //     object carries a value no user could have picked.
            //   * AsLong is lenient: a bool, an object or a non-numeric string
            //     all return the default, so `{"enabled": true}` on an enum
            //     property silently wrote index 0 and reported success.
            //
            // Found by a subagent while fixing the identical shape in
            // UapUiValueCoercion (design note section 9.4). Same defect class
            // as uap_material_set and uap_editor_ui_click: a write that cannot
            // fail is a write whose success means nothing.
            if (!value.IsNumber)
            {
                throw new ArgumentException("Enum property '" + prop.propertyPath + "' needs either one of"
                    + " its value names or a numeric index, but was given " + DescribeJsonType(value)
                    + ". Valid values: " + string.Join(", ", prop.enumNames));
            }
            long requested = value.AsLong(-1);
            string[] validNames = prop.enumNames;
            if (requested < 0 || requested >= validNames.Length)
            {
                throw new ArgumentException("Enum index " + requested + " is out of range for '"
                    + prop.propertyPath + "', which has " + validNames.Length + " values (0.."
                    + (validNames.Length - 1) + "). Valid values: " + string.Join(", ", validNames));
            }
            prop.enumValueIndex = (int)requested;
        }

        /// <summary>
        /// OPS defect #1 (measured 2026-09-08): Unity silently drops an
        /// ObjectReference assignment back to null when the assigned
        /// object's type does not match the field -- assigning a
        /// ScriptableObject asset into a MeshRenderer's m_Materials slot
        /// leaves the reference null with no exception, so the old
        /// `prop.objectReferenceValue = asset;` reported "Set ... OK" while
        /// writing nothing. This method now (a) throws naming both the
        /// given asset's actual type and the property's expected type when
        /// the plain main-asset write fails outright, (b) accepts a
        /// "path#name" sub-asset address so a Material inside a TextMeshPro
        /// font asset or a Mesh inside an FBX can be targeted at all (only
        /// the main asset was ever reachable before), and (c) when a plain
        /// path's main asset is incompatible but exactly one sub-asset at
        /// that path IS compatible, uses it automatically -- the common
        /// case of an agent passing a TMP font asset path for
        /// m_fontSharedMaterial, where the compatible Material is a
        /// sub-asset rather than the main asset.
        /// </summary>
        private static void WriteObjectReference(SerializedProperty prop, JsonNode value)
        {
            string raw = value.AsString(null);
            if (raw == null)
            {
                throw new ArgumentException(
                    "ObjectReference property '" + prop.propertyPath + "' requires a string asset path.");
            }

            int hashIndex = raw.LastIndexOf('#');
            if (hashIndex >= 0)
            {
                string subAssetPath = raw.Substring(0, hashIndex);
                string subAssetName = raw.Substring(hashIndex + 1);
                WriteSubAssetReference(prop, subAssetPath, subAssetName);
                return;
            }

            UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(raw);
            if (mainAsset == null)
            {
                throw new InvalidOperationException("Asset not found: " + raw);
            }

            UnityEngine.Object previous = prop.objectReferenceValue;
            prop.objectReferenceValue = mainAsset;
            if (prop.objectReferenceValue != null)
            {
                return;
            }
            prop.objectReferenceValue = previous;

            // Main asset is type-incompatible. Probe every OTHER asset at
            // this path (sub-assets) for compatibility -- Unity's own
            // assignment does the real type check, so probing is just
            // "assign, look at objectReferenceValue, restore".
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(raw);
            var compatible = new System.Collections.Generic.List<UnityEngine.Object>();
            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.Object candidate = all[i];
                if (candidate == null || candidate == mainAsset)
                {
                    continue;
                }
                if (ProbeCompatible(prop, candidate))
                {
                    compatible.Add(candidate);
                }
            }

            if (compatible.Count == 1)
            {
                prop.objectReferenceValue = compatible[0];
                return;
            }
            if (compatible.Count > 1)
            {
                var options = new System.Collections.Generic.List<string>();
                for (int i = 0; i < compatible.Count; i++)
                {
                    options.Add(raw + "#" + compatible[i].name + " (" + compatible[i].GetType().Name + ")");
                }
                throw new InvalidOperationException("Asset '" + raw + "' has " + compatible.Count
                    + " sub-assets compatible with '" + prop.propertyPath + "' (expects " + ExpectedTypeName(prop)
                    + "). Use one of: " + string.Join(", ", options) + ".");
            }

            throw new InvalidOperationException("Asset '" + raw + "' is a " + mainAsset.GetType().Name
                + " but property '" + prop.propertyPath + "' expects " + ExpectedTypeName(prop) + ".");
        }

        /// <summary>
        /// Resolves an explicit "path#name" sub-asset address (a Material
        /// inside a font asset, a Mesh inside an FBX, a Sprite inside a
        /// sliced texture) -- these were never reachable through
        /// LoadMainAssetAtPath at all before OPS defect #1's fix.
        /// </summary>
        private static void WriteSubAssetReference(SerializedProperty prop, string assetPath, string subAssetName)
        {
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (all == null || all.Length == 0)
            {
                throw new InvalidOperationException("Asset not found: " + assetPath);
            }

            UnityEngine.Object match = FindSubAssetByName(all, subAssetName, StringComparison.Ordinal)
                ?? FindSubAssetByName(all, subAssetName, StringComparison.OrdinalIgnoreCase);
            if (match == null)
            {
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null)
                    {
                        names.Add(all[i].name + " (" + all[i].GetType().Name + ")");
                    }
                }
                throw new InvalidOperationException("No sub-asset named '" + subAssetName + "' at '" + assetPath
                    + "'. Available: " + (names.Count > 0 ? string.Join(", ", names) : "(none)") + ".");
            }

            UnityEngine.Object previous = prop.objectReferenceValue;
            prop.objectReferenceValue = match;
            if (prop.objectReferenceValue == null)
            {
                prop.objectReferenceValue = previous;
                throw new InvalidOperationException("Sub-asset '" + subAssetName + "' at '" + assetPath + "' is a "
                    + match.GetType().Name + " but property '" + prop.propertyPath + "' expects "
                    + ExpectedTypeName(prop) + ".");
            }
        }

        private static UnityEngine.Object FindSubAssetByName(
            UnityEngine.Object[] candidates, string name, StringComparison comparison)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && string.Equals(candidates[i].name, name, comparison))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        /// <summary>Assigns, checks Unity's own type check via the resulting value, then restores. Never leaves <paramref name="prop"/> changed.</summary>
        private static bool ProbeCompatible(SerializedProperty prop, UnityEngine.Object candidate)
        {
            UnityEngine.Object previous = prop.objectReferenceValue;
            prop.objectReferenceValue = candidate;
            bool compatible = prop.objectReferenceValue != null;
            prop.objectReferenceValue = previous;
            return compatible;
        }

        /// <summary>
        /// prop.type for an ObjectReference looks like "PPtr&lt;Material&gt;"
        /// or "PPtr&lt;$Material&gt;" (the leading '$' shows up for some
        /// built-in types in 2022.3) -- strip both wrappers down to the bare
        /// type name for error messages.
        /// </summary>
        private static string ExpectedTypeName(SerializedProperty prop)
        {
            string t = prop.type ?? string.Empty;
            if (t.StartsWith("PPtr<", StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
            {
                t = t.Substring(5, t.Length - 6);
            }
            if (t.StartsWith("$", StringComparison.Ordinal))
            {
                t = t.Substring(1);
            }
            return t;
        }

        /// <summary>Removes every non-alphanumeric character and lower-cases, so "SolidColor" and "Solid Color" compare equal.</summary>
        private static string NormalizeEnumToken(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// OPS defect #2 (measured): SerializedPropertyType.LayerMask threw
        /// "does not support propertyType LayerMask" -- Write's switch had
        /// no case for it at all, so uap_property_set could never touch a
        /// Camera's m_CullingMask or any other layer field. Accepts a raw
        /// int bitmask, a layer name resolved via LayerMask.NameToLayer, or
        /// an array mixing both (ORed together).
        /// </summary>
        private static int ReadLayerMask(JsonNode value, SerializedProperty prop)
        {
            if (value.IsNumber)
            {
                RequireInteger(value, prop);
                return (int)value.AsLong();
            }
            if (value.IsString)
            {
                return 1 << LayerNameToIndexOrThrow(value.AsString(string.Empty), prop);
            }
            if (value.IsArray)
            {
                int mask = 0;
                foreach (JsonNode item in value.Items)
                {
                    if (item.IsString)
                    {
                        mask |= 1 << LayerNameToIndexOrThrow(item.AsString(string.Empty), prop);
                    }
                    else if (item.IsNumber)
                    {
                        mask |= (int)item.AsLong();
                    }
                    else
                    {
                        throw new ArgumentException("LayerMask property '" + prop.propertyPath
                            + "' array elements must be layer names or integers, but found "
                            + DescribeJsonType(item) + ".");
                    }
                }
                return mask;
            }
            throw new ArgumentException("LayerMask property '" + prop.propertyPath
                + "' needs an integer bitmask, a layer name string, or an array of names/integers, but was given "
                + DescribeJsonType(value) + ".");
        }

        private static int LayerNameToIndexOrThrow(string name, SerializedProperty prop)
        {
            int index = LayerMask.NameToLayer(name);
            if (index < 0)
            {
                throw new ArgumentException("Unknown layer name '" + name + "' for LayerMask property '"
                    + prop.propertyPath + "'.");
            }
            return index;
        }

        /// <summary>
        /// OPS-7: the value for an Integer property. A BOOLEAN maps to 1/0
        /// rather than being refused -- Unity serializes plenty of
        /// conceptually-boolean settings as ints (TextureImporter's
        /// m_EnableMipMap is the one that caught this), so `true` is the
        /// natural thing for a caller to send and it must land as 1. That
        /// is also what the pre-OPS-7 code got WRONG in the worst way:
        /// AsLong on a bool returned its silent default 0, so "set mipmaps
        /// on" reported success while writing OFF. Everything else still
        /// has to be a number (or a numeric string).
        /// </summary>
        private static int ReadInteger(JsonNode value, SerializedProperty prop)
        {
            if (value.IsBool)
            {
                return value.AsBool() ? 1 : 0;
            }
            RequireInteger(value, prop);
            return (int)value.AsLong();
        }

        /// <summary>OPS-7: a number, or a string that parses as one (the As* accessors have always accepted numeric strings).</summary>
        private static void RequireNumber(JsonNode value, SerializedProperty prop)
        {
            if (value.IsNumber)
            {
                return;
            }
            double parsed;
            if (value.IsString && double.TryParse(value.AsString(string.Empty),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return;
            }
            throw new ArgumentException("Property '" + prop.propertyPath + "' (" + prop.propertyType
                + ") needs a number, but was given " + DescribeJsonType(value) + ".");
        }

        /// <summary>OPS-7: like RequireNumber, but a numeric STRING must parse as an integer -- "1.5" on an int property would otherwise fall to AsLong's silent default.</summary>
        private static void RequireInteger(JsonNode value, SerializedProperty prop)
        {
            if (value.IsNumber)
            {
                return;
            }
            long parsed;
            if (value.IsString && long.TryParse(value.AsString(string.Empty),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return;
            }
            throw new ArgumentException("Property '" + prop.propertyPath + "' (" + prop.propertyType
                + ") needs an integer, but was given " + DescribeJsonType(value) + ".");
        }

        /// <summary>
        /// OPS-7: the struct readers merge the given components over the
        /// property's current value (partial updates are a feature), so
        /// anything that is not an object at all would resolve to ALL
        /// current values -- a silent no-op. Refuse the shape instead.
        /// </summary>
        private static void RequireObject(JsonNode value, SerializedProperty prop)
        {
            if (value.IsObject)
            {
                return;
            }
            throw new ArgumentException("Property '" + prop.propertyPath + "' (" + prop.propertyType
                + ") needs an object with its component fields (e.g. {\"x\": 1, \"y\": 2}), but was given "
                + DescribeJsonType(value) + ".");
        }

        private static Color ReadColor(JsonNode value, Color fallback)
        {
            return new Color(
                (float)value["r"].AsDouble(fallback.r),
                (float)value["g"].AsDouble(fallback.g),
                (float)value["b"].AsDouble(fallback.b),
                (float)value["a"].AsDouble(fallback.a));
        }

        private static Vector2 ReadVector(JsonNode value, float fx, float fy, float fz, float fw)
        {
            return new Vector2((float)value["x"].AsDouble(fx), (float)value["y"].AsDouble(fy));
        }

        private static Vector3 ReadVector3(JsonNode value, Vector3 fallback)
        {
            return new Vector3(
                (float)value["x"].AsDouble(fallback.x),
                (float)value["y"].AsDouble(fallback.y),
                (float)value["z"].AsDouble(fallback.z));
        }

        private static Vector4 ReadVector4(JsonNode value, Vector4 fallback)
        {
            return new Vector4(
                (float)value["x"].AsDouble(fallback.x),
                (float)value["y"].AsDouble(fallback.y),
                (float)value["z"].AsDouble(fallback.z),
                (float)value["w"].AsDouble(fallback.w));
        }

        private static Rect ReadRect(JsonNode value, Rect fallback)
        {
            return new Rect(
                (float)value["x"].AsDouble(fallback.x),
                (float)value["y"].AsDouble(fallback.y),
                (float)value["width"].AsDouble(fallback.width),
                (float)value["height"].AsDouble(fallback.height));
        }

        /// <summary>
        /// Names the JSON type actually supplied, so a refusal can say what
        /// arrived instead of only what was wanted -- the difference between
        /// "give me a number" and "you sent a boolean" is what stops the
        /// caller retrying the same wrong shape.
        /// </summary>
        private static string DescribeJsonType(JsonNode value)
        {
            if (value == null || value.IsNull) { return "null"; }
            if (value.IsBool) { return "a boolean"; }
            if (value.IsString) { return "a string"; }
            if (value.IsNumber) { return "a number"; }
            if (value.IsArray) { return "an array"; }
            if (value.IsObject) { return "an object"; }
            return "an unsupported value";
        }
    }
}

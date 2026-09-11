using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// In-memory shape of one Extension Profile (design section 3b/8.2 B3):
    /// a JSON "knowledge pack" describing a detected third-party SDK
    /// (VRChat SDK3, UniVRM, MagicaCloth2, FinalIK, ...) so the agent starts
    /// a conversation already knowing its component types and safe usage
    /// notes, without a bespoke code path per SDK.
    ///
    /// Schema (see the bundled Editor/Ops/Profiles/*.json files):
    /// <code>
    /// {
    ///   "id": "vrchat-sdk3",
    ///   "displayName": "VRChat SDK3 (Avatars)",
    ///   "detection": { "packageIds": [...], "typeNames": [...] },
    ///   "instructionLines": ["...", "..."]
    /// }
    /// </code>
    ///
    /// Pure C# (no Unity dependency beyond the shared JSON DOM), so parsing
    /// is unit-tested directly against both hand-built JSON strings and the
    /// real bundled files.
    /// </summary>
    public sealed class ExtensionProfile
    {
        public string Id;
        public string DisplayName;

        /// <summary>Package identifiers (Packages/manifest.json dependency keys or Library/PackageCache directory names) that indicate this SDK is installed.</summary>
        public List<string> PackageIds = new List<string>();

        /// <summary>Compiled Component/ScriptableObject type names (short or fully-qualified) that indicate this SDK is present even without a package id (e.g. an Assets/-installed Asset Store package like FinalIK).</summary>
        public List<string> TypeNames = new List<string>();

        /// <summary>Lines appended verbatim under a "## DisplayName" heading when this profile is detected and trusted (ExtensionProfileInjector).</summary>
        public List<string> InstructionLines = new List<string>();

        /// <summary>Parses profile JSON text. Returns null and sets <paramref name="error"/> on any malformed input; never throws.</summary>
        public static ExtensionProfile Parse(string json, out string error)
        {
            if (string.IsNullOrEmpty(json))
            {
                error = "empty content";
                return null;
            }
            JsonNode root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                error = "invalid JSON: " + ex.Message;
                return null;
            }
            return Parse(root, out error);
        }

        /// <summary>Parses an already-decoded JSON DOM node (test seam so callers do not have to round-trip through text).</summary>
        public static ExtensionProfile Parse(JsonNode root, out string error)
        {
            error = null;
            if (root == null || !root.IsObject)
            {
                error = "root is not a JSON object";
                return null;
            }
            string id = root["id"].AsString(null);
            if (string.IsNullOrEmpty(id))
            {
                error = "'id' is required";
                return null;
            }
            string displayName = root["displayName"].AsString(id);

            var profile = new ExtensionProfile { Id = id, DisplayName = displayName };
            JsonNode detection = root["detection"];
            AppendStrings(detection["packageIds"], profile.PackageIds);
            AppendStrings(detection["typeNames"], profile.TypeNames);
            AppendStrings(root["instructionLines"], profile.InstructionLines);
            return profile;
        }

        private static void AppendStrings(JsonNode arrayNode, List<string> target)
        {
            foreach (JsonNode item in arrayNode.Items)
            {
                string value = item.AsString(null);
                if (!string.IsNullOrEmpty(value))
                {
                    target.Add(value);
                }
            }
        }
    }
}

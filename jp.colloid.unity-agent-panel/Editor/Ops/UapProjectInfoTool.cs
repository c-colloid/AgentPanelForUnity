using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reports the project facts an agent needs before reaching for
    /// another uap_* tool: Unity version, product identity, active build
    /// target/scripting backend, Active Input Handling, Enter Play Mode
    /// Settings, Run In Background, color space, and which of the packages
    /// that change this panel's own behaviour are installed (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 4 -- uLoop's
    /// get-version + get-project-info folded into one tool since this
    /// panel has no separate CLI process to ask a version question of).
    ///
    /// Every value below comes from a UnityEditor GETTER (Application.
    /// unityVersion, PlayerSettings.*, EditorUserBuildSettings.
    /// activeBuildTarget, EditorSettings.*) or a read-only File.ReadAllText
    /// of ProjectSettings.asset / Packages/manifest.json / Packages/
    /// packages-lock.json -- nothing here is ever written, which is what
    /// makes ReadOnly=true (auto-approve without a permission card) safe.
    /// Active Input Handling is the one field with no public getter
    /// (PlayerSettings exposes it to the native Player Settings window
    /// only), so it is text-scanned out of ProjectSettings.asset by
    /// <see cref="UapProjectInfoPolicy.ParseActiveInputHandler"/> instead.
    /// </summary>
    public sealed class UapProjectInfoTool : IUapTool
    {
        public const string ToolName = "uap_project_info";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Reports project facts other uap_* tools depend on: Unity version, product/company/"
                    + "bundle version, active build target + scripting backend, Active Input Handling (whether"
                    + " uap_game_key/uap_game_mouse have a device to inject into), Enter Play Mode Settings"
                    + " (domain/scene reload), Run In Background, color space, and presence+version of the"
                    + " packages that change this panel's behaviour (com.unity.ugui, com.unity.inputsystem,"
                    + " com.unity.test-framework, com.unity.textmeshpro, uLoop, Agent Panel Pro). Call it once"
                    + " at the start of a session, or whenever a tool refuses for a reason that sounds like a"
                    + " project setting. Read-only, takes no arguments.";
            }
        }

        public string Module
        {
            get { return "editor"; }
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
                    .Set("properties", JsonNode.NewObject())
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string projectRoot = ProjectRoot();

            BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
            string scriptingBackend = DescribeScriptingBackend(activeTarget);

            int activeInputHandler = UapProjectInfoPolicy.ParseActiveInputHandler(
                ReadTextOrNull(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset")));

            JsonNode manifestRoot = ReadJsonOrNull(Path.Combine(projectRoot, "Packages", "manifest.json"));
            JsonNode lockRoot = ReadJsonOrNull(Path.Combine(projectRoot, "Packages", "packages-lock.json"));
            JsonNode manifestDeps = manifestRoot["dependencies"];
            JsonNode lockDeps = lockRoot["dependencies"];

            JsonNode packages = JsonNode.NewArray();
            bool inputSystemPresent = false;
            bool uguiPresent = false;
            for (int i = 0; i < UapProjectInfoPolicy.TrackedPackages.Length; i++)
            {
                UapProjectInfoPolicy.TrackedPackage tracked = UapProjectInfoPolicy.TrackedPackages[i];
                UapPackageResolution resolution = UapProjectInfoPolicy.TryResolvePackage(manifestDeps, lockDeps, tracked);
                if (tracked.ReportedId == "com.unity.inputsystem")
                {
                    inputSystemPresent = resolution.Present;
                }
                else if (tracked.ReportedId == "com.unity.ugui")
                {
                    uguiPresent = resolution.Present;
                }
                JsonNode entry = JsonNode.NewObject()
                    .Set("id", resolution.RequestedId)
                    .Set("present", resolution.Present)
                    .Set("version", resolution.Present ? resolution.Version ?? string.Empty : string.Empty);
                if (resolution.Present && resolution.MatchedId != resolution.RequestedId)
                {
                    // uLoop's unity-cli-loop rename: say which id actually resolved.
                    entry.Set("resolvedAs", resolution.MatchedId);
                }
                packages.Add(entry);
            }

            bool enterPlayModeEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions options = EditorSettings.enterPlayModeOptions;
            bool domainReloadDisabled = enterPlayModeEnabled
                && (options & EnterPlayModeOptions.DisableDomainReload) != 0;
            bool sceneReloadDisabled = enterPlayModeEnabled
                && (options & EnterPlayModeOptions.DisableSceneReload) != 0;

            List<string> implications = UapProjectInfoPolicy.BuildImplications(
                activeInputHandler, inputSystemPresent, uguiPresent, PlayerSettings.runInBackground,
                domainReloadDisabled);

            JsonNode implicationsNode = JsonNode.NewArray();
            for (int i = 0; i < implications.Count; i++)
            {
                implicationsNode.Add(implications[i]);
            }

            JsonNode result = JsonNode.NewObject()
                .Set("unityVersion", Application.unityVersion ?? string.Empty)
                .Set("productName", PlayerSettings.productName ?? string.Empty)
                .Set("companyName", PlayerSettings.companyName ?? string.Empty)
                .Set("bundleVersion", PlayerSettings.bundleVersion ?? string.Empty)
                .Set("activeBuildTarget", activeTarget.ToString())
                .Set("scriptingBackend", scriptingBackend)
                .Set("activeInputHandling", UapProjectInfoPolicy.DescribeInputHandling(activeInputHandler))
                .Set("activeInputHandlingRaw", activeInputHandler)
                .Set("enterPlayModeSettings", JsonNode.NewObject()
                    .Set("enabled", enterPlayModeEnabled)
                    .Set("domainReloadDisabled", domainReloadDisabled)
                    .Set("sceneReloadDisabled", sceneReloadDisabled))
                .Set("runInBackground", PlayerSettings.runInBackground)
                .Set("colorSpace", PlayerSettings.colorSpace.ToString())
                .Set("packages", packages)
                .Set("implications", implicationsNode)
                .Set("message", implications.Count == 0
                    ? "Project info collected; nothing here currently limits an existing uap_* tool."
                    : implications.Count + " item(s) below limit an existing uap_* tool -- see 'implications'.");
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>
        /// PlayerSettings.GetScriptingBackend(BuildTargetGroup) throws for
        /// a group the native side does not recognise (BuildTargetGroup.
        /// Unknown -- e.g. EditorUserBuildSettings.activeBuildTarget ==
        /// BuildTarget.NoTarget, which happens in some CI/batch-mode
        /// states). Reported as "Unknown" instead of failing the whole
        /// read-only tool over one field.
        /// </summary>
        private static string DescribeScriptingBackend(BuildTarget activeTarget)
        {
            try
            {
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(activeTarget);
                if (group == BuildTargetGroup.Unknown)
                {
                    return "Unknown";
                }
                return PlayerSettings.GetScriptingBackend(group).ToString();
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        private static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        /// <summary>Never throws: a missing/unreadable file reads as absent, exactly like ParseActiveInputHandler(null) does.</summary>
        private static string ReadTextOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Never throws: a missing/unreadable/malformed file reads as JsonNode.Null, so callers can index straight into it.</summary>
        private static JsonNode ReadJsonOrNull(string path)
        {
            string text = ReadTextOrNull(path);
            if (text == null)
            {
                return JsonNode.Null;
            }
            JsonNode node;
            string error;
            return JsonParser.TryParse(text, out node, out error) ? node : JsonNode.Null;
        }
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One tracked package's resolved presence/version, and the id under
    /// which it was actually found (see <see cref="UapProjectInfoPolicy.
    /// TryResolvePackage"/> -- uLoop's own rename means the id that
    /// resolves is not always the id that was asked for).
    /// </summary>
    public struct UapPackageResolution
    {
        public string RequestedId;
        public bool Present;
        public string MatchedId;
        public string Version;
    }

    /// <summary>
    /// Unity-free half of uap_project_info (docs/design-notes/2026-09-22-
    /// uloop-phase1.md section 4): parses the two things the tool needs
    /// that have no public UnityEditor API, and turns raw facts into the
    /// "why does this matter to another uap_* tool" lines the brief asked
    /// for. UapProjectInfoTool supplies everything else (Application.
    /// unityVersion, PlayerSettings, EditorSettings) straight from the
    /// live Editor, because those DO have a public getter and re-deriving
    /// them from ProjectSettings.asset text would just be a second, more
    /// fragile copy of what the Editor already knows.
    /// </summary>
    public static class UapProjectInfoPolicy
    {
        public const int InputHandlerOld = 0;
        public const int InputHandlerNew = 1;
        public const int InputHandlerBoth = 2;

        // "  activeInputHandler: 1" inside ProjectSettings/ProjectSettings.asset's
        // YAML (PlayerSettings block, two-space indent, verified against a real
        // project export -- see the design note). PlayerSettings has no public
        // getter for this field: the Player Settings window writes/reads it
        // through a native-only path, so a text scan is the only route from
        // Editor script code short of reading it in a completely different
        // project's Library cache. No end-of-line anchor: whatever trails the
        // digits (a CR before an LF, trailing spaces, a same-line comment) is
        // irrelevant to the value, and anchoring to it would just be one more
        // way to fail to match a file this method did not write.
        private static readonly Regex ActiveInputHandlerLine =
            new Regex(@"^[ \t]*activeInputHandler:[ \t]*(-?\d+)", RegexOptions.Multiline);

        /// <summary>
        /// Parses Active Input Handling out of ProjectSettings.asset's raw
        /// text. Absent (a project saved before this field existed, or the
        /// file could not be read) reads as <see cref="InputHandlerOld"/>,
        /// Unity's own default and the value a fresh project has before the
        /// line is ever written.
        /// </summary>
        public static int ParseActiveInputHandler(string projectSettingsAssetText)
        {
            if (string.IsNullOrEmpty(projectSettingsAssetText))
            {
                return InputHandlerOld;
            }
            Match match = ActiveInputHandlerLine.Match(projectSettingsAssetText);
            if (!match.Success)
            {
                return InputHandlerOld;
            }
            int value;
            return int.TryParse(match.Groups[1].Value, out value) ? value : InputHandlerOld;
        }

        /// <summary>Human label for the raw activeInputHandler value; an out-of-range value (a future 4th mode) is reported rather than misclassified.</summary>
        public static string DescribeInputHandling(int activeInputHandler)
        {
            switch (activeInputHandler)
            {
                case InputHandlerOld: return "Input Manager (Old)";
                case InputHandlerNew: return "Input System Package (New)";
                case InputHandlerBoth: return "Both";
                default: return "Unknown (" + activeInputHandler + ")";
            }
        }

        /// <summary>True when a Keyboard/Mouse device CAN exist -- New or Both. Old-only means uap_game_key/uap_game_mouse have nothing to inject into even if com.unity.inputsystem is installed.</summary>
        public static bool InputSystemDevicesAvailable(int activeInputHandler)
        {
            return activeInputHandler == InputHandlerNew || activeInputHandler == InputHandlerBoth;
        }

        /// <summary>
        /// One tracked package: the id this tool reports it as, and every
        /// id that should count as "present" under that name. Only
        /// io.github.hatayama.uloopmcp needs more than one candidate (its
        /// unity-cli-loop rename, same list UloopDetector/UloopUninstaller
        /// use so a renamed install is never reported as absent).
        /// </summary>
        public struct TrackedPackage
        {
            public string ReportedId;
            public string[] CandidateIds;

            public TrackedPackage(string reportedId, params string[] candidateIds)
            {
                ReportedId = reportedId;
                CandidateIds = candidateIds;
            }
        }

        /// <summary>Packages whose presence/absence changes how another uap_* tool behaves, in report order.</summary>
        public static readonly TrackedPackage[] TrackedPackages =
        {
            new TrackedPackage("com.unity.ugui", "com.unity.ugui"),
            new TrackedPackage("com.unity.inputsystem", "com.unity.inputsystem"),
            new TrackedPackage("com.unity.test-framework", "com.unity.test-framework"),
            new TrackedPackage("com.unity.textmeshpro", "com.unity.textmeshpro"),
            // Same two ids as UloopDetector.KnownPackageIdList() (design
            // section 8.4 of the roadmap note this ships under); duplicated
            // rather than referenced so this Policy stays a single pure
            // file with no dependency the smoke-test project would also
            // need to add.
            new TrackedPackage("io.github.hatayama.uloopmcp",
                "io.github.hatayama.uloopmcp", "io.github.hatayama.unitycliloop"),
            new TrackedPackage("jp.colloid.agent-panel-pro", "jp.colloid.agent-panel-pro"),
        };

        /// <summary>
        /// Resolves one tracked package from the parsed manifest.json /
        /// packages-lock.json "dependencies" objects (either may be
        /// JsonNode.Null -- a missing/unreadable file -- callers pass it
        /// rather than skip the call so this stays a pure function of its
        /// inputs). The lock file's RESOLVED version wins over the
        /// manifest's declared one, because the manifest's may be a
        /// version RANGE, a "file:" path or a git URL; every candidate id
        /// is checked against the lock file before any candidate is
        /// checked against the manifest, so a resolved version is always
        /// preferred over an unresolved declaration under a different
        /// candidate id.
        /// </summary>
        public static UapPackageResolution TryResolvePackage(JsonNode manifestDependencies,
            JsonNode lockDependencies, TrackedPackage package)
        {
            var result = new UapPackageResolution { RequestedId = package.ReportedId };
            for (int i = 0; i < package.CandidateIds.Length; i++)
            {
                string id = package.CandidateIds[i];
                JsonNode lockEntry = lockDependencies[id];
                if (lockEntry.IsObject && lockEntry.HasKey("version") && !lockEntry["version"].IsNull)
                {
                    result.Present = true;
                    result.MatchedId = id;
                    result.Version = lockEntry["version"].AsString(string.Empty);
                    return result;
                }
            }
            for (int i = 0; i < package.CandidateIds.Length; i++)
            {
                string id = package.CandidateIds[i];
                JsonNode manifestEntry = manifestDependencies[id];
                if (manifestEntry.IsString)
                {
                    result.Present = true;
                    result.MatchedId = id;
                    result.Version = manifestEntry.AsString(string.Empty);
                    return result;
                }
            }
            return result;
        }

        /// <summary>
        /// One line per flag that actually limits an existing uap_* tool
        /// right now -- not every tracked package/setting gets a line, only
        /// the ones the brief called out as a precondition for another
        /// tool. Empty when nothing here is a blocker.
        /// </summary>
        public static List<string> BuildImplications(int activeInputHandler, bool inputSystemPresent,
            bool uguiPresent, bool runInBackground, bool domainReloadDisabled)
        {
            var lines = new List<string>();
            if (!inputSystemPresent)
            {
                lines.Add("com.unity.inputsystem is not installed: uap_game_key and uap_game_mouse cannot"
                    + " run (no Keyboard.current/Mouse.current device to inject into).");
            }
            else if (!InputSystemDevicesAvailable(activeInputHandler))
            {
                lines.Add("Active Input Handling is '" + DescribeInputHandling(activeInputHandler)
                    + "': uap_game_key and uap_game_mouse cannot run until Project Settings > Player >"
                    + " Active Input Handling is 'Input System Package (New)' or 'Both'.");
            }
            if (!uguiPresent)
            {
                lines.Add("com.unity.ugui is not installed: uap_game_pointer cannot dispatch UI events"
                    + " (no EventSystem/Canvas pipeline in this project).");
            }
            if (!runInBackground)
            {
                lines.Add("Run In Background is off: the running game stops receiving input the instant"
                    + " the Editor loses OS focus, which uap_game_key/uap_game_pointer/uap_game_mouse rely"
                    + " on -- call uap_editor_focus first, or enable Run In Background in Player Settings.");
            }
            if (domainReloadDisabled)
            {
                lines.Add("Enter Play Mode Settings has domain reload disabled: static state survives"
                    + " across a Play Mode session, so uap_play_mode start/stop resets the scene but not"
                    + " every script's static fields the way a full domain reload would.");
            }
            return lines;
        }
    }
}

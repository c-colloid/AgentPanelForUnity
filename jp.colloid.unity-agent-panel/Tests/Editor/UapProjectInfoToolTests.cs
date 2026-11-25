using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_project_info (docs/design-notes/2026-09-22-uloop-phase1.md
    /// section 4).
    ///
    /// The cases below cover the pure parsing/resolution rules (also
    /// pinned in ci/SmokeTests) and that Execute() itself answers cleanly
    /// in a batch-mode CI project -- every field this tool reports comes
    /// from a getter or a read-only file read, so unlike uap_game_key/
    /// uap_game_view_size there is no Play-Mode-only or package-only path
    /// to skip here.
    /// </summary>
    [TestFixture]
    public class UapProjectInfoToolTests
    {
        [Test]
        public void ParseActiveInputHandler_ReadsTheYamlLine()
        {
            Assert.AreEqual(0, UapProjectInfoPolicy.ParseActiveInputHandler(
                "PlayerSettings:\n  productName: X\n  activeInputHandler: 0\n  bundleVersion: 1\n"));
            Assert.AreEqual(1, UapProjectInfoPolicy.ParseActiveInputHandler(
                "PlayerSettings:\n  activeInputHandler: 1\n"));
            Assert.AreEqual(2, UapProjectInfoPolicy.ParseActiveInputHandler(
                "PlayerSettings:\n  activeInputHandler: 2\n"));
        }

        [Test]
        public void ParseActiveInputHandler_ToleratesRealWorldFormatting()
        {
            // Tabs, trailing spaces, CRLF line endings, and the field
            // appearing among many unrelated sibling keys.
            Assert.AreEqual(1, UapProjectInfoPolicy.ParseActiveInputHandler(
                "PlayerSettings:\r\n\tcaptureStartupLogs: {}\r\n\tactiveInputHandler: 1 \r\n\twindowsGamepadBackendHint: 0\r\n"));
        }

        [Test]
        public void ParseActiveInputHandler_MissingOrEmptyReadsAsOld()
        {
            Assert.AreEqual(UapProjectInfoPolicy.InputHandlerOld,
                UapProjectInfoPolicy.ParseActiveInputHandler(null));
            Assert.AreEqual(UapProjectInfoPolicy.InputHandlerOld,
                UapProjectInfoPolicy.ParseActiveInputHandler(string.Empty));
            Assert.AreEqual(UapProjectInfoPolicy.InputHandlerOld,
                UapProjectInfoPolicy.ParseActiveInputHandler("PlayerSettings:\n  productName: X\n"));
        }

        [Test]
        public void ParseActiveInputHandler_DoesNotMatchAnUnrelatedKeyWithTheSameSuffix()
        {
            // A key that merely ENDS in "activeInputHandler" (or embeds it
            // mid-word) must not be mistaken for the real field.
            Assert.AreEqual(UapProjectInfoPolicy.InputHandlerOld,
                UapProjectInfoPolicy.ParseActiveInputHandler("  someOtheractiveInputHandler: 2\n"));
        }

        [Test]
        public void DescribeInputHandling_NamesAllThreeModes_AndAnUnknownValue()
        {
            Assert.AreEqual("Input Manager (Old)", UapProjectInfoPolicy.DescribeInputHandling(0));
            Assert.AreEqual("Input System Package (New)", UapProjectInfoPolicy.DescribeInputHandling(1));
            Assert.AreEqual("Both", UapProjectInfoPolicy.DescribeInputHandling(2));
            StringAssert.Contains("Unknown", UapProjectInfoPolicy.DescribeInputHandling(9));
            StringAssert.Contains("9", UapProjectInfoPolicy.DescribeInputHandling(9));
        }

        [Test]
        public void InputSystemDevicesAvailable_OnlyNewAndBoth()
        {
            Assert.IsFalse(UapProjectInfoPolicy.InputSystemDevicesAvailable(0));
            Assert.IsTrue(UapProjectInfoPolicy.InputSystemDevicesAvailable(1));
            Assert.IsTrue(UapProjectInfoPolicy.InputSystemDevicesAvailable(2));
        }

        private static UapProjectInfoPolicy.TrackedPackage Single(string id)
        {
            return new UapProjectInfoPolicy.TrackedPackage(id, id);
        }

        [Test]
        public void TryResolvePackage_LockFileVersionWinsOverManifestRange()
        {
            JsonNode manifest = JsonNode.NewObject().Set("com.unity.ugui", "1.0.0");
            JsonNode lockDeps = JsonNode.NewObject().Set("com.unity.ugui",
                JsonNode.NewObject().Set("version", "1.0.0").Set("depth", 0));
            UapPackageResolution resolved = UapProjectInfoPolicy.TryResolvePackage(manifest, lockDeps, Single("com.unity.ugui"));
            Assert.IsTrue(resolved.Present);
            Assert.AreEqual("com.unity.ugui", resolved.MatchedId);
            Assert.AreEqual("1.0.0", resolved.Version);
        }

        [Test]
        public void TryResolvePackage_FallsBackToManifestWhenThereIsNoLockEntry()
        {
            JsonNode manifest = JsonNode.NewObject().Set("com.unity.textmeshpro", "3.0.6");
            UapPackageResolution resolved = UapProjectInfoPolicy.TryResolvePackage(
                manifest, JsonNode.Null, Single("com.unity.textmeshpro"));
            Assert.IsTrue(resolved.Present);
            Assert.AreEqual("3.0.6", resolved.Version);
        }

        [Test]
        public void TryResolvePackage_AbsentFromBothFilesReadsAsNotPresent()
        {
            UapPackageResolution resolved = UapProjectInfoPolicy.TryResolvePackage(
                JsonNode.Null, JsonNode.Null, Single("com.unity.inputsystem"));
            Assert.IsFalse(resolved.Present);
            Assert.IsNull(resolved.MatchedId);
        }

        [Test]
        public void TryResolvePackage_UloopRename_ResolvesUnderEitherCandidateId()
        {
            var package = new UapProjectInfoPolicy.TrackedPackage("io.github.hatayama.uloopmcp",
                "io.github.hatayama.uloopmcp", "io.github.hatayama.unitycliloop");

            JsonNode renamedLock = JsonNode.NewObject().Set("io.github.hatayama.unitycliloop",
                JsonNode.NewObject().Set("version", "3.6.3"));
            UapPackageResolution resolved = UapProjectInfoPolicy.TryResolvePackage(JsonNode.Null, renamedLock, package);
            Assert.IsTrue(resolved.Present);
            Assert.AreEqual("io.github.hatayama.uloopmcp", resolved.RequestedId);
            Assert.AreEqual("io.github.hatayama.unitycliloop", resolved.MatchedId);
            Assert.AreEqual("3.6.3", resolved.Version);
        }

        [Test]
        public void TrackedPackages_HaveNoDuplicateReportedIds_AndCoverTheBriefsList()
        {
            var seen = new HashSet<string>();
            foreach (UapProjectInfoPolicy.TrackedPackage tracked in UapProjectInfoPolicy.TrackedPackages)
            {
                Assert.IsTrue(seen.Add(tracked.ReportedId), "duplicate reported id: " + tracked.ReportedId);
            }
            CollectionAssert.Contains(seen, "com.unity.ugui");
            CollectionAssert.Contains(seen, "com.unity.inputsystem");
            CollectionAssert.Contains(seen, "com.unity.test-framework");
            CollectionAssert.Contains(seen, "com.unity.textmeshpro");
            CollectionAssert.Contains(seen, "io.github.hatayama.uloopmcp");
            CollectionAssert.Contains(seen, "jp.colloid.agent-panel-pro");
        }

        [Test]
        public void BuildImplications_EmptyWhenNothingLimitsAnything()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerBoth, inputSystemPresent: true, uguiPresent: true,
                runInBackground: true, domainReloadDisabled: false);
            Assert.IsEmpty(lines);
        }

        [Test]
        public void BuildImplications_MissingInputSystemPackage_NamesBothInputTools()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerOld, inputSystemPresent: false, uguiPresent: true,
                runInBackground: true, domainReloadDisabled: false);
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains("uap_game_key", lines[0]);
            StringAssert.Contains("uap_game_mouse", lines[0]);
        }

        [Test]
        public void BuildImplications_InputSystemPresentButOldHandler_StillBlocksInputTools()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerOld, inputSystemPresent: true, uguiPresent: true,
                runInBackground: true, domainReloadDisabled: false);
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains("Active Input Handling", lines[0]);
        }

        [Test]
        public void BuildImplications_MissingUgui_NamesGamePointer()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerBoth, inputSystemPresent: true, uguiPresent: false,
                runInBackground: true, domainReloadDisabled: false);
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains("uap_game_pointer", lines[0]);
        }

        [Test]
        public void BuildImplications_RunInBackgroundOff_NamesEditorFocusTool()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerBoth, inputSystemPresent: true, uguiPresent: true,
                runInBackground: false, domainReloadDisabled: false);
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains("uap_editor_focus", lines[0]);
        }

        [Test]
        public void BuildImplications_DomainReloadDisabled_IsMentioned()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerBoth, inputSystemPresent: true, uguiPresent: true,
                runInBackground: true, domainReloadDisabled: true);
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains("domain reload", lines[0]);
        }

        [Test]
        public void BuildImplications_EverythingWrongAtOnce_ReportsEveryLine()
        {
            List<string> lines = UapProjectInfoPolicy.BuildImplications(
                UapProjectInfoPolicy.InputHandlerOld, inputSystemPresent: false, uguiPresent: false,
                runInBackground: false, domainReloadDisabled: true);
            Assert.AreEqual(4, lines.Count);
        }

        // -- Tool metadata + a real Execute() in this batch-mode project --------

        [Test]
        public void Tool_IsReadOnly_NotUndoable_InEditorModule()
        {
            var tool = new UapProjectInfoTool();
            Assert.AreEqual("uap_project_info", tool.Name);
            Assert.AreEqual("editor", tool.Module);
            Assert.IsTrue(tool.ReadOnly, "uap_project_info must be ReadOnly: it never writes anything.");
            Assert.IsFalse(tool.Undoable);
            Assert.IsFalse(string.IsNullOrEmpty(tool.Description));
        }

        [Test]
        public void Tool_InputSchema_TakesNoArguments()
        {
            JsonNode schema = new UapProjectInfoTool().InputSchema;
            Assert.AreEqual("object", schema["type"].AsString());
            Assert.IsTrue(schema["properties"].IsObject);
            Assert.AreEqual(0, schema["properties"].Count);
        }

        [Test]
        public void Execute_ReportsEveryTrackedPackage_AndNeverThrows()
        {
            string text = new UapProjectInfoTool().Execute(JsonNode.NewObject())[0]["text"].AsString();
            JsonNode reply = JsonParser.Parse(text);

            Assert.IsFalse(string.IsNullOrEmpty(reply["unityVersion"].AsString()));
            Assert.IsTrue(reply["activeInputHandlingRaw"].AsInt(-1) >= 0);
            Assert.IsTrue(reply["enterPlayModeSettings"]["enabled"].IsBool);
            Assert.IsTrue(reply["runInBackground"].IsBool);
            Assert.IsFalse(string.IsNullOrEmpty(reply["colorSpace"].AsString()));
            Assert.AreEqual(UapProjectInfoPolicy.TrackedPackages.Length, reply["packages"].Count);
            Assert.IsTrue(reply["implications"].IsArray);
            Assert.IsFalse(string.IsNullOrEmpty(reply["message"].AsString()));

            foreach (JsonNode entry in reply["packages"].Items)
            {
                Assert.IsFalse(string.IsNullOrEmpty(entry["id"].AsString()), text);
                Assert.IsTrue(entry["present"].IsBool, text);
            }
        }
    }
}

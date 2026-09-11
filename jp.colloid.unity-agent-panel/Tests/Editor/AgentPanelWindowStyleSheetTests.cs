using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guard for the Settings/History/markdown-table
    /// layout-freeze defect (see
    /// docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md
    /// and docs/design-notes/2026-07-31-scroll-container-shrink-fix.md).
    /// The proven root cause was `root.styleSheets.Clear()` in
    /// AgentPanelWindow.CreateGUI wholesale-removing every stylesheet on the
    /// window root, including Unity's OWN implicitly attached editor
    /// default stylesheet (the sole source of
    /// `.unity-scroll-view__content-container { flex-shrink: 0; }` on a
    /// real EditorWindow) -- not just the package's own 4 sheets.
    ///
    /// These tests exercise the exact mechanism that regressed -- what
    /// CreateGUI does and does not remove from `root.styleSheets` -- fully
    /// headlessly, via `ScriptableObject.CreateInstance` + `CreateGUI()`
    /// (the same pattern AgentPanelWindowTests already uses: no
    /// GetWindow/Show, no window-manager side effects, no docking).
    ///
    /// A plain in-memory `StyleSheet` instance stands in for Unity's
    /// implicit default sheet: `CreateInstance`-only construction never
    /// attaches this root to a real panel (`rootVisualElement.panel` stays
    /// null -- see docs/design-notes/2026-07-31-font-scale-cascade-fix.md,
    /// which documents this exact limitation and why the checked-in suite
    /// never uses GetWindow/Show), so no style RESOLUTION ever runs against
    /// the sentinel sheet's (empty) contents -- only set membership
    /// (Add/Remove/Contains) is exercised, which is also all CreateGUI's
    /// teardown logic itself does. What is verified here is exactly the
    /// bug's mechanism (does CreateGUI remove a sheet it did not add
    /// itself?); actual Yoga flex-shrink/layout-crush behavior requires a
    /// real attached panel and is out of reach for a headless EditMode test
    /// under this codebase's established testing conventions -- see the
    /// design note's "Verification" section for how that was confirmed
    /// live instead.
    /// </summary>
    [TestFixture]
    public class AgentPanelWindowStyleSheetTests
    {
        /// <summary>
        /// 2026-09-08: CreateGUI applies the persisted language setting
        /// (Auto resolves to the OS language); restore the suite default
        /// after every test so later fixtures see English -- see the
        /// regression note on SceneMarkerPinTests.
        /// </summary>
        [TearDown]
        public void RestoreLanguage()
        {
            UI.L10n.OverrideForTests(null);
        }

        [Test]
        public void CreateGUI_DoesNotRemoveAForeignStyleSheet_ItDidNotAddItself()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            var foreignSheet = ScriptableObject.CreateInstance<StyleSheet>();
            try
            {
                window.rootVisualElement.styleSheets.Add(foreignSheet);

                window.CreateGUI();

                Assert.IsTrue(window.rootVisualElement.styleSheets.Contains(foreignSheet),
                    "CreateGUI must never remove a stylesheet it did not add itself -- on a "
                    + "real EditorWindow this stands in for Unity's own implicitly attached "
                    + "editor default stylesheet (DefaultCommonDark/Light_inter.uss), the "
                    + "sole source of '.unity-scroll-view__content-container "
                    + "{ flex-shrink: 0; }'. Losing it is the proven root cause of the "
                    + "Settings/History/markdown-table layout-freeze defect: every "
                    + "ScrollView content-container falls back to flex-shrink:1 and gets "
                    + "crushed to viewport size instead of scrolling.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(foreignSheet);
            }
        }

        [Test]
        public void CreateGUI_ReentrantCall_LeavesTheForeignStyleSheetUntouched()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            var foreignSheet = ScriptableObject.CreateInstance<StyleSheet>();
            try
            {
                window.rootVisualElement.styleSheets.Add(foreignSheet);

                window.CreateGUI();
                // Re-entrant rebuild on the same instance -- the documented
                // scenario CreateGUI's `_built` guard exists to handle.
                window.CreateGUI();

                Assert.IsTrue(window.rootVisualElement.styleSheets.Contains(foreignSheet),
                    "A re-entrant CreateGUI must still leave a foreign stylesheet "
                    + "untouched on the second teardown/rebuild pass, not just the first.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(foreignSheet);
            }
        }

        [Test]
        public void CreateGUI_ReentrantCall_DoesNotDuplicateThePackageStyleSheets()
        {
            var window = ScriptableObject.CreateInstance<UI.AgentPanelWindow>();
            try
            {
                window.CreateGUI();
                int countAfterFirst = window.rootVisualElement.styleSheets.count;
                Assert.Greater(countAfterFirst, 0,
                    "First CreateGUI must have loaded the package stylesheets.");

                window.CreateGUI();
                int countAfterSecond = window.rootVisualElement.styleSheets.count;

                Assert.AreEqual(countAfterFirst, countAfterSecond,
                    "A re-entrant CreateGUI must remove its own previously-added package "
                    + "stylesheets before re-adding them, never accumulate duplicate "
                    + "entries for the same 4 assets.");
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        /// <summary>
        /// Source-level guard: bans the specific call that caused the
        /// defect from ever reappearing as ACTUAL CODE in this file, even if
        /// a future edit re-touches the teardown logic without noticing why
        /// it was changed. Mirrors the SourceScan_* pattern already
        /// established in UssHygieneTests.cs.
        ///
        /// Scans code only, with `//`/`///` line comments stripped per line
        /// first (this file has no `/* */` block comments -- see
        /// AgentPanelWindow.cs's own doc comments right above both the call
        /// site and RemovePackageStyleSheets, which deliberately spell out
        /// "NOT root.styleSheets.Clear()" / "Deliberately NOT
        /// `root.styleSheets.Clear()`" as part of explaining the fix; a
        /// raw whole-file Contains check would permanently fail against
        /// that legitimate, load-bearing documentation).
        /// </summary>
        [Test]
        public void SourceScan_AgentPanelWindow_DoesNotCallStyleSheetsClear()
        {
            string path = Path.GetFullPath(
                "Packages/jp.colloid.unity-agent-panel/Editor/UI/AgentPanelWindow.cs");
            Assert.IsTrue(File.Exists(path), "AgentPanelWindow.cs not found: " + path);
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int commentIndex = line.IndexOf("//", System.StringComparison.Ordinal);
                string code = commentIndex >= 0 ? line.Substring(0, commentIndex) : line;

                Assert.IsFalse(code.Contains("styleSheets.Clear()"),
                    "AgentPanelWindow must never call root.styleSheets.Clear() as CODE "
                    + "(line " + (i + 1) + ") -- on an EditorWindow root that "
                    + "wholesale-removes Unity's own implicitly attached editor default "
                    + "stylesheet along with the package's own sheets, which is the proven "
                    + "root cause of the Settings/History/markdown-table layout-freeze "
                    + "defect (see "
                    + "docs/design-notes/2026-07-31-settings-layout-freeze-investigation.md). "
                    + "Use RemovePackageStyleSheets (targeted removal of only the sheets this "
                    + "window added) instead.");
            }
        }
    }
}

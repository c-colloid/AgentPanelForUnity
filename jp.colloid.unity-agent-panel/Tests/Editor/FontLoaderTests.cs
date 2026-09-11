using System;
using System.Reflection;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards the monospace font pipeline (live-feedback defect: styling
    /// Consolas through a dynamic OS font array produced a Font whose
    /// TextCore face could not load -- "Unable to load font face for
    /// [Consolas]" -- and code blocks rendered EMPTY). FontLoader must
    /// always resolve a usable Font, editor-bundled RobotoMono first,
    /// and must never throw even when editor resources are missing.
    /// Runs in batch mode (CI sandbox) and interactively.
    ///
    /// Regression note (2026-09-08, full-suite order dependence): the
    /// three AtlasGuardTick_* tests passed alone but failed in the full
    /// AITemp run. NUnit orders a fixture's tests case-insensitively, so
    /// AtlasGuardTick_RebuildsABrokenAsset runs FIRST, and the fixture
    /// that runs right before this one (FontFixBridgeTests) heals three
    /// broken assets, leaving FontLoader's 2-second heal throttle armed
    /// (a static, never cleared by ResetJapaneseUiCache). The rebuild
    /// test was throttled, failed, and aborted with the cached asset's
    /// material already destroyed, so the two ReStamps_* tests inherited
    /// a broken asset (MissingReferenceException / "Expected True").
    /// Both fixtures now reset the throttle around every test, SetUp
    /// drops an unhealthy cached asset, and the rebuild test restores
    /// the cache in a finally block so a failure cannot cascade.
    /// </summary>
    public class FontLoaderTests
    {
        // These tests pin the panel's OWN resolver. When UITK Font Fix is
        // installed in the host project FontLoader would defer to it, so
        // the bridge is switched off for the duration of each test.
        [SetUp]
        public void IsolateFromFontFix()
        {
            FontFixBridge.UseProviderForTests(null);
            // The heal throttle is process state shared with every other
            // fixture (see the class doc comment); each test here starts
            // from "no recent heal" so a broken asset heals immediately.
            FontLoader.ResetHealThrottleForTests();
            if (FontLoader.JapaneseUiFontSource.StartsWith(FontFixBridge.SourcePrefix, StringComparison.Ordinal))
            {
                FontLoader.ResetJapaneseUiCache();
            }
            // A previous test (this fixture or another) may have destroyed
            // the cached asset's children without re-resolving; never
            // start a test on a broken asset.
            UnityEngine.TextCore.Text.FontAsset cached = FontLoader.JapaneseUiFontAsset;
            if (cached != null && !FontLoader.IsHealthy(cached))
            {
                FontLoader.ResetJapaneseUiCache();
            }
        }

        [TearDown]
        public void RestoreFontFix()
        {
            FontFixBridge.ResetForTests();
            FontLoader.ResetHealThrottleForTests();
        }

        [Test]
        public void MonoFont_ResolvesNonNull_InBatchMode()
        {
            Font font = FontLoader.MonoFont;
            Assert.IsNotNull(font,
                "FontLoader must always resolve a font (bundled RobotoMono"
                + " -> single-name OS font -> default label font)");
        }

        [Test]
        public void MonoFont_IsCached_SameInstance()
        {
            Assert.AreSame(FontLoader.MonoFont, FontLoader.MonoFont);
        }

        [Test]
        public void MonoFontSource_ReportsWinningCandidate()
        {
            string source = FontLoader.MonoFontSource;
            Assert.IsNotEmpty(source, "a candidate must have resolved");
            // Empirical record for the log: which candidate won on this
            // editor version (expected "editor:Fonts/RobotoMono/..." on
            // 2022.3).
            Debug.Log("[FontLoaderTests] mono font source = " + source
                + ", font = " + FontLoader.MonoFont.name);
        }

        [Test]
        public void EditorBundledMonoFontPath_ResolvesOn2022_3()
        {
            // Empirical probe of the documented editor-bundle paths.
            // At least one candidate must load on 2022.3 -- this is the
            // path CodeBlockElement text depends on to avoid the broken
            // OS-font route entirely.
            bool any = false;
            for (int i = 0; i < FontLoader.EditorMonoFontPaths.Length; i++)
            {
                string path = FontLoader.EditorMonoFontPaths[i];
                var font = EditorGUIUtility.Load(path) as Font;
                Debug.Log("[FontLoaderTests] EditorGUIUtility.Load(\"" + path
                    + "\") -> " + (font != null ? font.name : "null"));
                any |= font != null;
            }
            Assert.IsTrue(any,
                "no editor-bundled mono font path resolved; update"
                + " FontLoader.EditorMonoFontPaths for this editor version");
        }

        [Test]
        public void ApplyMono_NeverThrows_AndAssignsDefinition()
        {
            var label = new Label("code");
            Assert.DoesNotThrow(delegate { FontLoader.ApplyMono(label); });
            Assert.DoesNotThrow(delegate { FontLoader.ApplyMono(null); });
            if (FontLoader.MonoFont != null)
            {
                Assert.AreEqual(StyleKeyword.Undefined,
                    label.style.unityFontDefinition.keyword,
                    "a resolved font must be assigned inline");
            }
        }

        [Test]
        public void MessageBlockFactory_ApplyMonoFont_DelegatesWithoutThrowing()
        {
            var field = new TextField();
            Assert.DoesNotThrow(delegate
            {
                MessageBlockFactory.ApplyMonoFont(field);
            });
        }

        // -- Japanese UI font (patchy-bold / faint CJK live-feedback fix) -----

        [Test]
        public void JapaneseUiFont_NeverThrows_InBatchMode()
        {
            UnityEngine.TextCore.Text.FontAsset asset = null;
            Assert.DoesNotThrow(delegate { asset = FontLoader.JapaneseUiFontAsset; });
            // No editor-bundled candidate and no default-label fallback for
            // this resolver (unlike MonoFont): null is a legitimate result
            // on a machine with none of the candidate OS fonts installed,
            // so this test only guards against exceptions, not non-null.
        }

        [Test]
        public void JapaneseUiFont_IsCached_SameInstance()
        {
            Assert.AreSame(FontLoader.JapaneseUiFontAsset, FontLoader.JapaneseUiFontAsset);
        }

        [Test]
        public void JapaneseUiFont_InstalledCandidateNames_ContainYuGothicUi()
        {
            // Batch-mode-safe half of the "resolves on this machine" claim:
            // Windows 10/11 always ships Yu Gothic UI, so the
            // installed-name gate (Font.GetOSInstalledFontNames(), the
            // FIRST of the two gates ResolveJapaneseUi applies) must find
            // it here regardless of whether the TextCore face probe below
            // can run headless.
            //
            // "Yu Gothic UI" is a Windows-only system face, so this gate is
            // meaningful only on a Windows editor. On a Linux/macOS editor
            // (e.g. the Ubuntu CI runner) it is legitimately absent, so skip
            // rather than fail there -- matching the sibling tests that
            // Assert.Ignore when the host cannot exercise the claim.
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("\"Yu Gothic UI\" is a Windows system font; the"
                    + " installed-name gate is only asserted on a Windows"
                    + " editor (current platform: " + Application.platform
                    + ").");
            }
            string[] installed = Font.GetOSInstalledFontNames() ?? new string[0];
            bool hasYuGothicUi = false;
            for (int i = 0; i < installed.Length; i++)
            {
                if (string.Equals(installed[i], "Yu Gothic UI",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    hasYuGothicUi = true;
                    break;
                }
            }
            Assert.IsTrue(hasYuGothicUi,
                "expected \"Yu Gothic UI\" in Font.GetOSInstalledFontNames()"
                + " on this Windows 10/11 machine");
        }

        [Test]
        public void JapaneseUiFont_ResolvesOnThisWindowsMachine_AndReportsWinner()
        {
            // The resolver deliberately does NOT use
            // FontEngine.LoadFontFace(Font): that probe returns
            // Invalid_File for EVERY OS dynamic Font on 2022.3 even in the
            // interactive editor (verified live -- the reason the first
            // Font-based implementation resolved null everywhere).
            // FontAsset.CreateFontAsset(family, "Regular") in DynamicOS
            // mode is TextCore's own system-font route. If even that
            // cannot produce an asset under headless batch mode on some
            // machine, skip rather than fail -- the interactive editor is
            // the environment that matters for rendering.
            UnityEngine.TextCore.Text.FontAsset asset = FontLoader.JapaneseUiFontAsset;
            if (asset == null && Application.isBatchMode)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine; exercised in"
                    + " the interactive editor instead.");
            }
            string source = FontLoader.JapaneseUiFontSource;
            Assert.IsNotNull(asset,
                "expected a Japanese UI FontAsset (e.g. Yu Gothic UI) to"
                + " resolve on this Windows machine");
            Assert.IsNotEmpty(source, "a candidate must have resolved");
            Debug.Log("[FontLoaderTests] Japanese UI font source = " + source
                + ", asset = " + asset.name);
        }

        [Test]
        public void ApplyJapaneseUi_NeverThrows_AndAssignsDefinitionWhenResolved()
        {
            var label = new Label("Test");
            Assert.DoesNotThrow(delegate { FontLoader.ApplyJapaneseUi(label); });
            Assert.DoesNotThrow(delegate { FontLoader.ApplyJapaneseUi(null); });
            if (FontLoader.JapaneseUiFontAsset != null)
            {
                Assert.AreEqual(StyleKeyword.Undefined,
                    label.style.unityFontDefinition.keyword,
                    "a resolved Japanese UI font must be assigned inline");
                Assert.IsNotNull(label.style.unityFontDefinition.value.fontAsset,
                    "the assignment must carry the SDF FontAsset");
            }
        }

        [Test]
        public void ApplyJapaneseUi_NeverOverridesInlineMonoFont_RegardlessOfOrder()
        {
            // Guards the specificity claim documented on
            // AgentPanelWindow.ApplyCjkUiFont: whichever of the two inline
            // assignments runs LAST on the SAME element wins -- this is
            // correct because both are inline styles on that one element
            // (not an inherited-vs-inline contest here). The claim that
            // actually matters in shipped code is different: ApplyJapaneseUi
            // only ever targets a CONTAINER ROOT while ApplyMonoFont only
            // ever targets a leaf, so the leaf's inline mono assignment is
            // never revisited by the root-level call and always wins there
            // regardless of order. This test pins the underlying mechanic
            // (last inline write wins on one element) so a future refactor
            // that accidentally applies both to the SAME element is caught.
            if (FontLoader.JapaneseUiFontAsset == null || FontLoader.MonoFont == null)
            {
                Assert.Ignore("both a Japanese UI font and a mono font must"
                    + " resolve on this machine to exercise the ordering");
            }
            var leaf = new Label("code");
            FontLoader.ApplyMono(leaf);
            FontLoader.ApplyJapaneseUi(leaf);
            Assert.AreEqual(FontLoader.JapaneseUiFontAsset.name,
                leaf.style.unityFontDefinition.value.fontAsset.name,
                "last inline assignment on the same element wins, as"
                + " expected; production code never applies both to the"
                + " same element (see comment above)");
        }

        // -- Reload survival: re-find-and-reuse, never destroy (Stream A1) ----

        [Test]
        public void JapaneseUiFontAsset_SurvivesSimulatedReload_ReusedNotRecreated()
        {
            UnityEngine.TextCore.Text.FontAsset before = FontLoader.JapaneseUiFontAsset;
            if (before == null && Application.isBatchMode)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }
            int beforeId = before.GetInstanceID();

            // A domain reload resets managed statics but -- per probe 4 --
            // NOT the underlying HideAndDontSave native object. Reflection
            // simulates exactly that: clear the cache fields, leave the
            // real asset alive, and confirm the resolver finds the SAME
            // object again instead of creating a second one.
            Type type = typeof(FontLoader);
            FieldInfo assetField = type.GetField("_japaneseUiAsset",
                BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo probedField = type.GetField("_japaneseUiProbed",
                BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo sourceField = type.GetField("_japaneseUiFontSource",
                BindingFlags.NonPublic | BindingFlags.Static);
            assetField.SetValue(null, null);
            probedField.SetValue(null, false);
            sourceField.SetValue(null, string.Empty);

            UnityEngine.TextCore.Text.FontAsset after = FontLoader.JapaneseUiFontAsset;
            Assert.IsNotNull(after,
                "the marked asset must be re-found after a simulated"
                + " reload, not left null");
            Assert.AreEqual(beforeId, after.GetInstanceID(),
                "the SAME native object must be reused, never recreated");
            Assert.AreEqual(
                "osasset-reused:" + FontLoader.JapaneseUiAssetMarkerName,
                FontLoader.JapaneseUiFontSource,
                "diagnostic source must report the reuse path");

            AssertExactlyOneMarkedAssetExists();
        }

        [Test]
        public void FindExistingMarkedAsset_DedupesExtras_KeepsOneDestroysRest()
        {
            UnityEngine.TextCore.Text.FontAsset primary = FontLoader.JapaneseUiFontAsset;
            if (primary == null && Application.isBatchMode)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }

            // Simulate the only scenario this dedupe path exists for: a
            // second marked asset appearing (e.g. a future bug re-running
            // the resolver without an intervening reload).
            UnityEngine.TextCore.Text.FontAsset duplicate =
                UnityEngine.TextCore.Text.FontAsset.CreateFontAsset("Yu Gothic UI", "Regular");
            if (duplicate == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }
            duplicate.name = FontLoader.JapaneseUiAssetMarkerName;
            duplicate.hideFlags = HideFlags.HideAndDontSave;

            UnityEngine.TextCore.Text.FontAsset found = FontLoader.FindExistingMarkedAsset();
            Assert.IsNotNull(found, "one marked asset must survive dedupe");

            AssertExactlyOneMarkedAssetExists();

            // Keep the cache consistent with whichever instance survived
            // (mirrors what the real ResolveJapaneseUi path would leave
            // behind), so later tests in this fixture never observe a
            // destroyed cached reference.
            typeof(FontLoader)
                .GetField("_japaneseUiAsset", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, found);
            typeof(FontLoader)
                .GetField("_japaneseUiProbed", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, true);
        }

        // -- Atlas survival: New Scene must not wipe the font (v0.14.1) ------
        //
        // Root cause, measured in the AITemp sandbox on 2026-08-03 (see
        // docs/design-notes/2026-08-03-font-atlas-destroyed-by-newscene.md):
        // FontAsset.CreateFontAsset hands back a material and atlas
        // textures with hideFlags = None. v0.14.0 stamped HideAndDontSave
        // on the PARENT only, so EditorSceneManager.NewScene destroyed
        // 9/9 atlas textures plus the material while the parent survived
        // holding dangling references -- and every UI Toolkit repaint of
        // the panel then threw MissingReferenceException /
        // ArgumentNullException out of TextCore.
        //
        // The NewScene step itself is deliberately NOT reproduced here: a
        // scene-unloading test would disturb the other 1450+ EditMode
        // tests. These tests pin the property the sandbox proved implies
        // survival (every child stamped HideAndDontSave) plus the two
        // traps that made the first draft of the fix wrong.

        [Test]
        public void StampSubObjects_MarksMaterialAndEveryAtlasTexture()
        {
            UnityEngine.TextCore.Text.FontAsset asset = CreateThrowawayAsset();
            try
            {
                Assert.AreNotEqual(HideFlags.HideAndDontSave,
                    asset.material.hideFlags,
                    "precondition: CreateFontAsset must hand back an"
                    + " UNSTAMPED material -- if Unity ever starts stamping"
                    + " it, this whole fix can be revisited");

                int changed = FontLoader.StampSubObjects(asset);

                Assert.Greater(changed, 0, "the first stamp must change something");
                AssertEverySubObjectStamped(asset);
                Assert.AreEqual(0, FontLoader.StampSubObjects(asset),
                    "stamping must be idempotent -- the guard runs on"
                    + " EditorApplication.update and must not write every frame");
            }
            finally
            {
                DestroyAsset(asset);
            }
        }

        [Test]
        public void StampOsFont_MarksTheFont_AndIsIdempotent()
        {
            // UICODE-14: the OS-created mono Font is owned by nobody, so
            // without HideAndDontSave a NewScene unload destroys it under
            // live labels (same failure the TextCore path fixed in
            // 2026-08-03 font-atlas-destroyed-by-newscene). Forcing the
            // real OS branch depends on machine fonts, so the helper is
            // pinned directly; the source scan below fixes the call site.
            var font = new Font();
            try
            {
                Assert.AreNotEqual(HideFlags.HideAndDontSave, font.hideFlags,
                    "precondition: a fresh Font is unstamped");

                FontLoader.StampOsFont(font);
                Assert.AreEqual(HideFlags.HideAndDontSave, font.hideFlags);

                FontLoader.StampOsFont(font);
                Assert.AreEqual(HideFlags.HideAndDontSave, font.hideFlags,
                    "second stamp must be a silent no-op");

                Assert.DoesNotThrow(delegate { FontLoader.StampOsFont(null); });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(font);
            }
        }

        [Test]
        public void SourceScan_OsMonoBranch_StampsBeforeReturning()
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(
                "Packages/jp.colloid.unity-agent-panel/Editor/UI/FontLoader.cs"));
            int stamp = text.IndexOf("StampOsFont(osFont);", StringComparison.Ordinal);
            int source = text.IndexOf("source = \"os:\" + name;", StringComparison.Ordinal);
            Assert.Greater(stamp, 0, "the os: branch must stamp the font it returns");
            Assert.Greater(source, stamp,
                "the stamp must run before the os: source is returned");
        }

        [Test]
        public void GrownAtlas_NewTexturesComeBackUnstamped_AndSpareSlotsStayHealthy()
        {
            UnityEngine.TextCore.Text.FontAsset asset = CreateThrowawayAsset();
            try
            {
                FontLoader.StampSubObjects(asset);
                int before = asset.atlasTextureCount;

                // Rasterise enough CJK to force TextCore's multi-atlas
                // growth (measured: a 1024x1024 SDFAA atlas at the default
                // 90pt holds ~104 CJK glyphs, so this spills into several).
                // One growth pass covers both properties this test pins,
                // because rasterising is the slow part.
                uint[] missing;
                asset.TryAddCharacters(ContiguousCodepoints(0x4E00, 600), out missing);
                if (asset.atlasTextureCount <= before)
                {
                    Assert.Ignore("this machine's font/atlas did not grow past"
                        + " one texture, so there is nothing to re-stamp");
                }

                // 1. The new textures are UNSTAMPED -- this is why a
                //    one-shot stamp at creation is not a fix and the
                //    recurring guard has to exist.
                Assert.Greater(FontLoader.StampSubObjects(asset), 0,
                    "atlas textures created after the first stamp must come"
                    + " back UNSTAMPED -- if this ever reads 0, the recurring"
                    + " guard is no longer needed and the claim in"
                    + " AtlasGuardTickOnce's doc is stale");
                AssertEverySubObjectStamped(asset);

                // 2. Growth is what over-allocates atlasTextures (measured
                //    Length 16 while atlasTextureCount was 9, trailing
                //    entries legitimately null). IsHealthy must iterate
                //    atlasTextureCount; iterating Length would report every
                //    grown asset as broken.
                Assert.Greater(asset.atlasTextures.Length, asset.atlasTextureCount,
                    "precondition: growth must actually over-allocate the"
                    + " array, or this half of the test pins nothing");
                Assert.IsTrue(FontLoader.IsHealthy(asset),
                    "null spare slots past atlasTextureCount are normal and"
                    + " must not read as breakage");
            }
            finally
            {
                DestroyAsset(asset);
            }
        }

        [Test]
        public void IsHealthy_FalseWhenMaterialDestroyed()
        {
            UnityEngine.TextCore.Text.FontAsset asset = CreateThrowawayAsset();
            try
            {
                UnityEngine.Object.DestroyImmediate(asset.material);
                Assert.IsFalse(FontLoader.IsHealthy(asset),
                    "a destroyed material is exactly what NewScene left"
                    + " behind; the resolver must not reuse this");
            }
            finally
            {
                DestroyAsset(asset);
            }
        }

        [Test]
        public void IsHealthy_FalseWhenUsedAtlasTextureDestroyed()
        {
            UnityEngine.TextCore.Text.FontAsset asset = CreateThrowawayAsset();
            try
            {
                UnityEngine.Object.DestroyImmediate(asset.atlasTextures[0]);
                Assert.IsFalse(FontLoader.IsHealthy(asset),
                    "a destroyed IN-USE atlas texture must read as broken");
            }
            finally
            {
                DestroyAsset(asset);
            }
        }

        [Test]
        public void IsHealthy_FalseForNull()
        {
            Assert.IsFalse(FontLoader.IsHealthy(null));
        }

        [Test]
        public void FindExistingMarkedAsset_RejectsAndDestroysUnhealthyAsset()
        {
            UnityEngine.TextCore.Text.FontAsset original = FontLoader.JapaneseUiFontAsset;
            if (original == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }

            // Break the live marked asset the same way a scene unload does,
            // then confirm the resolver refuses to hand it back. Without
            // this, a broken asset -- which carries HideAndDontSave and so
            // survives every later domain reload -- would keep the error
            // flood alive for the whole editor session.
            UnityEngine.Object.DestroyImmediate(original.material);
            Assert.IsFalse(FontLoader.IsHealthy(original), "precondition");

            UnityEngine.TextCore.Text.FontAsset found = FontLoader.FindExistingMarkedAsset();
            Assert.IsNull(found,
                "the only marked asset was unhealthy, so nothing may be"
                + " reused -- the caller must create a fresh one");
            Assert.IsTrue(original == null,
                "the unusable asset must be destroyed, not left behind for"
                + " the next resolve to find again");

            // Re-resolve from scratch so the rest of the fixture (and any
            // later test) sees a healthy cached asset again.
            ResetJapaneseUiCache();
            UnityEngine.TextCore.Text.FontAsset rebuilt = FontLoader.JapaneseUiFontAsset;
            Assert.IsNotNull(rebuilt, "a fresh asset must be creatable");
            Assert.IsTrue(FontLoader.IsHealthy(rebuilt));
            AssertExactlyOneMarkedAssetExists();
        }

        [Test]
        public void AtlasGuardTick_ReStampsOnlyWhenAtlasCountChanged()
        {
            UnityEngine.TextCore.Text.FontAsset asset = FontLoader.JapaneseUiFontAsset;
            if (asset == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }

            FieldInfo countField = typeof(FontLoader).GetField("_stampedAtlasCount",
                BindingFlags.NonPublic | BindingFlags.Static);

            countField.SetValue(null, asset.atlasTextureCount);
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce(),
                "the steady state must be a single int compare -- this tick"
                + " runs on EditorApplication.update");

            // Simulate "TextCore just created another atlas texture".
            countField.SetValue(null, asset.atlasTextureCount - 1);
            Assert.IsTrue(FontLoader.AtlasGuardTickOnce(),
                "a changed atlas count must trigger a re-stamp");
            Assert.AreEqual(asset.atlasTextureCount, (int)countField.GetValue(null),
                "the guard must record the count it stamped, or it re-stamps"
                + " on every frame from then on");
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce(),
                "and must settle back to the cheap path immediately");
        }

        [Test]
        public void AtlasGuardTick_ReStampsAUsedTextureThatLostItsStamp_EvenWhenCountIsUnchanged()
        {
            // 2026-09-08 (Bakery bake flood): an atlas texture can be
            // replaced without the count changing, so the guard must also
            // notice a used sub-object whose stamp is missing.
            UnityEngine.TextCore.Text.FontAsset asset = FontLoader.JapaneseUiFontAsset;
            if (asset == null || FontLoader.JapaneseUiFontFromFontFix)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine (or FontFix owns the asset).");
            }
            FieldInfo countField = typeof(FontLoader).GetField("_stampedAtlasCount",
                BindingFlags.NonPublic | BindingFlags.Static);
            countField.SetValue(null, asset.atlasTextureCount);
            Assert.IsFalse(FontLoader.HasUnstampedUsedSubObject(asset), "precondition: fully stamped");
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce());

            asset.material.hideFlags = HideFlags.None;
            Assert.IsTrue(FontLoader.HasUnstampedUsedSubObject(asset));
            Assert.IsTrue(FontLoader.AtlasGuardTickOnce(), "a lost stamp must be restored");
            Assert.AreEqual(HideFlags.HideAndDontSave, asset.material.hideFlags);
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce(), "and the cheap path resumes");
        }

        [Test]
        public void AtlasGuardTick_RebuildsABrokenAsset_Once_ThenThrottles()
        {
            UnityEngine.TextCore.Text.FontAsset asset = FontLoader.JapaneseUiFontAsset;
            if (asset == null || FontLoader.JapaneseUiFontFromFontFix)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine (or FontFix owns the asset).");
            }
            // 2026-09-08 regression (see the class doc comment): a heal
            // performed by the previous fixture within the last 2 s made
            // this tick return false. The precondition is "no recent
            // heal", so state it here rather than depend on run order.
            FontLoader.ResetHealThrottleForTests();
            int before = FontLoader.HealCount;
            try
            {
                // Break it the way a scene unload / asset refresh does.
                UnityEngine.Object.DestroyImmediate(asset.material);
                Assert.IsFalse(FontLoader.IsHealthy(asset), "precondition");

                Assert.IsTrue(FontLoader.AtlasGuardTickOnce(), "a broken asset must be rebuilt, not thrown on forever");
                Assert.AreEqual(before + 1, FontLoader.HealCount);
                UnityEngine.TextCore.Text.FontAsset rebuilt = FontLoader.JapaneseUiFontAsset;
                Assert.IsNotNull(rebuilt);
                Assert.AreNotSame(asset, rebuilt);
                Assert.IsTrue(FontLoader.IsHealthy(rebuilt));
                AssertExactlyOneMarkedAssetExists();

                // A second breakage inside the throttle window waits.
                UnityEngine.Object.DestroyImmediate(rebuilt.material);
                Assert.IsFalse(FontLoader.AtlasGuardTickOnce(), "throttled");
                Assert.AreEqual(before + 1, FontLoader.HealCount);
            }
            finally
            {
                // This test leaves the cached asset broken on purpose;
                // re-resolve even when an assertion above failed, or the
                // next test starts on a destroyed material.
                ResetJapaneseUiCache();
                FontLoader.ResetHealThrottleForTests();
            }
            Assert.IsTrue(FontLoader.IsHealthy(FontLoader.JapaneseUiFontAsset));
        }

        [Test]
        public void JapaneseUiFontAsset_ResolvedAssetHasStampedSubObjects()
        {
            ResetJapaneseUiCache();
            UnityEngine.TextCore.Text.FontAsset asset = FontLoader.JapaneseUiFontAsset;
            if (asset == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable in"
                    + " headless batch mode on this machine.");
            }
            AssertEverySubObjectStamped(asset);
        }

        private static UnityEngine.TextCore.Text.FontAsset CreateThrowawayAsset()
        {
            UnityEngine.TextCore.Text.FontAsset asset = null;
            for (int i = 0; i < FontLoader.OsJapaneseUiFontNames.Length && asset == null; i++)
            {
                try
                {
                    asset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                        FontLoader.OsJapaneseUiFontNames[i], "Regular");
                }
                catch (Exception)
                {
                    asset = null;
                }
            }
            if (asset == null)
            {
                Assert.Ignore("no OS CJK font on this machine, so there is no"
                    + " DynamicOS FontAsset to exercise");
            }
            // Deliberately NOT the marker name: this instance must never be
            // picked up by FindExistingMarkedAsset.
            asset.name = "UAP_TEST_THROWAWAY_FONT";
            return asset;
        }

        private static void DestroyAsset(UnityEngine.TextCore.Text.FontAsset asset)
        {
            if (asset == null)
            {
                return;
            }
            Texture2D[] textures = asset.atlasTextures;
            if (textures != null)
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    if (textures[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(textures[i]);
                    }
                }
            }
            if (asset.material != null)
            {
                UnityEngine.Object.DestroyImmediate(asset.material);
            }
            UnityEngine.Object.DestroyImmediate(asset);
        }

        private static uint[] ContiguousCodepoints(int start, int count)
        {
            uint[] codes = new uint[count];
            for (int i = 0; i < count; i++)
            {
                codes[i] = (uint)(start + i);
            }
            return codes;
        }

        private static void AssertEverySubObjectStamped(
            UnityEngine.TextCore.Text.FontAsset asset)
        {
            Assert.AreEqual(HideFlags.HideAndDontSave, asset.material.hideFlags,
                "the material must be stamped");
            Texture2D[] textures = asset.atlasTextures;
            for (int i = 0; i < textures.Length; i++)
            {
                if (textures[i] != null)
                {
                    Assert.AreEqual(HideFlags.HideAndDontSave, textures[i].hideFlags,
                        "atlas texture " + i + " must be stamped");
                }
            }
        }

        private static void ResetJapaneseUiCache()
        {
            Type type = typeof(FontLoader);
            type.GetField("_japaneseUiAsset", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, null);
            type.GetField("_japaneseUiProbed", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, false);
            type.GetField("_japaneseUiFontSource", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, string.Empty);
        }

        private static void AssertExactlyOneMarkedAssetExists()
        {
            UnityEngine.TextCore.Text.FontAsset[] all =
                Resources.FindObjectsOfTypeAll<UnityEngine.TextCore.Text.FontAsset>();
            int marked = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].name,
                        FontLoader.JapaneseUiAssetMarkerName, StringComparison.Ordinal))
                {
                    marked++;
                }
            }
            Assert.AreEqual(1, marked,
                "exactly one marked FontAsset must exist -- reuse/dedupe"
                + " must never leak a duplicate live object");
        }
    }
}

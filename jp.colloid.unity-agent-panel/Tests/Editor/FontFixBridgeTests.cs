using System;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// 2026-09-06: the optional UITK Font Fix bridge. The package is not a
    /// dependency, so these tests drive FontFixBridge through a stand-in
    /// static type with the same member shape as
    /// Colloid.UitkFontFix.FontFix, and check that FontLoader prefers
    /// the bridged asset, reports it, re-probes on invalidation and
    /// falls back to its own chain when the package is absent.
    /// </summary>
    public class FontFixBridgeTests
    {
        /// <summary>Stand-in for Colloid.UitkFontFix.FontFix.</summary>
        public static class FakeFontFix
        {
            public static FontAsset Asset;
            public static string Source = string.Empty;
            public static int Reads;
            public static int Resets;
            public static event Action CachesInvalidated;

            /// <summary>
            /// Mirrors FontFix's read-time repair: invoked with the
            /// cached asset on every read so a test can restore a
            /// destroyed child (0.4.1 swaps the material; 0.4.0 kept a
            /// surviving one). Null = no repair.
            /// </summary>
            public static Action<FontAsset> RepairOnRead;

            /// <summary>
            /// Mirrors FontFix.ResetCaches: the owned asset is destroyed
            /// (so the panel's own chain takes over with a fresh one) and
            /// CachesInvalidated is raised synchronously afterwards.
            /// </summary>
            public static void ResetCaches()
            {
                Resets++;
                if (Asset != null)
                {
                    UnityEngine.Object.DestroyImmediate(Asset);
                }
                Asset = null;
                Source = string.Empty;
                RaiseInvalidated();
            }

            public static FontAsset CjkUiFontAsset
            {
                get
                {
                    Reads++;
                    if (RepairOnRead != null && !ReferenceEquals(Asset, null))
                    {
                        RepairOnRead(Asset);
                    }
                    return Asset;
                }
            }

            public static string CjkUiFontSource
            {
                get { return Source; }
            }

            public static void RaiseInvalidated()
            {
                Action handler = CachesInvalidated;
                if (handler != null)
                {
                    handler();
                }
            }

            public static void Reset()
            {
                Asset = null;
                Source = string.Empty;
                Reads = 0;
                Resets = 0;
                RepairOnRead = null;
            }
        }

        /// <summary>A facade that resolves an asset but has no ResetCaches (an older/newer FontFix).</summary>
        public static class NoResetShape
        {
            public static FontAsset Asset;
            public static FontAsset CjkUiFontAsset { get { return Asset; } }
            public static string CjkUiFontSource { get { return "osasset:NoReset Family"; } }
        }

        /// <summary>A type that lacks the facade's asset property: must be ignored.</summary>
        public static class WrongShape
        {
            public static string CjkUiFontSource { get { return "x"; } }
        }

        [SetUp]
        public void SetUp()
        {
            FakeFontFix.Reset();
            FontLoader.ResetJapaneseUiCache();
        }

        [TearDown]
        public void TearDown()
        {
            FontFixBridge.ResetForTests();
            FontLoader.ResetJapaneseUiCache();
            // Regression note (2026-09-08, full-suite order dependence):
            // the GuardHeal_* tests each perform a heal, which arms
            // FontLoader's 2-second heal throttle -- a static that
            // ResetJapaneseUiCache does not clear. FontLoaderTests runs
            // immediately after this fixture and its
            // AtlasGuardTick_RebuildsABrokenAsset test was throttled into
            // failure (then cascaded into two more). Hand the throttle
            // back disarmed, the way it was found.
            FontLoader.ResetHealThrottleForTests();
        }

        [Test]
        public void Absent_IsUnavailable_ReadsNullAndEmpty()
        {
            FontFixBridge.UseProviderForTests(null);
            Assert.IsFalse(FontFixBridge.IsAvailable);
            Assert.IsNull(FontFixBridge.CjkUiFontAsset);
            Assert.AreEqual(string.Empty, FontFixBridge.CjkUiFontSource);
        }

        [Test]
        public void WrongShape_IsTreatedAsAbsent()
        {
            FontFixBridge.UseProviderForTests(typeof(WrongShape));
            Assert.IsFalse(FontFixBridge.IsAvailable);
            Assert.AreEqual(string.Empty, FontFixBridge.CjkUiFontSource);
        }

        [Test]
        public void Present_ReadsAssetAndSource_AndForwardsInvalidation()
        {
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            FakeFontFix.Source = "osasset:Fake Family";
            Assert.IsTrue(FontFixBridge.IsAvailable);
            Assert.AreEqual("osasset:Fake Family", FontFixBridge.CjkUiFontSource);
            Assert.IsNull(FontFixBridge.CjkUiFontAsset);
            Assert.AreEqual(1, FakeFontFix.Reads);

            int raised = 0;
            Action handler = delegate { raised++; };
            FontFixBridge.Invalidated += handler;
            try
            {
                FakeFontFix.RaiseInvalidated();
                Assert.AreEqual(1, raised, "provider invalidation reaches bridge subscribers");
            }
            finally
            {
                FontFixBridge.Invalidated -= handler;
            }
        }

        [Test]
        public void LazyFirstAccess_ReadsTheAssetProperty_NotNull()
        {
            // Regression (2026-09-06, seen only in the GUI editor): on the
            // very first access the bridge located the package but
            // returned no asset, because the property field was evaluated
            // before the lazy bind had filled it. Go through the lazy
            // locator path with the asset read as the FIRST call.
            FontAsset own = FontLoader.JapaneseUiFontAsset;
            if (own == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable on this machine.");
            }
            FontLoader.ResetJapaneseUiCache();
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Lazy Family";
            FontFixBridge.ResetForTests();
            FontFixBridge.LocatorForTests = delegate { return typeof(FakeFontFix); };

            Assert.AreSame(own, FontFixBridge.CjkUiFontAsset, "first access must already read the asset");
            Assert.AreEqual(1, FakeFontFix.Reads);

            FontFixBridge.ResetForTests();
            FontFixBridge.LocatorForTests = delegate { return typeof(FakeFontFix); };
            Assert.AreEqual("osasset:Lazy Family", FontFixBridge.CjkUiFontSource, "source-first access works too");
            Assert.AreEqual(FontFixBridge.SourcePrefix + "osasset:Lazy Family", FontLoader.JapaneseUiFontSource);
        }

        [Test]
        public void FontLoader_WithoutAsset_FallsBackToOwnChain()
        {
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            string source = FontLoader.JapaneseUiFontSource;
            Assert.IsFalse(source.StartsWith(FontFixBridge.SourcePrefix, StringComparison.Ordinal),
                "an unresolved provider must not be reported as the winner: " + source);
            Assert.IsFalse(FontLoader.JapaneseUiFontFromFontFix);
            Assert.GreaterOrEqual(FakeFontFix.Reads, 1, "the provider was consulted first");
        }

        [Test]
        public void FontLoader_WithAsset_PrefersIt_ReportsPrefix_AndReprobesOnInvalidation()
        {
            FontAsset own = FontLoader.JapaneseUiFontAsset;
            if (own == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable on this machine.");
            }
            FontLoader.ResetJapaneseUiCache();

            // Any live FontAsset stands in for FontFix's own; identity is
            // what the test pins, not provenance.
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Fake Family";
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));

            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset);
            Assert.AreEqual(FontFixBridge.SourcePrefix + "osasset:Fake Family", FontLoader.JapaneseUiFontSource);
            Assert.IsTrue(FontLoader.JapaneseUiFontFromFontFix);
            Assert.AreEqual("Fake Family", SettingsView.DescribeFontSource(FontLoader.JapaneseUiFontSource));
            int readsAfterResolve = FakeFontFix.Reads;
            FontAsset unused = FontLoader.JapaneseUiFontAsset;
            Assert.AreEqual(readsAfterResolve, FakeFontFix.Reads, "cached: no re-read per access");

            // The provider changes its mind (settings edited): FontLoader
            // must drop its cache and read the provider again.
            FakeFontFix.Source = "osasset:Other Family";
            FakeFontFix.RaiseInvalidated();
            Assert.AreEqual(FontFixBridge.SourcePrefix + "osasset:Other Family", FontLoader.JapaneseUiFontSource);
            Assert.Greater(FakeFontFix.Reads, readsAfterResolve);
        }

        [Test]
        public void ApplyJapaneseUi_ClearsInlineDefinition_WhenProviderPresentButUnresolved()
        {
            var element = new VisualElement();
            element.style.unityFontDefinition = StyleKeyword.Initial; // "some earlier asset"
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix)); // present, Asset == null
            // This machine's own chain would still resolve, which is not
            // the state FontFix disabling CJK produces; pin the resolver
            // to "probed, nothing" the way FontLoaderTests simulate a
            // reload.
            Type type = typeof(FontLoader);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            type.GetField("_japaneseUiAsset", flags).SetValue(null, null);
            type.GetField("_japaneseUiProbed", flags).SetValue(null, true);
            type.GetField("_japaneseUiFontSource", flags).SetValue(null, string.Empty);

            FontLoader.ApplyJapaneseUi(element);

            Assert.AreEqual(StyleKeyword.Null, element.style.unityFontDefinition.keyword,
                "with the package present, an unresolved font clears the stale inline definition");
        }

        [Test]
        public void RequestRebuild_CallsProviderResetCaches_AndForwardsItsInvalidation()
        {
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            Assert.IsTrue(FontFixBridge.CanRequestRebuild);
            int raised = 0;
            Action handler = delegate { raised++; };
            FontFixBridge.Invalidated += handler;
            try
            {
                Assert.IsTrue(FontFixBridge.RequestRebuild());
                Assert.AreEqual(1, FakeFontFix.Resets);
                Assert.AreEqual(1, raised, "the rebuild's invalidation reaches bridge subscribers");
            }
            finally
            {
                FontFixBridge.Invalidated -= handler;
            }
        }

        [Test]
        public void RequestRebuild_FalseWhenAbsent_OrWhenTheFacadeLacksResetCaches()
        {
            FontFixBridge.UseProviderForTests(null);
            Assert.IsFalse(FontFixBridge.CanRequestRebuild);
            Assert.IsFalse(FontFixBridge.RequestRebuild());

            FontFixBridge.UseProviderForTests(typeof(NoResetShape));
            Assert.IsTrue(FontFixBridge.IsAvailable, "the asset property alone makes a usable facade");
            Assert.IsFalse(FontFixBridge.CanRequestRebuild);
            Assert.IsFalse(FontFixBridge.RequestRebuild());
        }

        [Test]
        public void FontLoader_StampsAFontFixOwnedAssetsSubObjects_OnResolveAndOnGuardTicks()
        {
            // 2026-09-08 (live report): creating a New Scene threw
            // NullReferenceException from UIRStylePainter.DrawTextInfo with
            // FontFix installed. FontFix only re-stamps its children on
            // ITS reads and Play Mode transitions; the panel reads once,
            // and its guard used to skip FontFix's asset, so every atlas
            // page TextCore added later was unflagged and died with the
            // scene. The guard must now protect a bridged asset exactly
            // like the panel's own.
            FontAsset own = OwnResolvedAsset();
            own.material.hideFlags = HideFlags.None;
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Fake Family";
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));

            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset);
            Assert.IsTrue(FontLoader.JapaneseUiFontFromFontFix, "precondition: bridged");
            Assert.IsTrue(FontLoader.IsProtected(own.material.hideFlags),
                "resolving through the bridge must stamp the children");
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce(), "steady state stays the cheap path");

            own.material.hideFlags = HideFlags.None;
            Assert.IsTrue(FontLoader.AtlasGuardTickOnce(),
                "a bridged asset that lost a stamp must be re-stamped, not skipped");
            Assert.IsTrue(FontLoader.IsProtected(own.material.hideFlags));
            Assert.IsFalse(FontLoader.AtlasGuardTickOnce());
        }

        [Test]
        public void IsProtected_AcceptsFontFixsDontSave_AndThePanelsHideAndDontSave()
        {
            // FontFix stamps plain DontSave on the same children the panel
            // stamps HideAndDontSave. Both survive a scene unload; treating
            // DontSave as "unstamped" would make the guard rewrite the flags
            // on every frame after every FontFix read.
            Assert.IsTrue(FontLoader.IsProtected(HideFlags.DontSave));
            Assert.IsTrue(FontLoader.IsProtected(HideFlags.HideAndDontSave));
            Assert.IsFalse(FontLoader.IsProtected(HideFlags.None));
            Assert.IsFalse(FontLoader.IsProtected(HideFlags.HideInHierarchy));
            Assert.IsFalse(FontLoader.IsProtected(HideFlags.DontSaveInEditor),
                "a partial DontSave set (no DontUnloadUnusedAsset) is not protection");
        }

        [Test]
        public void GuardHeal_RebuildsABrokenFontFixAsset_ThroughFontFix_WhenTheReadDoesNotRepairIt()
        {
            // The heal used to re-read FontFix and accept whatever came
            // back. FontFix 0.4.0 repairs in place and returns the SAME
            // instance; UI Toolkit keys each label's cached text
            // generation on the instance and its material, so nothing on
            // screen regenerated and the panel stayed garbled after the
            // "rebuilt it as fontfix:..." log line. When the read leaves
            // the asset broken (this fake) the heal must ask FontFix for a
            // rebuild (new instance + invalidation) instead.
            FontAsset own = OwnResolvedAsset();
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Fake Family";
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset);
            Assert.IsTrue(FontLoader.JapaneseUiFontFromFontFix, "precondition: bridged");

            FontLoader.ResetHealThrottleForTests();
            int healsBefore = FontLoader.HealCount;
            UnityEngine.Object.DestroyImmediate(own.material);
            Assert.IsFalse(FontLoader.IsHealthy(own), "precondition: broken the way NewScene breaks it");

            Assert.IsTrue(FontLoader.AtlasGuardTickOnce(), "a broken bridged asset must heal");
            Assert.AreEqual(healsBefore + 1, FontLoader.HealCount);
            Assert.AreEqual(1, FakeFontFix.Resets, "the heal must go through FontFix.ResetCaches");

            // The fake's rebuild yields nothing, so the panel's own chain
            // takes over with a fresh, healthy instance -- never the broken
            // one FontFix would have repaired in place.
            FontAsset after = FontLoader.JapaneseUiFontAsset;
            Assert.IsNotNull(after, "a fresh asset must be creatable");
            Assert.AreNotSame(own, after);
            Assert.IsTrue(FontLoader.IsHealthy(after));
            Assert.IsFalse(FontLoader.JapaneseUiFontFromFontFix);
        }

        [Test]
        public void GuardHeal_AcceptsAnInPlaceRepair_ThatSwappedTheMaterial()
        {
            // FontFix 0.4.1 repairs on its read by installing a NEW atlas
            // material on the same instance, which changes UI Toolkit's
            // generation hash and regenerates every label. That is the
            // cheapest heal and must be taken as-is: no ResetCaches, same
            // instance, still bridged.
            FontAsset own = OwnResolvedAsset();
            Shader shader = own.material.shader;
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Fake Family";
            FakeFontFix.RepairOnRead = delegate (FontAsset asset)
            {
                if (asset.material == null)
                {
                    asset.material = new Material(shader);
                }
            };
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset);

            FontLoader.ResetHealThrottleForTests();
            int healsBefore = FontLoader.HealCount;
            UnityEngine.Object.DestroyImmediate(own.material);
            Assert.IsFalse(FontLoader.IsHealthy(own), "precondition");

            Assert.IsTrue(FontLoader.AtlasGuardTickOnce());
            Assert.AreEqual(healsBefore + 1, FontLoader.HealCount);
            Assert.AreEqual(0, FakeFontFix.Resets, "an accepted in-place repair never forces a rebuild");
            Assert.AreEqual(" (repaired in place by UITK Font Fix)", FontLoader.LastFontFixHeal);
            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset, "same instance, as FontFix intends");
            Assert.IsTrue(FontLoader.IsHealthy(own));
            Assert.IsTrue(FontLoader.JapaneseUiFontFromFontFix);
            Assert.IsTrue(FontLoader.IsProtected(own.material.hideFlags),
                "the replacement material is adopted by the guard like any other child");
        }

        [Test]
        public void GuardHeal_RejectsAnInPlaceRepair_ThatKeptTheMaterial()
        {
            // FontFix 0.4.0: when only atlas pages died, the repair kept
            // the surviving material. The instance AND its material are
            // unchanged, so UI Toolkit would keep every stale text mesh;
            // the heal must fall through to ResetCaches.
            FontAsset own = OwnResolvedAsset();
            Texture2D[] pages = own.atlasTextures;
            if (pages == null || pages.Length == 0 || pages[0] == null)
            {
                Assert.Ignore("no atlas page to destroy on this machine");
            }
            FakeFontFix.Asset = own;
            FakeFontFix.Source = "osasset:Fake Family";
            FakeFontFix.RepairOnRead = delegate (FontAsset asset)
            {
                Texture2D[] live = asset.atlasTextures;
                if (live != null && live.Length > 0 && live[0] == null)
                {
                    live[0] = new Texture2D(1, 1, TextureFormat.Alpha8, false);
                }
            };
            FontFixBridge.UseProviderForTests(typeof(FakeFontFix));
            Assert.AreSame(own, FontLoader.JapaneseUiFontAsset);

            FontLoader.ResetHealThrottleForTests();
            int healsBefore = FontLoader.HealCount;
            UnityEngine.Object.DestroyImmediate(pages[0]);
            Assert.IsFalse(FontLoader.IsHealthy(own), "precondition: a used page died");

            Assert.IsTrue(FontLoader.AtlasGuardTickOnce());
            Assert.AreEqual(healsBefore + 1, FontLoader.HealCount);
            Assert.AreEqual(1, FakeFontFix.Resets,
                "a repair UI Toolkit cannot see must be escalated to a rebuild");
            Assert.AreEqual(" (rebuilt via UITK Font Fix ResetCaches)", FontLoader.LastFontFixHeal);
            FontAsset after = FontLoader.JapaneseUiFontAsset;
            Assert.IsNotNull(after);
            Assert.AreNotSame(own, after);
            Assert.IsTrue(FontLoader.IsHealthy(after));
        }

        /// <summary>
        /// The panel's OWN marked asset (bridge off), left unresolved in
        /// FontLoader so a test can hand it to the fake provider as the
        /// "FontFix-owned" instance. Damaging it never touches a real
        /// FontFix installation in the host project.
        /// </summary>
        private static FontAsset OwnResolvedAsset()
        {
            FontFixBridge.UseProviderForTests(null);
            FontLoader.ResetJapaneseUiCache();
            FontAsset own = FontLoader.JapaneseUiFontAsset;
            if (own == null)
            {
                Assert.Ignore("DynamicOS FontAsset creation unavailable on this machine.");
            }
            FontLoader.ResetJapaneseUiCache();
            return own;
        }

        [Test]
        public void ApplyJapaneseUi_WithoutProvider_NeverClears()
        {
            FontFixBridge.UseProviderForTests(null);
            var element = new VisualElement();
            element.style.unityFontDefinition = StyleKeyword.Initial;
            FontLoader.ApplyJapaneseUi(element);
            Assert.AreNotEqual(StyleKeyword.Null, element.style.unityFontDefinition.keyword,
                "with no package the historical no-op contract holds");
        }
    }
}

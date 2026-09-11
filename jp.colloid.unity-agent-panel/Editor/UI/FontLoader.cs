using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Single source of the monospace font used for code blocks,
    /// attachment payloads and tool-card previews.
    ///
    /// Live-feedback defect: styling Consolas through
    /// Font.CreateDynamicFontFromOSFont with a NAME ARRAY produced a Font
    /// whose face UI Toolkit's TextCore could not load ("Unable to load
    /// font face for [Consolas]" + "Can't Generate Mesh, No Font Asset has
    /// been assigned"), rendering code block content EMPTY. The fix order:
    ///
    ///   1. The EDITOR-BUNDLED mono font (RobotoMono) via
    ///      EditorGUIUtility.Load -- a real TTF asset TextCore always
    ///      accepts, present in every 2022.3 install.
    ///   2. Single-name OS fonts (Consolas, then Menlo/DejaVu for other
    ///      platforms, then Courier New), each accepted only after the
    ///      TextCore face probe (FontEngine.LoadFontFace) succeeds, so a
    ///      broken dynamic font can never reach a FontDefinition again.
    ///   3. The default editor label font -- never null, never a broken
    ///      face, worst case just not monospaced.
    ///
    /// Every probe result is cached; the class never throws even when
    /// editor resources are missing (batch mode safe).
    ///
    /// JapaneseUiFont mirrors the same OS-probe half of this pipeline for
    /// the PATCHY-BOLD live-feedback defect: the editor's default UI font
    /// (Inter) has no CJK coverage, so any Japanese glyph falls back to
    /// per-glyph OS font substitution -- and when more than one candidate
    /// font/weight can supply a given glyph, TextCore's dynamic fallback
    /// SDF atlas mixes them, producing visibly heavier ("patchy bold")
    /// strokes next to normal-weight Latin text. Assigning ONE font that
    /// covers both scripts to the body-text root removes the fallback
    /// path entirely for Japanese. Unlike MonoFont there is no
    /// editor-bundled candidate to try first (Inter itself is the
    /// problem) and no default-label-font last resort: a null result
    /// must leave the panel exactly as it renders today.
    /// </summary>
    public static class FontLoader
    {
        /// <summary>
        /// Stable name marker for the OS-derived Japanese UI FontAsset.
        /// Measured (probe 4, 2026-08-02 design note section 1): a
        /// HideAndDontSave TextCore FontAsset created via
        /// FontAsset.CreateFontAsset SURVIVES a real domain reload intact
        /// and remains fully usable -- the SAME native object, found again
        /// by Resources.FindObjectsOfTypeAll after reload. So the asset is
        /// never destroyed on beforeAssemblyReload; instead each resolve
        /// FIRST looks for an existing asset carrying this marker and
        /// reuses it, only creating a new one when none is found. This is
        /// the fix for the 1106x MissingReferenceException flood: labels
        /// still holding the old asset via unityFontDefinition never see it
        /// destroyed out from under them.
        /// </summary>
        internal const string JapaneseUiAssetMarkerName = "UAP_JA_UI_FONT";

        /// <summary>
        /// Last <c>atlasTextureCount</c> the atlas guard stamped, or -1
        /// when nothing has been stamped yet. See
        /// <see cref="AtlasGuardTickOnce"/>.
        /// </summary>
        private static int _stampedAtlasCount = -1;

        private static bool _atlasGuardInstalled;

        /// <summary>
        /// Editor-bundle candidate paths for the bundled mono TTF
        /// (2022.3; verified empirically by FontLoaderTests in the
        /// sandbox project -- the first path resolves on 2022.3.22f1).
        /// </summary>
        public static readonly string[] EditorMonoFontPaths =
        {
            "Fonts/RobotoMono/RobotoMono-Regular.ttf",
            "fonts/robotomono/robotomono-regular.ttf"
        };

        /// <summary>OS font names probed one at a time (never as an array).</summary>
        public static readonly string[] OsMonoFontNames =
        {
            "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New"
        };

        /// <summary>
        /// OS Latin+Japanese UI font names probed one at a time, most
        /// preferred first. Yu Gothic UI/Yu Gothic ship with Windows 10/11
        /// (Meiryo predates them on older installs); Noto Sans CJK JP
        /// covers Linux/manually-provisioned machines; MS UI Gothic is the
        /// last-ditch legacy Windows fallback.
        /// </summary>
        public static readonly string[] OsJapaneseUiFontNames =
        {
            "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo",
            "Noto Sans CJK JP", "MS UI Gothic"
        };

        private const int ProbePointSize = 12;

        private static Font _monoFont;
        private static bool _probed;
        private static string _monoFontSource = string.Empty;

        private static UnityEngine.TextCore.Text.FontAsset _japaneseUiAsset;
        private static bool _japaneseUiProbed;
        private static string _japaneseUiFontSource = string.Empty;

        /// <summary>
        /// The resolved monospace (or last-resort label) font. Never null
        /// in a functioning editor; may be null only if every candidate
        /// including the built-in label font is unavailable, in which case
        /// ApplyMono simply leaves the inherited font in place.
        /// </summary>
        public static Font MonoFont
        {
            get
            {
                if (!_probed)
                {
                    _probed = true;
                    _monoFont = Resolve(out _monoFontSource);
                }
                return _monoFont;
            }
        }

        /// <summary>Diagnostic: which candidate won ("editor:...", "os:...", "label").</summary>
        public static string MonoFontSource
        {
            get
            {
                Font unused = MonoFont;
                return _monoFontSource;
            }
        }

        /// <summary>
        /// The resolved Latin+Japanese UI font as a TextCore FontAsset in
        /// DynamicOS mode, or null when no candidate in
        /// <see cref="OsJapaneseUiFontNames"/> is installed/creatable.
        /// Cached after the first probe (never throws).
        ///
        /// Why a FontAsset and not Font.CreateDynamicFontFromOSFont:
        /// verified empirically in the live 2022.3 editor that
        /// FontEngine.LoadFontFace(Font) returns Invalid_File for EVERY
        /// OS dynamic Font (interactive included), so a Font-based route
        /// can never pass the face gate. FontAsset.CreateFontAsset(
        /// familyName, styleName) is TextCore's own system-font mechanism
        /// (atlasPopulationMode = DynamicOS, glyphs fetched lazily at
        /// render time) and is the supported way to hand an OS font to
        /// UI Toolkit via FontDefinition.FromSDFFontAsset.
        /// </summary>
        public static UnityEngine.TextCore.Text.FontAsset JapaneseUiFontAsset
        {
            get
            {
                if (!_japaneseUiProbed)
                {
                    _japaneseUiProbed = true;
                    EnsureFontFixHooked();
                    _japaneseUiAsset = ResolveJapaneseUi(out _japaneseUiFontSource);
                    if (_japaneseUiAsset != null)
                    {
                        // The guard covers every resolved asset,
                        // FontFix-owned included (see ResolveJapaneseUi).
                        EnsureAtlasGuardInstalled();
                    }
                }
                return _japaneseUiAsset;
            }
        }

        /// <summary>
        /// True when the current Japanese UI font came through
        /// <see cref="FontFixBridge"/> (UITK Font Fix installed and
        /// resolved), i.e. the project's Font Fix settings are in effect.
        /// </summary>
        public static bool JapaneseUiFontFromFontFix
        {
            get
            {
                return JapaneseUiFontSource.StartsWith(FontFixBridge.SourcePrefix, System.StringComparison.Ordinal);
            }
        }

        private static bool _fontFixHooked;

        private static void EnsureFontFixHooked()
        {
            if (_fontFixHooked)
            {
                return;
            }
            _fontFixHooked = true;
            FontFixBridge.Invalidated += OnFontFixInvalidated;
        }

        /// <summary>
        /// UITK Font Fix dropped or replaced its asset (settings changed,
        /// ResetCaches, rebuild): forget ours so the next resolve reads
        /// the new one, then re-apply on every open window so no root
        /// keeps pointing at a destroyed FontAsset.
        /// </summary>
        private static void OnFontFixInvalidated()
        {
            ResetJapaneseUiCache();
            AgentPanelWindow.ReapplyContentRootStyling();
        }

        /// <summary>Drops the cached Japanese UI resolution so the next access re-probes.</summary>
        internal static void ResetJapaneseUiCache()
        {
            _japaneseUiProbed = false;
            _japaneseUiAsset = null;
            _japaneseUiFontSource = string.Empty;
        }

        /// <summary>Diagnostic: which OS font name won ("osasset:..."), or empty when none resolved.</summary>
        public static string JapaneseUiFontSource
        {
            get
            {
                UnityEngine.TextCore.Text.FontAsset unused = JapaneseUiFontAsset;
                return _japaneseUiFontSource;
            }
        }

        /// <summary>
        /// Applies the mono font to one element via unityFontDefinition.
        /// No-ops (keeping the inherited font) when nothing resolved.
        /// </summary>
        public static void ApplyMono(VisualElement element)
        {
            if (element == null)
            {
                return;
            }
            Font font = MonoFont;
            if (font != null)
            {
                element.style.unityFontDefinition =
                    new StyleFontDefinition(FontDefinition.FromFont(font));
            }
        }

        /// <summary>
        /// Applies the resolved Japanese UI font to one element via
        /// unityFontDefinition (an INLINE style assignment). No-ops
        /// (keeping the inherited/default font) when nothing resolved --
        /// callers must not assume the element's font changed.
        /// </summary>
        public static void ApplyJapaneseUi(VisualElement element)
        {
            if (element == null)
            {
                return;
            }
            UnityEngine.TextCore.Text.FontAsset asset = JapaneseUiFontAsset;
            if (asset == null)
            {
                // With UITK Font Fix installed the font is ITS decision:
                // "nothing resolved" means its settings disabled CJK (or
                // changed to an unavailable family), and the element may
                // still carry the asset FontFix has since destroyed. Drop
                // the inline definition so it falls back to the editor
                // default instead of a dead asset. Without the package
                // this stays the historical no-op.
                if (FontFixBridge.IsAvailable)
                {
                    element.style.unityFontDefinition = StyleKeyword.Null;
                }
                return;
            }
            {
                // API name verified twice: by reflection on the live
                // 2022.3 editor AND against the Unity 6000.0 docs --
                // FromSDFFont is the stable name across versions.
                // (A method named FromSDFFontAsset has never existed;
                // an earlier comment here claimed otherwise in error.)
                element.style.unityFontDefinition =
                    new StyleFontDefinition(FontDefinition.FromSDFFont(asset));
            }
        }

        // -- Resolution ---------------------------------------------------------

        private static Font Resolve(out string source)
        {
            // 1. Editor-bundled RobotoMono (real TTF asset).
            for (int i = 0; i < EditorMonoFontPaths.Length; i++)
            {
                Font bundled = LoadEditorFont(EditorMonoFontPaths[i]);
                if (bundled != null && FaceLoads(bundled))
                {
                    source = "editor:" + EditorMonoFontPaths[i];
                    return bundled;
                }
            }

            // 2. Single-name OS fonts, gated by the installed-name list
            //    AND the TextCore face probe.
            string[] installed = InstalledOsFontNames();
            for (int i = 0; i < OsMonoFontNames.Length; i++)
            {
                string name = OsMonoFontNames[i];
                if (!ContainsIgnoreCase(installed, name))
                {
                    continue;
                }
                Font osFont = CreateOsFont(name);
                if (osFont == null)
                {
                    continue;
                }
                if (FaceLoads(osFont))
                {
                    // This branch ONLY: the OS-created Font is owned by
                    // nobody, so without HideAndDontSave the next
                    // EditorSceneManager.NewScene unloads it and every
                    // mono label holding it starts flooding
                    // MissingReference warnings (the same failure the
                    // TextCore path fixed in 2026-08-03
                    // font-atlas-destroyed-by-newscene). The
                    // editor-bundled and default-label fonts are
                    // editor-owned and must NOT be stamped.
                    StampOsFont(osFont);
                    source = "os:" + name;
                    return osFont;
                }
                Object.DestroyImmediate(osFont);
            }

            // 3. Default editor label font (never a broken face).
            Font label = DefaultLabelFont();
            source = label != null ? "label" : string.Empty;
            return label;
        }

        /// <summary>
        /// OS-only probe (no editor-bundled candidate, no default-label
        /// fallback): each name in <see cref="OsJapaneseUiFontNames"/> is
        /// tried only if Font.GetOSInstalledFontNames() reports it AND the
        /// TextCore face probe (FaceLoads, identical to the mono path)
        /// accepts it, so a name Windows advertises but TextCore cannot
        /// rasterize can never reach a FontDefinition. Returns null when
        /// no candidate clears both gates -- the caller then leaves the
        /// element on its inherited/editor-default font, matching current
        /// pre-feature behavior exactly.
        /// </summary>
        private static UnityEngine.TextCore.Text.FontAsset ResolveJapaneseUi(out string source)
        {
            // 0. UITK Font Fix, when installed: its asset already reflects
            //    the project's Font Fix settings (family chain, face
            //    style, bold wiring), which is what a project that
            //    installed the package expects every editor UI to use.
            //    FontFix owns that asset's lifecycle (creation, Play Mode
            //    repair, reload cleanup); the marker-name reuse and
            //    dedupe below are for the panel's own asset only.
            //
            //    2026-09-08 (live report: NewScene threw
            //    NullReferenceException from UIRStylePainter.DrawTextInfo
            //    and the panel stayed garbled): FontFix stamps its
            //    asset's children on ITS reads and Play Mode transitions
            //    only, while the panel reads the asset once and caches
            //    it, and the atlas guard used to skip FontFix's asset
            //    entirely. Every atlas page TextCore added while the
            //    panel rendered was therefore unflagged and died with the
            //    scene. So the guard now stamps this asset exactly like
            //    the panel's own -- the flags it writes are a superset of
            //    FontFix's DontSave and FontFix's own re-stamp keeps
            //    satisfying the guard's check, so the two never fight.
            UnityEngine.TextCore.Text.FontAsset fromFontFix = FontFixBridge.CjkUiFontAsset;
            if (fromFontFix != null)
            {
                AdoptSubObjects(fromFontFix);
                source = FontFixBridge.SourcePrefix + FontFixBridge.CjkUiFontSource;
                return fromFontFix;
            }

            // Re-find-and-reuse FIRST (measured viable: probe 4 in the
            // 2026-08-02 design note). A marked asset can only survive to
            // this point via a real domain reload -- within one domain
            // load the result is cached in _japaneseUiAsset and this
            // method never runs twice -- so finding one here means "the
            // asset that already exists from before the reload", which is
            // exactly what any live label's unityFontDefinition still
            // points at. Reusing it (never creating a second one) is what
            // stops the destroyed-material MissingReferenceException flood.
            UnityEngine.TextCore.Text.FontAsset existing = FindExistingMarkedAsset();
            if (existing != null)
            {
                // An asset created by a pre-0.14.1 version arrives with
                // UNSTAMPED children, so stamping on the reuse path (not
                // just at creation) is what protects an editor that has
                // been running since before this fix landed.
                AdoptSubObjects(existing);
                source = "osasset-reused:" + JapaneseUiAssetMarkerName;
                return existing;
            }

            string[] installed = InstalledOsFontNames();
            for (int i = 0; i < OsJapaneseUiFontNames.Length; i++)
            {
                string name = OsJapaneseUiFontNames[i];
                if (!ContainsIgnoreCase(installed, name))
                {
                    continue;
                }
                UnityEngine.TextCore.Text.FontAsset asset = CreateOsFontAsset(name);
                if (asset != null)
                {
                    AdoptSubObjects(asset);
                    source = "osasset:" + name;
                    return asset;
                }
            }
            source = string.Empty;
            return null;
        }

        /// <summary>
        /// Stamps <paramref name="asset"/>'s children and arms the atlas
        /// guard for it. Called on every resolve path (FontFix, create
        /// and reuse).
        /// </summary>
        private static void AdoptSubObjects(UnityEngine.TextCore.Text.FontAsset asset)
        {
            StampSubObjects(asset);
            _stampedAtlasCount = asset != null ? asset.atlasTextureCount : -1;
            EnsureAtlasGuardInstalled();
        }

        /// <summary>
        /// Finds a previously-created, still-alive AND STILL USABLE marked
        /// FontAsset via Resources.FindObjectsOfTypeAll (the only way to
        /// see a HideAndDontSave object survive a domain reload -- it is
        /// not reachable through any asset path or normal reference).
        ///
        /// Two kinds of candidate are destroyed here, and this remains the
        /// ONLY destroy point in this class:
        ///
        ///   * EXTRAS. If more than one usable marked asset is found (only
        ///     possible if this resolver somehow ran twice without an
        ///     intervening reload, e.g. a bug), the first is kept and the
        ///     rest are destroyed: a second marked instance is provably a
        ///     leak, never something a live label depends on.
        ///   * UNHEALTHY ones (<see cref="IsHealthy"/> false). Measured
        ///     2026-08-03: an asset whose children were destroyed by a
        ///     scene unload cannot be repaired -- TryAddCharacters returns
        ///     false -- and because it carries HideAndDontSave it survives
        ///     every later domain reload, so reusing it would keep the
        ///     error flood alive for the rest of the editor session.
        ///
        /// Destroying at this point is safe for both: resolve only runs on
        /// the first request of a domain load, before any live
        /// VisualElement has been styled with the result.
        /// </summary>
        internal static UnityEngine.TextCore.Text.FontAsset FindExistingMarkedAsset()
        {
            UnityEngine.TextCore.Text.FontAsset[] all =
                Resources.FindObjectsOfTypeAll<UnityEngine.TextCore.Text.FontAsset>();
            UnityEngine.TextCore.Text.FontAsset found = null;
            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.TextCore.Text.FontAsset candidate = all[i];
                if (candidate == null || !string.Equals(candidate.name,
                        JapaneseUiAssetMarkerName, System.StringComparison.Ordinal))
                {
                    continue;
                }
                if (found == null && IsHealthy(candidate))
                {
                    found = candidate;
                }
                else
                {
                    Object.DestroyImmediate(candidate);
                }
            }
            return found;
        }

        /// <summary>
        /// True when <paramref name="flags"/> keep an object alive across
        /// a scene unload: the DontSave set (DontSaveInEditor |
        /// DontSaveInBuild | DontUnloadUnusedAsset), which Unity documents
        /// as "will not be destroyed when a new Scene is loaded". The
        /// panel stamps HideAndDontSave (DontSave plus HideInHierarchy);
        /// UITK Font Fix stamps plain DontSave on the same children. Both
        /// protect, so the guard must accept both or it would re-stamp
        /// FontFix's asset on every frame after every FontFix read.
        /// </summary>
        internal static bool IsProtected(HideFlags flags)
        {
            return (flags & HideFlags.DontSave) == HideFlags.DontSave;
        }

        /// <summary>
        /// True when the asset can still render: its material and every
        /// atlas texture it actually uses are alive.
        ///
        /// Iterates atlasTextureCount, NOT atlasTextures.Length. Measured
        /// 2026-08-03: the array is over-allocated (Length 16 while
        /// atlasTextureCount was 9), and the trailing entries are
        /// legitimately null -- treating those as breakage reports every
        /// healthy asset as broken.
        /// </summary>
        internal static bool IsHealthy(UnityEngine.TextCore.Text.FontAsset asset)
        {
            if (asset == null || asset.material == null)
            {
                return false;
            }
            Texture2D[] textures = asset.atlasTextures;
            int used = asset.atlasTextureCount;
            if (textures == null)
            {
                return used == 0;
            }
            for (int i = 0; i < used && i < textures.Length; i++)
            {
                if (textures[i] == null)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Marks the asset's material and every atlas texture
        /// HideAndDontSave, and returns how many objects this call had to
        /// change (0 when everything was already stamped).
        ///
        /// This is the actual fix for the 2026-08-03 "New Scene wipes the
        /// font" flood. FontAsset.CreateFontAsset hands back children with
        /// hideFlags = None; HideAndDontSave (which includes
        /// DontUnloadUnusedAsset) was set on the PARENT only, so the parent
        /// survived a scene unload holding dangling references to a
        /// destroyed material and destroyed atlas textures, and every
        /// repaint of the panel threw. Measured in the AITemp sandbox:
        /// EditorSceneManager.NewScene destroys 9/9 unstamped atlas
        /// textures plus the material, and 0/9 stamped ones.
        /// (Resources.UnloadUnusedAssets destroys neither -- it is not the
        /// trigger, despite being the intuitive suspect.)
        ///
        /// Every non-null slot is stamped, not just the first
        /// atlasTextureCount, because stamping a spare slot is free and a
        /// slot that becomes used later is then already protected.
        /// </summary>
        internal static int StampSubObjects(UnityEngine.TextCore.Text.FontAsset asset)
        {
            if (asset == null)
            {
                return 0;
            }
            int stamped = 0;
            Material material = asset.material;
            if (material != null && !IsProtected(material.hideFlags))
            {
                material.hideFlags = HideFlags.HideAndDontSave;
                stamped++;
            }
            Texture2D[] textures = asset.atlasTextures;
            if (textures == null)
            {
                return stamped;
            }
            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D texture = textures[i];
                if (texture != null && !IsProtected(texture.hideFlags))
                {
                    texture.hideFlags = HideFlags.HideAndDontSave;
                    stamped++;
                }
            }
            return stamped;
        }

        /// <summary>
        /// One iteration of the atlas guard; returns true when it
        /// re-stamped. Returns false (doing nothing but an int compare) in
        /// the overwhelmingly common case, which is what makes it
        /// affordable on EditorApplication.update.
        ///
        /// Stamping once at creation is NOT enough: measured 2026-08-03, a
        /// DynamicOS FontAsset stamped right after creation grew to nine
        /// atlas textures during glyph rasterisation and the eight NEW ones
        /// all came back with hideFlags = None. Atlas textures are created
        /// lazily inside text generation, with no creation hook to attach
        /// to, so the only way to keep them protected is to notice
        /// afterwards. A change in atlasTextureCount is the only way a new
        /// atlas texture can appear; it also covers ClearFontAssetData,
        /// which resets the count.
        /// </summary>
        internal static bool AtlasGuardTickOnce()
        {
            UnityEngine.TextCore.Text.FontAsset asset = _japaneseUiAsset;
            if (asset == null)
            {
                return false;
            }
            // 2026-09-08 (live report during a Bakery bake): the flood
            // came back -- UIRStylePainter.DrawText threw
            // MissingReferenceException "Texture2D has been destroyed" on
            // every repaint. Bakery's bake loop reloads scenes, refreshes
            // assets and unloads unused ones between EditorApplication.
            // update ticks, so an atlas texture TextCore created during a
            // repaint can be destroyed before the count compare below
            // ever sees it, and once a used atlas texture is gone the
            // asset cannot be repaired (TryAddCharacters fails). Two
            // additions: (a) a broken asset is rebuilt from scratch
            // (throttled) instead of throwing forever, and (b) a used
            // sub-object that lost its stamp is re-stamped even when the
            // count did not change.
            if (!IsHealthy(asset))
            {
                return TryHealBrokenAsset();
            }
            // FontFix-owned assets are stamped too (2026-09-08, see
            // ResolveJapaneseUi): FontFix only re-stamps on its own
            // reads, which the panel does not repeat, so without this
            // every lazily added atlas page dies on the next NewScene.
            int count = asset.atlasTextureCount;
            if (count == _stampedAtlasCount && !HasUnstampedUsedSubObject(asset))
            {
                return false;
            }
            StampSubObjects(asset);
            _stampedAtlasCount = count;
            return true;
        }

        /// <summary>Minimum seconds between two automatic rebuilds of a broken asset, so a font that breaks on every resolve cannot thrash the panel.</summary>
        internal const double HealThrottleSeconds = 2.0;

        private static double _lastHealAt = -1.0;

        /// <summary>Diagnostics/test seam: how many automatic rebuilds the guard performed this domain load.</summary>
        internal static int HealCount;

        /// <summary>Tests: forget the last heal time so the next broken asset heals immediately.</summary>
        internal static void ResetHealThrottleForTests()
        {
            _lastHealAt = -1.0;
        }

        /// <summary>
        /// Drops the broken cached asset and re-applies the font on every
        /// open panel: the re-resolve destroys the unhealthy marked asset
        /// (<see cref="FindExistingMarkedAsset"/>) and creates a fresh one,
        /// and every live label is repointed at it. Throttled by
        /// <see cref="HealThrottleSeconds"/>.
        ///
        /// A FontFix-owned asset goes through <see cref="HealThroughFontFix"/>
        /// first: FontFix repairs damage on its own read, and only a
        /// repair that UI Toolkit will actually pick up is accepted.
        /// </summary>
        private static bool TryHealBrokenAsset()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_lastHealAt >= 0.0 && now - _lastHealAt < HealThrottleSeconds)
            {
                return false;
            }
            _lastHealAt = now;
            HealCount++;
            string previousSource = _japaneseUiFontSource;
            string how = JapaneseUiFontFromFontFix
                ? HealThroughFontFix(_japaneseUiAsset)
                : string.Empty;
            // Harmless after a FontFix rebuild (whose invalidation already
            // re-resolved and re-applied) or in-place repair (same
            // instance, so the style is unchanged): the re-apply reads
            // FontFix's current asset and re-arms the guard for it.
            // Required when FontFix could not help.
            ResetJapaneseUiCache();
            AgentPanelWindow.ReapplyContentRootStyling();
            Debug.Log("[AgentPanel] The CJK UI font's atlas or material was destroyed (source: "
                + previousSource + "); rebuilt it as " + _japaneseUiFontSource + how + ".");
            return true;
        }

        /// <summary>Diagnostics/test seam: how the last FontFix-owned heal was resolved (see <see cref="HealThroughFontFix"/>).</summary>
        internal static string LastFontFixHeal = string.Empty;

        /// <summary>
        /// Heals a broken FontFix-owned asset, cheapest route first, and
        /// returns a log suffix naming the route.
        ///
        /// 1. Re-read FontFix. Its getter repairs a damaged asset in
        ///    place (0.4.0+) or, when it cannot, rebuilds and raises
        ///    CachesInvalidated, which <see cref="OnFontFixInvalidated"/>
        ///    already turned into a re-apply of the new instance.
        /// 2. An in-place repair is only accepted when it REPLACED the
        ///    atlas material: UI Toolkit regenerates a label's cached text
        ///    mesh only when its generation-settings hash changes, and
        ///    that hash covers the font asset and its material. FontFix
        ///    0.4.1 always installs a new material for exactly this
        ///    reason; 0.4.0 kept a surviving material when only atlas
        ///    pages died, which is the "rebuilt it as fontfix:... but the
        ///    panel stayed garbled" report of 2026-09-08. Accepting such a
        ///    repair also marks every panel label for repaint, because a
        ///    label whose last draw threw is no longer dirty (FontFix
        ///    0.4.1 does the same sweep across all windows; the panel
        ///    cannot know which version it is talking to).
        /// 3. Otherwise ask FontFix for a full rebuild
        ///    (<see cref="FontFixBridge.RequestRebuild"/>): a new instance
        ///    changes the hash unconditionally. Falls through silently
        ///    when the facade has no ResetCaches.
        /// </summary>
        private static string HealThroughFontFix(UnityEngine.TextCore.Text.FontAsset broken)
        {
            int materialBefore = InstanceIdOrZero(broken != null ? broken.material : null);
            UnityEngine.TextCore.Text.FontAsset afterRead = FontFixBridge.CjkUiFontAsset;
            if (afterRead != null && !ReferenceEquals(afterRead, broken))
            {
                return LastFontFixHeal = " (rebuilt by UITK Font Fix)";
            }
            if (afterRead != null && IsHealthy(afterRead)
                && InstanceIdOrZero(afterRead.material) != materialBefore)
            {
                AgentPanelWindow.MarkAllTextDirty();
                return LastFontFixHeal = " (repaired in place by UITK Font Fix)";
            }
            if (FontFixBridge.RequestRebuild())
            {
                return LastFontFixHeal = " (rebuilt via UITK Font Fix ResetCaches)";
            }
            return LastFontFixHeal = string.Empty;
        }

        /// <summary>
        /// Instance id of <paramref name="obj"/>, or 0 for a true null.
        /// Works on a DESTROYED object too (the id is cached managed
        /// state), which is what lets a material swap be detected when
        /// the previous material died with the scene.
        /// </summary>
        private static int InstanceIdOrZero(Object obj)
        {
            if (ReferenceEquals(obj, null))
            {
                return 0;
            }
            try
            {
                return obj.GetInstanceID();
            }
            catch (System.Exception)
            {
                return 0;
            }
        }

        /// <summary>True when the material or any USED atlas texture is alive but not HideAndDontSave (the guard's cheap scan; used slots only, bounded by atlasTextureCount).</summary>
        internal static bool HasUnstampedUsedSubObject(UnityEngine.TextCore.Text.FontAsset asset)
        {
            if (asset == null)
            {
                return false;
            }
            Material material = asset.material;
            if (material != null && !IsProtected(material.hideFlags))
            {
                return true;
            }
            Texture2D[] textures = asset.atlasTextures;
            if (textures == null)
            {
                return false;
            }
            int used = asset.atlasTextureCount;
            for (int i = 0; i < used && i < textures.Length; i++)
            {
                Texture2D texture = textures[i];
                if (texture != null && !IsProtected(texture.hideFlags))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Installs the atlas guard on EditorApplication.update, once per
        /// domain load. Never unsubscribed: the callback is a single int
        /// compare once an asset is resolved and an immediate null check
        /// when none is, and this class is static with no lifetime to
        /// piggyback on.
        ///
        /// The same tick also runs from the scene-lifecycle callbacks
        /// (new scene created, scene opened). The update hook alone is
        /// what keeps the asset healthy; these run the tick one step
        /// EARLIER: NewScene destroys unflagged objects synchronously and
        /// the editor repaints the panel before the next update tick,
        /// so an asset that did lose a page threw once from
        /// UIRStylePainter before the guard could heal it. Ticking inside
        /// the callback heals before that repaint.
        /// </summary>
        private static void EnsureAtlasGuardInstalled()
        {
            if (_atlasGuardInstalled)
            {
                return;
            }
            _atlasGuardInstalled = true;
            EditorApplication.update += AtlasGuardUpdate;
            EditorSceneManager.newSceneCreated += AtlasGuardOnNewSceneCreated;
            EditorSceneManager.sceneOpened += AtlasGuardOnSceneOpened;
        }

        private static void AtlasGuardUpdate()
        {
            AtlasGuardTickOnce();
        }

        private static void AtlasGuardOnNewSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            AtlasGuardTickOnce();
        }

        private static void AtlasGuardOnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            AtlasGuardTickOnce();
        }

        private static UnityEngine.TextCore.Text.FontAsset CreateOsFontAsset(string familyName)
        {
            try
            {
                UnityEngine.TextCore.Text.FontAsset asset =
                    UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(familyName, "Regular");
                if (asset != null)
                {
                    // Tagged + HideAndDontSave, but deliberately NEVER
                    // destroyed on beforeAssemblyReload: measured to
                    // survive a real domain reload intact and remain
                    // usable (see JapaneseUiAssetMarkerName doc), so
                    // destroying it here would race any label still
                    // holding it via unityFontDefinition. ResolveJapaneseUi
                    // re-finds and reuses this exact object after reload.
                    //
                    // This flag covers the PARENT only. Its material and
                    // atlas textures are born with hideFlags = None and
                    // need StampSubObjects, which ResolveJapaneseUi applies
                    // via AdoptSubObjects on both resolve paths -- see that
                    // method for why the parent surviving alone is worse
                    // than useless.
                    asset.name = JapaneseUiAssetMarkerName;
                    asset.hideFlags = HideFlags.HideAndDontSave;
                }
                return asset;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static Font LoadEditorFont(string path)
        {
            try
            {
                return EditorGUIUtility.Load(path) as Font;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Marks an OS-created dynamic mono Font as HideAndDontSave so a
        /// scene unload never destroys it out from under live labels.
        /// Idempotent (writes only on a flag difference), mirroring
        /// StampSubObjects. Font.CreateDynamicFontFromOSFont manages a
        /// single internal atlas (no multi-atlas growth), so stamping the
        /// parent Font is sufficient -- no recurring guard needed.
        /// </summary>
        internal static void StampOsFont(Font font)
        {
            if (font != null && font.hideFlags != HideFlags.HideAndDontSave)
            {
                font.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        private static Font CreateOsFont(string name)
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(name, ProbePointSize);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static string[] InstalledOsFontNames()
        {
            try
            {
                return Font.GetOSInstalledFontNames() ?? new string[0];
            }
            catch (System.Exception)
            {
                return new string[0];
            }
        }

        private static Font DefaultLabelFont()
        {
            try
            {
                if (EditorStyles.label != null && EditorStyles.label.font != null)
                {
                    return EditorStyles.label.font;
                }
            }
            catch (System.Exception)
            {
                // EditorStyles may be unavailable extremely early; fall
                // through to the built-in runtime font.
            }
            try
            {
                return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// TextCore face probe: exactly the load UI Toolkit performs when
        /// a FontDefinition renders, so a font that fails here is the one
        /// that used to log "Unable to load font face" and draw nothing.
        /// If the probe machinery itself is unavailable the candidate is
        /// accepted (cannot disprove usability).
        /// </summary>
        private static bool FaceLoads(Font font)
        {
            try
            {
                FontEngine.InitializeFontEngine(); // Idempotent.
                return FontEngine.LoadFontFace(font, ProbePointSize)
                    == FontEngineError.Success;
            }
            catch (System.Exception)
            {
                return true;
            }
        }

        private static bool ContainsIgnoreCase(string[] values, string name)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], name,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

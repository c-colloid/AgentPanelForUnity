using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Optional bridge to the UITK Font Fix package
    /// (jp.colloid.uitk-font-fix, static class Colloid.UitkFontFix.FontFix).
    /// The panel keeps zero package dependencies, so the package is
    /// located by reflection at first use: when it is installed, its
    /// resolved Latin+CJK UI FontAsset -- which already honours the
    /// project's FontFixSettings / settings.json (family list, face
    /// style, bold-face wiring) -- is what FontLoader hands to every
    /// panel window instead of the panel's own hard-coded family chain.
    /// Without the package every member reads null/empty and FontLoader
    /// behaves exactly as before.
    ///
    /// Only the CJK UI font is bridged. The mono font is deliberately
    /// not: FontFix destroys an OS-derived mono Font on invalidation and
    /// expects subscribers to re-apply every leaf, while the panel's mono
    /// assignments live inline on many leaves (code blocks, tool
    /// previews) that nothing re-visits. The panel's own RobotoMono
    /// resolution is stable and identical to FontFix's default anyway.
    ///
    /// FontFix.CachesInvalidated (settings changed in its UI or by code,
    /// ResetCaches, rebuild after unrepairable damage) is forwarded as
    /// <see cref="Invalidated"/> so FontLoader can drop its cached
    /// reference and the open windows re-apply the new asset.
    /// </summary>
    internal static class FontFixBridge
    {
        /// <summary>Assembly-qualified name of the FontFix facade.</summary>
        public const string ProviderTypeName =
            "Colloid.UitkFontFix.FontFix, Colloid.UitkFontFix.Editor";

        /// <summary>Prefix FontLoader puts on a source string that came through this bridge.</summary>
        public const string SourcePrefix = "fontfix:";

        private static bool _resolved;
        private static Type _provider;
        private static PropertyInfo _cjkAsset;
        private static PropertyInfo _cjkSource;
        private static EventInfo _invalidatedEvent;
        private static MethodInfo _resetCaches;
        private static Delegate _handler;

        /// <summary>Raised when the provider reports its caches invalidated.</summary>
        public static event Action Invalidated;

        /// <summary>True when the FontFix facade was found with a compatible shape.</summary>
        public static bool IsAvailable
        {
            get { return Provider != null; }
        }

        /// <summary>
        /// FontFix.CjkUiFontAsset, or null when the package is absent or
        /// nothing resolved. Read it each time rather than caching for
        /// long: the provider repairs a damaged asset in place on this
        /// read (same instance) and raises Invalidated when it must
        /// hand out a new one.
        /// </summary>
        public static FontAsset CjkUiFontAsset
        {
            get
            {
                // Bind BEFORE naming the property field: on the very
                // first access the field is still null until Provider
                // has run, and evaluating it as an argument first would
                // silently read nothing (the 2026-09-06 GUI regression).
                if (Provider == null)
                {
                    return null;
                }
                return Read(_cjkAsset) as FontAsset;
            }
        }

        /// <summary>FontFix.CjkUiFontSource ("osasset:&lt;family&gt;"), or empty.</summary>
        public static string CjkUiFontSource
        {
            get
            {
                if (Provider == null)
                {
                    return string.Empty;
                }
                return (Read(_cjkSource) as string) ?? string.Empty;
            }
        }

        /// <summary>
        /// True when the provider exposes FontFix.ResetCaches, i.e.
        /// <see cref="RequestRebuild"/> can do something.
        /// </summary>
        public static bool CanRequestRebuild
        {
            get { return Provider != null && _resetCaches != null; }
        }

        /// <summary>
        /// Asks FontFix to drop and rebuild its cached fonts
        /// (FontFix.ResetCaches). Used when the atlas guard finds the
        /// bridged asset with a destroyed material or atlas page: FontFix
        /// repairs such damage IN PLACE on its next read (same instance),
        /// but UI Toolkit keys each TextElement's cached text generation
        /// on the FontAsset instance, so a same-instance repair leaves
        /// every already-drawn label holding the dead atlas until
        /// something else dirties it. A rebuild hands out a NEW instance
        /// and raises CachesInvalidated (forwarded as
        /// <see cref="Invalidated"/>), which re-applies every panel root
        /// and thereby regenerates every label. Returns false when the
        /// package is absent or its facade lacks ResetCaches, in which
        /// case the caller falls back to a plain re-read.
        /// </summary>
        public static bool RequestRebuild()
        {
            if (Provider == null || _resetCaches == null)
            {
                return false;
            }
            try
            {
                _resetCaches.Invoke(null, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Tests: replaces the Type.GetType lookup so the lazy first-access path can be exercised.</summary>
        internal static Func<Type> LocatorForTests;

        private static Type Provider
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    Type type = null;
                    try
                    {
                        type = LocatorForTests != null
                            ? LocatorForTests()
                            : Type.GetType(ProviderTypeName, false);
                    }
                    catch (Exception)
                    {
                        type = null;
                    }
                    Bind(type);
                }
                return _provider;
            }
        }

        /// <summary>
        /// Tests: substitute a stand-in static type carrying the same
        /// members (null = simulate the package being absent).
        /// </summary>
        internal static void UseProviderForTests(Type type)
        {
            Unhook();
            _resolved = true;
            Bind(type);
        }

        /// <summary>Tests: forget any override so the next access re-locates the real package.</summary>
        internal static void ResetForTests()
        {
            Unhook();
            LocatorForTests = null;
            _resolved = false;
            _provider = null;
        }

        private static void Bind(Type type)
        {
            _provider = null;
            _cjkAsset = null;
            _cjkSource = null;
            _invalidatedEvent = null;
            _resetCaches = null;
            if (type == null)
            {
                return;
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            PropertyInfo asset = type.GetProperty("CjkUiFontAsset", flags);
            if (asset == null || !typeof(FontAsset).IsAssignableFrom(asset.PropertyType))
            {
                // A future FontFix whose facade changed shape: stay
                // silent and let FontLoader use its own chain.
                return;
            }
            _cjkAsset = asset;
            _cjkSource = type.GetProperty("CjkUiFontSource", flags);
            _invalidatedEvent = type.GetEvent("CachesInvalidated", flags);
            _resetCaches = type.GetMethod("ResetCaches", flags, null, Type.EmptyTypes, null);
            _provider = type;
            Hook();
        }

        private static void Hook()
        {
            if (_invalidatedEvent == null || _handler != null)
            {
                return;
            }
            try
            {
                MethodInfo target = typeof(FontFixBridge).GetMethod("OnProviderInvalidated",
                    BindingFlags.NonPublic | BindingFlags.Static);
                _handler = Delegate.CreateDelegate(_invalidatedEvent.EventHandlerType, target);
                _invalidatedEvent.AddEventHandler(null, _handler);
            }
            catch (Exception)
            {
                _handler = null;
            }
        }

        private static void Unhook()
        {
            if (_invalidatedEvent != null && _handler != null)
            {
                try
                {
                    _invalidatedEvent.RemoveEventHandler(null, _handler);
                }
                catch (Exception)
                {
                }
            }
            _handler = null;
        }

        private static void OnProviderInvalidated()
        {
            Action handler = Invalidated;
            if (handler != null)
            {
                handler();
            }
        }

        private static object Read(PropertyInfo property)
        {
            if (_provider == null || property == null)
            {
                return null;
            }
            try
            {
                object value = property.GetValue(null, null);
                return value;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

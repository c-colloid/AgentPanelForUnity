using System;
using System.Globalization;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using UnityEngine;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Current-language string table + resolution
    /// (docs/design-notes/2026-08-01-i18n.md). `L10n.S.SomeField` reads the
    /// CURRENT catalog; `L10n.F` wraps string.Format with
    /// CultureInfo.InvariantCulture for the `{0}`/`{1}` format fields on
    /// UiStrings.
    ///
    /// CRITICAL test-determinism rule (this dev machine's OS language is
    /// Japanese; EditMode tests that build real AgentPanelWindow/
    /// PermissionWindow instances and assert English strings must never see
    /// them flip to Japanese just because the batch-mode process happens to
    /// run under a Japanese OS):
    /// <list type="bullet">
    /// <item>The default -- before ANYTHING explicitly applies a language --
    /// is English. Nothing here reads Application.systemLanguage as a
    /// static/module-load side effect.</item>
    /// <item>PanelSettings.language (Auto/English/Japanese) is only ever
    /// applied by an explicit <see cref="ApplyFromSettings"/> call.
    /// SettingsView's language dropdown (a real user action) is the only
    /// production call site added in this stage -- nothing reads
    /// PanelSettings.language automatically from CreateGUI, OnEnable, or
    /// any other implicit lifecycle hook yet.</item>
    /// <item>PanelLanguage.Auto resolves Application.systemLanguage LAZILY
    /// -- i.e. only inside <see cref="Resolve"/>/<see cref="ResolveAuto"/>,
    /// called from ApplyFromSettings -- never in a static field initializer
    /// or a ScriptableObject ctor/field initializer (the exact regression
    /// class as PanelSettings.preferCjkUiFont's f445c22 window-breaking
    /// bug).</item>
    /// <item><see cref="OverrideForTests"/> is the seam individual tests use
    /// to pin a language deterministically regardless of any of the
    /// above.</item>
    /// </list>
    /// </summary>
    public static class L10n
    {
        private static UiStrings _current = new UiStrings();
        private static PanelLanguage _effective = PanelLanguage.English;
        private static PanelLanguage? _testOverride;

        /// <summary>The current catalog. Starts English; see the class doc comment.</summary>
        public static UiStrings S
        {
            get { return _current; }
        }

        /// <summary>The resolved language backing <see cref="S"/> right now.</summary>
        public static PanelLanguage EffectiveLanguage
        {
            get { return _effective; }
        }

        /// <summary>
        /// Raised whenever the EFFECTIVE (resolved) language actually
        /// changes via <see cref="ApplyFromSettings"/> -- never raised by
        /// <see cref="OverrideForTests"/>, which is a test-only seam, not a
        /// real user action. AgentPanelWindow subscribes once to rebuild
        /// every open panel window / floating permission window.
        /// </summary>
        public static event Action LanguageChanged;

        /// <summary>
        /// Applies PanelSettings.language, resolving Auto lazily against
        /// Application.systemLanguage right here (never earlier). This is
        /// the path meant to be wired to a real user action -- the Settings
        /// language dropdown -- never to a static initializer or a
        /// ScriptableObject OnEnable.
        /// </summary>
        public static void ApplyFromSettings(PanelLanguage settingLanguage)
        {
            if (_testOverride.HasValue)
            {
                // A test has pinned a language; a real settings change must
                // not silently override it out from under the test.
                return;
            }
            SetEffective(Resolve(settingLanguage), true);
        }

        /// <summary>
        /// Test seam: pins the effective language regardless of
        /// PanelSettings, and blocks <see cref="ApplyFromSettings"/> until
        /// cleared (pass null). Never raises <see cref="LanguageChanged"/>
        /// -- pinning a language for a test is not a "real" user action.
        /// </summary>
        internal static void OverrideForTests(PanelLanguage? language)
        {
            _testOverride = language;
            SetEffective(language.HasValue ? Resolve(language.Value) : PanelLanguage.English, false);
        }

        /// <summary>Auto resolves to the OS language (lazily); English/Japanese pass through unchanged.</summary>
        internal static PanelLanguage Resolve(PanelLanguage setting)
        {
            return setting == PanelLanguage.Auto ? ResolveAuto(Application.systemLanguage) : setting;
        }

        /// <summary>
        /// Pure Auto-resolution function, parameterized on SystemLanguage
        /// (same pattern as PanelSettings.ComputeDefaultPreferCjkUiFont) so
        /// tests can exercise every branch without reading the real
        /// Application.systemLanguage.
        /// </summary>
        internal static PanelLanguage ResolveAuto(SystemLanguage systemLanguage)
        {
            return systemLanguage == SystemLanguage.Japanese
                ? PanelLanguage.Japanese
                : PanelLanguage.English;
        }

        private static void SetEffective(PanelLanguage resolved, bool raiseEvent)
        {
            bool changed = resolved != _effective;
            _effective = resolved;
            _current = resolved == PanelLanguage.Japanese ? UiStringsJa.Create() : new UiStrings();
            if (changed && raiseEvent)
            {
                Action handler = LanguageChanged;
                if (handler != null)
                {
                    handler();
                }
            }
        }

        /// <summary>
        /// string.Format wrapper for every `{0}`/`{1}` UiStrings field --
        /// InvariantCulture on purpose (these are UI sentences being
        /// assembled from other UI strings/counts, not locale-sensitive
        /// number/date formatting).
        /// </summary>
        public static string F(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, A(format), args);
        }

        /// <summary>Placeholder every catalog string uses for the agent's name (design note 2026-09-10-agent-name-in-ui.md).</summary>
        public const string AgentPlaceholder = "{agent}";

        private static string _agentNameOverride;

        /// <summary>
        /// Test seam: pins <see cref="AgentName"/> regardless of settings.
        /// Null restores the live lookup.
        /// </summary>
        internal static string AgentNameOverride
        {
            get { return _agentNameOverride; }
            set { _agentNameOverride = value; }
        }

        /// <summary>
        /// What <see cref="AgentPlaceholder"/> expands to right now: the
        /// selected backend's short product name (Claude / Gemini /
        /// Codex / Grok), a custom ACP agent's self-reported name once
        /// it has introduced itself, else the localized generic word.
        /// Read at render time, never cached, so a backend switch is
        /// reflected by the next repaint.
        /// </summary>
        public static string AgentName
        {
            get
            {
                if (_agentNameOverride != null)
                {
                    return _agentNameOverride;
                }
                return ResolveAgentName(AgentHub.CurrentBackend, AgentHub.CurrentAcpAgentName, S.AgentGenericName);
            }
        }

        /// <summary>Pure: the name precedence behind <see cref="AgentName"/>.</summary>
        internal static string ResolveAgentName(AgentBackend backend, string selfReportedName, string genericName)
        {
            string name = AgentBackends.ShortName(backend);
            if (name.Length > 0)
            {
                return name;
            }
            if (!string.IsNullOrEmpty(selfReportedName))
            {
                return selfReportedName;
            }
            return genericName ?? string.Empty;
        }

        /// <summary>
        /// The backend's sign-in paths in one sentence -- which
        /// subscription login it takes, and which environment variable
        /// holds an API key instead (design note 2026-09-10-acp-auth-
        /// guidance-and-method-display.md section 3). Empty for Claude Code
        /// (the Account card owns that) and for a custom ACP agent, whose
        /// sign-in the panel cannot know.
        /// </summary>
        public static string AcpAuthSummary(AgentBackend backend)
        {
            return ResolveAcpAuthSummary(backend, S);
        }

        /// <summary>Pure: the catalog field behind <see cref="AcpAuthSummary"/>.</summary>
        internal static string ResolveAcpAuthSummary(AgentBackend backend, UiStrings strings)
        {
            if (strings == null)
            {
                return string.Empty;
            }
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return strings.AcpAuthSummaryGemini;
                case AgentBackend.CodexAcp:
                    return strings.AcpAuthSummaryCodex;
                case AgentBackend.GrokBuild:
                    return strings.AcpAuthSummaryGrok;
                default:
                    return string.Empty;
            }
        }

        /// <summary>Pure: the catalog field behind <see cref="AcpAuthTooltip"/>.</summary>
        internal static string ResolveAcpAuthDetail(AgentBackend backend, UiStrings strings)
        {
            if (strings == null)
            {
                return string.Empty;
            }
            switch (backend)
            {
                case AgentBackend.GeminiCli:
                    return strings.AcpAuthDetailGemini;
                case AgentBackend.CodexAcp:
                    return strings.AcpAuthDetailCodex;
                case AgentBackend.GrokBuild:
                    return strings.AcpAuthDetailGrok;
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The ONE short inline line under the Agent picker and on the
        /// first-run card: the backend's sign-in paths, or -- for a backend
        /// with no known paths, i.e. a custom ACP agent -- the terminal-
        /// login line that lived there before. The long explanation belongs
        /// in <see cref="AcpAuthTooltip"/> (docs/design-notes/2026-08-04-
        /// settings-annotation-load.md section 4).
        /// <paramref name="loginHintFormat"/> is the caller's own `{0}` =
        /// login-command string (Settings and the first-run card word it
        /// differently).
        /// </summary>
        public static string AcpAuthHint(AgentBackend backend, string loginHintFormat)
        {
            string summary = AcpAuthSummary(backend);
            return summary.Length > 0 ? summary : ComposeLoginHint(backend, loginHintFormat);
        }

        /// <summary>
        /// The hover half of <see cref="AcpAuthHint"/>: why the backend's
        /// sign-in paths are what they are, that the panel stores no API
        /// keys, and the terminal-login fallback. Empty when the backend has
        /// no detail to give, which is the caller's cue to set no tooltip.
        /// </summary>
        public static string AcpAuthTooltip(AgentBackend backend, string loginHintFormat)
        {
            string detail = ResolveAcpAuthDetail(backend, S);
            if (detail.Length == 0)
            {
                return string.Empty;
            }
            return ComposeAcpAuthHint(
                ComposeAcpAuthHint(detail, S.AcpAuthKeysNotStoredNote),
                ComposeLoginHint(backend, loginHintFormat));
        }

        private static string ComposeLoginHint(AgentBackend backend, string loginHintFormat)
        {
            string login = AgentBackends.LoginCommand(backend);
            return string.IsNullOrEmpty(login) || string.IsNullOrEmpty(loginHintFormat)
                ? string.Empty
                : F(loginHintFormat, login);
        }

        /// <summary>Pure: joins the backend's auth summary with the terminal-login hint; either half may be empty.</summary>
        internal static string ComposeAcpAuthHint(string summary, string loginHint)
        {
            if (string.IsNullOrEmpty(summary))
            {
                return loginHint ?? string.Empty;
            }
            if (string.IsNullOrEmpty(loginHint))
            {
                return summary;
            }
            return summary + " " + loginHint;
        }

        /// <summary>
        /// Expands <see cref="AgentPlaceholder"/> in a catalog string.
        /// <see cref="F"/> does this itself; call it for strings used
        /// without formatting (labels, tooltips, placeholders).
        /// </summary>
        public static string A(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(AgentPlaceholder, StringComparison.Ordinal) < 0)
            {
                return text;
            }
            return text.Replace(AgentPlaceholder, AgentName);
        }
    }
}

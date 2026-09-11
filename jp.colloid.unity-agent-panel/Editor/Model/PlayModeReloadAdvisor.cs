using UnityEditor;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Design note 2026-09-10 section 4: the panel never told the user
    /// whether entering/exiting Play Mode would cut a running turn short --
    /// that depends entirely on a PROJECT setting
    /// (EditorSettings.enterPlayModeOptionsEnabled /
    /// EditorSettings.enterPlayModeOptions) the panel does not own and,
    /// per that note's stated policy, must not silently change on the
    /// user's behalf. Without any visibility, a user hitting Play mid-turn
    /// either assumed the interruption was this package's doing or had no
    /// way to discover the one setting that would stop it short of
    /// enabling autoContinueInterruptedTurn as a workaround. This class is
    /// a pure predicate SettingsView reads to render a hint; it changes
    /// nothing.
    /// </summary>
    public static class PlayModeReloadAdvisor
    {
        /// <summary>
        /// True when entering/exiting Play Mode triggers a domain reload
        /// under the given project settings. This is exactly the
        /// predicate Tests/Editor/ReloadLifecycleLiveTests.cs (around line
        /// 155, MidTurn_EnterPlayMode_ResumesSameSession_FlagsMidTurn_
        /// ThenExitKeepsIt's own precondition assertion) uses to decide
        /// whether its "Play reloads the domain" assumption holds: a
        /// reload is skipped only when Enter Play Mode Options are enabled
        /// AND DisableDomainReload is one of the selected options.
        /// DisableSceneReload alone (options enabled, that flag NOT set)
        /// still reloads the domain -- only DisableDomainReload matters
        /// here.
        /// </summary>
        public static bool ReloadsOnPlay(bool enterPlayModeOptionsEnabled, EnterPlayModeOptions options)
        {
            return !(enterPlayModeOptionsEnabled
                && (options & EnterPlayModeOptions.DisableDomainReload) != 0);
        }

        /// <summary>
        /// <see cref="ReloadsOnPlay"/> against the CURRENT project
        /// settings, for SettingsView's live hint.
        /// </summary>
        public static bool ReloadsOnPlayNow()
        {
            return ReloadsOnPlay(EditorSettings.enterPlayModeOptionsEnabled, EditorSettings.enterPlayModeOptions);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Project-scoped persistent SETTINGS (survives editor restarts):
    /// PanelSettings plus small scalars only (ARCHITECTURE.md D5). Stored
    /// under UserSettings/ so it is per-user and outside Library/.
    ///
    /// The transcript display cache deliberately does NOT live here:
    /// message text (code blocks, brace-heavy JSON, very long lines)
    /// round-trips unreliably through UnityYAML ("Parser Failure ...
    /// Expected closing '}'") and one bad string used to corrupt this whole
    /// asset. Transcripts persist through SessionCacheFile (plain JSON via
    /// Core/Json) instead; keep it that way -- never add message-content
    /// strings to this asset (SessionCacheFileTests enforces this by
    /// reflection).
    ///
    /// SaveNow() writes the asset only when the settings snapshot actually
    /// changed, so hot paths (turn completion etc.) can call it for free.
    /// A corrupted asset on disk simply loads as defaults; the version
    /// check in OnEnable then schedules one clean rewrite, so a broken
    /// load is never cached forever.
    /// </summary>
    [FilePath("UserSettings/AgentPanel/State.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class PanelStateStore : ScriptableSingleton<PanelStateStore>
    {
        /// <summary>
        /// Serialized-layout version. v2 dropped the embedded ChatSession;
        /// bump on any future layout change.
        /// </summary>
        private const int CurrentStateVersion = 2;

        [SerializeField]
        private PanelSettings settings = new PanelSettings();

        /// <summary>
        /// 0 on legacy transcript-carrying assets AND on defaults created
        /// after a corrupt-asset load -- both get rewritten cleanly once.
        /// </summary>
        [SerializeField]
        private int stateVersion;

        /// <summary>Settings JSON as of the last disk write (null = must write).</summary>
        [NonSerialized]
        private string _lastSavedSettingsJson;

        /// <summary>
        /// MODEL-4: the Console-error ignore store's JSON sidecar. The two
        /// PanelSettings ignore fields are [NonSerialized] (raw Console
        /// message text must never ride UnityYAML -- the exact corruption
        /// class this asset's own class comment forbids), so OnEnable
        /// hydrates them from here and SaveNow persists them back
        /// (write-if-changed, so hot paths stay free).
        /// </summary>
        [NonSerialized]
        private ConsoleIgnoreFile _consoleIgnoreFile;

        private ConsoleIgnoreFile ConsoleIgnore
        {
            get
            {
                if (_consoleIgnoreFile == null)
                {
                    // The editor's cwd is the project root, matching this
                    // asset's own project-relative [FilePath].
                    _consoleIgnoreFile = ConsoleIgnoreFile.CreateDefault(null, Debug.LogWarning);
                }
                return _consoleIgnoreFile;
            }
        }

        /// <summary>Panel settings (mutate then call SaveNow()).</summary>
        public PanelSettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = new PanelSettings();
                }
                return settings;
            }
        }

        private void OnEnable()
        {
            // One-time system-language default for the CJK UI font.
            // MUST live here and not in a PanelSettings field initializer:
            // Application.systemLanguage throws inside a ScriptableObject
            // constructor/field initializer during singleton load (live
            // regression 2026-07-31 that broke the whole window).
            bool cjkDefaultApplied =
                Settings.EnsureCjkUiFontDefault(Application.systemLanguage);

            // Modules that became default-ON in a newer build must reach an
            // asset persisted by an older one -- a field initializer never
            // does (the stored list deserializes over it). Generation-gated
            // so a module the user switched OFF stays off.
            bool moduleDefaultsApplied = Settings.EnsureUapOpsModuleDefaults();

            // v0.16.0 replaced the boolean "auto-approve read-only ops"
            // toggle with a four-level auto-approve setting. Seeding it from
            // the old boolean has to happen HERE, on load, for the same
            // reason the module defaults do: a field initializer is
            // overwritten by the deserialized asset, so an upgrading user
            // would silently land on the new field's default instead of the
            // level matching what they had chosen. Generation-gated, so a
            // level the user picks afterwards is never re-seeded.
            bool autoApproveMigrated = Settings.EnsureAutoApproveLevelMigrated();

            // MODEL-4: hydrate the [NonSerialized] ignore fields from their
            // JSON sidecar, then fold in whatever an older build left
            // serialized inside THIS asset (one-time; clears the legacy
            // holders so the free text leaves the YAML on the rewrite
            // below). Order matters: sidecar first, so the migration's
            // "live store wins" precedence sees it.
            List<string> sidecarErrors;
            string sidecarPatterns;
            if (ConsoleIgnore.Load(out sidecarErrors, out sidecarPatterns))
            {
                Settings.ignoredConsoleErrors = sidecarErrors;
                Settings.ignoredConsoleErrorPatterns = sidecarPatterns;
            }
            bool consoleIgnoresMigrated = Settings.MigrateLegacyConsoleIgnores();
            if (consoleIgnoresMigrated)
            {
                ConsoleIgnore.Save(Settings.ignoredConsoleErrors,
                    Settings.ignoredConsoleErrorPatterns);
            }

            if (stateVersion != CurrentStateVersion || cjkDefaultApplied
                || moduleDefaultsApplied || autoApproveMigrated
                || consoleIgnoresMigrated)
            {
                // Legacy layout, a corrupted asset that loaded as
                // defaults, or a freshly-decided CJK default: rewrite the
                // asset cleanly once. Deferred off the load path;
                // _lastSavedSettingsJson stays null so the deferred
                // SaveNow always writes.
                stateVersion = CurrentStateVersion;
                EditorApplication.delayCall += SaveNow;
            }
            else
            {
                _lastSavedSettingsJson = JsonUtility.ToJson(Settings);
            }
        }

        /// <summary>
        /// Writes the asset (text-serialized) when the settings actually
        /// changed since the last write; otherwise a no-op. The
        /// console-ignore sidecar is saved FIRST and unconditionally
        /// (write-if-changed internally): its fields are [NonSerialized],
        /// so an ignore-only mutation never changes the asset snapshot
        /// below -- gating the sidecar behind that check would silently
        /// drop exactly the saves the ignore feature calls SaveNow for.
        /// </summary>
        public void SaveNow()
        {
            ConsoleIgnore.Save(Settings.ignoredConsoleErrors,
                Settings.ignoredConsoleErrorPatterns);
            string snapshot = JsonUtility.ToJson(Settings);
            if (snapshot == _lastSavedSettingsJson)
            {
                return;
            }
            Save(true);
            _lastSavedSettingsJson = snapshot;
        }
    }
}

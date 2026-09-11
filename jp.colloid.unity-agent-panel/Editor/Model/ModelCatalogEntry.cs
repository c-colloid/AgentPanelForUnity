using System;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One cached entry from the CLI's system/init "initialize"
    /// control_response models[] catalog (docs/design-notes/2026-08-01-
    /// model-settings.md section 1): just enough to populate the Settings
    /// "Default model" dropdown (and the per-agent override rows) across
    /// editor restarts, before any connection this session has re-supplied
    /// the live list. HeaderView.ParseModels is the richer, session-only
    /// parse of the same wire data; this is its YAML-safe persisted subset.
    ///
    /// Through v0.10.0 this subset was value/displayName only, deliberately
    /// dropping the free-text description field some catalog entries carry.
    /// v0.11.0 (docs/design-notes/2026-08-02-subagent-model-precedence.md
    /// section 3.1) added resolvedModel/description below, additive and
    /// serialization-compatible with any cache written before this change
    /// (JsonUtility/UnityYAML fill a missing field with its declared
    /// default -- empty string here -- rather than failing to load). These
    /// let the Settings "Default model" dropdown show the TRUTH about what
    /// the server-defined "default" catalog entry actually resolves to
    /// (R07 section 13: nothing client-side can retarget it, only display
    /// it honestly).
    /// </summary>
    [Serializable]
    public sealed class ModelCatalogEntry
    {
        /// <summary>The --model / set_model wire value (e.g. "sonnet").</summary>
        public string value = string.Empty;

        /// <summary>Human-readable label for the dropdown; falls back to value when empty.</summary>
        public string displayName = string.Empty;

        /// <summary>The resolved model id this entry currently targets (e.g. "claude-opus-5[1m]"), matches SystemInitMessage.Model/HeaderView.ModelOption.ResolvedModel. Empty for entries cached before v0.11.0.</summary>
        public string resolvedModel = string.Empty;

        /// <summary>Free-text server-provided description (e.g. pricing/positioning blurb), matches HeaderView.ModelOption.Description. Empty for entries cached before v0.11.0.</summary>
        public string description = string.Empty;
    }
}

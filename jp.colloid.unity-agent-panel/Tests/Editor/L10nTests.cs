using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Regression guards for the L10n mechanism
    /// (docs/design-notes/2026-08-01-i18n.md #3):
    /// <list type="bullet">
    /// <item>reflection parity -- every string field on UiStrings is
    /// non-null/non-empty in BOTH catalogs (en and ja);</item>
    /// <item>placeholder-set equality -- each field's `{N}` set matches
    /// between en and ja, so a translation can never trigger a
    /// FormatException at runtime;</item>
    /// <item>OverrideForTests round-trips and blocks ApplyFromSettings while
    /// active;</item>
    /// <item>the default (absent any override) is English, even though this
    /// machine's OS language is Japanese -- see L10n's class doc comment
    /// for why that matters.</item>
    /// </list>
    /// Every test resets L10n to a known state ([SetUp]/[TearDown]) so this
    /// fixture's outcome never depends on what any other fixture (or test
    /// run order) did to L10n's global static state first.
    /// </summary>
    public class L10nTests
    {
        private static readonly Regex PlaceholderPattern = new Regex(@"\{(\d+)\}");

        [SetUp]
        public void SetUp()
        {
            L10n.OverrideForTests(null);
        }

        [TearDown]
        public void TearDown()
        {
            L10n.OverrideForTests(null);
        }

        private static FieldInfo[] StringFields()
        {
            FieldInfo[] fields = typeof(UiStrings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            var stringFields = new List<FieldInfo>();
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    stringFields.Add(field);
                }
            }
            return stringFields.ToArray();
        }

        // ==================================================================
        // Settings inline-hint length guard (docs/design-notes/2026-08-04-
        // settings-annotation-load.md #5): the settings panel's inline
        // annotations grew to 1180px (40% of the cards' height) because
        // design rationale kept being written into hint strings next to
        // checkboxes -- the worst single field was 387 characters. The fix
        // moves long text into Settings*Tooltip fields, which SettingsView
        // attaches to the field's row as a hover tooltip; this section makes
        // sure the next long string written into a hint field fails a test
        // instead of shipping.
        // ==================================================================

        /// <summary>Inline hint/help cap in characters. Applies to both catalogs.</summary>
        private const int HintCharCap = 110;

        /// <summary>
        /// Looser cap for the three allowlisted safety warnings below -- still
        /// bounded, just not to inline-hint size, so "exempt" cannot drift into
        /// "unbounded".
        /// </summary>
        private const int ExemptHintCharCap = 220;

        /// <summary>
        /// Fields exempt from HintCharCap because their annotation is a
        /// WARNING about what enabling the setting does, not design
        /// rationale -- a warning nobody can read without hovering is not a
        /// warning (design note #4). Exemption is by exact name on purpose;
        /// widen this list deliberately, never by loosening the general cap.
        /// </summary>
        private static readonly string[] ExemptHintFieldNames =
        {
            "SettingsUapOpsGateEnabledHint",
            "SettingsAutoApproveHelp",
            "SettingsAutoContinueHelp",
        };

        /// <summary>
        /// True for a field the settings panel renders as an inline hint --
        /// name starts with "Settings" and ends with "Hint", "HintFmt" or
        /// "Help". Settings*Tooltip fields (the long-form destination) must
        /// NOT match this: "Tooltip" does not end with any of those three
        /// suffixes, so no separate exclusion is needed, but
        /// SettingsTooltipFields_AreNotCapped_ByTheHintFilter below checks
        /// that claim against the real fields rather than just this comment.
        /// </summary>
        private static bool IsSettingsHintField(string fieldName)
        {
            if (!fieldName.StartsWith("Settings", System.StringComparison.Ordinal))
            {
                return false;
            }
            return fieldName.EndsWith("Hint", System.StringComparison.Ordinal)
                || fieldName.EndsWith("HintFmt", System.StringComparison.Ordinal)
                || fieldName.EndsWith("Help", System.StringComparison.Ordinal);
        }

        private static bool IsExemptHintField(string fieldName)
        {
            foreach (string exempt in ExemptHintFieldNames)
            {
                if (fieldName == exempt)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Turns e.g. "SettingsUapOpsStagingFolderHintFmt" into "SettingsUapOpsStagingFolderTooltip", for failure messages only.</summary>
        private static string SuggestedTooltipFieldName(string hintFieldName)
        {
            string baseName = hintFieldName;
            if (baseName.EndsWith("HintFmt", System.StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - "HintFmt".Length);
            }
            else if (baseName.EndsWith("Hint", System.StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - "Hint".Length);
            }
            else if (baseName.EndsWith("Help", System.StringComparison.Ordinal))
            {
                baseName = baseName.Substring(0, baseName.Length - "Help".Length);
            }
            return baseName + "Tooltip";
        }

        [Test]
        public void HintFieldFilter_MatchesSettingsHintHintFmtAndHelpSuffixesOnly()
        {
            // Would still pass under the obvious wrong way: checking
            // fieldName.Contains("Settings") instead of StartsWith would
            // wrongly sweep in a field that merely mentions "Settings" in
            // the middle of its name; checking EndsWith("Hint") alone
            // (without a separate EndsWith("HintFmt") branch) would silently
            // stop catching every "...HintFmt" field, since "HintFmt" does
            // not itself end with "Hint".
            Assert.IsTrue(IsSettingsHintField("SettingsFooHint"));
            Assert.IsTrue(IsSettingsHintField("SettingsFooHintFmt"));
            Assert.IsTrue(IsSettingsHintField("SettingsFooHelp"));
            Assert.IsFalse(IsSettingsHintField("SettingsFooTooltip"));
            Assert.IsFalse(IsSettingsHintField("SettingsFooLabel"));
            Assert.IsFalse(IsSettingsHintField("FooHint"));
            Assert.IsFalse(IsSettingsHintField("XSettingsFooHint"));
        }

        [Test]
        public void SettingsTooltipFields_AreNotCapped_ByTheHintFilter()
        {
            // Would still pass under the obvious wrong way: a filter written
            // as fieldName.Contains("Hint") || fieldName.Contains("Help")
            // happens to also exclude "Tooltip" names (neither substring
            // appears in "Tooltip"), so a synthetic-name check alone cannot
            // prove this claim -- this walks the REAL UiStrings fields so a
            // future Settings*Tooltip field with an unexpected name shape
            // still gets checked here, not just reasoned about in a comment.
            foreach (FieldInfo field in StringFields())
            {
                if (!field.Name.StartsWith("Settings", System.StringComparison.Ordinal)
                    || !field.Name.EndsWith("Tooltip", System.StringComparison.Ordinal))
                {
                    continue;
                }
                Assert.IsFalse(IsSettingsHintField(field.Name),
                    field.Name + " ends with Tooltip but matched the inline-hint suffix filter --"
                    + " Settings*Tooltip fields are the long-form destination and must never be capped.");
            }
        }

        [Test]
        public void SettingsHintFields_StayShort_InBothCatalogs()
        {
            // Would still pass under the obvious wrong way: checking only
            // the English catalog would miss a Japanese-only regression,
            // since a translation can independently grow past the cap even
            // when the English source line stays short.
            var en = new UiStrings();
            var ja = UiStringsJa.Create();
            foreach (FieldInfo field in StringFields())
            {
                if (!IsSettingsHintField(field.Name) || IsExemptHintField(field.Name))
                {
                    continue;
                }
                AssertHintWithinCap(field, (string)field.GetValue(en), "en", HintCharCap, false);
                AssertHintWithinCap(field, (string)field.GetValue(ja), "ja", HintCharCap, false);
            }
        }

        [Test]
        public void ExemptSettingsHintFields_StayWithinLooserCap_InBothCatalogs()
        {
            // "Exempt" from the 110-char inline cap must not mean unbounded --
            // these three are read as warnings, not essays, so they get their
            // own cap instead of no cap at all.
            var en = new UiStrings();
            var ja = UiStringsJa.Create();
            foreach (FieldInfo field in StringFields())
            {
                if (!IsExemptHintField(field.Name))
                {
                    continue;
                }
                AssertHintWithinCap(field, (string)field.GetValue(en), "en", ExemptHintCharCap, true);
                AssertHintWithinCap(field, (string)field.GetValue(ja), "ja", ExemptHintCharCap, true);
            }
        }

        private static void AssertHintWithinCap(FieldInfo field, string value, string catalog, int cap, bool isExempt)
        {
            string advice = isExempt
                ? field.Name + " is an explicit safety-warning exemption (see ExemptHintFieldNames in this"
                    + " file) with its own " + ExemptHintCharCap + "-char cap -- shorten the warning itself;"
                    + " do not move it to a tooltip, since a warning nobody can read without hovering is not"
                    + " a warning."
                : "Move the long explanation into a new " + SuggestedTooltipFieldName(field.Name)
                    + " field -- SettingsView attaches a Settings*Tooltip field to the row as a hover"
                    + " tooltip -- and leave one short line inline in " + field.Name + ".";
            Assert.LessOrEqual(value.Length, cap,
                field.Name + " (" + catalog + ") is " + value.Length + " chars, over the " + cap
                + "-char cap. " + advice);
        }

        [Test]
        public void ExemptHintFieldAllowlist_NamesExist_OnUiStrings()
        {
            // Would still pass under the obvious wrong way: a version of
            // this fixture that only ever reads ExemptHintFieldNames to
            // decide what to SKIP (as SettingsHintFields_StayShort_-
            // InBothCatalogs does above) would never notice a typo'd or
            // renamed entry, because skipping a name that matches nothing
            // is indistinguishable from skipping a name that matches the
            // intended field -- both skip zero fields either way. This test
            // instead resolves each entry against UiStrings directly, so a
            // stale allowlist entry (dead exemption, hiding the very
            // regression it was meant to permit) fails loudly here.
            foreach (string name in ExemptHintFieldNames)
            {
                FieldInfo field = typeof(UiStrings).GetField(name, BindingFlags.Public | BindingFlags.Instance);
                Assert.IsNotNull(field,
                    "Allowlist entry '" + name + "' does not match any field on UiStrings"
                    + " (typo or rename) -- fix ExemptHintFieldNames in L10nTests.cs.");
                Assert.AreEqual(typeof(string), field.FieldType,
                    "Allowlist entry '" + name + "' matches a field on UiStrings that is not a string.");
            }
        }

        [Test]
        public void FieldCount_HasNotSilentlyShrunk()
        {
            // Loose lower bound (not an exact count) so adding new UI
            // strings later does not require touching this assertion --
            // it only guards against the field list being accidentally
            // gutted (e.g. a bad merge). Measured at 423 fields when this
            // comment was written (docs/design-notes/2026-08-04-settings-
            // annotation-load.md's Settings*Tooltip split adds roughly 18
            // more on top of that); 300 stays comfortably below both the
            // measured count and the post-split count, so this does not
            // need to move again for that change specifically. If it ever
            // needs to move for a real trim, raise it -- never lower it.
            Assert.GreaterOrEqual(StringFields().Length, 300,
                "UiStrings fields moved or were trimmed unexpectedly");
        }

        [Test]
        public void AllStringFields_AreNonEmpty_InBothCatalogs()
        {
            var en = new UiStrings();
            var ja = UiStringsJa.Create();
            foreach (FieldInfo field in StringFields())
            {
                var enValue = (string)field.GetValue(en);
                var jaValue = (string)field.GetValue(ja);
                Assert.IsFalse(string.IsNullOrEmpty(enValue), field.Name + " (en)");
                Assert.IsFalse(string.IsNullOrEmpty(jaValue), field.Name + " (ja)");
            }
        }

        [Test]
        public void PlaceholderSets_MatchBetweenEnAndJa_ForEveryField()
        {
            var en = new UiStrings();
            var ja = UiStringsJa.Create();
            foreach (FieldInfo field in StringFields())
            {
                var enValue = (string)field.GetValue(en);
                var jaValue = (string)field.GetValue(ja);
                HashSet<string> enPlaceholders = ExtractPlaceholders(enValue);
                HashSet<string> jaPlaceholders = ExtractPlaceholders(jaValue);
                CollectionAssert.AreEquivalent(enPlaceholders, jaPlaceholders,
                    field.Name + ": en=" + enValue + " ja=" + jaValue);
            }
        }

        private static HashSet<string> ExtractPlaceholders(string text)
        {
            var result = new HashSet<string>();
            foreach (Match match in PlaceholderPattern.Matches(text))
            {
                result.Add(match.Groups[1].Value);
            }
            return result;
        }

        [Test]
        public void Default_IsEnglish_WithoutOverride()
        {
            Assert.AreEqual(PanelLanguage.English, L10n.EffectiveLanguage);
            Assert.AreEqual("Settings", L10n.S.SettingsTitle);
        }

        [Test]
        public void OverrideForTests_SwitchesToJapanese_AndRoundTrips()
        {
            L10n.OverrideForTests(PanelLanguage.Japanese);
            Assert.AreEqual(PanelLanguage.Japanese, L10n.EffectiveLanguage);
            Assert.AreNotEqual("Settings", L10n.S.SettingsTitle);

            L10n.OverrideForTests(PanelLanguage.English);
            Assert.AreEqual(PanelLanguage.English, L10n.EffectiveLanguage);
            Assert.AreEqual("Settings", L10n.S.SettingsTitle);

            L10n.OverrideForTests(null);
            Assert.AreEqual(PanelLanguage.English, L10n.EffectiveLanguage);
            Assert.AreEqual("Settings", L10n.S.SettingsTitle);
        }

        [Test]
        public void ApplyFromSettings_IsIgnored_WhileOverrideIsActive()
        {
            L10n.OverrideForTests(PanelLanguage.English);
            L10n.ApplyFromSettings(PanelLanguage.Japanese);
            Assert.AreEqual(PanelLanguage.English, L10n.EffectiveLanguage);
            Assert.AreEqual("Settings", L10n.S.SettingsTitle);
        }

        [Test]
        public void ApplyFromSettings_ResolvesExplicitLanguages_WhenNoOverride()
        {
            L10n.ApplyFromSettings(PanelLanguage.Japanese);
            Assert.AreEqual(PanelLanguage.Japanese, L10n.EffectiveLanguage);

            L10n.ApplyFromSettings(PanelLanguage.English);
            Assert.AreEqual(PanelLanguage.English, L10n.EffectiveLanguage);
        }

        [Test]
        public void ResolveAuto_Japanese_ResolvesToJapanese()
        {
            Assert.AreEqual(PanelLanguage.Japanese, L10n.ResolveAuto(SystemLanguage.Japanese));
        }

        [Test]
        public void ResolveAuto_English_ResolvesToEnglish()
        {
            Assert.AreEqual(PanelLanguage.English, L10n.ResolveAuto(SystemLanguage.English));
        }

        [Test]
        public void ResolveAuto_OtherLanguage_ResolvesToEnglishFallback()
        {
            Assert.AreEqual(PanelLanguage.English, L10n.ResolveAuto(SystemLanguage.French));
        }

        [Test]
        public void Resolve_NonAutoSettings_PassThroughUnchanged()
        {
            Assert.AreEqual(PanelLanguage.English, L10n.Resolve(PanelLanguage.English));
            Assert.AreEqual(PanelLanguage.Japanese, L10n.Resolve(PanelLanguage.Japanese));
        }

        [Test]
        public void F_FormatsWithInvariantCulture()
        {
            Assert.AreEqual("3 tools (2s)", L10n.F("{0} tools ({1}s)", 3, 2));
        }
    }
}

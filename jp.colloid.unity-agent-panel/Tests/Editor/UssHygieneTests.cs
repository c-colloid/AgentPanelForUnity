using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// USS hygiene regression guards (Fix round: eliminate the
    /// priority-flag CSS keyword from every .uss file entirely -- Unity USS
    /// does not support it as a priority mechanism, so the font-scale
    /// mechanism that relied on it was measured to be 100% inert whenever a
    /// theme class was also present, which is always in production -- see
    /// docs/design-notes/2026-07-31-font-scale-cascade-fix.md).
    ///
    /// The banned keyword is built from string parts rather than written
    /// literally so this file itself (and its own doc comments) can discuss
    /// the defect without tripping the guard it defines.
    /// </summary>
    [TestFixture]
    public class UssHygieneTests
    {
        private const string PackageUssDir =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/Uss";

        // "!important" spelled out via concatenation -- see class doc.
        private static readonly string PriorityFlagKeyword = "!" + "important";

        private static readonly string[] ShorthandProperties =
        {
            "padding", "margin", "border",
            "border-radius", "border-color", "border-width", "border-style"
        };

        private static string[] UssFiles()
        {
            string root = Path.GetFullPath(PackageUssDir);
            Assert.IsTrue(Directory.Exists(root), "package USS dir not found: " + root);
            return Directory.GetFiles(root, "*.uss", SearchOption.AllDirectories);
        }

        [Test]
        public void SourceScan_NoUssFile_ContainsThePriorityFlagKeyword()
        {
            var offenders = new List<string>();
            foreach (string file in UssFiles())
            {
                string text = File.ReadAllText(file);
                if (text.IndexOf(PriorityFlagKeyword, System.StringComparison.Ordinal) >= 0)
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
            Assert.IsEmpty(offenders,
                "Unity USS (2022.3) does not support the CSS priority-flag keyword as a "
                + "priority mechanism -- it is silently inert, not an error, which is what "
                + "made the old font-scale override mechanism invisibly non-functional "
                + "whenever a theme class was also active (always, in production). It must "
                + "never reappear in any .uss file, including inside comments (reword the "
                + "comment instead -- see FontScale.uss for the pattern). Offending files: "
                + string.Join(", ", offenders));
        }

        // One combined pattern per shorthand property: "<prop>:" (optionally
        // preceded by other selector/whitespace chars on the same line,
        // since USS/CSS declarations are one-per-line by convention in this
        // codebase) followed eventually by "var(" before the terminating
        // semicolon. Longhand forms (padding-top, border-top-color, ...)
        // never match because the property name check requires the colon
        // immediately after the bare property name (no trailing "-word").
        private static readonly Regex[] ShorthandVarPatterns = BuildPatterns();

        private static Regex[] BuildPatterns()
        {
            var patterns = new Regex[ShorthandProperties.Length];
            for (int i = 0; i < ShorthandProperties.Length; i++)
            {
                string prop = Regex.Escape(ShorthandProperties[i]);
                patterns[i] = new Regex(
                    @"^\s*" + prop + @"\s*:[^;]*var\(",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            return patterns;
        }

        [Test]
        public void SourceScan_NoShorthandProperty_ContainsAVarToken()
        {
            var offenders = new List<string>();
            foreach (string file in UssFiles())
            {
                string text = File.ReadAllText(file);
                for (int i = 0; i < ShorthandVarPatterns.Length; i++)
                {
                    MatchCollection matches = ShorthandVarPatterns[i].Matches(text);
                    foreach (Match m in matches)
                    {
                        int line = CountLines(text, m.Index);
                        offenders.Add(Path.GetFileName(file) + ":" + line
                            + " (" + ShorthandProperties[i] + ")");
                    }
                }
            }
            Assert.IsEmpty(offenders,
                "var() inside a multi-value SHORTHAND property (padding/margin/border-*) "
                + "must be split into explicit longhand declarations (padding-top, "
                + "border-top-color, etc.) -- one declaration per side, each with its own "
                + "var() reference. Offending declarations: " + string.Join(", ", offenders));
        }

        private static int CountLines(string text, int uptoIndex)
        {
            int line = 1;
            for (int i = 0; i < uptoIndex && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }
            return line;
        }

        /// <summary>
        /// The collapsed permission/AskUserQuestion card is a single summary
        /// row with no min-height floor of its own. If it is ever allowed to
        /// flex-shrink again, Yoga crushes the card root to ~2px under height
        /// pressure and the summary row bleeds over the context-chip bar with
        /// 0-height button hitboxes (measured live -- see
        /// docs/design-notes/2026-08-01-perm-card-collapsed-crush.md).
        /// Shrink is only legal while expanded, where the
        /// var(--uap-perm-card-min) floor plus internal scrolling apply.
        /// </summary>
        [Test]
        public void SourceScan_PermCard_KeepsCollapsedCrushGuards()
        {
            string file = Path.Combine(Path.GetFullPath(PackageUssDir), "AgentPanel.uss");
            string text = File.ReadAllText(file);

            string baseBlock = ExtractRuleBlock(text, ".uap-perm");
            StringAssert.Contains("flex-shrink: 0", baseBlock,
                ".uap-perm (collapsed state) must keep flex-shrink: 0 -- see class doc");
            StringAssert.Contains("overflow: hidden", baseBlock,
                ".uap-perm must clip its content if crushed instead of painting over "
                + "the context bar below");

            string expandedBlock = ExtractRuleBlock(text, ".uap-perm--expanded");
            StringAssert.Contains("flex-shrink: 1", expandedBlock,
                ".uap-perm--expanded is the only state where shrinking is legal "
                + "(details scroll internally)");
            StringAssert.Contains("min-height", expandedBlock,
                ".uap-perm--expanded must keep its usability floor");
        }

        /// <summary>
        /// Row layout fix regression guard (docs/design-notes/2026-08-01-
        /// model-settings-rework.md section 4.3): UITK/Yoga defaults
        /// flex-shrink to 0 (unlike web CSS's 1). Before this fix, the
        /// agent-override row's name field was fixed-width/no-shrink and
        /// the model picker was flex-grow:1 at the default shrink (0), so
        /// a long model display name could never shrink to fit -- the row
        /// overflowed and pushed the trailing remove button off-screen
        /// (user feedback 2026-08-01). Both columns must opt into
        /// shrinking with a floor; the remove button must stay
        /// flex-shrink:0 so it can never be the one pushed out.
        /// </summary>
        [Test]
        public void SourceScan_SettingsModelRow_KeepsShrinkGuards()
        {
            string file = Path.Combine(Path.GetFullPath(PackageUssDir), "AgentPanel.uss");
            string text = File.ReadAllText(file);

            string pickerBlock = ExtractRuleBlock(text, ".uap-settings-model-picker");
            StringAssert.Contains("flex-shrink: 1", pickerBlock,
                ".uap-settings-model-picker must be allowed to shrink so a long model "
                + "display name cannot push the remove button off-screen");
            StringAssert.Contains("min-width", pickerBlock,
                ".uap-settings-model-picker must keep a usability floor while shrinking");

            string nameBlock = ExtractRuleBlock(text, ".uap-settings-model-name");
            StringAssert.Contains("flex-shrink: 1", nameBlock,
                ".uap-settings-model-name must also be allowed to shrink under pressure");
            StringAssert.Contains("min-width", nameBlock,
                ".uap-settings-model-name must keep a usability floor while shrinking");

            string removeBlock = ExtractRuleBlock(text, ".uap-settings-model-remove");
            StringAssert.Contains("flex-shrink: 0", removeBlock,
                ".uap-settings-model-remove must never shrink -- it is the row's fixed "
                + "anchor, the whole point of the guard above");
        }

        /// <summary>
        /// Extension-profile rows (design note 2026-08-02-composer-newline-
        /// and-profile-row.md section 2): the row used to borrow the
        /// quick-action label class (fixed 110px, flex-shrink 0) for an
        /// arbitrary-length SDK display name and the BLOCK hint class for
        /// its inline status, so the name was clipped and the status
        /// wrapped onto its own baseline. The name must shrink+ellipsize,
        /// the status must stay inline and unshrinkable.
        /// </summary>
        [Test]
        public void SourceScan_SettingsProfileRow_KeepsInlineLayoutGuards()
        {
            string file = Path.Combine(Path.GetFullPath(PackageUssDir), "AgentPanel.uss");
            string text = File.ReadAllText(file);

            string nameBlock = ExtractRuleBlock(text, ".uap-settings-profile-name");
            StringAssert.Contains("flex-shrink: 1", nameBlock,
                ".uap-settings-profile-name must shrink so a long SDK name cannot push "
                + "the approve/revoke button off-screen");
            StringAssert.Contains("min-width", nameBlock,
                ".uap-settings-profile-name must keep a usability floor while shrinking");
            StringAssert.Contains("text-overflow: ellipsis", nameBlock,
                ".uap-settings-profile-name must ellipsize rather than clip mid-glyph");
            StringAssert.Contains("white-space: nowrap", nameBlock,
                ".uap-settings-profile-name must stay on one line inside the row");

            string statusBlock = ExtractRuleBlock(text, ".uap-settings-profile-status");
            StringAssert.Contains("flex-shrink: 0", statusBlock,
                ".uap-settings-profile-status is a fixed inline chip, not a shrinkable column");
            StringAssert.Contains("white-space: nowrap", statusBlock,
                ".uap-settings-profile-status must not wrap (the block hint style did)");
            StringAssert.Contains("margin-top: 0", statusBlock,
                ".uap-settings-profile-status must not carry the block hint's vertical "
                + "margins -- they knocked it off the row baseline");
        }

        /// <summary>
        /// USS classes that act as the flexible CONTENT sibling in a list
        /// row -- the column that must absorb all spare row width so a
        /// trailing per-row control (a menu button, a remove button, etc.)
        /// lands in a content-independent column instead of tracking each
        /// row's content width. Every entry here must declare
        /// flex-grow: 1 in its rule block.
        ///
        /// This is one half of a two-sided rule -- see
        /// JustifyContentSpaceBetweenContainers below for the companion
        /// guard on the row CONTAINER, and
        /// SourceScan_JustifyContentContainers_DeclareSpaceBetween for why
        /// the two are not alternatives.
        ///
        /// Seeded with the one incident already fixed below (see the XML
        /// doc on SourceScan_FlexGrowContentColumns_DeclareFlexGrowOne for
        /// the full evidence). The remaining six entries came from the
        /// 2026-08-03 package-wide audit: no other row was found actually
        /// broken, but these six had flex-grow: 1 correct-by-luck with no
        /// test pinning it -- the declaration could be deleted today and
        /// nothing would fail. Adding a class is a one-liner -- append the
        /// selector here, nothing else in this file needs to change.
        /// </summary>
        private static readonly string[] FlexGrowContentColumns =
        {
            ".uap-banner-text",
            ".uap-history-row-main",
            ".uap-perm-summary-title",
            // SourceScan_SettingsModelRow_KeepsShrinkGuards already covers
            // this row's flex-shrink/min-width, but never flex-grow --
            // deleting the flex-grow declaration here would fail nothing
            // over there. This entry closes that exact gap.
            ".uap-settings-model-picker",
            // Same gap as above: SourceScan_SettingsProfileRow_KeepsInlineLayoutGuards
            // covers flex-shrink/min-width/ellipsis but never flex-grow.
            ".uap-settings-profile-name",
            ".uap-settings-errignore-msg",
            ".uap-subcard-desc",
            ".uap-toolcard-summary",
        };

        // Tolerates whitespace around the colon, the declaration appearing
        // anywhere in the rule block (including alongside comments), and
        // both "flex-grow: 1;" and "flex-grow: 1.0;" -- the same tolerance
        // BuildPatterns() above applies to the shorthand/var() guard.
        private static readonly Regex FlexGrowOnePattern = new Regex(
            @"flex-grow\s*:\s*1(\.0+)?\s*;", RegexOptions.IgnoreCase);

        /// <summary>
        /// flex-grow (like flex-shrink) defaults to 0 in UI Toolkit. A
        /// content column that sits beside a trailing per-row control and
        /// does not opt into flex-grow: 1 leaves the row's spare width
        /// unclaimed by that column, so the trailing control's X position
        /// tracks the column's own content width instead of forming a
        /// straight vertical line down the list. This exact defect has now
        /// shipped twice:
        ///
        /// v0.9.0: a PopupField with flex-shrink: 0 next to a delete
        /// button pushed the delete button off-screen when the picked
        /// value was long.
        ///
        /// v0.15.0: .uap-history-row-main -- the content column next to a
        /// trailing "..." menu button inside a flex-direction: row row
        /// container -- declared no flex-grow. Measured live: rows were
        /// all 300.5px wide, but the content column measured 137.0 to
        /// 276.5px, so the "..." button's X coordinate varied by 139.5px
        /// row to row. The user reported it as eye-travel cost: a control
        /// repeated once per row must form a straight vertical column.
        ///
        /// FlexGrowContentColumns above is the curated list this guard
        /// checks; extend it with one selector per audited row.
        /// </summary>
        [Test]
        public void SourceScan_FlexGrowContentColumns_DeclareFlexGrowOne()
        {
            string[] files = UssFiles();
            var offenders = new List<string>();

            foreach (string selector in FlexGrowContentColumns)
            {
                string body;
                string fileName;
                int line;
                if (!TryFindRuleBlock(files, selector, out body, out fileName, out line))
                {
                    offenders.Add(selector + " -- no rule block found in any .uss "
                        + "file under " + PackageUssDir + ". The class was renamed "
                        + "or removed; update FlexGrowContentColumns in this test to "
                        + "match, or drop the entry if the row no longer needs the "
                        + "guard.");
                    continue;
                }

                if (!FlexGrowOnePattern.IsMatch(body))
                {
                    offenders.Add(selector + " (" + fileName + ":" + line + ") -- "
                        + "missing flex-grow: 1. A content column that sits beside "
                        + "a trailing per-row control must declare flex-grow: 1, or "
                        + "the control's X position tracks the content width and "
                        + "the column of buttons zig-zags.");
                }
            }

            Assert.IsEmpty(offenders,
                "Flex-grow content column guard failed: " + string.Join(" | ", offenders));
        }

        /// <summary>
        /// USS classes that act as the row CONTAINER for a flex-grow
        /// content column plus a trailing per-row control (see
        /// FlexGrowContentColumns above). Every entry here must declare
        /// justify-content: space-between in its rule block.
        ///
        /// This is not redundant with the content column's flex-grow: 1 --
        /// justify-content and flex-grow are not alternatives, they cover
        /// overlapping halves of the same defect. flex-grow: 1 on the
        /// content sibling leaves no free space at all, which is what
        /// makes the hover highlight and click target cover the whole row
        /// and gives text-overflow: ellipsis the full row width.
        /// justify-content: space-between on the container is the safety
        /// net: if a future edit ever drops flex-grow from the content
        /// column, the trailing control still lands pinned to the row's
        /// right edge instead of regressing to the measured 139.5px
        /// zig-zag (see SourceScan_FlexGrowContentColumns_DeclareFlexGrowOne
        /// for the incident history). With flex-grow present,
        /// justify-content has no visible effect today -- which is exactly
        /// why it needs its own guard, or it reads as dead style and gets
        /// deleted.
        ///
        /// Seeded with the one incident already fixed below. The remaining
        /// six entries came from the 2026-08-03 package-wide audit: each
        /// already has a flex-grow child, so justify-content: space-between
        /// has no free space left to distribute and therefore no visible
        /// effect in any of them today -- every one of these six is purely
        /// the future-edit safety net described above, which is precisely
        /// why they need this guard instead of being left to look like
        /// dead style. Adding a class is a one-liner -- append the
        /// selector here, nothing else in this file needs to change.
        /// </summary>
        private static readonly string[] JustifyContentSpaceBetweenContainers =
        {
            ".uap-banner",
            ".uap-history-row-top",
            ".uap-perm-summary",
            ".uap-settings-errignore-row",
            ".uap-settings-model-row",
            ".uap-settings-qa-row",
            ".uap-subcard-header",
            ".uap-toolcard-header",
        };

        // Tolerates whitespace around the colon and semicolon, and the
        // declaration appearing anywhere in the rule block (including
        // alongside comments) -- same tolerance as FlexGrowOnePattern
        // above.
        private static readonly Regex JustifyContentSpaceBetweenPattern = new Regex(
            @"justify-content\s*:\s*space-between\s*;", RegexOptions.IgnoreCase);

        /// <summary>
        /// Companion guard to
        /// SourceScan_FlexGrowContentColumns_DeclareFlexGrowOne: the row
        /// CONTAINER's justify-content: space-between and the content
        /// column's flex-grow: 1 cover overlapping halves of the same
        /// v0.9.0 / v0.15.0 defect (see that test's XML doc for the full
        /// incident history and the 139.5px measurement). flex-grow on
        /// the content sibling is what makes the hover highlight and
        /// click target cover the whole row; justify-content on the
        /// container is the safety net that keeps the trailing control
        /// pinned to the row's right edge even if a future edit drops
        /// flex-grow. Because flex-grow already leaves no free space
        /// today, justify-content has no visible effect right now --
        /// which is exactly why it needs its own guard, or it reads as
        /// dead style and gets deleted.
        ///
        /// JustifyContentSpaceBetweenContainers above is the curated list
        /// this guard checks; extend it with one selector per audited row.
        /// </summary>
        [Test]
        public void SourceScan_JustifyContentContainers_DeclareSpaceBetween()
        {
            string[] files = UssFiles();
            var offenders = new List<string>();

            foreach (string selector in JustifyContentSpaceBetweenContainers)
            {
                string body;
                string fileName;
                int line;
                if (!TryFindRuleBlock(files, selector, out body, out fileName, out line))
                {
                    offenders.Add(selector + " -- no rule block found in any .uss "
                        + "file under " + PackageUssDir + ". The class was renamed "
                        + "or removed; update JustifyContentSpaceBetweenContainers "
                        + "in this test to match, or drop the entry if the row no "
                        + "longer needs the guard.");
                    continue;
                }

                if (!JustifyContentSpaceBetweenPattern.IsMatch(body))
                {
                    offenders.Add(selector + " (" + fileName + ":" + line + ") -- "
                        + "missing justify-content: space-between. A row "
                        + "container that pairs a flex-grow content column with "
                        + "a trailing per-row control must declare "
                        + "justify-content: space-between as a safety net, or "
                        + "the control's position has no fallback if flex-grow "
                        + "is ever dropped from the content column.");
                }
            }

            Assert.IsEmpty(offenders,
                "Justify-content container guard failed: " + string.Join(" | ", offenders));
        }

        /// <summary>
        /// USS classes for BaseField-derived controls (PopupField, Toggle,
        /// TextField, ...) that sit in a flex row and must be allowed to
        /// shrink below their own preferred width. A BaseField reports a
        /// preferred width driven by its OWN CURRENT CONTENT; at the UI
        /// Toolkit default of flex-shrink: 0 it cannot give any of that
        /// back under row pressure, so it overflows the row instead. Every
        /// entry here must declare flex-shrink: 1 in its rule block.
        ///
        /// This is the mirror image of FlexGrowContentColumns /
        /// JustifyContentSpaceBetweenContainers above: those two guard a
        /// CONTENT column that must claim spare row width; this one
        /// guards a FIELD that must give width back instead of hoarding
        /// it. See SourceScan_FlexShrinkFields_DeclareFlexShrinkOne for
        /// the full incident history and the live measurement.
        ///
        /// Deliberately EXCLUDED -- do not "helpfully" add these back:
        ///   .uap-settings-qa-label has an explicit width: 110px, so it
        ///   clips inside a fixed box by design rather than growing the
        ///   row; flex-shrink: 0 is correct there.
        ///   .uap-history-archived-toggle is intentionally flex-shrink: 0;
        ///   it is protected by the filter bar's flex-wrap plus a short
        ///   static label, so it never needs to give width back.
        ///   Button classes (.uap-card-btn, .uap-settings-btn,
        ///   .uap-settings-model-remove, .uap-settings-qa-remove, header
        ///   buttons) are not BaseFields at all and are intentionally
        ///   non-shrinking trailing controls -- they are the fixed anchor
        ///   on the OPPOSITE side of this rule, not a candidate for it.
        ///
        /// Adding a class is a one-liner -- append the selector here,
        /// nothing else in this file needs to change.
        /// </summary>
        private static readonly string[] FlexShrinkFields =
        {
            ".uap-card-path",
            ".uap-history-groupby",
            ".uap-history-rename-field",
            ".uap-history-search",
            ".uap-settings-model-name",
            ".uap-settings-model-picker",
            ".uap-settings-qa-prompt",
            ".uap-settings-slider",
        };

        // Tolerates whitespace around the colon, the declaration appearing
        // anywhere in the rule block (including alongside comments), and
        // both "flex-shrink: 1;" and "flex-shrink: 1.0;" -- same tolerance
        // as FlexGrowOnePattern above.
        private static readonly Regex FlexShrinkOnePattern = new Regex(
            @"flex-shrink\s*:\s*1(\.0+)?\s*;", RegexOptions.IgnoreCase);

        /// <summary>
        /// A BaseField-derived control (PopupField, Toggle, TextField,
        /// ...) reports a preferred width driven by its OWN CURRENT
        /// CONTENT. In a flex row at UI Toolkit's default flex-shrink: 0
        /// it cannot give any of that width back under pressure, so it
        /// overflows the row instead of shrinking to fit. This exact
        /// defect has now shipped three times:
        ///
        /// v0.9.0: a subagent-model PopupField pushed its delete button
        /// off-screen.
        ///
        /// v0.15.1: an unshrinkable leading label starved a history row.
        ///
        /// Today: the History filter bar's group PopupField, measured
        /// live before the fix -- the filter bar row was 302.5px wide,
        /// the PopupField alone occupied 295.5px, and the row's children
        /// ran out to x=492 (190px of overflow), which UI Toolkit turned
        /// into a horizontal scrollbar plus a blank strip, with the
        /// ScrollView settling at scrollOffset.x = 189.5. It only
        /// reproduced on the longer localized choices -- every visible
        /// string in this package is localized, and the Japanese strings
        /// run much longer than the English the layout was eyeballed
        /// against, so this class of bug is invisible in English.
        ///
        /// FlexShrinkFields above is the curated list this guard checks
        /// (including the deliberate exclusions documented there); extend
        /// it with one selector per audited BaseField.
        /// </summary>
        [Test]
        public void SourceScan_FlexShrinkFields_DeclareFlexShrinkOne()
        {
            string[] files = UssFiles();
            var offenders = new List<string>();

            foreach (string selector in FlexShrinkFields)
            {
                string body;
                string fileName;
                int line;
                if (!TryFindRuleBlock(files, selector, out body, out fileName, out line))
                {
                    offenders.Add(selector + " -- no rule block found in any .uss "
                        + "file under " + PackageUssDir + ". The class was renamed "
                        + "or removed; update FlexShrinkFields in this test to "
                        + "match, or drop the entry if the field no longer needs "
                        + "the guard.");
                    continue;
                }

                if (!FlexShrinkOnePattern.IsMatch(body))
                {
                    offenders.Add(selector + " (" + fileName + ":" + line + ") -- "
                        + "missing flex-shrink: 1. A BaseField-derived control "
                        + "(PopupField, Toggle, TextField, ...) reports a "
                        + "preferred width driven by its own current content and "
                        + "must declare flex-shrink: 1, or it cannot give width "
                        + "back under row pressure and overflows instead.");
                }
            }

            Assert.IsEmpty(offenders,
                "Flex-shrink field guard failed: " + string.Join(" | ", offenders));
        }

        // Custom property DEFINITION: "--name:" at the start of a
        // declaration line (only whitespace before the leading "--").
        // Anchoring on start-of-line is what keeps this from matching
        // inside a BEM modifier selector like
        // ".uap-send-btn--stop:hover" -- that line starts with "."
        // (from the class name), not "--".
        private static readonly Regex CustomPropertyDefinitionPattern = new Regex(
            @"^\s*--([\w-]+)\s*:", RegexOptions.Multiline);

        // Custom property REFERENCE: "var(--name". Deliberately NOT
        // anchored to the closing ")" or ";" -- matching each
        // "var(--name" occurrence independently, regardless of nesting,
        // is what makes a fallback form like "var(--a, var(--b))" fall
        // out for free: the scan finds "var(--a" and "var(--b" as two
        // separate, unrelated reference matches without ever needing to
        // track parenthesis depth.
        private static readonly Regex CustomPropertyReferencePattern = new Regex(
            @"var\(--([\w-]+)");

        /// <summary>
        /// This file's doc comments routinely spell out var(--uap-*) or
        /// --name: examples in PROSE (the file-header comment in
        /// AgentPanel.uss literally contains the substring
        /// "var(--uap-*)" as a wildcard description, and the theme files
        /// discuss "--uap-bg-sunken" and "--uap-text-secondary" by name
        /// in comments elsewhere). None of that is CSS and must not be
        /// counted as a real definition or reference. Block comments are
        /// stripped before either scan runs; only their internal
        /// newlines survive (replaced with an equal-length run of "\n")
        /// so CountLines() still reports the correct original line
        /// number for a match found after a stripped comment.
        /// </summary>
        private static string StripComments(string text)
        {
            return Regex.Replace(text, @"/\*[\s\S]*?\*/", CommentToNewlines);
        }

        private static string CommentToNewlines(Match m)
        {
            int newlines = 0;
            for (int i = 0; i < m.Value.Length; i++)
            {
                if (m.Value[i] == '\n')
                {
                    newlines++;
                }
            }
            return new string('\n', newlines);
        }

        /// <summary>
        /// UI Toolkit does not raise an error for a var(--name) reference
        /// whose custom property is never defined anywhere in the loaded
        /// stylesheets -- the declaration silently has no effect, the
        /// same "not an error, just inert" failure mode as the
        /// priority-flag keyword this file already bans (see the class
        /// doc at the top of this file). Found by accident during an
        /// unrelated search-and-replace: a rule referenced
        /// var(--uap-text-error), a property defined NOWHERE in
        /// ThemeDark.uss or ThemeLight.uss, so an error label had been
        /// shipping with no error color and nothing noticed (fixed
        /// before this guard was added). The three flex tests above all
        /// check that a specific declaration is PRESENT; this one checks
        /// that the value a declaration POINTS AT actually resolves -- a
        /// different failure mode entirely, and one none of those three
        /// would ever catch.
        ///
        /// Custom property definitions can live in any .uss file (in
        /// practice ThemeDark.uss / ThemeLight.uss), while the
        /// references that must resolve against them live mainly in
        /// AgentPanel.uss, so the definition set used to check
        /// resolution is a whole-package union across every .uss file
        /// returned by UssFiles(), not a per-file check.
        ///
        /// A property defined in only ONE of the two theme files is
        /// also a live bug -- it renders correctly in that theme and
        /// silently does nothing in the other -- and is reported as its
        /// own offender category below, keyed specifically off
        /// ThemeDark.uss / ThemeLight.uss by filename (this package's
        /// actual theme pair, per their own file-header comments: "must
        /// exist in BOTH themes"). As of this writing every property
        /// defined in either theme file is defined in both (FontScale.uss
        /// separately overrides a subset of the font-size properties,
        /// which is expected and not a parity violation, since parity is
        /// only checked between the two theme files themselves) -- so no
        /// exemption list exists for this half today. If a genuinely
        /// theme-only property is ever needed, exempt it here explicitly
        /// rather than weakening this check silently.
        /// </summary>
        [Test]
        public void SourceScan_VarReferences_ResolveToADefinition()
        {
            string[] files = UssFiles();

            var allDefinitions = new HashSet<string>();
            var themeDarkDefinitions = new HashSet<string>();
            var themeLightDefinitions = new HashSet<string>();
            var strippedTextByFile = new Dictionary<string, string>();

            foreach (string file in files)
            {
                string stripped = StripComments(File.ReadAllText(file));
                strippedTextByFile[file] = stripped;

                string fileName = Path.GetFileName(file);
                MatchCollection defs = CustomPropertyDefinitionPattern.Matches(stripped);
                foreach (Match m in defs)
                {
                    string name = m.Groups[1].Value;
                    allDefinitions.Add(name);
                    if (fileName == "ThemeDark.uss")
                    {
                        themeDarkDefinitions.Add(name);
                    }
                    else if (fileName == "ThemeLight.uss")
                    {
                        themeLightDefinitions.Add(name);
                    }
                }
            }

            var offenders = new List<string>();

            foreach (string file in files)
            {
                string stripped = strippedTextByFile[file];
                string fileName = Path.GetFileName(file);
                MatchCollection refs = CustomPropertyReferencePattern.Matches(stripped);
                foreach (Match m in refs)
                {
                    string name = m.Groups[1].Value;
                    if (!allDefinitions.Contains(name))
                    {
                        int line = CountLines(stripped, m.Index);
                        offenders.Add(fileName + ":" + line + " -- var(--" + name
                            + ") has no matching \"--" + name + ":\" definition in "
                            + "any .uss file. UI Toolkit silently ignores an "
                            + "unresolvable var() reference (it is not an error), "
                            + "so the declaration that uses it has no effect at "
                            + "runtime.");
                    }
                }
            }

            foreach (string name in themeDarkDefinitions)
            {
                if (!themeLightDefinitions.Contains(name))
                {
                    offenders.Add("--" + name + " is defined in ThemeDark.uss but "
                        + "not in ThemeLight.uss -- it renders correctly in the "
                        + "dark theme and silently does nothing (or falls back to "
                        + "an unrelated same-named default) in the light theme. "
                        + "Define it in both theme files, or exempt it explicitly "
                        + "in this test if it is deliberately theme-only.");
                }
            }

            foreach (string name in themeLightDefinitions)
            {
                if (!themeDarkDefinitions.Contains(name))
                {
                    offenders.Add("--" + name + " is defined in ThemeLight.uss but "
                        + "not in ThemeDark.uss -- it renders correctly in the "
                        + "light theme and silently does nothing (or falls back to "
                        + "an unrelated same-named default) in the dark theme. "
                        + "Define it in both theme files, or exempt it explicitly "
                        + "in this test if it is deliberately theme-only.");
                }
            }

            Assert.IsEmpty(offenders,
                "Custom-property resolution guard failed: " + string.Join(" | ", offenders));
        }

        /// <summary>First rule block whose selector list line starts with the
        /// EXACT selector (guards against matching .uap-perm-summary etc.).</summary>
        private static string ExtractRuleBlock(string text, string selector)
        {
            Regex rule = new Regex(
                "^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}",
                RegexOptions.Multiline);
            Match m = rule.Match(text);
            Assert.IsTrue(m.Success, "rule block not found for selector: " + selector);
            return m.Groups["body"].Value;
        }

        /// <summary>Same selector-at-start-of-line rule-block match as
        /// ExtractRuleBlock above, but searched across every .uss file and
        /// reported as a bool instead of asserting, so a missing rule block
        /// becomes its own distinct offender instead of aborting the scan
        /// of the remaining curated entries.</summary>
        private static bool TryFindRuleBlock(string[] files, string selector,
            out string body, out string fileName, out int line)
        {
            Regex rule = new Regex(
                "^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}",
                RegexOptions.Multiline);
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                Match m = rule.Match(text);
                if (m.Success)
                {
                    body = m.Groups["body"].Value;
                    fileName = Path.GetFileName(file);
                    line = CountLines(text, m.Index);
                    return true;
                }
            }
            body = null;
            fileName = null;
            line = 0;
            return false;
        }
    }
}

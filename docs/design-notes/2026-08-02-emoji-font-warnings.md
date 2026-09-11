# Emoji in model output floods the console with font warnings

Date: 2026-08-02
Status: fixed (v0.13.1)
Reported: user, live panel ("エージェントを走らせるとフォント関連で警告が頻発する")

## 1. Symptom and root cause (measured)

The live editor log fills with:

```
The character with Unicode value ❌ was not found in the [] font asset
or any potential fallbacks. It was replaced by Unicode character □.
```

Observed codepoints: U+274C (cross mark) and U+2705 (check mark button) --
i.e. ordinary emoji the model writes in its markdown. They are NOT panel
glyphs: `IconLoader.SafeGlyphCodepoints` was curated for exactly this reason
and none of its entries warn.

Why a *flood* rather than one line: Unity's text generator logs once per
missing-character OCCURRENCE on EVERY text-generation pass, with no caching
(probe P2b). Two panel paths reassign `.text` on a timer while a turn runs --
`StreamingLabelPump.OnUpdate` (~80 ms) and `SubagentCard`'s progress line
(500 ms) -- so a single visible emoji re-emits its warning for as long as it
stays on screen.

The pre-existing sanitizer (`IconLoader.StripVariationSelectors`) only
removed U+FE0F/U+FE0E, which does nothing for the base emoji codepoint.

## 2. Chokepoint audit (probe P3)

The probe mapped every path where untrusted text reaches a Label. Besides
the two flood sources it found genuine BYPASSES that never sanitized at all:
`PermissionCard.PlainLabel` and its summary title (model-authored tool
descriptions, AskUserQuestion option text), `CodeBlockElement`'s fence
language tag, `ToolActivityCard`'s tool-name label, `HeaderView`'s session
title, `HistoryView`'s preview row, and (found in review)
`MessageBlockFactory.CreateTextBlock`'s non-pump fallback plus the
AskUserQuestion option tooltip.

`InlineMarkupConverter.Escape` is the single chokepoint for all
markdown-rendered rich text and had its own duplicate FE0F logic.

## 3. Decision

`IconLoader.SanitizeForDisplay(string)` supersedes `StripVariationSelectors`
at every untrusted-text chokepoint:

1. strip variation selectors and zero-width joiners (no orphan ZWJ left);
2. map a short curated table to font-safe equivalents so meaning survives
   (U+2705/U+2714 -> the existing check glyph, U+274C/U+274E -> the existing
   cross glyph, U+2757 -> "!", U+2753 -> "?", U+2728 -> the spark glyph,
   directional emoji -> existing chevrons);
3. drop anything left in the known-uncoverable ranges (pictographs and
   Misc Symbols/Dingbats outside the curated/whitelisted set).

CJK, kana, accented Latin, box-drawing (markdown tables) and math symbols
are preserved verbatim -- the review lens specifically hunted for
over-stripping.

### Why range-dropping rather than querying the font

`FontAsset.HasCharacter(uint, searchFallbacks, tryAddCharacter)` does work
(the 3-arg overload only -- the 1-arg overloads returned false even for
U+2713, which renders fine), but reaching the font UI Toolkit actually uses
requires `PanelTextSettings.defaultPanelTextSettings`, which is INTERNAL to
UnityEngine.UIElementsModule. Getting there needs reflection into a
version-specific internal, to decide something a static table decides just
as well. Rejected: reflection fragility on the streaming hot path buys
nothing over the curated table plus range drop. Documented so a future
Unity version exposing the API can revisit.

## 4. Regression guards

Pure-function tests: curated mappings, unknown emoji dropped, CJK/accents/
box-drawing preserved verbatim, ZWJ sequences fully removed, lone surrogate
handling across streaming delta boundaries, idempotence -- plus a source
scan asserting each identified render path calls the sanitizer, so a new
Label added to a render path fails the suite rather than quietly
reintroducing the flood. All emoji in test sources are \uXXXX escapes
(ASCII-only source rule).

## 5. Review catches worth recording

- The pictograph drop range started at U+1F300, leaving U+1F000-U+1F2FF
  (Mahjong/Domino/Playing Cards, Enclosed Alphanumerics) still able to warn.
- Two more raw assignments (the non-pump text fallback, the AskUserQuestion
  option tooltip) that the initial pass missed -- the source-scan guard
  exists precisely because this class of miss is easy.

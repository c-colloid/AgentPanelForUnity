# 2026-08-27 -- The context meter measures context, not the bill

The status bar's context meter answered "how full is my context window"
with `result.modelUsage` -- which is not context. It is CUMULATIVE
BILLING: the sum over every API call the turn made, each of which
re-reads the same context from cache, plus every subagent's tokens
folded into the parent's bucket. The meter divided that by the context
window.

## What the captures say

Five real result payloads live in Tests/Editor/Fixtures. Every one of
them contains all three quantities, and they disagree by a factor of two
to three and a half:

| capture | last iteration | enclosing usage | modelUsage[opus] |
|---|---|---|---|
| success_bidi_inbound | 27,962 | 55,898 | 55,898 |
| permission_inbound | 28,161 | 56,190 | 56,190 |
| askuser_inbound | 27,999 | 55,978 | 55,978 |
| askuser3_inbound | 28,029 | 56,006 | 56,006 |
| task_subagent_inbound | **28,299** | 56,449 | **97,886** |

Two facts fall straight out of that table.

The overstatement is NOT a subagent problem. Four of the five captures
have no subagent at all and still bill ~2x their context, because the
turn made two API calls and the second re-read the whole prompt from
cache: 22,056 + 27,857 = 49,913, which is exactly the enclosing usage's
`cache_read_input_tokens`. The same context, counted twice.

The subagent is a second, independent inflation on top. Only in
task_subagent_inbound does modelUsage exceed the enclosing usage, and
the difference (97,886 - 56,449 = 41,437) is the subagent's own tokens,
field for field. Those tokens never occupied the main window: the
subagent's first call has `cache_read_input_tokens: 0` and builds its
own ~20k context from scratch. Counting them was simply wrong.

So the meter read 10% where the truth was 3%. The one number whose job
is to warn about imminent compaction was the most wrong when a turn did
the most work -- and under a subagent fan-out it would pin at 100% and
stay there.

## The fix

`usage.iterations` is the only thing on the wire that means "now".
`UsageInfo.LastIterationContextTokens` now carries the four-field total
of that array's last entry, and the meter's numerator comes from there
(`ResolveContextTokens`), with the old billing sum kept as the fallback
for a payload that carries no iterations. The fallback over-reads and
never under-reads, so a degraded rung can still not hide a compaction;
blanking a meter that has always shown something was the worse trade.

MEASURED CAVEAT, stated rather than hidden: all five captures carry
exactly ONE iteration, while their enclosing totals prove the turns made
two API calls. The array is therefore not a complete per-call log, and
its ordering could not be confirmed from captures. "Last" is the honest
reading of "now" and is identical to "only" for every sample we have.

`-1`, not `0`, marks an absent measurement throughout -- a genuinely
empty context is 0 and must not trigger the fallback.

`AgentHub._lastContextTokens` deliberately does NOT restore from the
session cache the way `_lastModelUsage` does: that cache stores per-model
billing totals, which is precisely the quantity this change exists to
stop standing in for context. A restored session falls back one rung
until its first completed turn.

## The selection hazard, closed on the way past

`SelectPrimaryModelUsage` picks which modelUsage entry supplies the
DENOMINATOR. Rule 1 is an exact key match against `CurrentModel`; the
old rule 2 was "largest bucket wins". The keys carry a context-window
suffix (`claude-opus-5[1m]`) while `CurrentModel` is whatever the
catalog's `resolvedModel` said, so an exact-key miss does not mean "not
this model" -- and the largest-bucket fallback becomes a live hazard the
moment subagents run on their own model, because the biggest bucket is
then the fan-out's and the meter would measure against a window the user
is not conversing in. A `canonicalModel` match now sits between the two.
The modelUsage entries have carried `canonicalModel` all along; the
panel simply was not parsing it.

## Honest residuals

- The per-model popover keeps the billing-total overload on purpose: it
  legitimately describes what each model BILLED this turn, which is a
  different question from what occupies the conversation's window.
- The status bar's token counter (`totalInputTokens + totalOutputTokens`)
  is unchanged and still reads only the main chain and still drops cache
  entirely -- it shows ~300 tok for a turn that billed 97,886. That is a
  separate defect, not this one.
- A restored session shows the fallback rung until its first completed
  turn. Sourcing context from the transcript on restore is possible and
  is not attempted here.

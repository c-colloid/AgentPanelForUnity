using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Plain data for one row of the session-history browser. Assembled by
    /// the caller from <c>SessionIndexEntry</c> (transcript-derived fields:
    /// SessionId / Preview / LastModifiedUtc / SizeBytes / Cwd), the
    /// transcript's own "ai-title" line (AiTitle), and <c>SessionMeta</c>
    /// (Pinned / Archived / TitleOverride / CustomGroupId /
    /// RecordedScenePath -- see docs/design-notes/
    /// 2026-08-03-history-usage-restore-and-browsing.md section 2.2). Kept
    /// as a flat struct-like class with public fields, rather than wrapping
    /// the two source objects, so every function in this file can be
    /// exercised with a plain object initializer and no Unity or file-I/O
    /// dependency whatsoever.
    /// </summary>
    public sealed class HistoryRow
    {
        /// <summary>The CLI session uuid. Never null/empty in practice --
        /// it is always the source jsonl file's own name -- but code below
        /// still treats it defensively where ordinal comparison needs a
        /// non-null operand.</summary>
        public string SessionId;

        /// <summary>The transcript's own "ai-title" line, or empty when the
        /// transcript predates that field or the scan never found one.</summary>
        public string AiTitle;

        /// <summary>First user message text (see
        /// <c>SessionIndexEntry.FirstUserTextPreview</c>), or empty when the
        /// transcript has no user text within the preview scan cap.</summary>
        public string Preview;

        public DateTime LastModifiedUtc;
        public long SizeBytes;

        /// <summary>Absolute working directory the session ran in, or empty
        /// for a malformed/ancient transcript that never recorded one.</summary>
        public string Cwd;

        public bool Pinned;
        public bool Archived;

        /// <summary>User-supplied rename. Empty means "no override", never
        /// a synonym for "untitled" -- see <c>SessionMeta.titleOverride</c>.</summary>
        public string TitleOverride;

        /// <summary>Id of a user-defined custom group, or empty for
        /// "ungrouped" in <see cref="HistoryGroupMode.Custom"/> mode.</summary>
        public string CustomGroupId;

        /// <summary>The model the session started on (first real assistant
        /// "message.model" -- see <c>SessionIndexEntry.ModelName</c>), or
        /// empty for a transcript with no in-cap assistant line.</summary>
        public string ModelName;

        /// <summary>Scene asset path recorded while this session was
        /// active, or empty for sessions that predate scene recording --
        /// see <c>SessionMeta.recordedScenePath</c> for why this can only
        /// ever be populated going forward.</summary>
        public string RecordedScenePath;
    }

    /// <summary>How <see cref="HistoryListModel.Build"/> buckets rows into
    /// <see cref="HistoryGroup"/>s, independent of the leading pinned group
    /// that always exists regardless of this setting.</summary>
    public enum HistoryGroupMode
    {
        Date,
        Scene,
        Project,
        Custom
    }

    /// <summary>One rendered section of the history list: a header plus its
    /// rows, already sorted per <see cref="HistoryListModel.Build"/> rule 5.</summary>
    public sealed class HistoryGroup
    {
        /// <summary>A sentinel the UI maps to a localized/display label --
        /// never text to show directly. In <see cref="HistoryGroupMode.Date"/>
        /// mode this is one of the <c>HistoryListModel.Date*</c> constants;
        /// in every other mode it is the raw grouping field itself (a scene
        /// path, a cwd, or a custom-group id) for the caller to resolve
        /// against its own lookup (scene asset name, project folder name,
        /// <c>SessionGroup.name</c>) -- "not display text" means "do not
        /// print this Key verbatim", not "this is always an opaque token".</summary>
        public string Key;

        /// <summary>True only for the single leading group produced by
        /// <see cref="HistoryListModel.PinnedGroupKey"/>.</summary>
        public bool IsPinnedGroup;

        public List<HistoryRow> Rows;
    }

    /// <summary>
    /// The entire filter/group/sort/page decision for the session-history
    /// browser, as pure static functions over explicit inputs (including
    /// "now") so it is deterministically unit-testable without touching
    /// Unity, the CLI, or the wall clock. This is the whole test surface for
    /// a UI (HistoryView) that is otherwise untestable, so every rule below
    /// is deliberately spelled out rather than left to "obvious" behaviour.
    /// See docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// section 2.4 for the product requirements this implements.
    /// </summary>
    public static class HistoryListModel
    {
        /// <summary>Leading group holding every pinned row, in every
        /// grouping mode. Shaped like the date/ungrouped sentinels (double
        /// underscore, lowercase) purely by convention; it cannot collide
        /// with a real cwd, scene path or custom-group id because none of
        /// those are ever written in this shape by the rest of the panel.</summary>
        public const string PinnedGroupKey = "__pinned__";

        /// <summary>
        /// Fallback bucket for Scene/Project/Custom mode when the row's
        /// grouping field is unset. Deliberately the same empty string that
        /// <c>HistoryRow.RecordedScenePath</c> / <c>Cwd</c> / <c>CustomGroupId</c>
        /// already use for "not set" (matching <c>SessionMeta</c>'s
        /// established convention), rather than inventing a second sentinel
        /// for the same concept -- <see cref="GroupKeyFor"/> can therefore
        /// just return the field's value unmodified in those three modes.
        /// </summary>
        public const string UngroupedKey = "";

        /// <summary>Same UTC calendar date as "now".</summary>
        public const string DateToday = "__today__";

        /// <summary>The UTC calendar date immediately before "today".</summary>
        public const string DateYesterday = "__yesterday__";

        /// <summary>2-7 UTC calendar days before "today" (i.e. after
        /// yesterday, within a week).</summary>
        public const string DateLast7 = "__last7__";

        /// <summary>8-30 UTC calendar days before "today".</summary>
        public const string DateLast30 = "__last30__";

        /// <summary>More than 30 UTC calendar days before "today".</summary>
        public const string DateOlder = "__older__";

        /// <summary>
        /// The title a row should display: <c>TitleOverride</c> (explicit
        /// user rename) wins, else the transcript's own <c>AiTitle</c>,
        /// else the first-user-message <c>Preview</c> as a last resort,
        /// else empty. Deliberately never invents placeholder text such as
        /// "Untitled session" -- that is a presentation decision the UI
        /// makes once, in one place, rather than baking a hardcoded English
        /// string into a model class that has no access to L10n and must
        /// not (per this repo's ownership rules) grow its own copy of one.
        /// </summary>
        public static string ResolveTitle(HistoryRow row)
        {
            if (row == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(row.TitleOverride))
            {
                return row.TitleOverride;
            }
            if (!string.IsNullOrEmpty(row.AiTitle))
            {
                return row.AiTitle;
            }
            if (!string.IsNullOrEmpty(row.Preview))
            {
                return row.Preview;
            }
            return string.Empty;
        }

        /// <summary>
        /// True when <paramref name="query"/> is a case-insensitive
        /// substring of the row's resolved title, its raw <c>AiTitle</c>,
        /// its <c>Preview</c>, or its <c>SessionId</c> -- four separate
        /// checks even though the resolved title often equals AiTitle or
        /// Preview, because a search should still hit e.g. the original
        /// ai-title after the user renames the session to something that
        /// doesn't contain the search term. A null/whitespace query matches
        /// every row (the "no search active" state), so callers can run
        /// this unconditionally instead of special-casing an empty search
        /// box. Uses <see cref="string.IndexOf(string, StringComparison)"/>
        /// with <see cref="StringComparison.OrdinalIgnoreCase"/> rather than
        /// <c>ToLower()</c> comparisons, which would allocate two new
        /// strings per row on every keystroke of a search box that can run
        /// over hundreds of rows.
        /// </summary>
        public static bool Matches(HistoryRow row, string query)
        {
            if (row == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }
            return ContainsIgnoreCase(ResolveTitle(row), query)
                || ContainsIgnoreCase(row.AiTitle, query)
                || ContainsIgnoreCase(row.Preview, query)
                || ContainsIgnoreCase(row.SessionId, query);
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack))
            {
                return false;
            }
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The group a row belongs to under <paramref name="mode"/>, before
        /// the leading-pinned-group override in <see cref="Build"/> is
        /// applied (this function does not look at <c>row.Pinned</c> at
        /// all -- pinning is a cross-cutting concern layered on top by the
        /// caller, not a fifth grouping mode). <paramref name="nowUtc"/> is
        /// only consulted in <see cref="HistoryGroupMode.Date"/> mode and
        /// must be passed explicitly (never read from
        /// <see cref="DateTime.UtcNow"/> internally) so date-bucket tests
        /// are exact and never flaky around midnight UTC.
        /// </summary>
        public static string GroupKeyFor(HistoryRow row, HistoryGroupMode mode, DateTime nowUtc)
        {
            if (row == null)
            {
                return UngroupedKey;
            }
            switch (mode)
            {
                case HistoryGroupMode.Date:
                    return DateKeyFor(row.LastModifiedUtc, nowUtc);
                case HistoryGroupMode.Scene:
                    return row.RecordedScenePath ?? UngroupedKey;
                case HistoryGroupMode.Project:
                    return row.Cwd ?? UngroupedKey;
                case HistoryGroupMode.Custom:
                    return row.CustomGroupId ?? UngroupedKey;
                default:
                    return UngroupedKey;
            }
        }

        /// <summary>
        /// Buckets by UTC *calendar* day, not a rolling 24h window --
        /// "yesterday" means the previous UTC date, not "1-2 days ago" by
        /// elapsed time, so a session from 23:59 UTC yesterday and one from
        /// 00:01 UTC today are exactly one calendar day apart even though
        /// only two minutes separate them. A future timestamp (clock skew
        /// between the machine that wrote the file and the one reading it,
        /// or filesystem mtime rounding) must bucket as Today rather than
        /// crash on a negative day count or silently fall through to Older
        /// -- "the newest thing we know about" is the closest honest bucket
        /// for a timestamp that claims to be from the future.
        /// </summary>
        private static string DateKeyFor(DateTime lastModifiedUtc, DateTime nowUtc)
        {
            DateTime today = nowUtc.Date;
            DateTime rowDate = lastModifiedUtc.Date;
            if (rowDate >= today)
            {
                return DateToday;
            }
            int daysAgo = (today - rowDate).Days;
            if (daysAgo == 1)
            {
                return DateYesterday;
            }
            if (daysAgo <= 7)
            {
                return DateLast7;
            }
            if (daysAgo <= 30)
            {
                return DateLast30;
            }
            return DateOlder;
        }

        /// <summary>
        /// The full filter -&gt; pin-split -&gt; group -&gt; sort -&gt; page
        /// pipeline behind the history list. Never returns null and never
        /// throws on malformed input (null <paramref name="rows"/>, null
        /// entries inside it, or a nonsensical <paramref name="limit"/>) --
        /// this feeds a UI list that must always render *something*
        /// reasonable rather than an exception dialog.
        /// </summary>
        /// <param name="rows">Candidate rows; null entries are skipped
        /// rather than throwing, since the caller may be mapping a
        /// partially-stale index where one entry failed to resolve.</param>
        /// <param name="query">Search text; null/whitespace matches everything.</param>
        /// <param name="mode">Grouping mode for every non-pinned row.</param>
        /// <param name="includeArchived">When false (the default list view),
        /// archived rows are dropped entirely -- including from
        /// <paramref name="hiddenCount"/>'s accounting, since "hidden by the
        /// archive filter" and "hidden by the page limit" are different
        /// concepts and only the latter is what hiddenCount communicates
        /// ("N more below the fold, click to load more").</param>
        /// <param name="nowUtc">"Now", for date bucketing. Always pass this
        /// explicitly; never let this function read the wall clock.</param>
        /// <param name="limit">Caps the total row count across every
        /// returned group, applied after ordering (so the rows cut are
        /// always the "oldest" ones by the current sort, never an arbitrary
        /// subset). <c>limit &lt;= 0</c> means unlimited.</param>
        /// <param name="hiddenCount">How many rows survived filtering but
        /// were cut by <paramref name="limit"/>; 0 when nothing was cut.</param>
        public static List<HistoryGroup> Build(
            IList<HistoryRow> rows,
            string query,
            HistoryGroupMode mode,
            bool includeArchived,
            DateTime nowUtc,
            int limit,
            out int hiddenCount)
        {
            hiddenCount = 0;
            var groups = new List<HistoryGroup>();
            if (rows == null || rows.Count == 0)
            {
                return groups;
            }

            // Rule 1 (archived) + rule 2 (search) + the pin/remaining split
            // that rule 3 needs, all in one pass since each row only needs
            // to be looked at once.
            var pinned = new List<HistoryRow>();
            var remaining = new List<HistoryRow>();
            for (int i = 0; i < rows.Count; i++)
            {
                HistoryRow row = rows[i];
                if (row == null)
                {
                    continue;
                }
                if (row.Archived && !includeArchived)
                {
                    continue;
                }
                if (!Matches(row, query))
                {
                    continue;
                }
                if (row.Pinned)
                {
                    pinned.Add(row);
                }
                else
                {
                    remaining.Add(row);
                }
            }

            SortRowsDeterministic(pinned);

            // Bucket the non-pinned survivors by GroupKeyFor. A Dictionary's
            // enumeration order is not guaranteed, but that is fine here --
            // the ordering that ships to the UI is decided explicitly below
            // (fixed array for Date mode, an explicit Sort for every other
            // mode), never by iterating this dictionary directly.
            var buckets = new Dictionary<string, List<HistoryRow>>(StringComparer.Ordinal);
            for (int i = 0; i < remaining.Count; i++)
            {
                HistoryRow row = remaining[i];
                string key = GroupKeyFor(row, mode, nowUtc);
                List<HistoryRow> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new List<HistoryRow>();
                    buckets[key] = bucket;
                }
                bucket.Add(row);
            }
            foreach (KeyValuePair<string, List<HistoryRow>> pair in buckets)
            {
                SortRowsDeterministic(pair.Value);
            }

            var orderedKeys = new List<string>();
            if (mode == HistoryGroupMode.Date)
            {
                // Rule 4: fixed chronological order, NOT sorted by content
                // -- "today" always precedes "yesterday" even if (in some
                // hypothetical future extension) a today-dated row were
                // somehow older than a yesterday-dated one. Empty buckets
                // are skipped so the UI never renders a header with no rows
                // under it.
                string[] canonicalOrder =
                {
                    DateToday, DateYesterday, DateLast7, DateLast30, DateOlder
                };
                for (int i = 0; i < canonicalOrder.Length; i++)
                {
                    List<HistoryRow> bucket;
                    if (buckets.TryGetValue(canonicalOrder[i], out bucket) && bucket.Count > 0)
                    {
                        orderedKeys.Add(canonicalOrder[i]);
                    }
                }
            }
            else
            {
                // Rule 4: every other mode orders by newest member
                // descending, except UngroupedKey is always last -- a
                // session with "no recorded scene" is a data-gap fallback,
                // not a real group, so even if it happens to contain the
                // single most-recently-modified row in the whole list it
                // must not jump to the top and outrank actual scene/
                // project/custom groups.
                orderedKeys.AddRange(buckets.Keys);
                orderedKeys.Sort(delegate (string a, string b)
                {
                    bool aUngrouped = string.Equals(a, UngroupedKey, StringComparison.Ordinal);
                    bool bUngrouped = string.Equals(b, UngroupedKey, StringComparison.Ordinal);
                    if (aUngrouped != bUngrouped)
                    {
                        return aUngrouped ? 1 : -1;
                    }
                    DateTime aNewest = NewestOf(buckets[a]);
                    DateTime bNewest = NewestOf(buckets[b]);
                    int byRecency = bNewest.CompareTo(aNewest); // descending
                    if (byRecency != 0)
                    {
                        return byRecency;
                    }
                    // Two groups whose newest row landed at the exact same
                    // instant have no natural recency order; break the tie
                    // on the key itself purely for determinism, matching
                    // rule 5's reasoning for rows -- a list that reshuffles
                    // its section order on every refresh is a bug even when
                    // no individual row moved.
                    return string.CompareOrdinal(a, b);
                });
            }

            if (pinned.Count > 0)
            {
                groups.Add(new HistoryGroup { Key = PinnedGroupKey, IsPinnedGroup = true, Rows = pinned });
            }
            for (int i = 0; i < orderedKeys.Count; i++)
            {
                string key = orderedKeys[i];
                groups.Add(new HistoryGroup { Key = key, IsPinnedGroup = false, Rows = buckets[key] });
            }

            int totalRows = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                totalRows += groups[i].Rows.Count;
            }

            if (limit <= 0)
            {
                hiddenCount = 0;
                return groups;
            }

            int kept = totalRows < limit ? totalRows : limit;
            hiddenCount = totalRows - kept;

            var limited = new List<HistoryGroup>();
            int capacity = limit;
            for (int i = 0; i < groups.Count && capacity > 0; i++)
            {
                HistoryGroup group = groups[i];
                if (group.Rows.Count <= capacity)
                {
                    limited.Add(group);
                    capacity -= group.Rows.Count;
                }
                else
                {
                    // Rule 6: the cut always lands inside the last group the
                    // page can still (partially) show, never drops a whole
                    // group that had room for at least one more row.
                    limited.Add(new HistoryGroup
                    {
                        Key = group.Key,
                        IsPinnedGroup = group.IsPinnedGroup,
                        Rows = group.Rows.GetRange(0, capacity)
                    });
                    capacity = 0;
                }
            }
            // A group with zero rows is never added above (the loop only
            // adds a group when it has at least one row within capacity),
            // so rule 6's "a group left with zero rows is not returned"
            // holds without a separate filtering pass.
            return limited;
        }

        /// <summary>
        /// Rule 5's ordering, shared by every group (pinned or not):
        /// newest-first, tie-broken by SessionId ascending so that two rows
        /// with the exact same mtime (a plausible real case -- e.g. a batch
        /// restore, or a filesystem with 1-second mtime resolution) always
        /// land in the same relative order across repeated calls instead of
        /// depending on <see cref="List{T}.Sort"/>'s non-stable partition
        /// behaviour. SessionId is compared ordinally, matching every other
        /// id/key comparison in this file -- session ids are opaque tokens,
        /// not user-facing text, so there is no locale-sensitive comparison
        /// to prefer.
        /// </summary>
        private static void SortRowsDeterministic(List<HistoryRow> rows)
        {
            rows.Sort(delegate (HistoryRow a, HistoryRow b)
            {
                int byTime = b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
                if (byTime != 0)
                {
                    return byTime;
                }
                string idA = a.SessionId ?? string.Empty;
                string idB = b.SessionId ?? string.Empty;
                return string.CompareOrdinal(idA, idB);
            });
        }

        private static DateTime NewestOf(List<HistoryRow> rows)
        {
            DateTime newest = DateTime.MinValue;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].LastModifiedUtc > newest)
                {
                    newest = rows[i].LastModifiedUtc;
                }
            }
            return newest;
        }
    }
}

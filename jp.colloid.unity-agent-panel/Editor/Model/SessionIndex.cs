using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Enumerates the CLI's own session transcripts for a working directory
    /// (ARCHITECTURE.md D5: "the canonical transcript lives in
    /// ~/.claude/projects/&lt;cwd-transform&gt;/&lt;session-uuid&gt;.jsonl").
    /// Pure C# + Core/Json only; no Unity API usage so it is directly
    /// EditMode-testable against a fake directory tree. See
    /// docs/design-notes/2026-07-31-session-history-restore.md for the
    /// evidence behind the transform rule and the tolerance policy below.
    /// </summary>
    public sealed class SessionIndex
    {
        private readonly string _projectsRoot;
        private readonly string _panelSessionsDirOverride;
        private List<SessionIndexEntry> _entries = new List<SessionIndexEntry>();

        /// <summary>
        /// projectsRootOverride lets tests point at a temp directory instead
        /// of the real "~/.claude/projects"; production code should pass
        /// null (or omit the argument) to use the default.
        /// panelSessionsDirOverride does the same for the panel's own
        /// <see cref="PanelSessionStore"/> (ACP agents' sessions, design
        /// note 2026-09-13-acp-feature-parity.md section 1); null derives
        /// it from the cwd passed to <see cref="Refresh"/>.
        /// </summary>
        public SessionIndex(string projectsRootOverride = null, string panelSessionsDirOverride = null)
        {
            _projectsRoot = string.IsNullOrEmpty(projectsRootOverride)
                ? DefaultProjectsRoot()
                : projectsRootOverride;
            _panelSessionsDirOverride = string.IsNullOrEmpty(panelSessionsDirOverride)
                ? null : panelSessionsDirOverride;
        }

        /// <summary>Default "~/.claude/projects" for the current user.</summary>
        public static string DefaultProjectsRoot()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", "projects");
        }

        /// <summary>The root directory this index was constructed with.</summary>
        public string ProjectsRoot
        {
            get { return _projectsRoot; }
        }

        /// <summary>
        /// Sessions found by the last Refresh() call, newest (by last-write
        /// time) first. Empty before the first Refresh() and whenever the
        /// project directory does not exist or cannot be enumerated.
        /// </summary>
        public IReadOnlyList<SessionIndexEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// Transforms an absolute working-directory path into the CLI's
        /// project directory name: every character that is not an ASCII
        /// letter or digit becomes '-' (no collapsing of repeats, one '-'
        /// per replaced character). Verified against the real
        /// "~/.claude/projects" directory on this machine for three
        /// distinct cwd values (see the design note); e.g.
        /// "C:\Unity\UnityProjects\DevelopmentProject" -&gt;
        /// "C--Unity-UnityProjects-DevelopmentProject". ASCII-only
        /// (char.IsLetterOrDigit would also keep non-ASCII letters, e.g.
        /// CJK, but none of the verified real directory names contain
        /// non-ASCII text to confirm that against, and treating them as
        /// "keep" instead of "replace" risks resolving to the WRONG
        /// directory for a path with non-ASCII segments -- so this
        /// deliberately narrows to [a-zA-Z0-9], the common convention for
        /// this kind of slug transform, rather than assuming Unicode
        /// letters pass through unchanged. See the design note.
        /// </summary>
        public static string TransformCwdToProjectDirName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return string.Empty;
            }
            var chars = new char[cwd.Length];
            for (int i = 0; i < cwd.Length; i++)
            {
                char c = cwd[i];
                bool isAsciiAlnum = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9');
                chars[i] = isAsciiAlnum ? c : '-';
            }
            return new string(chars);
        }

        /// <summary>
        /// Re-enumerates *.jsonl files for the given working directory,
        /// replacing Entries. Tolerant of a missing project directory
        /// (empty result) and of individual files that fail to stat (they
        /// are skipped, never thrown). Does not read message content --
        /// callers access SessionIndexEntry.FirstUserTextPreview lazily
        /// per-row only when the UI actually needs it.
        ///
        /// Also lists the panel's own store of ACP-agent sessions for the
        /// same working directory (<see cref="PanelSessionStore"/>, under
        /// the project's UserSettings), as entries whose
        /// <see cref="SessionIndexEntry.IsPanelStore"/> is true. A Claude
        /// session is only ever on the CLI's jsonl and an ACP session only
        /// ever in the panel store, so no id appears twice.
        /// </summary>
        public void Refresh(string cwd)
        {
            var found = new List<SessionIndexEntry>();
            string dirName = TransformCwdToProjectDirName(cwd);
            if (!string.IsNullOrEmpty(dirName))
            {
                string sessionDir = Path.Combine(_projectsRoot, dirName);
                TryEnumerate(sessionDir, found);
            }
            string panelDir = _panelSessionsDirOverride
                ?? (string.IsNullOrEmpty(cwd) ? null : PanelSessionStore.DefaultDirectory(cwd));
            if (panelDir != null)
            {
                List<PanelSessionEntry> stored = new PanelSessionStore(panelDir).Enumerate();
                for (int i = 0; i < stored.Count; i++)
                {
                    found.Add(new SessionIndexEntry(stored[i], cwd));
                }
            }
            found.Sort(delegate (SessionIndexEntry a, SessionIndexEntry b)
            {
                return b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
            });
            _entries = found;
        }

        private static void TryEnumerate(string sessionDir, List<SessionIndexEntry> found)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(sessionDir))
                {
                    return;
                }
                files = Directory.GetFiles(sessionDir, "*.jsonl");
            }
            catch (Exception)
            {
                // Unreadable directory (permissions, race with deletion,
                // etc.): behave exactly like "no sessions here yet".
                return;
            }
            for (int i = 0; i < files.Length; i++)
            {
                SessionIndexEntry entry = TryStat(files[i]);
                if (entry != null)
                {
                    found.Add(entry);
                }
            }
        }

        private static SessionIndexEntry TryStat(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    return null;
                }
                string sessionId = Path.GetFileNameWithoutExtension(filePath);
                return new SessionIndexEntry(sessionId, filePath, info.LastWriteTimeUtc, info.Length);
            }
            catch (Exception)
            {
                // One bad file (locked, deleted mid-enumeration, permission
                // denied) must never blank out the rest of the list.
                return null;
            }
        }
    }

    /// <summary>
    /// One row of SessionIndex.Entries. Cheap fields (id/mtime/size) are
    /// computed eagerly by Refresh(); FirstUserTextPreview, AiTitle and Cwd
    /// are computed on first access and cached (never re-read after that,
    /// matching the "no caching beyond one Refresh() call" contract for the
    /// index itself -- the cache lives on the entry instance, which is
    /// discarded on the next Refresh() anyway).
    ///
    /// All three lazy fields are filled by a SINGLE scan of the file
    /// (<see cref="ComputeAll"/>), not three independent scans, because a
    /// row that displays a title and a cwd next to its preview would
    /// otherwise pay for reading the same jsonl file up to three times.
    /// This is safe (not just an optimisation that happens to work) because
    /// of measurements taken across 18 real transcripts under
    /// "~/.claude/projects" (see
    /// docs/design-notes/2026-07-31-session-history-restore.md):
    ///   - the first "ai-title" line appears at line index 6-8, and the
    ///     first top-level "cwd" field appears at line index 2 -- both far
    ///     inside the existing 500-line PreviewScanLineCap, so a single
    ///     bounded pass that also watches for these two fields costs
    ///     nothing extra in the common case;
    ///   - every one of those 18 files contained exactly ONE distinct
    ///     title string, repeated verbatim on later "ai-title" lines, so
    ///     taking the FIRST occurrence (rather than the last, or scanning
    ///     the whole file to confirm uniqueness) is not a guess about
    ///     which occurrence is "freshest" -- there is only one value to
    ///     find, ever;
    ///   - 18/18 files had an "ai-title" line at all, so treating it as a
    ///     reliably-present field (worth extracting unconditionally on the
    ///     same pass as the preview) rather than a best-effort extra that
    ///     would need its own opt-in scan is justified by the sample.
    /// </summary>
    public sealed class SessionIndexEntry
    {
        /// <summary>Cap on how far into the file the combined scan looks
        /// before giving up, so a pathological transcript (huge run of
        /// meta lines before the first real user message, or one that
        /// never contains an ai-title/cwd line at all) cannot make a
        /// history-list row expensive to render. Shared by all three lazy
        /// fields -- see the measurements in the class doc comment above
        /// for why line index 500 leaves comfortable headroom over the
        /// observed 2-8 range for cwd/ai-title.</summary>
        private const int PreviewScanLineCap = 500;
        private const int PreviewMaxChars = 80;

        private readonly string _filePath;
        private readonly PanelSessionEntry _panelEntry;
        private readonly string _panelCwd;
        private string _preview;
        private string _aiTitle;
        private string _cwd;
        private string _modelName;
        private bool _computed;

        internal SessionIndexEntry(string sessionId, string filePath,
            DateTime lastModifiedUtc, long sizeBytes)
        {
            SessionId = sessionId ?? string.Empty;
            _filePath = filePath;
            LastModifiedUtc = lastModifiedUtc;
            SizeBytes = sizeBytes;
        }

        /// <summary>
        /// An entry backed by the panel's own session store (an ACP
        /// agent's session) rather than a CLI jsonl. The lazy fields are
        /// answered from the stored JSON: the panel's title stands in for
        /// the ai-title, the working directory is the project itself.
        /// </summary>
        internal SessionIndexEntry(PanelSessionEntry panelEntry, string cwd)
        {
            _panelEntry = panelEntry;
            _panelCwd = cwd ?? string.Empty;
            SessionId = panelEntry.SessionId;
            _filePath = panelEntry.FilePath;
            LastModifiedUtc = panelEntry.LastModifiedUtc;
            SizeBytes = panelEntry.SizeBytes;
        }

        /// <summary>True when the transcript is a PanelSessionStore file (feed <see cref="FilePath"/> to SessionCacheFile, not TranscriptLoader).</summary>
        public bool IsPanelStore
        {
            get { return _panelEntry != null; }
        }

        /// <summary>
        /// The backend (AgentBackend as int) that owns the session: 0
        /// (Claude Code) for a CLI jsonl, the stored value for a panel-store
        /// file (-1 when the file does not say). Read lazily for the panel
        /// store.
        /// </summary>
        public int AgentBackend
        {
            get { return _panelEntry != null ? _panelEntry.AgentBackend : 0; }
        }

        /// <summary>The session uuid (the jsonl file name without extension).</summary>
        public string SessionId { get; private set; }

        /// <summary>Absolute path of the session's jsonl file (feed to TranscriptLoader.Load).</summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        public DateTime LastModifiedUtc { get; private set; }
        public long SizeBytes { get; private set; }

        /// <summary>
        /// First non-meta user text in the transcript, truncated to ~80
        /// chars with an ASCII ellipsis; empty string when the file has no
        /// user text within the scan cap or cannot be read. Computed lazily
        /// (together with <see cref="AiTitle"/> and <see cref="Cwd"/> in one
        /// pass) and cached on this instance.
        /// </summary>
        public string FirstUserTextPreview
        {
            get
            {
                EnsureComputed();
                return _preview;
            }
        }

        /// <summary>
        /// The "aiTitle" field carried on the first line whose "type" is
        /// "ai-title", truncated with the same rule as
        /// <see cref="FirstUserTextPreview"/> (same Truncate/FirstLine
        /// helpers, same <see cref="PreviewMaxChars"/> cap) so a
        /// pathological title cannot blow out a history-list row any more
        /// than a pathological user message could. Empty string when no
        /// such line exists within the scan cap, or when the file is
        /// unreadable. See the class doc comment for the measurement
        /// (18/18 sampled transcripts had exactly one such line, so taking
        /// the first occurrence is exact, not a heuristic) that justifies
        /// extracting this on the very same pass as the preview instead of
        /// a second file read. Computed lazily and cached on this instance.
        /// </summary>
        public string AiTitle
        {
            get
            {
                EnsureComputed();
                return _aiTitle;
            }
        }

        /// <summary>
        /// The first non-empty top-level "cwd" field seen while scanning,
        /// regardless of which line "type" carries it. Empty string when no
        /// line has a non-empty "cwd" within the scan cap, or when the file
        /// is unreadable. See the class doc comment: measured at line index
        /// 2 across the 18 sampled transcripts, so it is found essentially
        /// immediately and adds no meaningful cost to the shared scan.
        /// Computed lazily and cached on this instance.
        /// </summary>
        public string Cwd
        {
            get
            {
                EnsureComputed();
                return _cwd;
            }
        }

        /// <summary>
        /// The "message.model" of the first assistant line whose model is
        /// real -- TranscriptUsage.SyntheticModelName ("&lt;synthetic&gt;",
        /// the CLI's local no-API turns) is skipped, same rule
        /// TranscriptUsage itself applies -- or empty when no such line
        /// exists within the scan cap or the file is unreadable. A session
        /// can switch models mid-way; "the model it STARTED on" is the
        /// honest single label for a one-line history row, and one more
        /// latch on the existing shared scan is free (UXIA-5). Computed
        /// lazily and cached on this instance.
        /// </summary>
        public string ModelName
        {
            get
            {
                EnsureComputed();
                return _modelName;
            }
        }

        /// <summary>
        /// Runs <see cref="ComputeAll"/> at most once per entry instance and
        /// latches the three results with a single <see cref="_computed"/>
        /// flag. Replaces what used to be a preview-only bool
        /// ("_previewComputed") precisely so a caller touching AiTitle
        /// first, then Cwd, then FirstUserTextPreview (in any order) still
        /// triggers exactly one file read total, not one per property.
        /// </summary>
        private void EnsureComputed()
        {
            if (_computed)
            {
                return;
            }
            if (_panelEntry != null)
            {
                _preview = _panelEntry.FirstUserTextPreview ?? string.Empty;
                _aiTitle = _panelEntry.Title ?? string.Empty;
                _cwd = _panelCwd;
                _modelName = _panelEntry.ModelName ?? string.Empty;
                _computed = true;
                return;
            }
            string preview;
            string aiTitle;
            string cwd;
            string modelName;
            ComputeAll(_filePath, out preview, out aiTitle, out cwd, out modelName);
            _preview = preview;
            _aiTitle = aiTitle;
            _cwd = cwd;
            _modelName = modelName;
            _computed = true;
        }

        /// <summary>
        /// Single bounded scan of the transcript that fills all three lazy
        /// fields together. Each field has its own "found" latch so that,
        /// for example, a "cwd" line that appears after the "ai-title" line
        /// does not overwrite an already-found title, and vice versa --
        /// each value is independently "the first qualifying line for that
        /// field", not "whatever the scan happened to see last". The loop
        /// still honours PreviewScanLineCap as the hard upper bound on how
        /// far it will read, but breaks out earlier than that whenever all
        /// three fields are already found, and a malformed/truncated line
        /// (JsonParseException) is skipped rather than aborting the whole
        /// scan, matching the tolerance policy documented on the former
        /// ComputePreview. An unreadable file (locked, deleted mid-scan,
        /// permission denied) yields empty strings for all three rather
        /// than throwing.
        /// </summary>
        private static void ComputeAll(string filePath, out string preview, out string aiTitle, out string cwd,
            out string modelName)
        {
            preview = string.Empty;
            aiTitle = string.Empty;
            cwd = string.Empty;
            modelName = string.Empty;
            try
            {
                bool previewFound = false;
                bool aiTitleLineSeen = false;
                bool cwdFound = false;
                bool modelFound = false;
                int scanned = 0;
                foreach (string rawLine in File.ReadLines(filePath))
                {
                    if (scanned++ >= PreviewScanLineCap)
                    {
                        break;
                    }
                    if (previewFound && aiTitleLineSeen && cwdFound && modelFound)
                    {
                        // Every field this scan cares about already has its
                        // answer -- stop reading the rest of the file
                        // rather than burning time confirming there is
                        // nothing left to find.
                        break;
                    }
                    string line = rawLine;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    JsonNode node;
                    try
                    {
                        node = JsonParser.Parse(line);
                    }
                    catch (JsonParseException)
                    {
                        // Malformed or (on the very last line of a
                        // crash-time transcript) truncated JSON: skip this
                        // line and keep scanning, never throw.
                        continue;
                    }
                    if (!node.IsObject)
                    {
                        continue;
                    }

                    if (!cwdFound)
                    {
                        string cwdValue = node["cwd"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(cwdValue))
                        {
                            cwd = cwdValue;
                            cwdFound = true;
                        }
                    }

                    string type = node["type"].AsString(string.Empty);

                    if (!aiTitleLineSeen && type == "ai-title")
                    {
                        // "First line whose type is ai-title" is a
                        // positional rule, not a content rule: even if the
                        // aiTitle field itself is missing/empty on that
                        // line, this is still THE line, so we latch now and
                        // never look at a later ai-title line.
                        aiTitle = Truncate(FirstLine(node["aiTitle"].AsString(string.Empty)), PreviewMaxChars);
                        aiTitleLineSeen = true;
                    }

                    if (!modelFound && type == "assistant")
                    {
                        string model = node["message"]["model"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(model)
                            && !string.Equals(model, TranscriptUsage.SyntheticModelName, StringComparison.Ordinal))
                        {
                            modelName = model;
                            modelFound = true;
                        }
                    }

                    // isCompactSummary: the CLI-authored summary injected
                    // after a compaction is not something the person
                    // typed, so it must never become a session's preview.
                    if (!previewFound && !node["isMeta"].AsBool()
                        && !node["isCompactSummary"].AsBool() && type == "user")
                    {
                        string text = FirstTextContent(node["message"]["content"]);
                        if (!string.IsNullOrEmpty(text))
                        {
                            preview = Truncate(FirstLine(text), PreviewMaxChars);
                            previewFound = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable file (locked, deleted mid-scan, permission
                // denied): no preview/title/cwd, never throw. Reset all
                // three in case the exception happened after one of them
                // was already latched from a partial read.
                preview = string.Empty;
                aiTitle = string.Empty;
                cwd = string.Empty;
                modelName = string.Empty;
            }
        }

        private static string FirstTextContent(JsonNode content)
        {
            if (content.IsString)
            {
                return content.AsString(string.Empty);
            }
            if (content.IsArray)
            {
                foreach (JsonNode item in content.Items)
                {
                    if (item.IsObject && item["type"].AsString(string.Empty) == "text")
                    {
                        string text = item["text"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }
                }
            }
            return string.Empty;
        }

        private static string FirstLine(string text)
        {
            int newline = text.IndexOfAny(new[] { '\r', '\n' });
            return newline >= 0 ? text.Substring(0, newline) : text;
        }

        private static string Truncate(string value, int maxChars)
        {
            string trimmed = value.Trim();
            if (trimmed.Length <= maxChars)
            {
                return trimmed;
            }
            return trimmed.Substring(0, maxChars - 3) + "...";
        }
    }
}

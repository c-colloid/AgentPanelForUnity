using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Collapsible tool activity card (R05 section 2.4): status icon
    /// (spinner -&gt; check/cross), tool icon, bold name, one-line summary
    /// (ToolCardDescriber), duration and an expandable input/result
    /// preview with an internal scroll cap. Cards default to collapsed;
    /// because MessageListController rebuilds a row whenever the tool
    /// status changes, a completing tool automatically re-renders as a
    /// fresh collapsed card ("auto-collapse on success").
    /// </summary>
    public sealed class ToolActivityCard : VisualElement
    {
        private readonly VisualElement _details;
        private readonly Label _chevron;
        private readonly string _toolUseId = string.Empty;
        private bool _expanded;

        /// <summary>
        /// Expand/collapse memory keyed by ToolCallRecord.toolUseId -- the
        /// SAME mechanism as SubagentCard.ExpandedByToolUseId (see that
        /// field's doc comment for the full rebuild-discards-the-instance
        /// chain; design note 2026-08-03-subagent-ux-and-midturn-input.md
        /// section 1, "NESTED tool card expanded"). A standalone
        /// ToolActivityCard rebuilds under the identical MessageListController
        /// swap whenever its own ToolCallRecord's status changes (Running ->
        /// Succeeded is a signature change); a NESTED one -- rendered inside
        /// a SubagentCard's expanded details via MessageBlockFactory.
        /// CreateBlockElement -- rebuilds whenever the WHOLE SubagentCard
        /// rebuilds for any reason, which is far more frequent while the
        /// subagent is still running. Both paths discarded a manually
        /// expanded details pane before this fix, since _expanded lived only
        /// on the about-to-be-thrown-away instance. Editor-session lifetime
        /// only, not persisted -- same limitation SubagentCard.
        /// ExpandedByToolUseId documents for the identical reason.
        /// </summary>
        private static readonly Dictionary<string, bool> ExpandedByToolUseId = new Dictionary<string, bool>();

        public ToolActivityCard(ToolCallRecord record)
        {
            AddToClassList("uap-toolcard");
            if (record == null)
            {
                return;
            }
            _toolUseId = record.toolUseId ?? string.Empty;
            // 2026-09-05 UI redesign (D3): failed and denied are two
            // different facts ("it broke" vs "you said no") and get two
            // accent bars -- see AgentPanel.uss .uap-toolcard--denied.
            if (record.status == ToolCallStatus.Failed)
            {
                AddToClassList("uap-toolcard--failed");
            }
            else if (record.status == ToolCallStatus.Denied)
            {
                AddToClassList("uap-toolcard--denied");
            }

            ToolCardDescriber.Description desc =
                ToolCardDescriber.Describe(record.toolName, record.inputJson);

            var header = new VisualElement();
            header.AddToClassList("uap-toolcard-header");

            header.Add(CreateStatusIcon(record));

            if (!string.IsNullOrEmpty(desc.IconName) || !string.IsNullOrEmpty(desc.FallbackGlyph))
            {
                header.Add(IconLoader.CreateIcon(desc.IconName, desc.FallbackGlyph,
                    "uap-toolcard-toolicon", "uap-toolcard-toolglyph"));
            }

            // ShortenToolDisplayName strips the "mcp__server__" wire-protocol
            // prefix (see its doc comment for the live measurement that made
            // this necessary: a 48-char raw UapOps wire name needs 300.5 px
            // at this label's fontSize 11, against a ~300 px content column
            // -- the USS side now caps/ellipsizes this label so it can never
            // blow out the header again, but an end-truncated raw wire name
            // would hide the only part that says what the tool DOES). The
            // tooltip keeps the exact, un-shortened wire string reachable on
            // hover -- a user debugging a permission prompt needs the string
            // the CLI actually used, not just the friendly label.
            string rawToolName = record.toolName;
            string displayText = string.IsNullOrEmpty(rawToolName)
                ? L10n.S.ToolCardDefaultName
                : IconLoader.SanitizeForDisplay(ToolCardDescriber.ShortenToolDisplayName(rawToolName));
            var name = new Label(displayText);
            name.AddToClassList("uap-toolcard-name");
            name.enableRichText = false;
            if (!string.IsNullOrEmpty(rawToolName))
            {
                name.tooltip = IconLoader.SanitizeForDisplay(rawToolName);
            }
            header.Add(name);

            // Belt and suspenders (design note 2026-08-14-ui-polish-audit.md
            // contract item 4): when the describer table's generic
            // fallback (or the AskUserQuestion branch's own "no data"
            // fallback) has nothing better to say than the tool's own
            // name, the summary label would otherwise repeat the header
            // name label verbatim right next to it -- skip adding a
            // redundant second Label instead of rendering "Bash Bash".
            // Case-insensitive because displayText is already the
            // sanitized/shortened name a human reads, and the describer
            // table's own name-echoing fallbacks (e.g. the unknown-tool
            // branch) do not always match its exact casing.
            // Also compared against the RAW wire name: an MCP row whose
            // summary fell back to "mcp__server__tool" while the header
            // shows the shortened "tool" is the same redundancy.
            string sanitizedSummary = IconLoader.SanitizeForDisplay(desc.Summary);
            bool summaryIsEcho = string.IsNullOrEmpty(sanitizedSummary)
                || string.Equals(sanitizedSummary, displayText, System.StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(rawToolName) && string.Equals(sanitizedSummary,
                    IconLoader.SanitizeForDisplay(rawToolName), System.StringComparison.OrdinalIgnoreCase));
            if (!summaryIsEcho)
            {
                var summary = new Label(sanitizedSummary);
                summary.AddToClassList("uap-toolcard-summary");
                summary.enableRichText = false;
                header.Add(summary);
            }
            else
            {
                // The summary label is the header's only flex-grow child.
                // Without it, .uap-toolcard-header's justify-content:
                // space-between distributes the free space between the
                // name and the time/chevron column, floating the name into
                // the middle of the row ("Skill" centred, chevron far
                // right). An empty flex-grow spacer keeps the name pinned
                // to the left next to its icons.
                var spacer = new VisualElement();
                spacer.AddToClassList("uap-toolcard-spacer");
                spacer.pickingMode = PickingMode.Ignore;
                header.Add(spacer);
            }

            header.Add(CreateTimeLabel(record));

            bool hasDetails = !string.IsNullOrEmpty(record.inputJson)
                || !string.IsNullOrEmpty(record.resultSummary);
            _chevron = new Label(IconLoader.GlyphChevronRight);
            _chevron.AddToClassList("uap-toolcard-chevron");
            _chevron.style.visibility = hasDetails ? Visibility.Visible : Visibility.Hidden;
            header.Add(_chevron);
            Add(header);

            if (record.HasResultImages)
            {
                // Always visible, outside the collapsible details: a
                // screenshot or a generated picture is the result itself
                // (design note 2026-09-12-tool-result-image-preview.md).
                Add(BuildImageStrip(record));
            }

            if (hasDetails)
            {
                _details = BuildDetails(record);
                _details.style.display = DisplayStyle.None;
                Add(_details);
                header.RegisterCallback<ClickEvent>(delegate { ToggleExpanded(); });

                bool restoreExpanded;
                if (!string.IsNullOrEmpty(_toolUseId)
                    && ExpandedByToolUseId.TryGetValue(_toolUseId, out restoreExpanded)
                    && restoreExpanded)
                {
                    SetExpanded(true);
                }
            }
        }

        private void ToggleExpanded()
        {
            if (_details == null)
            {
                return;
            }
            SetExpanded(!_expanded);
        }

        /// <summary>
        /// Applies expand/collapse state (used both by the user's click via
        /// ToggleExpanded and by construction-time restore of a previously
        /// expanded card) and records it in <see cref="ExpandedByToolUseId"/>
        /// keyed by toolUseId, exactly mirroring SubagentCard.SetExpanded, so
        /// the next rebuilt card for the same tool call starts in the same
        /// state. Records are skipped for a null/empty toolUseId (nothing to
        /// key on -- e.g. a locally-constructed record in a context that
        /// never assigned one).
        /// </summary>
        private void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            _details.style.display = _expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _chevron.text = _expanded ? IconLoader.GlyphChevronDown : IconLoader.GlyphChevronRight;
            if (!string.IsNullOrEmpty(_toolUseId))
            {
                ExpandedByToolUseId[_toolUseId] = _expanded;
            }
        }

        /// <summary>Test seam (InternalsVisibleTo "Colloid.AgentPanel.Editor.Tests",
        /// see Editor/AssemblyInfo.cs): drives the same click path as the
        /// header's ClickEvent handler without needing a panel to dispatch
        /// the event.</summary>
        internal void ToggleExpandedForTests()
        {
            ToggleExpanded();
        }

        /// <summary>Test seam: current expand state, including a state
        /// restored from <see cref="ExpandedByToolUseId"/> at construction.</summary>
        internal bool IsExpandedForTests
        {
            get { return _expanded; }
        }

        /// <summary>Test seam: clears the expand-state memory so tests don't
        /// leak state into each other via toolUseId collisions across the
        /// static dictionary's editor-session lifetime.</summary>
        internal static void ResetExpandedStateForTests()
        {
            ExpandedByToolUseId.Clear();
        }

        // -- Sections -----------------------------------------------------------

        private static VisualElement BuildDetails(ToolCallRecord record)
        {
            var details = new VisualElement();
            details.AddToClassList("uap-toolcard-details");
            if (!string.IsNullOrEmpty(record.inputJson))
            {
                details.Add(CreateSection(L10n.S.ToolCardSectionInput, record.inputJson));
            }
            if (!string.IsNullOrEmpty(record.resultSummary))
            {
                details.Add(CreateSection(
                    record.isError ? L10n.S.ToolCardSectionError : L10n.S.ToolCardSectionResult,
                    record.resultSummary));
            }
            return details;
        }

        /// <summary>
        /// Thumbnails of the pictures the tool returned, one
        /// MessageBlockFactory image block each (same ScaleToFit box,
        /// click opens the file, "missing" placeholder once retention
        /// removed it), captioned with the file name. Internal so the
        /// EditMode suite can assert on the strip.
        /// </summary>
        internal static VisualElement BuildImageStrip(ToolCallRecord record)
        {
            var strip = new VisualElement();
            strip.AddToClassList("uap-toolcard-images");
            for (int i = 0; i < record.resultImagePaths.Count; i++)
            {
                string path = record.resultImagePaths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                string caption;
                try
                {
                    caption = System.IO.Path.GetFileName(path);
                }
                catch (ArgumentException)
                {
                    caption = path;
                }
                strip.Add(MessageBlockFactory.CreateImageBlock(ChatMessageBlock.MakeImage(path, caption)));
            }
            return strip;
        }

        /// <summary>Titled preview capped by an internal ScrollView.</summary>
        private static VisualElement CreateSection(string title, string body)
        {
            var section = new VisualElement();
            section.AddToClassList("uap-toolcard-section");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("uap-toolcard-section-title");
            section.Add(titleLabel);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("uap-toolcard-scroll");

            var pre = new Label(IconLoader.SanitizeForDisplay(body));
            pre.enableRichText = false;
            pre.AddToClassList("uap-toolcard-pre");
            MessageBlockFactory.ApplyMonoFont(pre);
            scroll.Add(pre);

            section.Add(scroll);
            return section;
        }

        /// <summary>
        /// Modifier class for a compressed tool group: failed outranks
        /// denied outranks nothing. Pure; pinned by
        /// ToolActivityCardStatusVocabularyTests.
        /// </summary>
        internal static string ResolveGroupModifier(bool anyFailed, bool anyDenied)
        {
            if (anyFailed)
            {
                return "uap-toolgroup--failed";
            }
            if (anyDenied)
            {
                return "uap-toolgroup--denied";
            }
            return null;
        }

        private static VisualElement CreateStatusIcon(ToolCallRecord record)
        {
            switch (record.status)
            {
                case ToolCallStatus.Running:
                    return MessageBlockFactory.CreateSpinner();
                case ToolCallStatus.Succeeded:
                    return IconLoader.CreateIcon("TestPassed", IconLoader.GlyphCheck,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--ok");
                case ToolCallStatus.Failed:
                    return IconLoader.CreateIcon("TestFailed", IconLoader.GlyphCross,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--fail");
                case ToolCallStatus.Denied:
                    // Test Runner's "ignored" icon (2022.3 built-in), the
                    // same family as TestPassed/TestFailed above so the
                    // three outcomes read as one vocabulary. The text
                    // fallback stays the proven-safe cross; the class
                    // (caution tone) carries the distinction when the
                    // icon lookup fails.
                    return IconLoader.CreateIcon("TestIgnored", IconLoader.GlyphCross,
                        "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--denied");
                default:
                    var pending = new Label(IconLoader.GlyphBullet);
                    pending.AddToClassList("uap-tool-glyph");
                    pending.AddToClassList("uap-tool-glyph--pending");
                    return pending;
            }
        }

        /// <summary>Duration for finished tools; live elapsed while running.
        /// The live ticker is paused on detach so a discarded card (row
        /// rebuilt by MessageListController, or pruned) never keeps ticking
        /// outside the visual tree.</summary>
        private static Label CreateTimeLabel(ToolCallRecord record)
        {
            var time = new Label(string.Empty);
            time.AddToClassList("uap-toolcard-time");
            if (record.status == ToolCallStatus.Running && record.startedAtUtcTicks > 0)
            {
                long startTicks = record.startedAtUtcTicks;
                time.text = FormatSeconds(ElapsedMs(startTicks));
                IVisualElementScheduledItem ticker = time.schedule.Execute(() =>
                {
                    time.text = FormatSeconds(ElapsedMs(startTicks));
                }).Every(500);
                time.RegisterCallback<DetachFromPanelEvent>(delegate
                {
                    ticker.Pause();
                });
            }
            else if (record.durationMs > 0)
            {
                time.text = FormatSeconds(record.durationMs);
            }
            return time;
        }

        /// <summary>Internal so SubagentCard (same "live elapsed" duration display) can reuse it.</summary>
        internal static long ElapsedMs(long startUtcTicks)
        {
            long delta = (DateTime.UtcNow.Ticks - startUtcTicks) / TimeSpan.TicksPerMillisecond;
            return delta > 0 ? delta : 0;
        }

        /// <summary>Internal so SubagentCard (same "live elapsed" duration display) can reuse it.</summary>
        internal static string FormatSeconds(long millis)
        {
            return L10n.F(L10n.S.ToolCardDurationSecondsFmt, (millis / 1000.0).ToString("0.0"));
        }

        // -- Group row (3+ consecutive completed cards; R05 section 2.4) --------------

        /// <summary>
        /// Builds the "N tools (total)" group row that collapses a run of
        /// completed tool calls; expanding reveals the individual cards.
        /// </summary>
        public static VisualElement CreateGroupRow(List<ToolCallRecord> records)
        {
            return CreateGroupRow(records, null);
        }

        /// <summary>
        /// <paramref name="stateKey"/> (ExpandStateMemory.BlockKey of the
        /// run's first block) restores the row's open state across the
        /// message rebuilds that happen on every appended block; null
        /// means no memory. A group that has never been toggled opens by
        /// itself when one of the cards it swallows is remembered as
        /// expanded (<see cref="IsRememberedExpanded"/>): the third
        /// completion in a run folds the cards the user was reading into
        /// this row, and hiding an open card behind a closed group would
        /// be the same "it closed on me" the memory exists to prevent.
        /// </summary>
        public static VisualElement CreateGroupRow(List<ToolCallRecord> records, string stateKey)
        {
            var group = new VisualElement();
            group.AddToClassList("uap-toolgroup");

            long totalMs = 0;
            bool anyFailed = false;
            bool anyDenied = false;
            for (int i = 0; i < records.Count; i++)
            {
                totalMs += records[i].durationMs;
                anyFailed |= records[i].status == ToolCallStatus.Failed;
                anyDenied |= records[i].status == ToolCallStatus.Denied;
            }
            // 2026-09-05 UI redesign (D3): the collapsed group row carries
            // the same accent bar and glyph as the worst child inside it,
            // failed outranking denied, so folding a group never hides an
            // outcome the expanded cards would have shown.
            string groupModifier = ResolveGroupModifier(anyFailed, anyDenied);
            if (groupModifier != null)
            {
                group.AddToClassList(groupModifier);
            }

            var header = new VisualElement();
            header.AddToClassList("uap-toolgroup-header");

            var chevron = new Label(IconLoader.GlyphChevronRight);
            chevron.AddToClassList("uap-toolcard-chevron");
            header.Add(chevron);

            if (anyFailed)
            {
                header.Add(IconLoader.CreateIcon("TestFailed", IconLoader.GlyphCross,
                    "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--fail"));
            }
            else if (anyDenied)
            {
                header.Add(IconLoader.CreateIcon("TestIgnored", IconLoader.GlyphCross,
                    "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--denied"));
            }
            else
            {
                header.Add(IconLoader.CreateIcon("TestPassed", IconLoader.GlyphCheck,
                    "uap-tool-icon", "uap-tool-glyph uap-tool-glyph--ok"));
            }

            string title = totalMs > 0
                ? L10n.F(L10n.S.ToolGroupCountWithDurationFmt, records.Count, FormatSeconds(totalMs))
                : L10n.F(L10n.S.ToolGroupCountFmt, records.Count);
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("uap-toolgroup-title");
            titleLabel.enableRichText = false;
            header.Add(titleLabel);
            group.Add(header);

            var children = new VisualElement();
            children.AddToClassList("uap-toolgroup-children");
            children.style.display = DisplayStyle.None;
            for (int i = 0; i < records.Count; i++)
            {
                children.Add(new ToolActivityCard(records[i]));
            }
            group.Add(children);

            bool expanded = false;
            Action<bool> apply = delegate(bool open)
            {
                expanded = open;
                children.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                chevron.text = expanded
                    ? IconLoader.GlyphChevronDown : IconLoader.GlyphChevronRight;
            };
            header.RegisterCallback<ClickEvent>(delegate
            {
                apply(!expanded);
                ExpandStateMemory.Set(stateKey, expanded);
            });
            if (ResolveGroupInitialExpanded(records, stateKey))
            {
                apply(true);
            }
            return group;
        }

        /// <summary>Initial open state of a group row: its own memory when
        /// it has one, else open iff any child card is remembered expanded
        /// (internal for the EditMode suite).</summary>
        internal static bool ResolveGroupInitialExpanded(List<ToolCallRecord> records, string stateKey)
        {
            bool remembered;
            if (ExpandStateMemory.TryGet(stateKey, out remembered))
            {
                return remembered;
            }
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i] != null && IsRememberedExpanded(records[i].toolUseId))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Whether a card for <paramref name="toolUseId"/> was
        /// last left expanded (see <see cref="ExpandedByToolUseId"/>).</summary>
        internal static bool IsRememberedExpanded(string toolUseId)
        {
            bool expanded;
            return !string.IsNullOrEmpty(toolUseId)
                && ExpandedByToolUseId.TryGetValue(toolUseId, out expanded)
                && expanded;
        }
    }
}

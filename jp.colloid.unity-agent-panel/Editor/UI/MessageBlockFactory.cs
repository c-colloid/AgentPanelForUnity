using System.Collections.Generic;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.UI.Markdown;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Translates ChatMessage blocks into VisualElements.
    ///
    /// Rendering policy (ARCHITECTURE.md D7):
    /// - STREAMING blocks stay plain: one Label with enableRichText=false
    ///   driven by StreamingLabelPump. No markup of any kind is
    ///   interpreted mid-stream.
    /// - FINALIZED text blocks get one full markdown render via
    ///   MarkdownRenderer. All rich text it produces passed through
    ///   InlineMarkupConverter, the single escape chokepoint, which is the
    ///   security boundary against tag injection (risk 8).
    /// - Tool calls render as ToolActivityCard; runs of 3+ consecutive
    ///   completed tool blocks compress into one expandable group row.
    /// </summary>
    public static class MessageBlockFactory
    {
        private const int ToolGroupMinRun = 3;

        /// <summary>
        /// Bumped whenever a display setting that changes how an ALREADY
        /// RENDERED message must look is toggled (currently just
        /// PanelSettings.showThinking -- see SettingsView.OnShowThinkingChanged).
        /// Mixed into MessageListController.ComputeSignature so every
        /// cached row's structural signature changes and the whole visible
        /// transcript rebuilds on the next Refresh, instead of only newly
        /// appended messages picking up the new setting.
        /// </summary>
        public static int SettingsGeneration;

        /// <summary>
        /// Builds the full element for one message (role header + blocks).
        /// Streaming text/thinking labels are registered with the pump.
        /// </summary>
        public static VisualElement CreateMessageElement(ChatMessage message,
            StreamingLabelPump pump)
        {
            var root = new VisualElement();
            root.AddToClassList("uap-msg");

            switch (message.role)
            {
                case ChatMessage.RoleUser:
                    root.AddToClassList("uap-msg--user");
                    AddRoleRow(root, null, L10n.S.ChatRoleUser, false);
                    break;
                case ChatMessage.RoleAssistant:
                    root.AddToClassList("uap-msg--assistant");
                    AddRoleRow(root, IconLoader.CurrentAgentGlyph, L10n.A(L10n.S.ChatRoleAssistant), true);
                    break;
                default:
                    root.AddToClassList("uap-msg--system");
                    break;
            }

            int i = 0;
            while (i < message.blocks.Count)
            {
                // Compress a run of 3+ consecutive COMPLETED tool cards
                // into one "N tools" group row (R05 section 2.4). A still
                // running tool ends the run and renders individually.
                if (IsCompletedToolBlock(message.blocks[i]))
                {
                    int runEnd = i;
                    while (runEnd < message.blocks.Count
                        && IsCompletedToolBlock(message.blocks[runEnd]))
                    {
                        runEnd++;
                    }
                    if (runEnd - i >= ToolGroupMinRun)
                    {
                        var records = new List<ToolCallRecord>(runEnd - i);
                        for (int r = i; r < runEnd; r++)
                        {
                            records.Add(message.blocks[r].toolCall);
                        }
                        root.Add(ToolActivityCard.CreateGroupRow(records,
                            ExpandStateMemory.BlockKey(message.id, i)));
                        i = runEnd;
                        continue;
                    }
                }

                VisualElement blockElement = CreateBlockElement(message.blocks[i], pump,
                    ExpandStateMemory.BlockKey(message.id, i));
                if (blockElement != null)
                {
                    root.Add(blockElement);
                }
                i++;
            }
            return root;
        }

        /// <summary>Builds the element for a single block with no
        /// expand-state memory (see the overload below).</summary>
        public static VisualElement CreateBlockElement(ChatMessageBlock block,
            StreamingLabelPump pump)
        {
            return CreateBlockElement(block, pump, null);
        }

        /// <summary>
        /// Builds the element for a single block. <paramref name="stateKey"/>
        /// (see <see cref="ExpandStateMemory.BlockKey"/>) lets the
        /// collapsible blocks -- thinking Foldout, context-attachment chip
        /// -- restore the open state a previous element for the SAME block
        /// had before MessageListController/SubagentCard rebuilt it; null
        /// means "no memory, start collapsed" (a locally-built element with
        /// no owning message).
        /// </summary>
        public static VisualElement CreateBlockElement(ChatMessageBlock block,
            StreamingLabelPump pump, string stateKey)
        {
            if (block == null)
            {
                return null;
            }
            switch (block.kind)
            {
                case ChatBlockKind.Text:
                    return CreateTextBlock(block, pump);
                case ChatBlockKind.Thinking:
                    // Settings enrichment #2 (design note 2026-08-01):
                    // showThinking defaults ON; when the user turns it OFF
                    // the block is skipped entirely rather than rendered
                    // collapsed, both live-streaming and in restored
                    // transcripts.
                    return PanelStateStore.instance.Settings.showThinking
                        ? CreateThinkingBlock(block, pump, stateKey)
                        : null;
                case ChatBlockKind.ToolCall:
                    if (block.toolCall == null)
                    {
                        return null;
                    }
                    // A subagent spawn renders as SubagentCard instead of
                    // the ordinary ToolActivityCard (Phase 4 design note
                    // section 5).
                    return block.toolCall.subagent != null
                        ? (VisualElement)new SubagentCard(block.toolCall)
                        : new ToolActivityCard(block.toolCall);
                case ChatBlockKind.SystemNote:
                    return CreateSystemNoteBlock(block);
                case ChatBlockKind.Error:
                    return CreateErrorBlock(block.text);
                case ChatBlockKind.Permission:
                    // The live permission request renders as the inline
                    // card (ChatView); a serialized block only appears in
                    // restored transcripts, as a note.
                    return CreatePlainLabel(block.text, "uap-note");
                case ChatBlockKind.ContextAttachment:
                    return CreateContextAttachmentBlock(block, stateKey);
                case ChatBlockKind.Image:
                    return CreateImageBlock(block);
                default:
                    return null;
            }
        }

        // -- Text ---------------------------------------------------------

        private static VisualElement CreateTextBlock(ChatMessageBlock block,
            StreamingLabelPump pump)
        {
            if (block.streaming)
            {
                // While streaming: one plain label driven by the pump
                // (enableRichText stays false; injection-safe by
                // construction). The full markdown tree is swapped in by
                // the structural rebuild once the block finalizes.
                Label label = CreatePlainLabel(string.Empty, "uap-text");
                if (pump != null)
                {
                    pump.Track(block, label);
                }
                else
                {
                    // No pump to route sanitization through (e.g. a caller
                    // building a streaming block without a live pump) --
                    // sanitize directly so model-controlled text never
                    // reaches the label unsanitized (matches every other
                    // render chokepoint's IconLoader.SanitizeForDisplay
                    // call).
                    label.text = IconLoader.SanitizeForDisplay(block.text ?? string.Empty);
                }
                return label;
            }
            return MarkdownRenderer.Render(block.text);
        }

        /// <summary>Monospace code box with language badge and Copy button.</summary>
        public static VisualElement CreateCodeBlock(string code, string language)
        {
            return new CodeBlockElement(code, language);
        }

        // -- Context attachments ------------------------------------------------------

        /// <summary>
        /// Compact CONTENT-HUGGING attachment chip inside a user message
        /// (live feedback: the old full-width header row with a far-right
        /// chevron read as a broken Foldout bar). Layout: transparent
        /// column root; the chip itself (attachment icon + title +
        /// chevron in one row) self-aligns to flex-start with a 60%
        /// max-width and title ellipsis; the expanded payload appears
        /// BELOW the chip, full-width, in a sunken monospace box with an
        /// internal scroll cap. Title and payload are editor/model
        /// -controlled text and never pass the markdown chokepoint, so
        /// every Label here keeps enableRichText = false.
        /// </summary>
        /// <summary>
        /// An attached image (design note 2026-09-07 section 2.3.5): a
        /// thumbnail (ScaleToFit inside the USS-capped box) with the source
        /// name as caption; clicking opens the file with the OS viewer. A
        /// file that no longer exists renders the caption with a "missing"
        /// placeholder instead of an empty box. Caption text is user/
        /// editor-controlled (file names), so rich text stays off.
        /// </summary>
        public static VisualElement CreateImageBlock(ChatMessageBlock block)
        {
            var root = new VisualElement();
            root.AddToClassList("uap-msg-image");
            string path = block.text;
            Texture2D texture = ImageThumbnailCache.Get(path);
            if (texture != null)
            {
                var image = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("uap-msg-image-thumb");
                image.tooltip = L10n.S.ChatImageOpenTooltip;
                image.RegisterCallback<ClickEvent>(delegate
                {
                    if (System.IO.File.Exists(path))
                    {
                        EditorUtility.OpenWithDefaultApp(path);
                    }
                });
                root.Add(image);
            }
            else
            {
                Label missing = CreatePlainLabel(L10n.S.ChatImageMissing, "uap-msg-image-missing");
                root.Add(missing);
            }
            string caption = string.IsNullOrEmpty(block.title) ? L10n.S.ChatImageDefaultCaption : block.title;
            if (texture != null)
            {
                caption += "  " + texture.width + "\u00D7" + texture.height;
            }
            root.Add(CreatePlainLabel(caption, "uap-msg-image-caption"));
            return root;
        }

        public static VisualElement CreateContextAttachmentBlock(ChatMessageBlock block)
        {
            return CreateContextAttachmentBlock(block, null);
        }

        /// <summary>See <see cref="CreateBlockElement(ChatMessageBlock, StreamingLabelPump, string)"/>
        /// for <paramref name="stateKey"/>.</summary>
        public static VisualElement CreateContextAttachmentBlock(ChatMessageBlock block,
            string stateKey)
        {
            var root = new VisualElement();
            root.AddToClassList("uap-attach");

            var chip = new VisualElement();
            chip.AddToClassList("uap-attach-chip");

            VisualElement clip = IconLoader.CreateIcon(
                IconLoader.IconNameAttachment, IconLoader.GlyphAttachAscii,
                "uap-attach-icon", "uap-attach-glyph");
            clip.pickingMode = PickingMode.Ignore;
            chip.Add(clip);

            string title = string.IsNullOrEmpty(block.title)
                ? L10n.S.ChatDefaultAttachTitle : block.title;
            Label titleLabel = CreatePlainLabel(title, "uap-attach-title");
            titleLabel.pickingMode = PickingMode.Ignore;
            chip.Add(titleLabel);

            var chevron = new Label(IconLoader.GlyphChevronRight);
            chevron.enableRichText = false;
            chevron.AddToClassList("uap-attach-chevron");
            chevron.pickingMode = PickingMode.Ignore;
            chip.Add(chevron);
            root.Add(chip);

            // Payload box (sunken, monospace, internal scroll cap via the
            // --uap-attach-pre-max token). Built lazily on first expand so
            // long transcripts do not pay for collapsed payload layouts.
            ScrollView payloadScroll = null;
            bool expanded = false;
            System.Action<bool> apply = delegate(bool open)
            {
                expanded = open;
                ExpandStateMemory.Set(stateKey, open);
                chevron.text = expanded
                    ? IconLoader.GlyphChevronDown : IconLoader.GlyphChevronRight;
                if (expanded && payloadScroll == null)
                {
                    payloadScroll = new ScrollView(ScrollViewMode.Vertical);
                    payloadScroll.AddToClassList("uap-attach-scroll");
                    Label payload = CreatePlainLabel(block.text, "uap-attach-pre");
                    ApplyMonoFont(payload);
                    payloadScroll.Add(payload);
                    root.Add(payloadScroll);
                }
                if (payloadScroll != null)
                {
                    payloadScroll.style.display = expanded
                        ? DisplayStyle.Flex : DisplayStyle.None;
                }
            };
            chip.RegisterCallback<ClickEvent>(delegate { apply(!expanded); });
            // Restore the state a previous element for this block had
            // before the row was rebuilt (ExpandStateMemory doc comment).
            if (ExpandStateMemory.Get(stateKey, false))
            {
                apply(true);
            }
            return root;
        }

        // -- Thinking -----------------------------------------------------------------

        private static VisualElement CreateThinkingBlock(ChatMessageBlock block,
            StreamingLabelPump pump, string stateKey)
        {
            // Design note 2026-08-01-thinking-content-loss.md section 6:
            // a redacted_thinking block never carries readable text (the
            // API sends only encrypted data) and never streams, so it is
            // checked first and short-circuits both branches below.
            if (block.thinkingRedacted)
            {
                return CreateRedactedThinkingNote();
            }

            // Design note 2026-08-01-thinking-content-loss.md section 5
            // point 1: the CLI's headless stream-json never sends thinking
            // body text (only a token estimate + a signature on the
            // finalized block), so an empty-text Thinking block is the norm
            // today, not an edge case. A foldout with nothing inside it
            // reads as a bug, so it is replaced by a compact one-line
            // indicator instead. Forward-compat: the moment the CLI ever
            // sends real thinking text, this block's text stops being
            // empty and the foldout path below (unchanged) takes over
            // automatically.
            if (string.IsNullOrEmpty(block.text))
            {
                return CreateThinkingIndicator(block);
            }

            var foldout = new Foldout();
            foldout.text = block.streaming ? L10n.S.ChatThinkingStreaming : L10n.S.ChatThinkingDone;
            // Start from whatever the previous element for this block was
            // left at (ExpandStateMemory doc comment): the owning message
            // rebuilds on every appended tool block and on the streaming
            // -> finalized flip, and a fresh Foldout defaults to closed.
            foldout.SetValueWithoutNotify(ExpandStateMemory.Get(stateKey, false));
            foldout.AddToClassList("uap-thinking");
            foldout.RegisterValueChangedCallback(evt =>
            {
                // The Foldout's own header Toggle stops its ChangeEvent
                // before it reaches here; only the Foldout's own event
                // (target == foldout) carries the state to remember.
                if (evt.target == foldout)
                {
                    ExpandStateMemory.Set(stateKey, evt.newValue);
                }
            });

            Label label = CreatePlainLabel(block.streaming ? string.Empty : block.text,
                "uap-thinking-text");
            if (block.streaming && pump != null)
            {
                pump.Track(block, label);
            }
            foldout.Add(label);
            return foldout;
        }

        /// <summary>
        /// Subtle one-line note for a redacted_thinking block (design note
        /// 2026-08-01-thinking-content-loss.md section 6). Reuses the
        /// compact thinking-indicator style rather than a Foldout -- there
        /// is nothing to expand, only encrypted data the panel can never
        /// show. Static (no live polling): unlike CreateThinkingIndicator,
        /// a redacted block never streams and its content never changes
        /// after creation.
        /// </summary>
        private static VisualElement CreateRedactedThinkingNote()
        {
            // CreatePlainLabel already sanitizes; L10n.S.ChatThinkingRedactedNote
            // is a fixed panel-authored string with no emoji anyway.
            return CreatePlainLabel(L10n.S.ChatThinkingRedactedNote, "uap-thinking-indicator");
        }

        /// <summary>
        /// Compact one-line indicator for a Thinking block whose text is
        /// empty. Not driven by StreamingLabelPump (that pump does a
        /// character-by-character typewriter advance over block.text, which
        /// is not what this renders) -- instead mirrors SubagentCard.
        /// StartLiveUpdate's idiom: thinkingTokens is mutated in place by
        /// AgentHub on every system/thinking_tokens event, and is
        /// deliberately EXCLUDED from MessageListController.ComputeSignature
        /// (same reasoning as SubagentCard's progress fields -- it would
        /// otherwise tear down and rebuild this row on every tick), so the
        /// label polls the live block directly instead of waiting for a
        /// structural rebuild. Stops polling once the block finishes
        /// streaming or the element leaves the panel.
        /// </summary>
        private static VisualElement CreateThinkingIndicator(ChatMessageBlock block)
        {
            Label label = CreatePlainLabel(string.Empty, "uap-thinking-indicator");
            RefreshThinkingIndicator(label, block);
            if (block.streaming)
            {
                IVisualElementScheduledItem ticker = null;
                ticker = label.schedule.Execute(() =>
                {
                    RefreshThinkingIndicator(label, block);
                    if (!block.streaming)
                    {
                        ticker.Pause();
                    }
                }).Every(500);
                label.RegisterCallback<DetachFromPanelEvent>(delegate
                {
                    ticker.Pause();
                });
            }
            return label;
        }

        private static void RefreshThinkingIndicator(Label label, ChatMessageBlock block)
        {
            string text;
            if (block.streaming)
            {
                text = block.thinkingTokens > 0
                    ? L10n.F(L10n.S.ChatThinkingIndicatorStreamingTokensFmt, block.thinkingTokens)
                    : L10n.S.ChatThinkingIndicatorStreaming;
            }
            else
            {
                text = block.thinkingTokens > 0
                    ? L10n.F(L10n.S.ChatThinkingIndicatorDoneTokensFmt, block.thinkingTokens)
                    : L10n.S.ChatThinkingIndicatorDone;
            }
            label.text = IconLoader.SanitizeForDisplay(text);
        }

        // -- Spinner (shared with ToolActivityCard) --------------------------------------

        /// <summary>Animated WaitSpin icon with an ASCII fallback.</summary>
        public static VisualElement CreateSpinner()
        {
            Texture2D[] frames = IconLoader.SpinnerFrames;
            if (frames.Length > 0)
            {
                var image = new Image { image = frames[0], scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("uap-tool-icon");
                int index = 0;
                image.schedule.Execute(() =>
                {
                    index = (index + 1) % frames.Length;
                    image.image = frames[index];
                }).Every(80);
                return image;
            }
            // ASCII fallback spinner.
            var label = new Label("|");
            ApplyClasses(label, "uap-tool-glyph uap-tool-glyph--busy");
            string glyphs = "|/-\\";
            int glyphIndex = 0;
            label.schedule.Execute(() =>
            {
                glyphIndex = (glyphIndex + 1) % glyphs.Length;
                label.text = glyphs[glyphIndex].ToString();
            }).Every(120);
            return label;
        }

        // -- Notes / errors -----------------------------------------------------------------

        /// <summary>
        /// A SystemNote block (design note 2026-08-14-ui-polish-audit.md
        /// contract item 2): the plain .uap-note label, plus .uap-note--warn
        /// (warn-colored, USS-only) when ChatMessageBlock.warning is set --
        /// today that is only AgentHub's process-exited/reconnecting note.
        /// A restored Permission block also renders via the plain
        /// CreatePlainLabel("uap-note") path above and is deliberately left
        /// out of this branch: it is never built through MakeSystemNote, so
        /// it has no warning flag to read.
        /// </summary>
        private static VisualElement CreateSystemNoteBlock(ChatMessageBlock block)
        {
            Label label = CreatePlainLabel(block.text, "uap-note");
            if (block.warning)
            {
                label.AddToClassList("uap-note--warn");
            }
            return label;
        }

        private static VisualElement CreateErrorBlock(string text)
        {
            var box = new VisualElement();
            box.AddToClassList("uap-error");
            box.Add(IconLoader.CreateIcon("d_console.erroricon.sml", IconLoader.GlyphCross,
                "uap-error-icon", "uap-tool-glyph uap-tool-glyph--fail"));
            Label label = CreatePlainLabel(text, "uap-error-text");
            box.Add(label);
            return box;
        }

        // -- Shared helpers ---------------------------------------------------------------------

        /// <summary>
        /// The single Label construction path for model-controlled text
        /// that does NOT go through the markdown pipeline: rich text is
        /// always disabled here (injection safety).
        /// </summary>
        private static Label CreatePlainLabel(string text, string ussClasses)
        {
            // Variation selectors and other font-uncovered emoji have no
            // glyph in the editor fonts and would log one console warning
            // per draw (docs/design-notes/2026-08-02-emoji-font-warning-
            // flood.md).
            var label = new Label(IconLoader.SanitizeForDisplay(text));
            label.enableRichText = false;
            ApplyClasses(label, ussClasses);
            return label;
        }

        private static void ApplyClasses(VisualElement element, string ussClasses)
        {
            if (string.IsNullOrEmpty(ussClasses))
            {
                return;
            }
            string[] parts = ussClasses.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    element.AddToClassList(parts[i]);
                }
            }
        }

        private static void AddRoleRow(VisualElement root, string sparkGlyph,
            string roleText, bool isAssistant)
        {
            var row = new VisualElement();
            row.AddToClassList("uap-msg-rolerow");
            if (!string.IsNullOrEmpty(sparkGlyph))
            {
                var spark = new Label(sparkGlyph);
                spark.AddToClassList("uap-msg-spark");
                row.Add(spark);
            }
            var role = new Label(roleText);
            role.AddToClassList("uap-msg-role");
            if (isAssistant)
            {
                role.AddToClassList("uap-msg-role--assistant");
            }
            row.Add(role);
            root.Add(row);
        }

        /// <summary>
        /// Assigns the monospace font resolved by FontLoader (editor
        /// bundled RobotoMono first; a broken OS-font face can never be
        /// assigned again -- see FontLoader).
        /// </summary>
        public static void ApplyMonoFont(VisualElement element)
        {
            FontLoader.ApplyMono(element);
        }

        private static bool IsCompletedToolBlock(ChatMessageBlock block)
        {
            if (block == null || block.kind != ChatBlockKind.ToolCall
                || block.toolCall == null || block.toolCall.subagent != null)
            {
                // Subagent cards are excluded from the 3+ group compression
                // (design note section 5): they carry their own status/
                // progress/summary and must stay individually visible.
                return false;
            }
            ToolCallStatus status = block.toolCall.status;
            return status == ToolCallStatus.Succeeded
                || status == ToolCallStatus.Failed
                || status == ToolCallStatus.Denied;
        }
    }
}

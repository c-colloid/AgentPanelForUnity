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
                    AddRevertButtonIfOffered(
                        AddRoleRow(root, null, L10n.S.ChatRoleUser, false), message);
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
            if (message.role == ChatMessage.RoleUser)
            {
                AddRevertConfirmIfOpen(root, message);
            }
            // Right-click menu (copy selection / copy message / ask about
            // selection) and the Ctrl+C shortcut; the labels below opted
            // into selection where they were built.
            TranscriptSelection.AttachMessageMenu(root, message);
            TextEscapes.Disable(root);
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
                    return Selectable(CreatePlainLabel(block.text, "uap-note"));
                case ChatBlockKind.ContextAttachment:
                    return CreateContextAttachmentBlock(block, stateKey);
                case ChatBlockKind.Image:
                    return CreateImageBlock(block);
                case ChatBlockKind.McpStatus:
                    return new McpStatusCard(block, stateKey);
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
                Label label = Selectable(CreatePlainLabel(string.Empty, "uap-text"));
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
            string caption = string.IsNullOrEmpty(block.title) ? L10n.S.ChatImageDefaultCaption : block.title;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                root.Add(CreatePlainLabel(L10n.S.ChatImageMissing, "uap-msg-image-missing"));
                root.Add(CreatePlainLabel(caption, "uap-msg-image-caption"));
                return root;
            }

            // The box has its size from USS, so the picture can arrive
            // later without moving anything. Decoding here, in the row
            // builder, froze the panel for every picture of every live
            // row (design note 2026-11-28-lazy-tool-card-details.md).
            var image = new Image { scaleMode = ScaleMode.ScaleToFit };
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
            Label captionLabel = CreatePlainLabel(caption, "uap-msg-image-caption");
            root.Add(captionLabel);

            System.Action<Texture2D> show = delegate(Texture2D texture)
            {
                if (texture == null)
                {
                    // Exists but does not decode: the placeholder a
                    // missing file gets, never an empty box.
                    int index = root.IndexOf(image);
                    if (index >= 0)
                    {
                        root.RemoveAt(index);
                        root.Insert(index, CreatePlainLabel(L10n.S.ChatImageMissing, "uap-msg-image-missing"));
                    }
                    return;
                }
                image.image = texture;
                captionLabel.text = IconLoader.SanitizeForDisplay(
                    caption + "  " + texture.width + "\u00D7" + texture.height);
            };
            Texture2D cached = ImageThumbnailCache.Peek(path);
            if (cached != null)
            {
                show(cached);
            }
            else
            {
                DeferredThumbnails.Request(image, path, show);
            }
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
                    // One Label per chunk (LongTextChunker): a pasted
                    // payload is exactly the kind of text that exceeds a
                    // single element's 65535-vertex ceiling.
                    List<string> chunks = LongTextChunker.Split(block.text);
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        Label payload = Selectable(CreatePlainLabel(chunks[i], "uap-attach-pre"));
                        if (i > 0)
                        {
                            payload.AddToClassList("uap-attach-pre--cont");
                        }
                        if (i < chunks.Count - 1)
                        {
                            payload.AddToClassList("uap-attach-pre--more");
                        }
                        ApplyMonoFont(payload);
                        payloadScroll.Add(payload);
                    }
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

            if (block.streaming)
            {
                // One label the pump drives; it grows continuation labels
                // beside this one as the text passes the per-element
                // ceiling (StreamingLabelPump doc comment).
                Label label = Selectable(CreatePlainLabel(string.Empty, "uap-thinking-text"));
                if (pump != null)
                {
                    pump.Track(block, label);
                }
                else
                {
                    label.text = IconLoader.SanitizeForDisplay(block.text ?? string.Empty);
                }
                foldout.Add(label);
                return foldout;
            }

            // Finalized: one Label per LongTextChunker chunk. A long
            // thinking block in one Label exceeded the 65535-vertex
            // ceiling and threw from the repaint the moment the foldout
            // was opened (design note 2026-09-29-thinking-vertex-limit.md).
            List<string> chunks = LongTextChunker.Split(block.text);
            for (int i = 0; i < chunks.Count; i++)
            {
                Label part = Selectable(CreatePlainLabel(chunks[i], "uap-thinking-text"));
                if (i > 0)
                {
                    part.AddToClassList(StreamingLabelPump.ContinuationClass);
                }
                foldout.Add(part);
            }
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
            Label label = Selectable(CreatePlainLabel(block.text, "uap-note"));
            if (block.title == ChatMessageBlock.NoteLayoutLines)
            {
                // Several facts, one per line: left-aligned, and a warning
                // block gets the same side bar as the revert card's caveats.
                label.AddToClassList("uap-note--lines");
                if (block.warning)
                {
                    label.AddToClassList("uap-note--lines-warn");
                }
                return label;
            }
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
            Label label = Selectable(CreatePlainLabel(text, "uap-error-text"));
            box.Add(label);
            return box;
        }

        // -- Shared helpers ---------------------------------------------------------------------

        /// <summary>Opts a conversation-text label into mouse selection
        /// (TranscriptSelection); returns the same label.</summary>
        private static Label Selectable(Label label)
        {
            TranscriptSelection.MakeSelectable(label);
            return label;
        }

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

        /// <summary>
        /// "Revert this request" on the user message whose checkpoint is
        /// the one that can be reverted now (design note 2026-11-23-
        /// request-checkpoints.md sections 5 and 11): the newest request
        /// that changed something, while no turn is running. The link only
        /// OPENS the confirmation card under the message
        /// (<see cref="AddRevertConfirmIfOpen"/>); nothing is reverted
        /// until that card's own button is pressed. While the card is open
        /// the link is not drawn at all -- the card's Cancel is the way
        /// back, and a greyed-out link next to it read as a rendering
        /// leftover. Link and card are both part of
        /// MessageListController's row signature, so the row is rebuilt
        /// when either answer changes.
        /// </summary>
        private static void AddRevertButtonIfOffered(VisualElement roleRow, ChatMessage message)
        {
            string checkpointId = message.checkpointId;
            if (!Colloid.AgentPanel.Integration.AgentHub.CanRevertRequest(checkpointId)
                || checkpointId == Colloid.AgentPanel.Integration.AgentHub.RevertConfirmId)
            {
                return;
            }
            if (_revertCardShownFor == checkpointId)
            {
                // The link is back, so the card was closed: opening it
                // again should bring it into view again.
                _revertCardShownFor = null;
            }
            var button = new Button(delegate
            {
                Colloid.AgentPanel.Integration.AgentHub.BeginRevertConfirm(checkpointId);
            })
            {
                text = L10n.S.ChatRevertRequestButton,
                tooltip = L10n.S.ChatRevertRequestTooltip
            };
            button.AddToClassList("uap-msg-revert");
            roleRow.Add(button);
        }

        /// <summary>
        /// The confirmation card, as the last element of the user message
        /// it belongs to. In the transcript rather than a modal dialog so
        /// the user can still scroll back and read what the request did
        /// while deciding, and so it looks like the panel's other prompts.
        /// </summary>
        private static void AddRevertConfirmIfOpen(VisualElement root, ChatMessage message)
        {
            string checkpointId = message.checkpointId;
            if (string.IsNullOrEmpty(checkpointId)
                || checkpointId != Colloid.AgentPanel.Integration.AgentHub.RevertConfirmId)
            {
                return;
            }
            Colloid.AgentPanel.Ops.UapRevertPreview preview =
                Colloid.AgentPanel.Integration.AgentHub.DescribeRevert(checkpointId);
            if (preview == null)
            {
                return;
            }
            root.Add(CreateRevertConfirmCard(preview, checkpointId));
        }

        /// <summary>
        /// What the revert will do, one fact per line, for a preview: the
        /// files (the wording follows which of restore / delete applies),
        /// then the scene. When there is nothing it can do, the one line
        /// that says so.
        /// </summary>
        internal static List<string> ComposeRevertEffectLines(Colloid.AgentPanel.Ops.UapRevertPreview preview)
        {
            var lines = new List<string>();
            if (preview.FilesToRestore > 0 && preview.FilesToRemove > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmFilesFmt, preview.FilesToRestore, preview.FilesToRemove));
            }
            else if (preview.FilesToRestore > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmRestoreOnlyFmt, preview.FilesToRestore));
            }
            else if (preview.FilesToRemove > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmDeleteOnlyFmt, preview.FilesToRemove));
            }
            if (preview.SceneUndo)
            {
                lines.Add(L10n.S.RevertConfirmSceneLine);
            }
            if (lines.Count == 0)
            {
                lines.Add(L10n.S.RevertConfirmNothingLine);
            }
            return lines;
        }

        /// <summary>
        /// What the user should know before pressing Revert: what will be
        /// lost with it, and what will NOT go back. Only the caveats that
        /// apply to THIS request -- a wall of standing disclaimers is what
        /// teaches people to stop reading them. Empty for a request with
        /// none, and then the card has no caveat group at all.
        /// </summary>
        internal static List<string> ComposeRevertCaveatLines(Colloid.AgentPanel.Ops.UapRevertPreview preview)
        {
            var lines = new List<string>();
            if (preview.SceneUndo)
            {
                // Unity's Undo is one stack: taking the request's scene
                // edits back takes everything after them back too.
                lines.Add(L10n.S.RevertConfirmManualEditsLine);
            }
            else if (preview.SceneUndoUnavailable)
            {
                lines.Add(L10n.S.RevertConfirmSceneUnavailableLine);
            }
            if (preview.ChangedSince > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmChangedAfterFmt, preview.ChangedSince));
            }
            if (preview.FilesChangedUntracked > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmUntrackedFilesFmt, preview.FilesChangedUntracked));
            }
            if (preview.FilesNotStored > 0)
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmNotStoredFmt, preview.FilesNotStored));
            }
            if (!string.IsNullOrEmpty(preview.Untracked))
            {
                lines.Add(L10n.F(L10n.S.RevertConfirmUntrackedFmt, preview.Untracked));
            }
            return lines;
        }

        /// <summary>Paths one confirmation card lists before it says how many more there are.</summary>
        internal const int RevertConfirmMaxListedFiles = 12;

        /// <summary>The checkpoint whose card was last scrolled into view, so a row rebuild does not scroll again.</summary>
        private static string _revertCardShownFor;

        private static VisualElement CreateRevertConfirmCard(
            Colloid.AgentPanel.Ops.UapRevertPreview preview, string checkpointId)
        {
            var card = new VisualElement();
            card.AddToClassList("uap-revert");

            var title = new Label(L10n.S.RevertConfirmTitle);
            title.AddToClassList("uap-revert-title");
            card.Add(title);

            List<string> effects = ComposeRevertEffectLines(preview);
            for (int i = 0; i < effects.Count; i++)
            {
                var line = new Label(effects[i]);
                line.AddToClassList("uap-revert-line");
                card.Add(line);
            }

            List<string> caveats = ComposeRevertCaveatLines(preview);
            if (caveats.Count > 0)
            {
                // The one accented element of the card: a warn-coloured bar
                // down the side of the lines to read before deciding. The
                // text itself stays in the primary colour (amber text on
                // this surface fails contrast in the light skin).
                var group = new VisualElement();
                group.AddToClassList("uap-revert-caveats");
                for (int i = 0; i < caveats.Count; i++)
                {
                    var line = new Label(caveats[i]);
                    line.AddToClassList("uap-revert-line");
                    group.Add(line);
                }
                card.Add(group);
            }

            int fileCount = preview.RestorePaths.Count + preview.RemovePaths.Count + preview.LeftAsIsPaths.Count;
            if (fileCount > 0)
            {
                string stateKey = "revert-files:" + checkpointId;
                var files = new Foldout { text = L10n.F(L10n.S.RevertConfirmFilesFoldoutFmt, fileCount) };
                files.SetValueWithoutNotify(ExpandStateMemory.Get(stateKey, false));
                files.AddToClassList("uap-revert-files");
                files.RegisterValueChangedCallback(evt =>
                {
                    if (evt.target == files)
                    {
                        ExpandStateMemory.Set(stateKey, evt.newValue);
                    }
                });
                // The files that will NOT go back first: with a long list
                // they are the ones a cap must never hide.
                int listed = 0;
                listed = AddRevertFileRows(files, preview.LeftAsIsPaths, L10n.S.RevertConfirmTagLeft, "uap-revert-tag--left", listed);
                listed = AddRevertFileRows(files, preview.RestorePaths, L10n.S.RevertConfirmTagRestore, null, listed);
                listed = AddRevertFileRows(files, preview.RemovePaths, L10n.S.RevertConfirmTagRemove, null, listed);
                if (fileCount > listed)
                {
                    var more = new Label(L10n.F(L10n.S.RevertConfirmMoreFilesFmt, fileCount - listed));
                    more.AddToClassList("uap-revert-more");
                    files.Add(more);
                }
                card.Add(files);
            }

            var footer = new VisualElement();
            footer.AddToClassList("uap-revert-footer");
            var note = new Label(L10n.S.RevertConfirmNoRedoLine);
            note.AddToClassList("uap-revert-note");
            footer.Add(note);

            var actions = new VisualElement();
            actions.AddToClassList("uap-revert-actions");
            Button confirm = null;
            Button cancel = null;
            confirm = new Button(delegate
            {
                // One revert per card: a second click while the first is
                // still restoring files would only add a "cannot revert"
                // note under the result.
                confirm.SetEnabled(false);
                cancel.SetEnabled(false);
                Colloid.AgentPanel.Integration.AgentHub.RevertRequest(checkpointId);
            })
            {
                text = L10n.S.RevertConfirmOkButton
            };
            confirm.AddToClassList("uap-card-btn");
            confirm.AddToClassList("uap-card-btn--primary");
            confirm.SetEnabled(preview.HasAnythingToDo);
            actions.Add(confirm);
            cancel = new Button(Colloid.AgentPanel.Integration.AgentHub.CancelRevertConfirm)
            {
                text = L10n.S.RevertConfirmCancelButton
            };
            cancel.AddToClassList("uap-card-btn");
            actions.Add(cancel);
            footer.Add(actions);
            card.Add(footer);

            if (_revertCardShownFor != checkpointId)
            {
                // Opened just now: bring the whole card into view once, so
                // its buttons are not left below the fold (or under the
                // "Latest" pill). Later rebuilds of the row leave the
                // scroll position alone.
                _revertCardShownFor = checkpointId;
                card.RegisterCallback<GeometryChangedEvent>(ScrollRevertCardIntoView);
            }
            return card;
        }

        private static void ScrollRevertCardIntoView(GeometryChangedEvent evt)
        {
            var card = evt.target as VisualElement;
            if (card == null)
            {
                return;
            }
            card.UnregisterCallback<GeometryChangedEvent>(ScrollRevertCardIntoView);
            ScrollView scroll = card.GetFirstAncestorOfType<ScrollView>();
            if (scroll != null)
            {
                // After the list's own post-rebuild scroll restore has run.
                card.schedule.Execute((System.Action)(() =>
                {
                    // The row may have been rebuilt in the meantime; ScrollTo
                    // throws for an element that is no longer inside it.
                    if (card.panel != null && scroll.contentContainer.Contains(card))
                    {
                        scroll.ScrollTo(card);
                    }
                })).StartingIn(60);
            }
        }

        private static int AddRevertFileRows(VisualElement parent, List<string> paths, string tagText,
            string tagModifier, int listed)
        {
            for (int i = 0; i < paths.Count && listed < RevertConfirmMaxListedFiles; i++, listed++)
            {
                string name;
                string directory;
                Colloid.AgentPanel.Ops.UapCheckpointPolicy.SplitPath(paths[i], out name, out directory);
                var row = new VisualElement();
                row.AddToClassList("uap-revert-file");
                row.tooltip = paths[i];
                var tag = new Label(tagText);
                tag.AddToClassList("uap-revert-tag");
                if (tagModifier != null)
                {
                    tag.AddToClassList(tagModifier);
                }
                row.Add(tag);
                // The file name whole and first; the folder after it is
                // what gets cut when the panel is narrow.
                // Name and folder share one wrapping box: side by side while
                // there is room, the folder under the name when there is
                // not. The name is never the one that gives way.
                var where = new VisualElement();
                where.AddToClassList("uap-revert-where");
                var file = new Label(name);
                file.AddToClassList("uap-revert-name");
                ApplyMonoFont(file);
                where.Add(file);
                if (directory.Length > 0)
                {
                    var folder = new Label(directory);
                    folder.AddToClassList("uap-revert-dir");
                    ApplyMonoFont(folder);
                    where.Add(folder);
                }
                row.Add(where);
                parent.Add(row);
            }
            return listed;
        }

        private static VisualElement AddRoleRow(VisualElement root, string sparkGlyph,
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
            return row;
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
            if (block.toolCall.HasResultImages)
            {
                // A card with a picture stays out of the "N tools" fold:
                // the thumbnail IS the outcome the user wants to see, and a
                // collapsed group row would hide it behind a click.
                return false;
            }
            ToolCallStatus status = block.toolCall.status;
            return status == ToolCallStatus.Succeeded
                || status == ToolCallStatus.Failed
                || status == ToolCallStatus.Denied;
        }
    }
}

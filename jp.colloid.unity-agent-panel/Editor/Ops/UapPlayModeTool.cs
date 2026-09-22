using System;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Enters, leaves, pauses, steps and reports Play Mode (design note
    /// docs/design-notes/2026-09-21-play-mode-and-console-log-tools.md).
    /// Until this tool existed the panel could edit a scene but never run
    /// it, so "check that the jump actually works" ended with the agent
    /// asking a human to press Play -- or reaching for `uloop
    /// control-play-mode`, one of the two gaps that made a project install
    /// uLoop for nothing else.
    ///
    /// The decision rules live in <see cref="UapPlayModePolicy"/>; what is
    /// here is the Editor half, and the one thing it does differently from
    /// every other tool in this package:
    ///
    /// **the play-state change is applied AFTER the reply, not during it.**
    /// Entering or leaving Play Mode reloads the domain (unless the project
    /// turned that off in Enter Play Mode Settings), which tears down this
    /// package's HTTP server -- including the worker thread that has yet to
    /// write this call's response. So Execute schedules the change
    /// <see cref="TransitionDelaySeconds"/> ahead and returns the state as
    /// it still is, plus what the agent should do next. Applying it inline
    /// would routinely lose the answer to the very call that caused it,
    /// which reads as a failed tool rather than a working one.
    ///
    /// NOT ReadOnly even for action "status": the tool as a whole changes
    /// the Editor's run state, and IUapTool.ReadOnly is per tool, not per
    /// argument (UapReadOnlyToolMetadataTests pins the rule).
    /// </summary>
    public sealed class UapPlayModeTool : IUapTool
    {
        public const string ToolName = "uap_play_mode";

        /// <summary>
        /// How long after Execute returns the play-state change is applied.
        /// Long enough for the dispatcher to hand the result back and the
        /// HTTP worker to write it (both sub-millisecond on an idle editor),
        /// short enough that the agent's next poll is not waiting on us.
        /// </summary>
        public const double TransitionDelaySeconds = 0.25;

        /// <summary>
        /// How a deferred play-state change is scheduled. Production is
        /// <see cref="ScheduleAfterResponse"/>; UapPlayModeToolTests
        /// substitutes a recorder so the fixture can prove Execute DEFERRED
        /// the change without a test run actually entering Play Mode.
        /// </summary>
        internal static Action<Action> Scheduler = ScheduleAfterResponse;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Starts, stops, pauses, resumes or steps Unity Play Mode, and reports whether the"
                    + " Editor is playing -- use it to actually RUN the scene you edited, then read what it"
                    + " logged with uap_console_logs and look at it with uap_editor_screenshot."
                    + " action: status (default) / start / stop / pause / resume / step."
                    + " start and stop return BEFORE the Editor has switched, because entering or leaving"
                    + " Play Mode reloads the domain and drops the connection: wait for uap_ping to answer"
                    + " again, then call action:status to confirm. Refused while scripts are compiling."
                    + " step runs a single frame and leaves Play Mode paused.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray()
                                .Add("status").Add("start").Add("stop")
                                .Add("pause").Add("resume").Add("step"))
                            .Set("description", "What to do. 'status' (the default) only reports."
                                + " 'start'/'stop' enter/leave Play Mode (domain reload -- see the tool"
                                + " description). 'pause'/'resume' hold and release the running game."
                                + " 'step' advances one frame and stays paused.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            UapPlayModeAction action = UapPlayModePolicy.ParseAction(input["action"].AsString(null));
            UapPlayModeSnapshot state = ReadState();
            string message;
            UapPlayModeOutcome outcome = UapPlayModePolicy.Decide(action, state, out message);

            switch (outcome)
            {
                case UapPlayModeOutcome.Refuse:
                    throw new InvalidOperationException(message);
                case UapPlayModeOutcome.Enter:
                    Scheduler(EnterPlayMode);
                    break;
                case UapPlayModeOutcome.Exit:
                    Scheduler(ExitPlayMode);
                    break;
                case UapPlayModeOutcome.Pause:
                    EditorApplication.isPaused = true;
                    break;
                case UapPlayModeOutcome.Resume:
                    EditorApplication.isPaused = false;
                    break;
                case UapPlayModeOutcome.Step:
                    // Step implies a pause: Unity advances exactly one frame
                    // and holds there, which is what the description promises.
                    EditorApplication.Step();
                    break;
            }

            return Describe(outcome, message, state);
        }

        private static void EnterPlayMode()
        {
            EditorApplication.isPlaying = true;
        }

        private static void ExitPlayMode()
        {
            EditorApplication.isPlaying = false;
        }

        private static UapPlayModeSnapshot ReadState()
        {
            return new UapPlayModeSnapshot
            {
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                WillChangePlayMode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsCompiling = EditorApplication.isCompiling
            };
        }

        private static JsonNode Describe(UapPlayModeOutcome outcome, string message,
            UapPlayModeSnapshot stateBefore)
        {
            // The state reported is the one BEFORE the scheduled transition.
            // Re-reading it here would report "edit" for a start that has
            // not been applied yet, which reads as a tool that did nothing;
            // 'requested' is what is coming instead.
            JsonNode result = JsonNode.NewObject()
                .Set("message", message)
                .Set("state", UapPlayModePolicy.DescribeState(stateBefore))
                .Set("isPlaying", stateBefore.IsPlaying)
                .Set("isPaused", stateBefore.IsPaused)
                .Set("isCompiling", stateBefore.IsCompiling);
            if (outcome == UapPlayModeOutcome.Enter)
            {
                result.Set("requested", "playing");
            }
            else if (outcome == UapPlayModeOutcome.Exit)
            {
                result.Set("requested", "edit");
            }
            string hint = UapPlayModePolicy.NextStepHint(outcome);
            if (!string.IsNullOrEmpty(hint))
            {
                result.Set("next", hint);
            }
            if (outcome == UapPlayModeOutcome.Enter && !Application.runInBackground)
            {
                // The same condition UapOpsServer.PlayModeUnfocusedNotice
                // reports once it has already bitten. Said up front it is
                // something the agent can plan around, instead of a timeout
                // it has to diagnose mid-run.
                result.Set("warning", "Run In Background is off in Player Settings, so while Play Mode runs"
                    + " and the Editor is unfocused it ticks only sporadically and uap_* calls may time out."
                    + " Keep the Editor window focused for this run, or turn Run In Background on.");
            }
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>
        /// Applies <paramref name="action"/> once the Editor has ticked past
        /// <see cref="TransitionDelaySeconds"/> -- see the class comment for
        /// why the play-state change must outlive this call's response.
        /// </summary>
        private static void ScheduleAfterResponse(Action action)
        {
            double dueAt = EditorApplication.timeSinceStartup + TransitionDelaySeconds;
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                if (EditorApplication.timeSinceStartup < dueAt)
                {
                    return;
                }
                // Unsubscribe BEFORE acting: entering Play Mode reloads the
                // domain from inside this callback, and a handler still in
                // the list when that happens is one Unity may invoke again
                // on the way out.
                EditorApplication.update -= tick;
                action();
            };
            EditorApplication.update += tick;
        }
    }
}

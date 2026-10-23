using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Recompiles the project's C# scripts on demand and reports the result
    /// (uLoop's `compile`; docs/design-notes/2026-09-22-uloop-phase1.md
    /// section 2.3, item "uap_compile"). Before this tool existed, "does the
    /// project still compile after this external edit?" had no answer
    /// inside the panel: uap_scripts_commit only validates files the agent
    /// itself staged, and reading the Console (uap_console_logs) only shows
    /// whatever Unity happened to log, with no way to ask it to check again.
    ///
    /// **action:request is the DEFAULT**, unlike every other action-typed
    /// tool in this package (uap_play_mode/uap_game_view_size default to a
    /// read) -- uap_compile exists specifically to trigger a check, and
    /// making "request" the one action an agent has to spell out would be
    /// exactly backwards for that.
    ///
    /// Same deferral as UapPlayModeTool and for the identical reason: a
    /// compile that actually changes something reloads the domain, which
    /// tears down this package's HTTP server before the worker thread can
    /// write THIS call's own response. So Execute never calls
    /// CompilationPipeline.RequestScriptCompilation() inline -- it schedules
    /// it <see cref="RequestDelaySeconds"/> after the reply and returns a
    /// request id immediately; the agent is told to wait for uap_ping, then
    /// call action:status.
    ///
    /// Results are collected by subscribing to
    /// CompilationPipeline.compilationStarted/assemblyCompilationFinished/
    /// compilationFinished -- but ONLY while a request is pending
    /// (Requested/Compiling), never from [InitializeOnLoadMethod]: the
    /// subscription is armed in ExecuteRequest right before scheduling the
    /// compile (and defensively re-armed by ExecuteStatus if it finds a
    /// pending state with nothing listening) and torn down the moment a run
    /// reaches a terminal state. Colloid.AgentPanel.Integration.
    /// ConsoleErrorProvider already listens to the SAME CompilationPipeline
    /// events, but deliberately was NOT reused as this tool's message
    /// source: it keeps only ERRORS (warnings are dropped), deduplicates
    /// across the whole editor session rather than scoping to one run, and
    /// collapses file+line into a single Location string with no column --
    /// none of which is enough for the per-message file/line/column/type
    /// data and per-request scoping action:status needs to report. Every
    /// existing compile-observing tool in this package (ConsoleErrorProvider
    /// itself, UapScriptsCommitTool's AssemblyBuilder.buildFinished) already
    /// owns its own subscription for its own shape of data; this tool
    /// follows the same pattern rather than growing ConsoleErrorProvider's
    /// already-large, heavily-documented surface for a second, incompatible
    /// consumer.
    ///
    /// The persisted state machine (idle/requested/compiling/succeeded/
    /// failed) and the JSON shape live in <see cref="UapCompilePolicy"/>
    /// (Unity-free, ci/SmokeTests-covered); the SessionState read/write
    /// itself lives in <see cref="UapCompileResultStore"/>.
    ///
    /// NOT ReadOnly even for action "status": IUapTool.ReadOnly is per
    /// tool, not per argument (UapPlayModeTool pins the same rule for the
    /// same reason), and this tool's default/primary action schedules a
    /// project recompile.
    /// </summary>
    public sealed class UapCompileTool : IUapTool
    {
        public const string ToolName = "uap_compile";

        /// <summary>
        /// How long after Execute returns the scheduled
        /// RequestScriptCompilation() call actually runs. Same value and
        /// same reasoning as UapPlayModeTool.TransitionDelaySeconds: long
        /// enough for the dispatcher to hand the result back and the HTTP
        /// worker to write it, short enough the agent's first uap_ping
        /// after the reply is not waiting on us.
        /// </summary>
        public const double RequestDelaySeconds = 0.25;

        /// <summary>
        /// How a deferred compile request is scheduled. Production is
        /// <see cref="ScheduleAfterResponse"/>; UapCompileToolTests
        /// substitutes a recorder so the fixture can prove Execute DEFERRED
        /// the request without a test run actually recompiling the project.
        /// </summary>
        internal static Action<Action> Scheduler = ScheduleAfterResponse;

        /// <summary>True while this class holds a live CompilationPipeline subscription. Test seam so a fixture can assert the lazy-subscribe/unsubscribe contract without waiting on a real compile.</summary>
        internal static bool SubscribedForTests
        {
            get { return _subscribed; }
        }

        private static bool _subscribed;

        /// <summary>Diagnostics accumulated for the run currently in flight; cleared at compilationStarted, flushed (and cleared again) at compilationFinished. In-memory, same idiom as ConsoleErrorProvider's own _pendingCompilerEntries buffer and for the same reason: assemblyCompilationFinished fires once PER ASSEMBLY, so a run's full message set only exists once every assembly in it has reported.</summary>
        private static readonly List<UapCompileMessage> _accumulated = new List<UapCompileMessage>();

        /// <summary>Hard safety cap on in-memory accumulation, independent of UapCompilePolicy.MaxPersistedMessages (which caps what gets WRITTEN to SessionState) -- this just stops a pathological compile from growing the in-memory list without bound before that cap is even applied.</summary>
        private const int MaxAccumulatedMessages = 4000;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Recompiles the project's C# scripts and reports errors/warnings (file/line/column/"
                    + "message/type) plus counts. Use uap_scripts_commit for new/changed scripts written by"
                    + " the agent (staged, only lands on success); use uap_compile to re-check the project"
                    + " after external edits, or to get the current error list."
                    + " action: request (default) schedules the compile AFTER this reply and returns a"
                    + " request id -- a compile that changes anything reloads the domain and would drop an"
                    + " inline response, so nothing compiles until slightly after you see this result."
                    + " Refused while Unity is in Play Mode, or while a compile it already tracks is in"
                    + " flight (action:status then explains which). clean:true also clears Unity's build"
                    + " cache (CleanBuildCache) for a full rebuild instead of an incremental one -- slower,"
                    + " useful when incremental state itself is suspect."
                    + " After request, wait for uap_ping, then call action:status; status also works with"
                    + " no prior request from this tool, reporting Unity's live scriptCompilationFailed flag"
                    + " plus the last known result.";
            }
        }

        public string Module
        {
            get { return "core"; }
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
                            .Set("enum", JsonNode.NewArray().Add("request").Add("status"))
                            .Set("description", "'request' (the default) schedules a recompile and returns a"
                                + " request id. 'status' reports the last known/in-progress result without"
                                + " scheduling anything."))
                        .Set("clean", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "action:request only. true also clears Unity's script"
                                + " compilation build cache first (RequestScriptCompilationOptions."
                                + "CleanBuildCache), forcing a full rebuild instead of an incremental one."
                                + " Default false.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            UapCompileAction action = UapCompilePolicy.ParseAction(input["action"].AsString(null));
            bool clean = input["clean"].AsBool(false);
            return action == UapCompileAction.Status ? ExecuteStatus() : ExecuteRequest(clean);
        }

        private static JsonNode ExecuteRequest(bool clean)
        {
            UapCompileResult existing = UapCompileResultStore.Load();
            bool playModeActive = EditorApplication.isPlayingOrWillChangePlaymode;
            bool compilingLive = EditorApplication.isCompiling;

            // Age of a persisted Requested record, so a request that never
            // produced compilation events cannot block later ones
            // (UapCompilePolicy.StaleRequestSeconds).
            double requestedAgeSeconds = existing.StartedAtUtcTicks > 0L
                ? new TimeSpan(DateTime.UtcNow.Ticks - existing.StartedAtUtcTicks).TotalSeconds
                : 0.0;
            string message;
            UapCompileRequestOutcome outcome = UapCompilePolicy.DecideRequest(
                existing.State, playModeActive, compilingLive, requestedAgeSeconds, out message);
            if (outcome != UapCompileRequestOutcome.Proceed)
            {
                throw new InvalidOperationException(message);
            }

            string requestId = Guid.NewGuid().ToString("N");
            var result = new UapCompileResult
            {
                State = UapCompileState.Requested,
                RequestId = requestId,
                Clean = clean,
                StartedAtUtcTicks = DateTime.UtcNow.Ticks,
                FinishedAtUtcTicks = 0L,
            };
            UapCompileResultStore.Save(result);
            EnsureSubscribed();
            Scheduler(delegate { RequestCompilation(clean); });

            JsonNode json = JsonNode.NewObject()
                .Set("message", message)
                .Set("state", UapCompilePolicy.StateName(UapCompileState.Requested))
                .Set("requestId", requestId)
                .Set("clean", clean)
                .Set("next", "Wait for uap_ping to answer, then call uap_compile action:status.");
            return UapToolResults.Text(JsonWriter.Write(json));
        }

        private static JsonNode ExecuteStatus()
        {
            UapCompileResult result = UapCompileResultStore.Load();
            bool compilingLive = EditorApplication.isCompiling;
            bool scriptCompilationFailed = EditorUtility.scriptCompilationFailed;

            if (result.State == UapCompileState.Requested || result.State == UapCompileState.Compiling)
            {
                // Defensive re-arm: a request is still pending by our own
                // record, but a subscription is not guaranteed to have
                // survived every path that could reach this state (see the
                // class comment). Idempotent -- a no-op when already
                // subscribed.
                EnsureSubscribed();
            }

            string message = UapCompilePolicy.DescribeStatusMessage(result, compilingLive, scriptCompilationFailed);

            JsonNode json = JsonNode.NewObject()
                .Set("message", message)
                .Set("state", UapCompilePolicy.StateName(result.State))
                .Set("requestId", result.RequestId ?? string.Empty)
                .Set("clean", result.Clean)
                .Set("startedAt", UapCompilePolicy.FormatUtcTicks(result.StartedAtUtcTicks))
                .Set("finishedAt", UapCompilePolicy.FormatUtcTicks(result.FinishedAtUtcTicks))
                .Set("errorCount", result.ErrorCount)
                .Set("warningCount", result.WarningCount)
                .Set("infoCount", result.InfoCount)
                .Set("isCompilingNow", compilingLive)
                .Set("scriptCompilationFailed", scriptCompilationFailed);

            JsonNode messages = JsonNode.NewArray();
            for (int i = 0; i < result.Messages.Count; i++)
            {
                UapCompileMessage m = result.Messages[i];
                messages.Add(JsonNode.NewObject()
                    .Set("file", m.File ?? string.Empty)
                    .Set("line", m.Line)
                    .Set("column", m.Column)
                    .Set("message", m.Message ?? string.Empty)
                    .Set("type", UapCompilePolicy.MessageTypeName(m.Type)));
            }
            json.Set("messages", messages);
            if (result.OverflowCount > 0)
            {
                json.Set("truncatedMessageCount", result.OverflowCount);
            }
            return UapToolResults.Text(JsonWriter.Write(json));
        }

        // -- Deferred request -----------------------------------------------

        private static void RequestCompilation(bool clean)
        {
            CompilationPipeline.RequestScriptCompilation(
                clean ? RequestScriptCompilationOptions.CleanBuildCache : RequestScriptCompilationOptions.None);
        }

        /// <summary>Applies <paramref name="action"/> once the Editor has ticked past RequestDelaySeconds -- see the class comment for why the compile request must outlive this call's response.</summary>
        private static void ScheduleAfterResponse(Action action)
        {
            double dueAt = EditorApplication.timeSinceStartup + RequestDelaySeconds;
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                if (EditorApplication.timeSinceStartup < dueAt)
                {
                    return;
                }
                // Unsubscribe BEFORE acting, same reasoning as
                // UapPlayModeTool's identical guard: a compile that needs no
                // domain reload still runs synchronously from inside this
                // callback on some editor versions, and a handler still in
                // the update list when that happens is one Unity may invoke
                // again on the way out.
                EditorApplication.update -= tick;
                action();
            };
            EditorApplication.update += tick;
        }

        // -- CompilationPipeline subscription (lazy, request-scoped) --------

        private static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            _subscribed = true;
        }

        private static void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            _subscribed = false;
        }

        private static void OnCompilationStarted(object context)
        {
            UapCompileResult result = UapCompileResultStore.Load();
            if (result.State != UapCompileState.Requested && result.State != UapCompileState.Compiling)
            {
                // Some OTHER compile started while nothing of ours was
                // pending (we only subscribe while Requested/Compiling, but
                // a race against an external RequestScriptCompilation caller
                // is not impossible) -- do not adopt it as our own.
                return;
            }
            result.State = UapCompileState.Compiling;
            UapCompileResultStore.Save(result);
            _accumulated.Clear();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }
            for (int i = 0; i < messages.Length && _accumulated.Count < MaxAccumulatedMessages; i++)
            {
                _accumulated.Add(new UapCompileMessage
                {
                    File = messages[i].file,
                    Line = messages[i].line,
                    Column = messages[i].column,
                    Message = messages[i].message,
                    Type = MapMessageType(messages[i].type),
                });
            }
        }

        /// <summary>
        /// The run is over: publish it. Synchronous on purpose, exactly the
        /// reason ConsoleErrorProvider.OnCompilationFinished gives for its
        /// own flush -- Unity can go straight from compilationFinished into
        /// beforeAssemblyReload with no update tick in between, so this
        /// SessionState write has to land BEFORE that happens or persisting
        /// at all would be pointless.
        /// </summary>
        private static void OnCompilationFinished(object context)
        {
            UapCompileResult result = UapCompileResultStore.Load();
            if (result.State != UapCompileState.Compiling)
            {
                // We never saw this run's compilationStarted claim it as
                // ours (e.g. action:status re-armed the subscription mid-run,
                // after the run had already started) -- nothing reliable to
                // publish. Stop listening so a later, unrelated compile is
                // not attributed to a stale pending request either.
                _accumulated.Clear();
                Unsubscribe();
                return;
            }

            int errorCount = 0;
            int warningCount = 0;
            int infoCount = 0;
            for (int i = 0; i < _accumulated.Count; i++)
            {
                switch (_accumulated[i].Type)
                {
                    case UapCompileMessageType.Error: errorCount++; break;
                    case UapCompileMessageType.Warning: warningCount++; break;
                    default: infoCount++; break;
                }
            }
            int overflow;
            List<UapCompileMessage> kept = UapCompilePolicy.Cap(_accumulated, UapCompilePolicy.MaxPersistedMessages, out overflow);

            result.State = errorCount > 0 ? UapCompileState.Failed : UapCompileState.Succeeded;
            result.FinishedAtUtcTicks = DateTime.UtcNow.Ticks;
            result.Messages = kept;
            result.ErrorCount = errorCount;
            result.WarningCount = warningCount;
            result.InfoCount = infoCount;
            result.OverflowCount = overflow;
            UapCompileResultStore.Save(result);

            _accumulated.Clear();
            Unsubscribe();
        }

        private static UapCompileMessageType MapMessageType(CompilerMessageType type)
        {
            switch (type)
            {
                case CompilerMessageType.Error: return UapCompileMessageType.Error;
                case CompilerMessageType.Warning: return UapCompileMessageType.Warning;
                default: return UapCompileMessageType.Info;
            }
        }

        // -- Test seams (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) --

        /// <summary>Drives OnCompilationStarted exactly as the real CompilationPipeline event would.</summary>
        internal static void NotifyCompilationStartedForTests()
        {
            OnCompilationStarted(null);
        }

        /// <summary>Drives OnAssemblyCompilationFinished exactly as the real CompilationPipeline event would.</summary>
        internal static void NotifyAssemblyCompiledForTests(CompilerMessage[] messages)
        {
            OnAssemblyCompilationFinished("Library/ScriptAssemblies/Test.dll", messages);
        }

        /// <summary>Drives OnCompilationFinished exactly as the real CompilationPipeline event would.</summary>
        internal static void NotifyCompilationFinishedForTests()
        {
            OnCompilationFinished(null);
        }

        /// <summary>Unsubscribes and forgets in-flight accumulation, for test teardown -- mirrors ConsoleErrorProvider.ResetForTests' role for its own hooks.</summary>
        internal static void ResetForTests()
        {
            Unsubscribe();
            _accumulated.Clear();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_compile was asked to do. Default is Request, unlike every other action-typed tool in this package (uap_play_mode/uap_game_view_size default to a read) -- see UapCompileTool's own doc comment for why.</summary>
    public enum UapCompileAction
    {
        Request = 0,
        Status = 1,
    }

    /// <summary>
    /// One compile request's lifecycle, persisted across the domain reload
    /// a successful compile causes (docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.3). Idle: nothing requested yet, or the last
    /// result was consumed/forgotten. Requested: uap_compile scheduled
    /// CompilationPipeline.RequestScriptCompilation() but Unity has not
    /// started running it yet. Compiling: CompilationPipeline.
    /// compilationStarted fired for this request. Succeeded/Failed: terminal,
    /// decided purely by whether any captured message has
    /// UapCompileMessageType.Error.
    /// </summary>
    public enum UapCompileState
    {
        Idle = 0,
        Requested = 1,
        Compiling = 2,
        Succeeded = 3,
        Failed = 4,
    }

    /// <summary>Mirrors UnityEditor.Compilation.CompilerMessageType, kept Unity-free so this whole file compiles in ci/SmokeTests.</summary>
    public enum UapCompileMessageType
    {
        Error = 0,
        Warning = 1,
        Info = 2,
    }

    /// <summary>One compiler diagnostic captured for a uap_compile run. Unlike Colloid.AgentPanel.Integration.ConsoleErrorProvider's Entry (error-only, deduplicated, no column, chip-oriented) this keeps every field CompilationPipeline.assemblyCompilationFinished hands over, including warnings -- see UapCompileTool's class comment for why that provider is not reused here.</summary>
    public struct UapCompileMessage
    {
        public string File;
        public int Line;
        public int Column;
        public string Message;
        public UapCompileMessageType Type;
    }

    /// <summary>What DecideRequest concluded for action:request.</summary>
    public enum UapCompileRequestOutcome
    {
        Proceed = 0,
        RefusePlayMode = 1,
        RefuseAlreadyRequested = 2,
        RefuseAlreadyCompiling = 3,
    }

    /// <summary>
    /// The full state uap_compile persists to SessionState (via
    /// UapCompileResultStore) and reports from action:status. Plain data,
    /// no Unity dependency -- <see cref="UapCompilePolicy.ToJson"/>/
    /// <see cref="UapCompilePolicy.FromJson"/> are its only (de)serializers.
    /// </summary>
    public sealed class UapCompileResult
    {
        public UapCompileState State;
        public string RequestId;
        public bool Clean;

        /// <summary>UTC DateTime.Ticks when action:request armed this record. 0 = unset (Idle).</summary>
        public long StartedAtUtcTicks;

        /// <summary>UTC DateTime.Ticks when the run reached Succeeded/Failed. 0 = not finished yet.</summary>
        public long FinishedAtUtcTicks;

        public List<UapCompileMessage> Messages = new List<UapCompileMessage>();
        public int ErrorCount;
        public int WarningCount;
        public int InfoCount;

        /// <summary>How many captured messages were dropped by UapCompilePolicy.Cap before persisting (0 = none dropped). The counts above always cover ALL captured messages, capped or not.</summary>
        public int OverflowCount;

        /// <summary>A fresh, empty record: nothing requested yet.</summary>
        public static UapCompileResult Idle()
        {
            return new UapCompileResult
            {
                State = UapCompileState.Idle,
                RequestId = string.Empty,
                Clean = false,
                StartedAtUtcTicks = 0L,
                FinishedAtUtcTicks = 0L,
                Messages = new List<UapCompileMessage>(),
                ErrorCount = 0,
                WarningCount = 0,
                InfoCount = 0,
                OverflowCount = 0,
            };
        }
    }

    /// <summary>
    /// Every rule uap_compile decides by, with no UnityEditor in sight
    /// (docs/design-notes/2026-09-22-uloop-phase1.md section 2.3), split out
    /// for the same reason UapPlayModePolicy/UapGameViewSizePolicy are: a
    /// refusal rule and a JSON shape are worth running in the license-free
    /// smoke gate (ci/SmokeTests) and not only under a Unity Editor.
    /// </summary>
    public static class UapCompilePolicy
    {
        /// <summary>Messages kept per persisted result; a run with more than this reports the rest only as counts (OverflowCount). Large monorepo compiles can emit thousands of warnings, and SessionState is a single string value -- an unbounded list would grow it without limit.</summary>
        public const int MaxPersistedMessages = 300;

        /// <summary>
        /// Pure: the whole refusal rule for action:request.
        /// <paramref name="persistedState"/> is the LAST KNOWN state before
        /// this call (from UapCompileResultStore.Load()); <paramref name="compilingLive"/>
        /// is EditorApplication.isCompiling read fresh. compilingLive is the
        /// one trusted as ground truth for "a compile is running right now" --
        /// a persistedState stuck at Compiling with compilingLive false (an
        /// orphaned record: the editor was restarted, or a domain reload the
        /// compile itself did not cause happened to wipe the subscription
        /// before compilationFinished ran) must NOT block every future
        /// request forever, so it falls through to Proceed instead. The one
        /// case that DOES still refuse on persisted state alone is
        /// Requested: uap_compile schedules RequestScriptCompilation() a
        /// short delay AFTER this reply (see UapCompileTool), so there is a
        /// real window where compilingLive is still false but a second call
        /// would double-schedule the same request.
        /// </summary>
        public static UapCompileRequestOutcome DecideRequest(UapCompileState persistedState,
            bool playModeActive, bool compilingLive, out string message)
        {
            return DecideRequest(persistedState, playModeActive, compilingLive, 0.0, out message);
        }

        /// <summary>
        /// How long a request may sit in Requested with no compile running
        /// before a new request is allowed anyway. A request that never
        /// produced compilation events -- nothing needed recompiling, or a
        /// script-compilation SETUP error such as a broken asmdef reference --
        /// would otherwise block every later request for the rest of the
        /// Editor session (the persisted state survives domain reloads).
        /// The scheduled request fires about a quarter second after the
        /// reply, so anything this old is not "about to start".
        /// </summary>
        public const double StaleRequestSeconds = 20.0;

        /// <summary>
        /// Pure: as the 4-argument overload, with the age of a persisted
        /// Requested record (seconds since it was armed; 0 when unknown).
        /// A Requested record older than <see cref="StaleRequestSeconds"/>
        /// with no live compile self-heals to Proceed, the same way an
        /// orphaned Compiling record already does.
        /// </summary>
        public static UapCompileRequestOutcome DecideRequest(UapCompileState persistedState,
            bool playModeActive, bool compilingLive, double requestedAgeSeconds, out string message)
        {
            if (playModeActive)
            {
                message = "Refused: Unity is in Play Mode, and recompiling now would replace assemblies the"
                    + " running game depends on. Stop Play Mode first (uap_play_mode action:stop), or call"
                    + " uap_compile action:status instead.";
                return UapCompileRequestOutcome.RefusePlayMode;
            }
            if (compilingLive)
            {
                message = "Refused: a compile is already running. Wait for uap_ping to answer, then call"
                    + " uap_compile action:status.";
                return UapCompileRequestOutcome.RefuseAlreadyCompiling;
            }
            if (persistedState == UapCompileState.Requested && requestedAgeSeconds < StaleRequestSeconds)
            {
                message = "Refused: a compile was already requested and is scheduled to start any moment."
                    + " Wait for uap_ping to answer, then call uap_compile action:status before requesting"
                    + " again.";
                return UapCompileRequestOutcome.RefuseAlreadyRequested;
            }
            message = "Compilation requested; scheduled to run right after this reply.";
            return UapCompileRequestOutcome.Proceed;
        }

        /// <summary>Pure: the status line for action:status, independent of the raw JSON fields the tool also reports alongside it.</summary>
        public static string DescribeStatusMessage(UapCompileResult result, bool compilingLive,
            bool scriptCompilationFailed)
        {
            if (result == null)
            {
                result = UapCompileResult.Idle();
            }
            switch (result.State)
            {
                case UapCompileState.Idle:
                    return "No compile has been requested through uap_compile yet. Unity currently reports"
                        + " isCompilingNow:" + BoolWord(compilingLive) + ", scriptCompilationFailed:"
                        + BoolWord(scriptCompilationFailed) + ".";
                case UapCompileState.Requested:
                    return "Requested; scheduled to start. If isCompilingNow is still false this can mean"
                        + " nothing needed recompiling, or it has simply not begun yet -- call status again"
                        + " in a moment.";
                case UapCompileState.Compiling:
                    // A Compiling record with no live compile was orphaned by a
                    // reload that landed before compilationFinished fired;
                    // DecideRequest already lets a new request through.
                    return compilingLive
                        ? "Compiling now."
                        : "A compile was tracked as running but Unity is not compiling now (the record was"
                          + " left behind by a domain reload) -- call uap_compile action:request to check again.";
                case UapCompileState.Succeeded:
                    return "Compile succeeded: " + Summarize(result.ErrorCount, result.WarningCount) + ".";
                case UapCompileState.Failed:
                    return "Compile failed: " + Summarize(result.ErrorCount, result.WarningCount) + ".";
                default:
                    return "Unknown state.";
            }
        }

        /// <summary>Pure: "N error(s), M warning(s)" -- the one place this wording is built, so a request and a status reply never drift apart.</summary>
        public static string Summarize(int errorCount, int warningCount)
        {
            return errorCount.ToString(CultureInfo.InvariantCulture) + " error(s), "
                + warningCount.ToString(CultureInfo.InvariantCulture) + " warning(s)";
        }

        /// <summary>Pure: keeps the first <paramref name="max"/> messages and reports how many were dropped. Order is preserved (oldest/first-reported first), matching how UapCompileTool accumulates them.</summary>
        public static List<UapCompileMessage> Cap(IList<UapCompileMessage> all, int max, out int overflow)
        {
            if (all == null)
            {
                overflow = 0;
                return new List<UapCompileMessage>();
            }
            if (max < 0)
            {
                max = 0;
            }
            if (all.Count <= max)
            {
                overflow = 0;
                return new List<UapCompileMessage>(all);
            }
            overflow = all.Count - max;
            var kept = new List<UapCompileMessage>(max);
            for (int i = 0; i < max; i++)
            {
                kept.Add(all[i]);
            }
            return kept;
        }

        /// <summary>Pure: one formatted diagnostic line, e.g. "error: Assets/Foo.cs(12,5): message" or "warning: message" when Unity gave no file.</summary>
        public static string FormatMessageLine(UapCompileMessage message)
        {
            string location = string.IsNullOrEmpty(message.File)
                ? string.Empty
                : message.File + "(" + message.Line.ToString(CultureInfo.InvariantCulture) + ","
                    + message.Column.ToString(CultureInfo.InvariantCulture) + "): ";
            return MessageTypeName(message.Type) + ": " + location + (message.Message ?? string.Empty);
        }

        /// <summary>Pure: an absent action is "request" (UapCompileTool's default); an unrecognized one is refused BY NAME.</summary>
        public static UapCompileAction ParseAction(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return UapCompileAction.Request;
            }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "request", StringComparison.OrdinalIgnoreCase))
            {
                return UapCompileAction.Request;
            }
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase))
            {
                return UapCompileAction.Status;
            }
            throw new ArgumentException("Unknown action '" + value + "'. Valid actions: request, status.");
        }

        /// <summary>Pure: the wire name for a state -- idle/requested/compiling/succeeded/failed.</summary>
        public static string StateName(UapCompileState state)
        {
            switch (state)
            {
                case UapCompileState.Idle: return "idle";
                case UapCompileState.Requested: return "requested";
                case UapCompileState.Compiling: return "compiling";
                case UapCompileState.Succeeded: return "succeeded";
                case UapCompileState.Failed: return "failed";
                default: return "idle";
            }
        }

        /// <summary>The inverse of <see cref="StateName"/>. Unrecognized/garbled text (a hand-edited SessionState value, or a future/older format) maps to Idle rather than throwing -- persisted state must never crash a read.</summary>
        public static bool TryParseStateName(string value, out UapCompileState state)
        {
            if (string.Equals(value, "idle", StringComparison.OrdinalIgnoreCase)) { state = UapCompileState.Idle; return true; }
            if (string.Equals(value, "requested", StringComparison.OrdinalIgnoreCase)) { state = UapCompileState.Requested; return true; }
            if (string.Equals(value, "compiling", StringComparison.OrdinalIgnoreCase)) { state = UapCompileState.Compiling; return true; }
            if (string.Equals(value, "succeeded", StringComparison.OrdinalIgnoreCase)) { state = UapCompileState.Succeeded; return true; }
            if (string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)) { state = UapCompileState.Failed; return true; }
            state = UapCompileState.Idle;
            return false;
        }

        /// <summary>Pure: the wire name for a message type -- error/warning/info.</summary>
        public static string MessageTypeName(UapCompileMessageType type)
        {
            switch (type)
            {
                case UapCompileMessageType.Error: return "error";
                case UapCompileMessageType.Warning: return "warning";
                case UapCompileMessageType.Info: return "info";
                default: return "info";
            }
        }

        /// <summary>The inverse of <see cref="MessageTypeName"/>; unrecognized text maps to Info (the least alarming guess) rather than throwing.</summary>
        public static bool TryParseMessageTypeName(string value, out UapCompileMessageType type)
        {
            if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase)) { type = UapCompileMessageType.Error; return true; }
            if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase)) { type = UapCompileMessageType.Warning; return true; }
            if (string.Equals(value, "info", StringComparison.OrdinalIgnoreCase)) { type = UapCompileMessageType.Info; return true; }
            type = UapCompileMessageType.Info;
            return false;
        }

        /// <summary>Pure: ISO-8601 UTC, or empty for the "not set yet" sentinel (0).</summary>
        public static string FormatUtcTicks(long ticks)
        {
            if (ticks <= 0)
            {
                return string.Empty;
            }
            return new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>Pure: the persisted-result JSON shape UapCompileResultStore writes to SessionState AND the shape action:status reports (UapCompileTool builds the status reply from the same node, with a couple of live-only fields added on top).</summary>
        public static JsonNode ToJson(UapCompileResult result)
        {
            if (result == null)
            {
                result = UapCompileResult.Idle();
            }
            JsonNode messages = JsonNode.NewArray();
            for (int i = 0; i < result.Messages.Count; i++)
            {
                UapCompileMessage m = result.Messages[i];
                messages.Add(JsonNode.NewObject()
                    .Set("file", m.File ?? string.Empty)
                    .Set("line", m.Line)
                    .Set("column", m.Column)
                    .Set("message", m.Message ?? string.Empty)
                    .Set("type", MessageTypeName(m.Type)));
            }
            return JsonNode.NewObject()
                .Set("state", StateName(result.State))
                .Set("requestId", result.RequestId ?? string.Empty)
                .Set("clean", result.Clean)
                .Set("startedAtUtcTicks", result.StartedAtUtcTicks)
                .Set("finishedAtUtcTicks", result.FinishedAtUtcTicks)
                .Set("errorCount", result.ErrorCount)
                .Set("warningCount", result.WarningCount)
                .Set("infoCount", result.InfoCount)
                .Set("overflowCount", result.OverflowCount)
                .Set("messages", messages);
        }

        /// <summary>The inverse of <see cref="ToJson"/>. Missing/garbled fields fall back to Idle-equivalent defaults field by field (never throws) -- a corrupt or foreign SessionState value must degrade to "nothing known" rather than break every future status call.</summary>
        public static UapCompileResult FromJson(JsonNode node)
        {
            UapCompileResult result = UapCompileResult.Idle();
            if (node == null || !node.IsObject)
            {
                return result;
            }
            UapCompileState state;
            TryParseStateName(node["state"].AsString(string.Empty), out state);
            result.State = state;
            result.RequestId = node["requestId"].AsString(string.Empty);
            result.Clean = node["clean"].AsBool(false);
            result.StartedAtUtcTicks = node["startedAtUtcTicks"].AsLong(0L);
            result.FinishedAtUtcTicks = node["finishedAtUtcTicks"].AsLong(0L);
            result.ErrorCount = node["errorCount"].AsInt(0);
            result.WarningCount = node["warningCount"].AsInt(0);
            result.InfoCount = node["infoCount"].AsInt(0);
            result.OverflowCount = node["overflowCount"].AsInt(0);
            result.Messages = new List<UapCompileMessage>();
            JsonNode messages = node["messages"];
            if (messages.IsArray)
            {
                foreach (JsonNode item in messages.Items)
                {
                    UapCompileMessageType type;
                    TryParseMessageTypeName(item["type"].AsString(string.Empty), out type);
                    result.Messages.Add(new UapCompileMessage
                    {
                        File = item["file"].AsString(string.Empty),
                        Line = item["line"].AsInt(0),
                        Column = item["column"].AsInt(0),
                        Message = item["message"].AsString(string.Empty),
                        Type = type,
                    });
                }
            }
            return result;
        }

        private static string BoolWord(bool value)
        {
            return value ? "true" : "false";
        }
    }
}

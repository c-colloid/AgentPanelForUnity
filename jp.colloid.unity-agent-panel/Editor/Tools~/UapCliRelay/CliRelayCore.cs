using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Colloid.AgentPanel.Tools.CliRelay
{
    // =====================================================================
    // CLI relay core (design note: CLI relay v1, "minimal" design).
    //
    // The cli-relay process owns the claude CLI as its child and the CLI's
    // stdio; Unity talks to it over one loopback TCP "control link",
    // detaches before a domain reload and re-attaches after it, so the CLI
    // and the running turn survive the reload.
    //
    // This file holds every piece that can be tested without real
    // processes or sockets: the frame codec, the stdout replay buffer,
    // stdin validation + de-duplication, the attach/lease/watchdog state
    // machine (driven by an injected clock), the MCP front's HTTP parsing
    // and its hold-and-forward policy (driven by an injected forward
    // delegate). CliRelayMain.cs is the thin host around it.
    //
    // No Unity dependency, C# 9, .NET 6 APIs only: UapStdioRelayBuilder
    // compiles it with Unity's bundled Roslyn against Unity's bundled
    // runtime (.NET 6 on Unity 2022.3), and ci/SmokeTests compiles it
    // verbatim (net8, LangVersion 9).
    // =====================================================================

    /// <summary>Wire constants shared by the relay and (by contract) the Unity side.</summary>
    public static class CliRelayProtocol
    {
        /// <summary>
        /// Version of the control-link protocol, carried in hello / welcome
        /// and in the ready file. The `shutdown` frame
        /// (<c>C {"type":"shutdown","graceMillis":n}</c>) is frozen forever:
        /// every version accepts it, even after a hello whose protocol does
        /// not match, so a newer Unity can always stop an older relay.
        /// </summary>
        public const int Version = 1;

        public const int ExitNormal = 0;
        public const int ExitBadArguments = 2;
        public const int ExitUnityGone = 3;
        public const int ExitLeaseExpired = 4;
        /// <summary>The CLI exited and its exit was reported to an attached Unity; the relay followed it.</summary>
        public const int ExitCliExited = 5;

        public const string EnvControlToken = "UAP_RELAY_TOKEN";
        public const string EnvMcpToken = "UAP_RELAY_MCP_TOKEN";
        public const string EnvLeaseSeconds = "UAP_RELAY_LEASE_SECONDS";
        public const string EnvMaxBufferBytes = "UAP_RELAY_MAX_BUFFER_BYTES";
        public const string EnvHoldMillis = "UAP_RELAY_HOLD_MILLIS";

        public const long DefaultLeaseMillis = 600L * 1000L;
        public const long DefaultMaxBufferBytes = 32L * 1024L * 1024L;
        public const int DefaultHoldMillis = 25000;
        public const int HelloTimeoutMillis = 2000;
        public const long ParentCheckIntervalMillis = 2000;
        public const int ParentFailuresToAct = 2;
        public const long CliExitLingerMillis = 2000;
        public const long ParentStartToleranceTicks = 2L * TimeSpan.TicksPerSecond;
    }

    // ---------------------------------------------------------------------
    // Frame codec
    // ---------------------------------------------------------------------

    /// <summary>One decoded control-link frame.</summary>
    public sealed class CliRelayFrame
    {
        /// <summary>'O', 'E', 'C' or 'I'.</summary>
        public char Tag;

        /// <summary>The `I` frame's sequence number; 0 for other tags.</summary>
        public long Seq;

        /// <summary>Everything after the tag (and, for `I`, after the sequence number) and one space.</summary>
        public string Payload = string.Empty;
    }

    /// <summary>
    /// Frame grammar (one UTF-8 line per frame, terminated by '\n', no BOM):
    /// <code>
    ///   O &lt;cli stdout line&gt;      relay -> Unity
    ///   E &lt;cli stderr line&gt;      relay -> Unity
    ///   C &lt;json object&gt;          both directions
    ///   I &lt;n&gt; &lt;cli stdin line&gt;  Unity -> relay, n = decimal, 1..18 digits
    /// </code>
    /// </summary>
    public static class CliRelayFrames
    {
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Out(string line) { return "O " + (line ?? string.Empty); }
        public static string Err(string line) { return "E " + (line ?? string.Empty); }
        public static string Ctl(string json) { return "C " + (json ?? "{}"); }

        public static string In(long seq, string payload)
        {
            return "I " + seq.ToString(CultureInfo.InvariantCulture) + " " + (payload ?? string.Empty);
        }

        /// <summary>The bytes to put on the wire for one frame (UTF-8 + '\n').</summary>
        public static byte[] Encode(string frame)
        {
            int count = Utf8NoBom.GetByteCount(frame);
            byte[] bytes = new byte[count + 1];
            Utf8NoBom.GetBytes(frame, 0, frame.Length, bytes, 0);
            bytes[count] = (byte)'\n';
            return bytes;
        }

        /// <summary>Parses one frame (without its '\n'). False for anything that does not fit the grammar.</summary>
        public static bool TryParse(string frame, out CliRelayFrame result)
        {
            result = null;
            if (frame == null || frame.Length < 2 || frame[1] != ' ')
            {
                return false;
            }
            char tag = frame[0];
            if (tag == 'O' || tag == 'E' || tag == 'C')
            {
                result = new CliRelayFrame { Tag = tag, Payload = frame.Substring(2) };
                return true;
            }
            if (tag != 'I')
            {
                return false;
            }
            int i = 2;
            long seq = 0;
            int digits = 0;
            while (i < frame.Length && frame[i] >= '0' && frame[i] <= '9')
            {
                seq = seq * 10 + (frame[i] - '0');
                digits++;
                i++;
                if (digits > 18)
                {
                    return false;
                }
            }
            if (digits == 0)
            {
                return false;
            }
            string payload;
            if (i == frame.Length)
            {
                payload = string.Empty;
            }
            else if (frame[i] == ' ')
            {
                payload = frame.Substring(i + 1);
            }
            else
            {
                return false;
            }
            result = new CliRelayFrame { Tag = 'I', Seq = seq, Payload = payload };
            return true;
        }

        /// <summary>At most <paramref name="max"/> characters of a payload, for the log (never log a whole payload).</summary>
        public static string Preview(string payload, int max = 80)
        {
            if (payload == null)
            {
                return string.Empty;
            }
            return payload.Length <= max ? payload : payload.Substring(0, max) + "...(" + payload.Length + " chars)";
        }
    }

    // ---------------------------------------------------------------------
    // Small JSON helpers (System.Text.Json reads, hand-written writes)
    // ---------------------------------------------------------------------

    /// <summary>Builds one flat JSON object. Values are escaped; Raw() inserts pre-built JSON.</summary>
    public sealed class CliRelayJsonObject
    {
        private readonly StringBuilder _sb = new StringBuilder("{");
        private bool _first = true;

        private CliRelayJsonObject Key(string name)
        {
            if (!_first)
            {
                _sb.Append(',');
            }
            _first = false;
            CliRelayJson.AppendString(_sb, name);
            _sb.Append(':');
            return this;
        }

        public CliRelayJsonObject Str(string name, string value)
        {
            Key(name);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                CliRelayJson.AppendString(_sb, value);
            }
            return this;
        }

        public CliRelayJsonObject Num(string name, long value)
        {
            Key(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public CliRelayJsonObject NumOrNull(string name, int? value)
        {
            Key(name);
            _sb.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
            return this;
        }

        public CliRelayJsonObject Bool(string name, bool value)
        {
            Key(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public CliRelayJsonObject Raw(string name, string json)
        {
            Key(name);
            _sb.Append(string.IsNullOrEmpty(json) ? "null" : json);
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString() + "}";
        }
    }

    public static class CliRelayJson
    {
        public static CliRelayJsonObject Obj() { return new CliRelayJsonObject(); }

        public static CliRelayJsonObject Ctl(string type) { return new CliRelayJsonObject().Str("type", type); }

        public static string Quote(string value)
        {
            StringBuilder sb = new StringBuilder();
            AppendString(sb, value ?? string.Empty);
            return sb.ToString();
        }

        public static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x2028 || c == 0x2029)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Parses a JSON object; null when the text is not a JSON object.</summary>
        public static JsonDocument TryParseObject(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return doc;
                }
                doc.Dispose();
            }
            catch (JsonException)
            {
            }
            catch (ArgumentException)
            {
            }
            return null;
        }

        public static string GetString(JsonElement obj, string name)
        {
            JsonElement value;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        public static long GetLong(JsonElement obj, string name, long fallback)
        {
            JsonElement value;
            long result;
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
            {
                return result;
            }
            return fallback;
        }

        public static bool TryGetObject(JsonElement obj, string name, out JsonElement child)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
            child = default(JsonElement);
            return false;
        }

        /// <summary>True when the property is absent or JSON null.</summary>
        public static bool IsNullOrAbsent(JsonElement obj, string name)
        {
            JsonElement value;
            return !(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value)
                && value.ValueKind != JsonValueKind.Null);
        }
    }

    // ---------------------------------------------------------------------
    // Light inspection of CLI stdout / stdin lines
    // ---------------------------------------------------------------------

    public enum CliLineKind
    {
        Other,
        SystemInit,
        Result,
        Assistant,
        StreamEvent,
        ControlRequest,
        ControlResponse,
    }

    /// <summary>What the relay needs to know about one CLI line. Never more than this.</summary>
    public struct CliLineInfo
    {
        public CliLineKind Kind;

        /// <summary>control_request / control_response: the request_id.</summary>
        public string RequestId;

        /// <summary>assistant / stream_event: parent_tool_use_id, or null for the top level.</summary>
        public string ParentToolUseId;

        /// <summary>control_request: request.subtype (e.g. "initialize", "can_use_tool").</summary>
        public string Subtype;
    }

    public static class CliLineInspector
    {
        /// <summary>
        /// Reads only "type", "subtype", "request_id", "response.request_id",
        /// "request.subtype" and "parent_tool_use_id". A line that is not a
        /// JSON object is <see cref="CliLineKind.Other"/>.
        /// </summary>
        public static CliLineInfo Inspect(string line)
        {
            CliLineInfo info = new CliLineInfo { Kind = CliLineKind.Other };
            if (string.IsNullOrEmpty(line) || line[0] != '{')
            {
                return info;
            }
            using (JsonDocument doc = CliRelayJson.TryParseObject(line))
            {
                if (doc == null)
                {
                    return info;
                }
                JsonElement root = doc.RootElement;
                string type = CliRelayJson.GetString(root, "type");
                JsonElement child;
                switch (type)
                {
                    case "system":
                        if (CliRelayJson.GetString(root, "subtype") == "init")
                        {
                            info.Kind = CliLineKind.SystemInit;
                        }
                        break;
                    case "result":
                        info.Kind = CliLineKind.Result;
                        break;
                    case "assistant":
                        info.Kind = CliLineKind.Assistant;
                        info.ParentToolUseId = CliRelayJson.GetString(root, "parent_tool_use_id");
                        break;
                    case "stream_event":
                        info.Kind = CliLineKind.StreamEvent;
                        info.ParentToolUseId = CliRelayJson.GetString(root, "parent_tool_use_id");
                        break;
                    case "control_request":
                        info.Kind = CliLineKind.ControlRequest;
                        info.RequestId = CliRelayJson.GetString(root, "request_id");
                        if (CliRelayJson.TryGetObject(root, "request", out child))
                        {
                            info.Subtype = CliRelayJson.GetString(child, "subtype");
                        }
                        break;
                    case "control_response":
                        info.Kind = CliLineKind.ControlResponse;
                        if (CliRelayJson.TryGetObject(root, "response", out child))
                        {
                            info.RequestId = CliRelayJson.GetString(child, "request_id");
                            info.Subtype = CliRelayJson.GetString(child, "subtype");
                        }
                        if (info.RequestId == null)
                        {
                            info.RequestId = CliRelayJson.GetString(root, "request_id");
                        }
                        break;
                }
            }
            return info;
        }
    }

    // ---------------------------------------------------------------------
    // The stdout replay buffer
    // ---------------------------------------------------------------------

    /// <summary>
    /// Keeps what a re-attaching Unity needs to rebuild the open turn:
    /// the latest system/init line (init slot), the initialize
    /// control_response (initialize-response slot), and every other stdout
    /// line after the last result Unity acknowledged.
    ///
    /// Each buffered line carries DropAt = (results seen before it) + 1: an
    /// ack of K drops every line with DropAt &lt;= K, i.e. everything up to
    /// and including result #K.
    ///
    /// Compaction rule 1: an assistant line removes the buffered
    /// stream_event lines with the same parent_tool_use_id (a top-level
    /// assistant line removes the top-level deltas; a subagent's assistant
    /// line removes only that subagent's deltas).
    /// Compaction rule 2: a control_response Unity writes to stdin removes
    /// the buffered control_request with the same request_id.
    ///
    /// Byte cap: a line that does not fit is not buffered (it is still
    /// forwarded live by the host); the buffer remembers the highest DropAt
    /// it lost, and <see cref="ReplayComplete"/> stays false until an ack
    /// reaches it. Not thread-safe: the host serializes calls.
    /// </summary>
    public sealed class CliRelayBuffer
    {
        private sealed class Entry
        {
            public string Line;
            public int DropAt;
            public CliLineKind Kind;
            public string RequestId;
            public string ParentToolUseId;
            public long Bytes;
        }

        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
        private readonly long _maxBytes;
        private int _lostDropAt;

        public CliRelayBuffer(long maxBytes)
        {
            _maxBytes = maxBytes > 0 ? maxBytes : CliRelayProtocol.DefaultMaxBufferBytes;
        }

        public string InitSlot { get; private set; }
        public string InitResponseSlot { get; private set; }

        /// <summary>request_id of the first initialize control_request Unity wrote to stdin.</summary>
        public string InitRequestId { get; private set; }

        /// <summary>Number of result lines the CLI has printed so far.</summary>
        public int ResultsSeen { get; private set; }

        /// <summary>Highest resultsProcessed Unity reported (ack / hello / detach). Monotonic.</summary>
        public int ResultsAcked { get; private set; }

        /// <summary>Buffered bytes, counted as UTF-16 length * 2.</summary>
        public long Bytes { get; private set; }

        public int Count { get { return _entries.Count; } }

        /// <summary>True when lines of a turn after the last ack were dropped for the byte cap.</summary>
        public bool Overflowed { get { return _lostDropAt > ResultsAcked; } }

        public bool ReplayComplete { get { return !Overflowed; } }

        public static long SizeOf(string line)
        {
            return (long)(line == null ? 0 : line.Length) * 2L;
        }

        /// <summary>Records one CLI stdout line. Returns its classification.</summary>
        public CliLineInfo OnStdout(string line)
        {
            CliLineInfo info = CliLineInspector.Inspect(line);
            if (info.Kind == CliLineKind.SystemInit)
            {
                InitSlot = line;
                return info;
            }
            if (info.Kind == CliLineKind.ControlResponse && InitRequestId != null
                && string.Equals(info.RequestId, InitRequestId, StringComparison.Ordinal))
            {
                InitResponseSlot = line;
                return info;
            }

            int dropAt = ResultsSeen + 1;
            if (info.Kind == CliLineKind.Result)
            {
                ResultsSeen++;
            }
            if (info.Kind == CliLineKind.Assistant)
            {
                RemoveStreamEvents(info.ParentToolUseId);
            }
            if (dropAt <= ResultsAcked)
            {
                return info;
            }
            long size = SizeOf(line);
            if (Bytes + size > _maxBytes)
            {
                if (dropAt > _lostDropAt)
                {
                    _lostDropAt = dropAt;
                }
                return info;
            }
            _entries.AddLast(new Entry
            {
                Line = line,
                DropAt = dropAt,
                Kind = info.Kind,
                RequestId = info.RequestId,
                ParentToolUseId = info.ParentToolUseId,
                Bytes = size,
            });
            Bytes += size;
            return info;
        }

        /// <summary>Records one line Unity wrote to CLI stdin (learns the initialize id; compaction rule 2).</summary>
        public void OnStdin(string payload)
        {
            CliLineInfo info = CliLineInspector.Inspect(payload);
            if (info.Kind == CliLineKind.ControlRequest && info.Subtype == "initialize"
                && InitRequestId == null && !string.IsNullOrEmpty(info.RequestId))
            {
                InitRequestId = info.RequestId;
                return;
            }
            if (info.Kind == CliLineKind.ControlResponse && !string.IsNullOrEmpty(info.RequestId))
            {
                LinkedListNode<Entry> node = _entries.First;
                while (node != null)
                {
                    LinkedListNode<Entry> next = node.Next;
                    if (node.Value.Kind == CliLineKind.ControlRequest
                        && string.Equals(node.Value.RequestId, info.RequestId, StringComparison.Ordinal))
                    {
                        Remove(node);
                    }
                    node = next;
                }
            }
        }

        /// <summary>Unity has processed <paramref name="resultsProcessed"/> result lines. Monotonic (max wins).</summary>
        public void Ack(int resultsProcessed)
        {
            if (resultsProcessed <= ResultsAcked)
            {
                return;
            }
            ResultsAcked = resultsProcessed;
            while (_entries.First != null && _entries.First.Value.DropAt <= ResultsAcked)
            {
                Remove(_entries.First);
            }
        }

        /// <summary>The replay, in order: init slot, initialize-response slot, buffered lines.</summary>
        public List<string> Snapshot()
        {
            List<string> lines = new List<string>(_entries.Count + 2);
            if (InitSlot != null)
            {
                lines.Add(InitSlot);
            }
            if (InitResponseSlot != null)
            {
                lines.Add(InitResponseSlot);
            }
            foreach (Entry e in _entries)
            {
                lines.Add(e.Line);
            }
            return lines;
        }

        private void RemoveStreamEvents(string parent)
        {
            LinkedListNode<Entry> node = _entries.First;
            while (node != null)
            {
                LinkedListNode<Entry> next = node.Next;
                if (node.Value.Kind == CliLineKind.StreamEvent
                    && string.Equals(node.Value.ParentToolUseId, parent, StringComparison.Ordinal))
                {
                    Remove(node);
                }
                node = next;
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            Bytes -= node.Value.Bytes;
            _entries.Remove(node);
        }
    }

    // ---------------------------------------------------------------------
    // stdin validation + de-duplication
    // ---------------------------------------------------------------------

    public enum StdinDecision
    {
        Write,
        Duplicate,
        Reject,
    }

    /// <summary>
    /// Every `I` payload goes through here before it reaches CLI stdin: one
    /// malformed stdin line kills the CLI, and a resend from a new Unity
    /// domain must never double-send. Not thread-safe.
    /// </summary>
    public sealed class CliRelayStdinGate
    {
        /// <summary>Highest sequence number accepted for writing (0 = none yet).</summary>
        public long LastInSeq { get; private set; }

        public StdinDecision Check(long seq, string payload, out string reason)
        {
            reason = null;
            if (seq <= LastInSeq)
            {
                reason = "duplicate";
                return StdinDecision.Duplicate;
            }
            reason = Validate(payload);
            if (reason != null)
            {
                return StdinDecision.Reject;
            }
            LastInSeq = seq;
            return StdinDecision.Write;
        }

        /// <summary>Null when the payload may be written to CLI stdin, else the reason it may not.</summary>
        public static string Validate(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return "empty";
            }
            if (payload.IndexOf('\n') >= 0 || payload.IndexOf('\r') >= 0)
            {
                return "contains CR or LF";
            }
            if (payload[0] != '{')
            {
                return "not a JSON object (first character is not '{')";
            }
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Attach / lease / watchdog state machine
    // ---------------------------------------------------------------------

    /// <summary>
    /// Decides when the relay must end on its own, from an injected
    /// monotonic clock (milliseconds) and an injected Unity-liveness probe:
    /// - Unity gone: the probe failed <see cref="CliRelayProtocol.ParentFailuresToAct"/>
    ///   times in a row, the checks <see cref="CliRelayProtocol.ParentCheckIntervalMillis"/> apart
    ///   (a single failed check after laptop sleep/wake never acts);
    /// - lease expired: nobody attached for longer than the lease;
    /// - CLI exited: its exit was queued to an attached Unity
    ///   <see cref="CliRelayProtocol.CliExitLingerMillis"/> ago.
    /// Not thread-safe: the host serializes calls.
    /// </summary>
    public sealed class CliRelaySession
    {
        private readonly long _leaseMillis;
        private readonly long _checkIntervalMillis;
        private long _detachedSince;
        private long _lastParentCheck;
        private long _exitReportedAt = -1;

        public CliRelaySession(long leaseMillis, long checkIntervalMillis, long nowMillis)
        {
            _leaseMillis = leaseMillis > 0 ? leaseMillis : CliRelayProtocol.DefaultLeaseMillis;
            _checkIntervalMillis = checkIntervalMillis > 0 ? checkIntervalMillis : CliRelayProtocol.ParentCheckIntervalMillis;
            _detachedSince = nowMillis;
            _lastParentCheck = nowMillis;
        }

        public bool Attached { get; private set; }
        public int ConsecutiveParentFailures { get; private set; }

        public void OnAttached(long nowMillis)
        {
            Attached = true;
        }

        public void OnDetached(long nowMillis)
        {
            if (Attached)
            {
                Attached = false;
                _detachedSince = nowMillis;
            }
        }

        /// <summary>The `exited` frame was queued to an attached Unity.</summary>
        public void OnCliExitReported(long nowMillis)
        {
            if (_exitReportedAt < 0)
            {
                _exitReportedAt = nowMillis;
            }
        }

        /// <summary>-1 to keep running, else the exit code the relay must end with.</summary>
        public int Tick(long nowMillis, Func<bool> unityAlive)
        {
            if (nowMillis - _lastParentCheck >= _checkIntervalMillis)
            {
                _lastParentCheck = nowMillis;
                bool alive = true;
                try
                {
                    alive = unityAlive == null || unityAlive();
                }
                catch (Exception)
                {
                    // A probe that throws proves nothing either way.
                    alive = true;
                }
                ConsecutiveParentFailures = alive ? 0 : ConsecutiveParentFailures + 1;
                if (ConsecutiveParentFailures >= CliRelayProtocol.ParentFailuresToAct)
                {
                    return CliRelayProtocol.ExitUnityGone;
                }
            }
            if (!Attached && nowMillis - _detachedSince > _leaseMillis)
            {
                return CliRelayProtocol.ExitLeaseExpired;
            }
            if (_exitReportedAt >= 0 && nowMillis - _exitReportedAt >= CliRelayProtocol.CliExitLingerMillis)
            {
                return CliRelayProtocol.ExitCliExited;
            }
            return -1;
        }
    }

    /// <summary>Hello evaluation and constant-time token compare.</summary>
    public static class CliRelayAuth
    {
        public enum HelloResult
        {
            /// <summary>Not a hello, or a wrong token: close the connection.</summary>
            Reject,
            /// <summary>Right token, right protocol: attach.</summary>
            Accept,
            /// <summary>Right token, other protocol: answer `mismatch`, accept only shutdown / ping.</summary>
            ProtocolMismatch,
        }

        public static bool TokenEquals(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected) || actual == null)
            {
                return false;
            }
            byte[] a = Encoding.UTF8.GetBytes(expected);
            byte[] b = Encoding.UTF8.GetBytes(actual);
            if (a.Length != b.Length)
            {
                // Still burn a compare so the length check is the only shortcut.
                CryptographicOperations.FixedTimeEquals(a, a);
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>Evaluates the first frame on a control connection.</summary>
        public static HelloResult EvaluateHello(string firstFrame, string expectedToken, out JsonElement hello, out JsonDocument doc)
        {
            hello = default(JsonElement);
            doc = null;
            CliRelayFrame frame;
            if (!CliRelayFrames.TryParse(firstFrame, out frame) || frame.Tag != 'C')
            {
                return HelloResult.Reject;
            }
            doc = CliRelayJson.TryParseObject(frame.Payload);
            if (doc == null)
            {
                return HelloResult.Reject;
            }
            hello = doc.RootElement;
            if (CliRelayJson.GetString(hello, "type") != "hello"
                || !TokenEquals(expectedToken, CliRelayJson.GetString(hello, "token")))
            {
                return HelloResult.Reject;
            }
            if (CliRelayJson.GetLong(hello, "protocol", -1) != CliRelayProtocol.Version)
            {
                return HelloResult.ProtocolMismatch;
            }
            return HelloResult.Accept;
        }

        /// <summary>
        /// The liveness rule for Unity's process, given what the host could
        /// observe: pid gone -> dead; pid present but its start time differs
        /// from the baseline by more than the tolerance (pid reuse) -> dead;
        /// baseline 0 or start time unreadable -> pid existence only.
        /// </summary>
        public static bool IsParentAlive(bool pidExists, long baselineStartTicks, long observedStartTicks)
        {
            if (!pidExists)
            {
                return false;
            }
            if (baselineStartTicks == 0 || observedStartTicks == 0)
            {
                return true;
            }
            return Math.Abs(observedStartTicks - baselineStartTicks) <= CliRelayProtocol.ParentStartToleranceTicks;
        }
    }

    // ---------------------------------------------------------------------
    // MCP front: minimal HTTP/1.1 request parsing and response writing
    // ---------------------------------------------------------------------

    public sealed class MiniHttpRequest
    {
        public string Method = string.Empty;
        public string Target = string.Empty;
        public string Version = string.Empty;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public string BodyText { get { return CliRelayFrames.Utf8NoBom.GetString(Body); } }
    }

    public sealed class MiniHttpResponse
    {
        public int Status = 200;
        public string ContentType;
        public byte[] Body = new byte[0];
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        public string BodyText { get { return CliRelayFrames.Utf8NoBom.GetString(Body ?? new byte[0]); } }

        public static MiniHttpResponse StatusOnly(int status)
        {
            return new MiniHttpResponse { Status = status };
        }

        public static MiniHttpResponse Json(int status, string json)
        {
            return new MiniHttpResponse
            {
                Status = status,
                ContentType = "application/json",
                Body = CliRelayFrames.Utf8NoBom.GetBytes(json),
            };
        }
    }

    public static class MiniHttp
    {
        public const int MaxHeaderBytes = 16 * 1024;
        public const int MaxBodyBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Reads one request from <paramref name="stream"/>. Returns 0 on
        /// success, -1 when the connection closed before a single byte, or
        /// the HTTP status to answer with: 400 malformed, 411 a POST without
        /// Content-Length or chunked encoding, 413 body over
        /// <paramref name="maxBody"/>, 431 head over <paramref name="maxHeader"/>,
        /// 501 an unsupported Transfer-Encoding.
        /// </summary>
        public static int ReadRequest(Stream stream, int maxHeader, int maxBody, out MiniHttpRequest request)
        {
            request = null;
            byte[] buffer = new byte[Math.Max(1024, maxHeader + 4)];
            int filled = 0;
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (filled >= maxHeader + 4)
                {
                    return 431;
                }
                int read = stream.Read(buffer, filled, Math.Min(buffer.Length - filled, 4096));
                if (read <= 0)
                {
                    return filled == 0 ? -1 : 400;
                }
                int searchFrom = Math.Max(0, filled - 3);
                filled += read;
                headEnd = IndexOfHeadEnd(buffer, searchFrom, filled);
                if (headEnd < 0 && filled > maxHeader)
                {
                    return 431;
                }
            }
            if (headEnd > maxHeader)
            {
                return 431;
            }
            string head = Encoding.ASCII.GetString(buffer, 0, headEnd);
            MiniHttpRequest req = new MiniHttpRequest();
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[0].Length == 0 || requestLine[1].Length == 0
                || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return 400;
            }
            req.Method = requestLine[0];
            req.Target = requestLine[1];
            req.Version = requestLine[2];
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    return 400;
                }
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string existing;
                if (req.Headers.TryGetValue(name, out existing))
                {
                    if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) && existing != value)
                    {
                        return 400;
                    }
                    req.Headers[name] = existing + ", " + value;
                }
                else
                {
                    req.Headers[name] = value;
                }
            }

            int leftoverStart = headEnd + 4;
            int leftover = filled - leftoverStart;
            LeftoverStream body = new LeftoverStream(buffer, leftoverStart, leftover, stream);

            string transferEncoding = req.Header("Transfer-Encoding");
            string contentLength = req.Header("Content-Length");
            if (!string.IsNullOrEmpty(transferEncoding))
            {
                if (!string.Equals(transferEncoding.Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return 501;
                }
                byte[] decoded;
                int status = ReadChunked(body, maxBody, out decoded);
                if (status != 0)
                {
                    return status;
                }
                req.Body = decoded;
            }
            else if (!string.IsNullOrEmpty(contentLength))
            {
                long length;
                if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0)
                {
                    return 400;
                }
                if (length > maxBody)
                {
                    return 413;
                }
                byte[] data = new byte[length];
                if (!body.ReadExactly(data, 0, (int)length))
                {
                    return 400;
                }
                req.Body = data;
            }
            else if (string.Equals(req.Method, "POST", StringComparison.Ordinal)
                || string.Equals(req.Method, "PUT", StringComparison.Ordinal))
            {
                return 411;
            }
            request = req;
            return 0;
        }

        private static int IndexOfHeadEnd(byte[] buffer, int from, int to)
        {
            for (int i = from; i + 3 < to; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static int ReadChunked(LeftoverStream body, int maxBody, out byte[] decoded)
        {
            decoded = null;
            MemoryStream output = new MemoryStream();
            while (true)
            {
                string sizeLine = body.ReadAsciiLine(256);
                if (sizeLine == null)
                {
                    return 400;
                }
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0)
                {
                    sizeLine = sizeLine.Substring(0, semi);
                }
                long size;
                if (!long.TryParse(sizeLine.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size) || size < 0)
                {
                    return 400;
                }
                if (size == 0)
                {
                    // Trailers, then the empty line.
                    while (true)
                    {
                        string trailer = body.ReadAsciiLine(8192);
                        if (trailer == null)
                        {
                            return 400;
                        }
                        if (trailer.Length == 0)
                        {
                            break;
                        }
                    }
                    decoded = output.ToArray();
                    return 0;
                }
                if (output.Length + size > maxBody)
                {
                    return 413;
                }
                byte[] chunk = new byte[size];
                if (!body.ReadExactly(chunk, 0, (int)size))
                {
                    return 400;
                }
                output.Write(chunk, 0, chunk.Length);
                string crlf = body.ReadAsciiLine(2);
                if (crlf == null || crlf.Length != 0)
                {
                    return 400;
                }
            }
        }

        /// <summary>Bytes already read past the head first, then the underlying stream.</summary>
        private sealed class LeftoverStream
        {
            private readonly byte[] _buffer;
            private int _offset;
            private int _count;
            private readonly Stream _inner;

            public LeftoverStream(byte[] buffer, int offset, int count, Stream inner)
            {
                _buffer = buffer;
                _offset = offset;
                _count = Math.Max(0, count);
                _inner = inner;
            }

            public int ReadByte()
            {
                if (_count > 0)
                {
                    _count--;
                    return _buffer[_offset++];
                }
                return _inner.ReadByte();
            }

            public bool ReadExactly(byte[] target, int offset, int count)
            {
                while (count > 0)
                {
                    int n;
                    if (_count > 0)
                    {
                        n = Math.Min(_count, count);
                        Buffer.BlockCopy(_buffer, _offset, target, offset, n);
                        _offset += n;
                        _count -= n;
                    }
                    else
                    {
                        n = _inner.Read(target, offset, count);
                        if (n <= 0)
                        {
                            return false;
                        }
                    }
                    offset += n;
                    count -= n;
                }
                return true;
            }

            /// <summary>One CRLF-terminated ASCII line without its CRLF; null on EOF or when longer than max.</summary>
            public string ReadAsciiLine(int max)
            {
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    int b = ReadByte();
                    if (b < 0)
                    {
                        return null;
                    }
                    if (b == '\r')
                    {
                        int lf = ReadByte();
                        return lf == '\n' ? sb.ToString() : null;
                    }
                    if (sb.Length >= max)
                    {
                        return null;
                    }
                    sb.Append((char)b);
                }
            }
        }

        public static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        /// <summary>Serializes a response; always Content-Length and Connection: close.</summary>
        public static byte[] FormatResponse(MiniHttpResponse response)
        {
            byte[] body = response.Body ?? new byte[0];
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
            if (!string.IsNullOrEmpty(response.ContentType))
            {
                sb.Append("Content-Type: ").Append(Sanitize(response.ContentType)).Append("\r\n");
            }
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                sb.Append(Sanitize(header.Key)).Append(": ").Append(Sanitize(header.Value)).Append("\r\n");
            }
            sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = CliRelayFrames.Utf8NoBom.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        /// <summary>Constant-time check of an "Authorization: Bearer &lt;token&gt;" header.</summary>
        public static bool IsAuthorized(string authorizationHeader, string token)
        {
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                return false;
            }
            const string prefix = "Bearer ";
            if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return CliRelayAuth.TokenEquals(token, authorizationHeader.Substring(prefix.Length).Trim());
        }
    }

    // ---------------------------------------------------------------------
    // MCP front: hold-and-forward policy
    // ---------------------------------------------------------------------

    /// <summary>Unity's current UapOps endpoint, as registered by a `backend` frame.</summary>
    public sealed class McpBackend
    {
        public McpBackend(string url, string token, long generation)
        {
            Url = (url ?? string.Empty).TrimEnd('/');
            Token = token ?? string.Empty;
            Generation = generation;
        }

        public string Url { get; private set; }
        public string Token { get; private set; }
        public long Generation { get; private set; }
    }

    /// <summary>The result of one forward attempt, as observed by the host's HTTP client.</summary>
    public sealed class McpForwardResult
    {
        /// <summary>The connection was never established (refused, or a socket error before anything was sent).</summary>
        public bool NotDelivered;

        /// <summary>Non-null when the attempt failed after the request may have reached Unity.</summary>
        public string Error;

        public int Status;
        public string ContentType;
        public byte[] Body = new byte[0];
        public string McpSessionId;

        /// <summary>The response carried "X-Uap-Not-Run: 1".</summary>
        public bool NotRunHeader;
    }

    /// <summary>Makes one HTTP POST to the backend. Must not throw.</summary>
    public delegate McpForwardResult McpForwardFunc(McpBackend backend, MiniHttpRequest request, int timeoutMillis);

    public enum McpOutcome
    {
        Response,
        NotDelivered,
        NotRun,
        OutcomeUnknown,
    }

    /// <summary>
    /// The current backend plus a version counter that changes whenever a
    /// `backend` frame arrives or Unity detaches; held requests wait on it.
    /// Thread-safe.
    /// </summary>
    public sealed class CliRelayMcpGate
    {
        private readonly object _lock = new object();
        private McpBackend _current;
        private long _generation;
        private long _version;

        public McpBackend Current { get { lock (_lock) { return _current; } } }

        public long Version { get { lock (_lock) { return _version; } } }

        public void SetBackend(string url, string token)
        {
            lock (_lock)
            {
                _generation++;
                _version++;
                _current = new McpBackend(url, token, _generation);
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Unity detached: hold new requests until the next `backend`.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _version++;
                _current = null;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Waits until the version differs from <paramref name="seenVersion"/> or the time runs out.</summary>
        public void WaitForChange(long seenVersion, int millis)
        {
            if (millis <= 0)
            {
                return;
            }
            lock (_lock)
            {
                if (_version != seenVersion)
                {
                    return;
                }
                Monitor.Wait(_lock, millis);
            }
        }
    }

    /// <summary>
    /// Handles one HTTP request from the CLI to the relay's MCP front:
    /// bearer check, then hold while Unity is away (up to HoldMillis),
    /// forward, classify:
    /// - NOT DELIVERED (no connection) or NOT RUN (503 + X-Uap-Not-Run: 1):
    ///   safe to repeat; wait for a new backend (or retry the same one
    ///   after <see cref="RetryBackoffMillis"/>) until the deadline;
    /// - a response: passed back as received;
    /// - OUTCOME UNKNOWN (failure after the request may have reached Unity):
    ///   never re-sent; an isError tool result says so;
    /// - deadline passed while holding: an isError tool result says the
    ///   call was NOT run and is safe to retry.
    /// A notification (no "id") that is not delivered gets 202 with an empty body.
    /// </summary>
    public sealed class CliRelayMcpFront
    {
        public const int UnityDispatchBudgetMillis = 15000;
        public const int RetryBackoffMillis = 1000;

        private readonly string _token;
        private readonly CliRelayMcpGate _gate;
        private readonly McpForwardFunc _forward;
        private readonly Func<long> _nowMillis;
        private readonly int _holdMillis;
        private readonly Action<string> _log;

        public CliRelayMcpFront(string token, CliRelayMcpGate gate, McpForwardFunc forward, Func<long> nowMillis, int holdMillis, Action<string> log)
        {
            _token = token;
            _gate = gate;
            _forward = forward;
            _nowMillis = nowMillis;
            _holdMillis = holdMillis > 0 ? holdMillis : CliRelayProtocol.DefaultHoldMillis;
            _log = log;
        }

        public static McpOutcome Classify(McpForwardResult result)
        {
            if (result == null)
            {
                return McpOutcome.OutcomeUnknown;
            }
            if (result.NotDelivered)
            {
                return McpOutcome.NotDelivered;
            }
            if (result.Error != null)
            {
                return McpOutcome.OutcomeUnknown;
            }
            if (result.Status == 503 && result.NotRunHeader)
            {
                return McpOutcome.NotRun;
            }
            return McpOutcome.Response;
        }

        public MiniHttpResponse Handle(MiniHttpRequest request)
        {
            string path = request.Target ?? string.Empty;
            int query = path.IndexOf('?');
            string bare = query >= 0 ? path.Substring(0, query) : path;
            if (!(bare == "/mcp" || bare.StartsWith("/mcp/", StringComparison.Ordinal)))
            {
                return MiniHttpResponse.StatusOnly(404);
            }
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                return MiniHttpResponse.StatusOnly(405);
            }
            McpRequestInfo info = McpRequestInfo.Read(request.BodyText);
            if (!MiniHttp.IsAuthorized(request.Header("Authorization"), _token))
            {
                return MiniHttpResponse.Json(401, JsonRpcError(info.IdJson, -32001,
                    "Unauthorized: missing or invalid bearer token"));
            }

            long deadline = _nowMillis() + _holdMillis;
            long lastFailedGeneration = -1;
            long retryAt = 0;
            while (true)
            {
                long now = _nowMillis();
                long remaining = deadline - now;
                if (remaining <= 0)
                {
                    Log("hold expired: " + info.Describe());
                    return info.IsNotification
                        ? MiniHttpResponse.StatusOnly(202)
                        : NotRunResponse(info, _holdMillis);
                }
                long version = _gate.Version;
                McpBackend backend = _gate.Current;
                if (backend == null)
                {
                    _gate.WaitForChange(version, (int)Math.Min(remaining, 1000));
                    continue;
                }
                if (backend.Generation == lastFailedGeneration && now < retryAt)
                {
                    _gate.WaitForChange(version, (int)Math.Min(remaining, retryAt - now));
                    continue;
                }

                int timeout = (int)Math.Min(int.MaxValue, remaining + UnityDispatchBudgetMillis);
                McpForwardResult result;
                try
                {
                    result = _forward(backend, request, timeout);
                }
                catch (Exception ex)
                {
                    result = new McpForwardResult { Error = ex.GetType().Name + ": " + ex.Message };
                }
                McpOutcome outcome = Classify(result);
                switch (outcome)
                {
                    case McpOutcome.Response:
                        return PassThrough(result);
                    case McpOutcome.NotDelivered:
                    case McpOutcome.NotRun:
                        Log("forward " + outcome + " (" + info.Describe() + "); holding");
                        lastFailedGeneration = backend.Generation;
                        retryAt = _nowMillis() + RetryBackoffMillis;
                        continue;
                    default:
                        Log("forward outcome unknown (" + info.Describe() + "): " + (result == null ? "null" : result.Error));
                        return info.IsNotification
                            ? MiniHttpResponse.StatusOnly(202)
                            : UnknownOutcomeResponse(info);
                }
            }
        }

        private static MiniHttpResponse PassThrough(McpForwardResult result)
        {
            MiniHttpResponse response = new MiniHttpResponse
            {
                Status = result.Status,
                ContentType = result.ContentType,
                Body = result.Body ?? new byte[0],
            };
            if (!string.IsNullOrEmpty(result.McpSessionId))
            {
                response.Headers.Add(new KeyValuePair<string, string>("Mcp-Session-Id", result.McpSessionId));
            }
            return response;
        }

        public static string NotRunText(int holdMillis)
        {
            int seconds = (int)Math.Round(holdMillis / 1000.0);
            return "Unity is still reloading (waited " + seconds.ToString(CultureInfo.InvariantCulture)
                + " s); the call was NOT run and is safe to retry.";
        }

        public static string UnknownOutcomeText(string toolName)
        {
            return "Unity reloaded scripts while '" + (string.IsNullOrEmpty(toolName) ? "the tool" : toolName)
                + "' was running, so its outcome is unknown. Check the project state (and the Console) before calling it again.";
        }

        /// <summary>tools/call: an isError tool result; other methods: a JSON-RPC error (-32000) with the same text.</summary>
        public static MiniHttpResponse NotRunResponse(McpRequestInfo info, int holdMillis)
        {
            return MiniHttpResponse.Json(200, ErrorBody(info, NotRunText(holdMillis)));
        }

        public static MiniHttpResponse UnknownOutcomeResponse(McpRequestInfo info)
        {
            return MiniHttpResponse.Json(200, ErrorBody(info, UnknownOutcomeText(info.ToolName)));
        }

        private static string ErrorBody(McpRequestInfo info, string text)
        {
            if (info.Method == "tools/call")
            {
                string content = "[" + CliRelayJson.Obj().Str("type", "text").Str("text", text) + "]";
                string result = CliRelayJson.Obj().Raw("content", content).Bool("isError", true).ToString();
                return CliRelayJson.Obj().Str("jsonrpc", "2.0").Raw("id", info.IdJson).Raw("result", result).ToString();
            }
            return JsonRpcError(info.IdJson, -32000, text);
        }

        public static string JsonRpcError(string idJson, int code, string message)
        {
            string error = CliRelayJson.Obj().Num("code", code).Str("message", message).ToString();
            return CliRelayJson.Obj().Str("jsonrpc", "2.0").Raw("id", idJson).Raw("error", error).ToString();
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                try { _log(message); } catch (Exception) { }
            }
        }
    }

    /// <summary>The few JSON-RPC fields the MCP front reads from a request body.</summary>
    public sealed class McpRequestInfo
    {
        /// <summary>The raw JSON of "id" ("null" when absent or unreadable).</summary>
        public string IdJson = "null";
        public bool IsNotification;
        public string Method;
        public string ToolName;

        public string Describe()
        {
            return (Method ?? "?") + (ToolName != null ? " " + ToolName : string.Empty) + " id=" + IdJson;
        }

        public static McpRequestInfo Read(string body)
        {
            McpRequestInfo info = new McpRequestInfo();
            using (JsonDocument doc = CliRelayJson.TryParseObject(body))
            {
                if (doc == null)
                {
                    return info;
                }
                JsonElement root = doc.RootElement;
                JsonElement id;
                if (root.TryGetProperty("id", out id))
                {
                    info.IdJson = id.GetRawText();
                }
                else
                {
                    info.IsNotification = true;
                }
                info.Method = CliRelayJson.GetString(root, "method");
                JsonElement parameters;
                if (CliRelayJson.TryGetObject(root, "params", out parameters))
                {
                    info.ToolName = CliRelayJson.GetString(parameters, "name");
                }
            }
            return info;
        }
    }
}

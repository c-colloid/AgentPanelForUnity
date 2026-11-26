using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Colloid.AgentPanel.Tools.CliRelay
{
    /// <summary>
    /// Entry point of cli-relay (see CliRelayCore.cs for the protocol).
    /// UapStdioRelayBuilder compiles this file together with CliRelayCore.cs
    /// with Unity's bundled Roslyn and Unity starts it as
    /// <c>&lt;dotnet host&gt; exec cli-relay.dll --ready-file &lt;path&gt; --log &lt;path&gt;
    /// --unity-pid &lt;int&gt; --unity-start-ticks &lt;long&gt;</c>, with the two tokens
    /// in the environment only (UAP_RELAY_TOKEN, UAP_RELAY_MCP_TOKEN; SEC-2).
    ///
    /// Not compiled into ci/SmokeTests (real sockets, processes, threads);
    /// ci/RelayLive drives the real thing end to end.
    /// </summary>
    public static class CliRelayMain
    {
        public static int Main(string[] args)
        {
            string readyFile = null;
            string logPath = null;
            int unityPid = 0;
            long unityStartTicks = -1;
            for (int i = 0; args != null && i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--ready-file": readyFile = next; i++; break;
                    case "--log": logPath = next; i++; break;
                    case "--unity-pid":
                        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out unityPid)) { unityPid = 0; }
                        i++;
                        break;
                    case "--unity-start-ticks":
                        if (!long.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out unityStartTicks)) { unityStartTicks = -1; }
                        i++;
                        break;
                    default:
                        Console.Error.WriteLine("cli-relay: unknown argument " + a);
                        return CliRelayProtocol.ExitBadArguments;
                }
            }
            string ctlToken = Environment.GetEnvironmentVariable(CliRelayProtocol.EnvControlToken);
            string mcpToken = Environment.GetEnvironmentVariable(CliRelayProtocol.EnvMcpToken);
            if (string.IsNullOrEmpty(readyFile) || string.IsNullOrEmpty(logPath) || unityPid <= 0 || unityStartTicks < 0
                || string.IsNullOrEmpty(ctlToken) || string.IsNullOrEmpty(mcpToken))
            {
                Console.Error.WriteLine("cli-relay: usage: --ready-file <path> --log <path> --unity-pid <int> --unity-start-ticks <long>"
                    + " (env " + CliRelayProtocol.EnvControlToken + " and " + CliRelayProtocol.EnvMcpToken + " required)");
                return CliRelayProtocol.ExitBadArguments;
            }
            // The tokens never reach the CLI, whatever the spawn frame says.
            Environment.SetEnvironmentVariable(CliRelayProtocol.EnvControlToken, null);
            Environment.SetEnvironmentVariable(CliRelayProtocol.EnvMcpToken, null);

            CliRelayHost host = new CliRelayHost(readyFile, logPath, unityPid, unityStartTicks, ctlToken, mcpToken);
            return host.Run();
        }
    }

    /// <summary>Size-capped log file: past 1 MB it is truncated and restarted. Thread-safe.</summary>
    internal sealed class CliRelayLog
    {
        private const long MaxBytes = 1024 * 1024;
        private readonly object _lock = new object();
        private readonly string _path;
        private FileStream _stream;

        public CliRelayLog(string path)
        {
            _path = path;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception)
            {
                _stream = null;
            }
        }

        public void Write(string message)
        {
            lock (_lock)
            {
                if (_stream == null) { return; }
                try
                {
                    if (_stream.Length > MaxBytes)
                    {
                        _stream.SetLength(0);
                        WriteRaw("(log truncated at 1 MB)");
                    }
                    WriteRaw(message);
                }
                catch (Exception)
                {
                }
            }
        }

        private void WriteRaw(string message)
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                + " [" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "] " + message + "\n";
            byte[] bytes = CliRelayFrames.Utf8NoBom.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    /// <summary>One control-link connection: a reader thread and a sender thread with a bounded queue.</summary>
    internal sealed class ControlConnection
    {
        private const long MaxQueueBytes = 64L * 1024 * 1024;
        private const int MaxUnauthenticatedFrame = 64 * 1024;
        private const int MaxFrame = 256 * 1024 * 1024;

        private static int _nextId;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _queueLock = new object();
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private long _queuedBytes;
        private bool _closed;
        private bool _closeWhenDrained;
        private int _closedOnce;

        public ControlConnection(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            Id = Interlocked.Increment(ref _nextId);
        }

        public int Id { get; private set; }

        /// <summary>Set once the hello was accepted (either attach or protocol-mismatch mode).</summary>
        public volatile bool Authenticated;

        /// <summary>Protocol mismatch: only shutdown and ping are honoured.</summary>
        public volatile bool Restricted;

        public bool IsClosed { get { lock (_queueLock) { return _closed; } } }

        public void StartSender()
        {
            Thread t = new Thread(SendLoop) { IsBackground = true, Name = "cli-relay ctl send " + Id };
            t.Start();
        }

        /// <summary>Queues one frame. False when closed or when the queue overflowed (the connection is then dropped).</summary>
        public bool Enqueue(string frame)
        {
            byte[] bytes = CliRelayFrames.Encode(frame);
            lock (_queueLock)
            {
                if (_closed || _closeWhenDrained) { return false; }
                if (_queuedBytes + bytes.Length > MaxQueueBytes)
                {
                    CloseLocked();
                    return false;
                }
                _queue.Enqueue(bytes);
                _queuedBytes += bytes.Length;
                Monitor.PulseAll(_queueLock);
                return true;
            }
        }

        /// <summary>Waits until everything queued so far was written (or the time ran out).</summary>
        public void Flush(int millis)
        {
            Stopwatch sw = Stopwatch.StartNew();
            lock (_queueLock)
            {
                while (!_closed && _queue.Count > 0 && sw.ElapsedMilliseconds < millis)
                {
                    Monitor.Wait(_queueLock, 50);
                }
            }
        }

        public void CloseWhenDrained()
        {
            lock (_queueLock)
            {
                _closeWhenDrained = true;
                Monitor.PulseAll(_queueLock);
            }
        }

        public void Close()
        {
            lock (_queueLock)
            {
                CloseLocked();
            }
        }

        private void CloseLocked()
        {
            _closed = true;
            _queue.Clear();
            _queuedBytes = 0;
            Monitor.PulseAll(_queueLock);
            if (Interlocked.Exchange(ref _closedOnce, 1) == 0)
            {
                try { _client.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                try { _client.Close(); } catch (Exception) { }
            }
        }

        private void SendLoop()
        {
            while (true)
            {
                byte[] next;
                lock (_queueLock)
                {
                    while (!_closed && _queue.Count == 0 && !_closeWhenDrained)
                    {
                        Monitor.Wait(_queueLock);
                    }
                    if (_closed) { return; }
                    if (_queue.Count == 0)
                    {
                        // _closeWhenDrained and nothing left.
                        CloseLocked();
                        return;
                    }
                    next = _queue.Peek();
                }
                try
                {
                    _stream.Write(next, 0, next.Length);
                }
                catch (Exception)
                {
                    Close();
                    return;
                }
                lock (_queueLock)
                {
                    if (_queue.Count > 0)
                    {
                        _queue.Dequeue();
                        _queuedBytes -= next.Length;
                    }
                    Monitor.PulseAll(_queueLock);
                }
            }
        }

        /// <summary>Reads frames until EOF / error / close, calling <paramref name="onFrame"/> for each.</summary>
        public void ReadLoop(Action<string> onFrame)
        {
            byte[] chunk = new byte[64 * 1024];
            MemoryStream line = new MemoryStream();
            try
            {
                while (true)
                {
                    int read = _stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0) { return; }
                    int start = 0;
                    for (int i = 0; i < read; i++)
                    {
                        if (chunk[i] != (byte)'\n') { continue; }
                        line.Write(chunk, start, i - start);
                        start = i + 1;
                        string frame = CliRelayFrames.Utf8NoBom.GetString(line.GetBuffer(), 0, (int)line.Length);
                        line.SetLength(0);
                        onFrame(frame);
                        if (IsClosed) { return; }
                    }
                    line.Write(chunk, start, read - start);
                    int limit = Authenticated ? MaxFrame : MaxUnauthenticatedFrame;
                    if (line.Length > limit) { return; }
                }
            }
            catch (Exception)
            {
                // IOException / ObjectDisposedException on close: end of this connection.
            }
        }
    }

    /// <summary>The relay process: control link, CLI child, MCP front, watchdog.</summary>
    internal sealed class CliRelayHost
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long NowMs() { return Clock.ElapsedMilliseconds; }

        private readonly string _readyFile;
        private readonly int _unityPid;
        private long _unityBaselineTicks;
        private readonly string _ctlToken;
        private readonly string _mcpToken;
        private readonly CliRelayLog _log;
        private readonly int _holdMillis;

        // All of the following are guarded by _lock.
        private readonly object _lock = new object();
        private readonly CliRelayBuffer _buffer;
        private readonly CliRelayStdinGate _stdinGate = new CliRelayStdinGate();
        private readonly CliRelaySession _session;
        private ControlConnection _current;
        private Process _cli;
        private int _cliPid;
        private long _cliStartTicks;
        private bool _cliSpawned;
        private bool _cliExited;
        private int? _cliExitCode;

        // Lock order: _stdinLock, then _lock.
        private readonly object _stdinLock = new object();
        private Stream _cliStdin;

        private readonly CliRelayMcpGate _mcpGate = new CliRelayMcpGate();
        private CliRelayMcpFront _mcpFront;
        private HttpClient _http;

        private readonly ManualResetEventSlim _exitEvent = new ManualResetEventSlim(false);
        private int _exiting;
        private int _exitCode;

        private TcpListener _ctlListener;
        private TcpListener _mcpListener;

        private static readonly HttpRequestOptionsKey<StrongBox<bool>> ConnectedKey =
            new HttpRequestOptionsKey<StrongBox<bool>>("uap-relay-connected");

        public CliRelayHost(string readyFile, string logPath, int unityPid, long unityStartTicks, string ctlToken, string mcpToken)
        {
            _readyFile = readyFile;
            _unityPid = unityPid;
            _unityBaselineTicks = unityStartTicks;
            _ctlToken = ctlToken;
            _mcpToken = mcpToken;
            _log = new CliRelayLog(logPath);

            long leaseMillis = CliRelayProtocol.DefaultLeaseMillis;
            long leaseSeconds;
            if (long.TryParse(Environment.GetEnvironmentVariable(CliRelayProtocol.EnvLeaseSeconds), NumberStyles.Integer, CultureInfo.InvariantCulture, out leaseSeconds) && leaseSeconds > 0)
            {
                leaseMillis = leaseSeconds * 1000;
            }
            long maxBuffer = CliRelayProtocol.DefaultMaxBufferBytes;
            long envMax;
            if (long.TryParse(Environment.GetEnvironmentVariable(CliRelayProtocol.EnvMaxBufferBytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out envMax) && envMax > 0)
            {
                maxBuffer = envMax;
            }
            _holdMillis = CliRelayProtocol.DefaultHoldMillis;
            int envHold;
            if (int.TryParse(Environment.GetEnvironmentVariable(CliRelayProtocol.EnvHoldMillis), NumberStyles.Integer, CultureInfo.InvariantCulture, out envHold) && envHold > 0)
            {
                _holdMillis = envHold;
            }
            _buffer = new CliRelayBuffer(maxBuffer);
            _session = new CliRelaySession(leaseMillis, CliRelayProtocol.ParentCheckIntervalMillis, NowMs());
            Log("start: unityPid=" + unityPid + " unityStartTicks=" + unityStartTicks + " leaseMs=" + leaseMillis
                + " maxBuffer=" + maxBuffer + " holdMs=" + _holdMillis + " runtime=" + Environment.Version);
        }

        private void Log(string message)
        {
            _log.Write(message);
        }

        public int Run()
        {
            AppDomain.CurrentDomain.ProcessExit += delegate { KillCliTree("process exit"); };
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                RequestExit(CliRelayProtocol.ExitNormal, "signal");
            };

            // Baseline of Unity's start time, as THIS process reads it
            // (Mono and .NET may compute StartTime differently, so a value
            // passed by Unity is only honoured when non-zero, for tests).
            if (_unityBaselineTicks == 0)
            {
                _unityBaselineTicks = ReadStartTicks(_unityPid);
                Log("unity start-time baseline (own reading): " + _unityBaselineTicks + (_unityBaselineTicks == 0 ? " (pid existence only)" : string.Empty));
            }

            try
            {
                _ctlListener = new TcpListener(IPAddress.Loopback, 0);
                _ctlListener.Start();
                _mcpListener = new TcpListener(IPAddress.Loopback, 0);
                _mcpListener.Start();
            }
            catch (Exception ex)
            {
                Log("listen failed: " + ex.Message);
                return CliRelayProtocol.ExitBadArguments;
            }
            int ctlPort = ((IPEndPoint)_ctlListener.LocalEndpoint).Port;
            int mcpPort = ((IPEndPoint)_mcpListener.LocalEndpoint).Port;

            _http = CreateHttpClient();
            _mcpFront = new CliRelayMcpFront(_mcpToken, _mcpGate, Forward, NowMs, _holdMillis, Log);

            StartThread(CtlAcceptLoop, "cli-relay ctl accept");
            StartThread(McpAcceptLoop, "cli-relay mcp accept");
            StartThread(WatchdogLoop, "cli-relay watchdog");

            long selfStart = 0;
            try { using (Process self = Process.GetCurrentProcess()) { selfStart = self.StartTime.ToUniversalTime().Ticks; } } catch (Exception) { }
            string ready = CliRelayJson.Obj()
                .Num("protocol", CliRelayProtocol.Version)
                .Num("ctlPort", ctlPort)
                .Num("mcpPort", mcpPort)
                .Num("pid", Environment.ProcessId)
                .Num("startTicks", selfStart)
                .ToString();
            try
            {
                string full = Path.GetFullPath(_readyFile);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                string tmp = full + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(tmp, ready, CliRelayFrames.Utf8NoBom);
                File.Move(tmp, full, true);
            }
            catch (Exception ex)
            {
                Log("ready file failed: " + ex.Message);
                return CliRelayProtocol.ExitBadArguments;
            }
            Log("ready: ctlPort=" + ctlPort + " mcpPort=" + mcpPort);

            _exitEvent.Wait();
            Log("exit " + _exitCode);
            try { _ctlListener.Stop(); } catch (Exception) { }
            try { _mcpListener.Stop(); } catch (Exception) { }
            return _exitCode;
        }

        private static void StartThread(ThreadStart body, string name)
        {
            Thread t = new Thread(body) { IsBackground = true, Name = name };
            t.Start();
        }

        // ------------------------------------------------------------------
        // Exit paths
        // ------------------------------------------------------------------

        private void KillCliTree(string why)
        {
            Process cli;
            lock (_lock) { cli = _cli; }
            if (cli == null) { return; }
            try
            {
                if (!cli.HasExited)
                {
                    Log("killing CLI tree (" + why + ")");
                }
                cli.Kill(true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Kills the CLI tree, says bye to an attached Unity and ends Run() with <paramref name="code"/>.</summary>
        private void RequestExit(int code, string reason)
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) { return; }
            Log("exiting: " + reason + " (code " + code + ")");
            Thread t = new Thread(delegate()
            {
                KillCliTree(reason);
                FinishExit(code, reason);
            }) { IsBackground = true, Name = "cli-relay exit" };
            t.Start();
        }

        private void FinishExit(int code, string reason)
        {
            ControlConnection conn;
            lock (_lock)
            {
                conn = _current;
                if (conn != null)
                {
                    conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("bye").Str("reason", reason).ToString()));
                }
            }
            if (conn != null)
            {
                conn.Flush(1000);
            }
            _exitCode = code;
            _exitEvent.Set();
        }

        private void Shutdown(int graceMillis)
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) { return; }
            graceMillis = Math.Max(0, Math.Min(graceMillis, 60000));
            Log("shutdown requested, grace " + graceMillis + " ms");
            Thread t = new Thread(delegate()
            {
                // A write to a CLI that stopped reading holds _stdinLock
                // for good: do not wait for it longer than the grace.
                bool gotStdin = false;
                try
                {
                    Monitor.TryEnter(_stdinLock, Math.Max(100, graceMillis), ref gotStdin);
                    if (gotStdin && _cliStdin != null)
                    {
                        try { _cliStdin.Close(); } catch (Exception) { }
                        _cliStdin = null;
                    }
                }
                finally
                {
                    if (gotStdin) { Monitor.Exit(_stdinLock); }
                }
                Process cli;
                lock (_lock) { cli = _cli; }
                if (cli != null)
                {
                    try { cli.WaitForExit(graceMillis); } catch (Exception) { }
                }
                KillCliTree("shutdown");
                FinishExit(CliRelayProtocol.ExitNormal, "shutdown");
            }) { IsBackground = true, Name = "cli-relay shutdown" };
            t.Start();
        }

        // ------------------------------------------------------------------
        // Watchdog
        // ------------------------------------------------------------------

        private void WatchdogLoop()
        {
            while (!_exitEvent.IsSet)
            {
                Thread.Sleep(200);
                int code;
                lock (_lock)
                {
                    code = _session.Tick(NowMs(), IsUnityAlive);
                }
                if (code >= 0)
                {
                    string reason = code == CliRelayProtocol.ExitUnityGone ? "unity-gone"
                        : code == CliRelayProtocol.ExitLeaseExpired ? "lease-expired" : "cli-exited";
                    RequestExit(code, reason);
                    return;
                }
            }
        }

        private bool IsUnityAlive()
        {
            bool exists = false;
            try
            {
                using (Process p = Process.GetProcessById(_unityPid))
                {
                    exists = !IsZombie(_unityPid);
                }
            }
            catch (ArgumentException)
            {
                exists = false;
            }
            catch (InvalidOperationException)
            {
                exists = false;
            }
            long observed = exists && _unityBaselineTicks != 0 ? ReadStartTicks(_unityPid) : 0;
            bool alive = CliRelayAuth.IsParentAlive(exists, _unityBaselineTicks, observed);
            if (!alive)
            {
                Log("unity liveness check failed (pid " + _unityPid + (exists ? ", start time differs" : ", gone") + ")");
            }
            return alive;
        }

        private static long ReadStartTicks(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.StartTime.ToUniversalTime().Ticks;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Linux: a killed-but-unreaped process still has a pid; treat state 'Z' as gone.</summary>
        private static bool IsZombie(int pid)
        {
            try
            {
                string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
                if (!File.Exists(stat)) { return false; }
                string text = File.ReadAllText(stat);
                int close = text.LastIndexOf(')');
                return close >= 0 && close + 2 < text.Length && text[close + 2] == 'Z';
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Control link
        // ------------------------------------------------------------------

        private void CtlAcceptLoop()
        {
            while (!_exitEvent.IsSet)
            {
                TcpClient client;
                try
                {
                    client = _ctlListener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_exitEvent.IsSet) { return; }
                    Thread.Sleep(50);
                    continue;
                }
                ControlConnection conn;
                try
                {
                    conn = new ControlConnection(client);
                }
                catch (Exception)
                {
                    try { client.Close(); } catch (Exception) { }
                    continue;
                }
                Thread t = new Thread(delegate() { RunConnection(conn); }) { IsBackground = true, Name = "cli-relay ctl read" };
                t.Start();
            }
        }

        private void RunConnection(ControlConnection conn)
        {
            conn.StartSender();
            using (Timer helloTimer = new Timer(delegate
            {
                if (!conn.Authenticated)
                {
                    Log("conn " + conn.Id + ": no hello within " + CliRelayProtocol.HelloTimeoutMillis + " ms, closing");
                    conn.Close();
                }
            }, null, CliRelayProtocol.HelloTimeoutMillis, Timeout.Infinite))
            {
                conn.ReadLoop(delegate(string frame) { OnFrame(conn, frame); });
            }
            conn.Close();
            OnConnectionClosed(conn);
        }

        private void OnConnectionClosed(ControlConnection conn)
        {
            lock (_lock)
            {
                if (_current == conn)
                {
                    Log("conn " + conn.Id + ": closed while attached -> detached");
                    MarkDetachedLocked();
                }
            }
        }

        private void MarkDetachedLocked()
        {
            _current = null;
            _session.OnDetached(NowMs());
            _mcpGate.Clear();
        }

        private void OnFrame(ControlConnection conn, string frameText)
        {
            if (!conn.Authenticated)
            {
                JsonElement hello;
                JsonDocument doc;
                CliRelayAuth.HelloResult result = CliRelayAuth.EvaluateHello(frameText, _ctlToken, out hello, out doc);
                using (doc)
                {
                    switch (result)
                    {
                        case CliRelayAuth.HelloResult.Accept:
                            conn.Authenticated = true;
                            Attach(conn, hello);
                            return;
                        case CliRelayAuth.HelloResult.ProtocolMismatch:
                            conn.Authenticated = true;
                            conn.Restricted = true;
                            Log("conn " + conn.Id + ": hello with protocol " + CliRelayJson.GetLong(hello, "protocol", -1) + ", restricted to shutdown");
                            conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("mismatch").Num("protocol", CliRelayProtocol.Version).ToString()));
                            return;
                        default:
                            Log("conn " + conn.Id + ": rejected (no valid hello)");
                            conn.Close();
                            return;
                    }
                }
            }

            CliRelayFrame frame;
            if (!CliRelayFrames.TryParse(frameText, out frame))
            {
                Log("conn " + conn.Id + ": unparseable frame ignored: " + CliRelayFrames.Preview(frameText));
                return;
            }
            if (frame.Tag == 'I')
            {
                if (!conn.Restricted) { HandleInput(conn, frame); }
                return;
            }
            if (frame.Tag != 'C')
            {
                Log("conn " + conn.Id + ": unexpected frame tag " + frame.Tag);
                return;
            }
            using (JsonDocument doc = CliRelayJson.TryParseObject(frame.Payload))
            {
                if (doc == null)
                {
                    Log("conn " + conn.Id + ": control frame is not a JSON object");
                    return;
                }
                JsonElement root = doc.RootElement;
                string type = CliRelayJson.GetString(root, "type");
                if (type == "shutdown")
                {
                    Shutdown((int)Math.Min(int.MaxValue, Math.Max(0, CliRelayJson.GetLong(root, "graceMillis", 0))));
                    return;
                }
                if (type == "ping")
                {
                    conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("pong").ToString()));
                    return;
                }
                if (conn.Restricted)
                {
                    return;
                }
                lock (_lock)
                {
                    if (_current != conn)
                    {
                        // A replaced connection: nothing it says counts any more.
                        return;
                    }
                }
                switch (type)
                {
                    case "spawn":
                        Spawn(conn, root);
                        break;
                    case "ack":
                        lock (_lock) { _buffer.Ack(ClampInt(CliRelayJson.GetLong(root, "resultsProcessed", 0))); }
                        break;
                    case "backend":
                        {
                            string url = CliRelayJson.GetString(root, "url");
                            string token = CliRelayJson.GetString(root, "token");
                            lock (_lock)
                            {
                                if (_current == conn && !string.IsNullOrEmpty(url))
                                {
                                    _mcpGate.SetBackend(url, token);
                                    Log("backend registered: " + url);
                                }
                            }
                            break;
                        }
                    case "detach":
                        lock (_lock)
                        {
                            _buffer.Ack(ClampInt(CliRelayJson.GetLong(root, "resultsProcessed", 0)));
                            if (_current == conn)
                            {
                                Log("conn " + conn.Id + ": detach (resultsProcessed " + _buffer.ResultsAcked + ")");
                                MarkDetachedLocked();
                            }
                        }
                        conn.CloseWhenDrained();
                        break;
                    default:
                        Log("conn " + conn.Id + ": unknown control frame type '" + type + "' ignored");
                        break;
                }
            }
        }

        private static int ClampInt(long value)
        {
            return (int)Math.Max(0, Math.Min(int.MaxValue, value));
        }

        private void Attach(ControlConnection conn, JsonElement hello)
        {
            int resultsProcessed = ClampInt(CliRelayJson.GetLong(hello, "resultsProcessed", 0));
            long helloPid = CliRelayJson.GetLong(hello, "unityPid", 0);
            if (helloPid != 0 && helloPid != _unityPid)
            {
                Log("conn " + conn.Id + ": hello unityPid " + helloPid + " differs from --unity-pid " + _unityPid + " (watchdog keeps --unity-pid)");
            }
            lock (_lock)
            {
                ControlConnection old = _current;
                if (old != null && old != conn)
                {
                    Log("conn " + old.Id + ": replaced by conn " + conn.Id);
                    old.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("bye").Str("reason", "replaced").ToString()));
                    old.CloseWhenDrained();
                }
                _buffer.Ack(resultsProcessed);
                bool cliRunning = _cliSpawned && !_cliExited;
                string welcome = CliRelayJson.Ctl("welcome")
                    .Num("protocol", CliRelayProtocol.Version)
                    .Num("cliPid", _cliPid)
                    .Num("cliStartTicks", _cliStartTicks)
                    .Bool("cliSpawned", _cliSpawned)
                    .Bool("cliRunning", cliRunning)
                    .NumOrNull("cliExitCode", _cliExitCode)
                    .Bool("replayComplete", _buffer.ReplayComplete)
                    .Num("mcpPort", ((IPEndPoint)_mcpListener.LocalEndpoint).Port)
                    .Num("lastInSeq", _stdinGate.LastInSeq)
                    .Num("resultsSeen", _buffer.ResultsSeen)
                    .ToString();
                conn.Enqueue(CliRelayFrames.Ctl(welcome));
                List<string> replay = _buffer.Snapshot();
                foreach (string line in replay)
                {
                    conn.Enqueue(CliRelayFrames.Out(line));
                }
                conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("live").ToString()));
                _current = conn;
                _session.OnAttached(NowMs());
                // The previous domain's UapOps endpoint is gone; hold MCP calls until `backend`.
                _mcpGate.Clear();
                if (_cliExited)
                {
                    conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("exited").NumOrNull("code", _cliExitCode).ToString()));
                    _session.OnCliExitReported(NowMs());
                }
                Log("conn " + conn.Id + ": attached (resultsProcessed " + resultsProcessed + ", replay " + replay.Count
                    + " lines, replayComplete " + _buffer.ReplayComplete + ", cliRunning " + cliRunning + ")");
            }
        }

        private void HandleInput(ControlConnection conn, CliRelayFrame frame)
        {
            lock (_stdinLock)
            {
                string reason = null;
                StdinDecision decision;
                lock (_lock)
                {
                    if (_current != conn) { return; }
                    if (!_cliSpawned || _cliExited || _cliStdin == null)
                    {
                        decision = StdinDecision.Reject;
                        reason = "cli not running";
                    }
                    else
                    {
                        decision = _stdinGate.Check(frame.Seq, frame.Payload, out reason);
                        if (decision == StdinDecision.Write)
                        {
                            _buffer.OnStdin(frame.Payload);
                        }
                    }
                }
                if (decision == StdinDecision.Duplicate)
                {
                    Log("I " + frame.Seq + " ignored (duplicate, last " + _stdinGate.LastInSeq + ")");
                    return;
                }
                if (decision == StdinDecision.Reject)
                {
                    Log("I " + frame.Seq + " rejected (" + reason + "): " + CliRelayFrames.Preview(frame.Payload));
                    conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("rejected").Num("seq", frame.Seq).Str("reason", reason).ToString()));
                    return;
                }
                Log("I " + frame.Seq + ": " + CliRelayFrames.Preview(frame.Payload));
                try
                {
                    byte[] bytes = CliRelayFrames.Encode(frame.Payload);
                    _cliStdin.Write(bytes, 0, bytes.Length);
                    _cliStdin.Flush();
                }
                catch (Exception ex)
                {
                    Log("stdin write failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // The CLI child
        // ------------------------------------------------------------------

        private void Spawn(ControlConnection conn, JsonElement spawn)
        {
            lock (_stdinLock)
            {
                lock (_lock)
                {
                    if (_cliSpawned)
                    {
                        conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("spawnFailed").Str("error", "already spawned (a relay runs one CLI)").ToString()));
                        return;
                    }
                    string exe = CliRelayJson.GetString(spawn, "exe");
                    string argsFile = CliRelayJson.GetString(spawn, "argsFile");
                    string cwd = CliRelayJson.GetString(spawn, "cwd");
                    Process p;
                    try
                    {
                        if (string.IsNullOrEmpty(exe)) { throw new ArgumentException("spawn: exe is missing"); }
                        string arguments = string.IsNullOrEmpty(argsFile) ? string.Empty : File.ReadAllText(argsFile, Encoding.UTF8);
                        ProcessStartInfo psi = new ProcessStartInfo(exe, arguments)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardOutputEncoding = CliRelayFrames.Utf8NoBom,
                            StandardErrorEncoding = CliRelayFrames.Utf8NoBom,
                            StandardInputEncoding = CliRelayFrames.Utf8NoBom,
                        };
                        if (!string.IsNullOrEmpty(cwd)) { psi.WorkingDirectory = cwd; }
                        JsonElement removeList;
                        if (spawn.TryGetProperty("envRemove", out removeList) && removeList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement name in removeList.EnumerateArray())
                            {
                                if (name.ValueKind == JsonValueKind.String) { psi.Environment.Remove(name.GetString()); }
                            }
                        }
                        JsonElement setMap;
                        if (spawn.TryGetProperty("envSet", out setMap) && setMap.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty prop in setMap.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String) { psi.Environment[prop.Name] = prop.Value.GetString(); }
                            }
                        }
                        psi.Environment.Remove(CliRelayProtocol.EnvControlToken);
                        psi.Environment.Remove(CliRelayProtocol.EnvMcpToken);
                        p = Process.Start(psi);
                        if (p == null) { throw new InvalidOperationException("Process.Start returned null"); }
                    }
                    catch (Exception ex)
                    {
                        Log("spawn failed: " + ex.GetType().Name + ": " + ex.Message);
                        conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("spawnFailed").Str("error", ex.Message).ToString()));
                        return;
                    }
                    _cli = p;
                    _cliSpawned = true;
                    _cliPid = p.Id;
                    try { _cliStartTicks = p.StartTime.ToUniversalTime().Ticks; } catch (Exception) { _cliStartTicks = 0; }
                    _cliStdin = p.StandardInput.BaseStream;
                    Log("spawned CLI pid " + _cliPid + ": " + exe);
                    conn.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("spawned").Num("cliPid", _cliPid).Num("cliStartTicks", _cliStartTicks).ToString()));

                    StreamReader stdout = p.StandardOutput;
                    StreamReader stderr = p.StandardError;
                    Thread errThread = new Thread(delegate() { PumpStderr(stderr); }) { IsBackground = true, Name = "cli-relay stderr" };
                    errThread.Start();
                    Thread outThread = new Thread(delegate() { PumpStdout(p, stdout, errThread); }) { IsBackground = true, Name = "cli-relay stdout" };
                    outThread.Start();
                }
            }
        }

        private void PumpStdout(Process p, StreamReader stdout, Thread errThread)
        {
            try
            {
                string line;
                while ((line = stdout.ReadLine()) != null)
                {
                    lock (_lock)
                    {
                        _buffer.OnStdout(line);
                        if (_current != null)
                        {
                            _current.Enqueue(CliRelayFrames.Out(line));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("stdout read failed: " + ex.Message);
            }
            int? code = null;
            try
            {
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (Exception)
            {
            }
            errThread.Join(2000);
            lock (_lock)
            {
                _cliExited = true;
                _cliExitCode = code;
                Log("CLI exited with code " + (code.HasValue ? code.Value.ToString(CultureInfo.InvariantCulture) : "?"));
                if (_current != null)
                {
                    _current.Enqueue(CliRelayFrames.Ctl(CliRelayJson.Ctl("exited").NumOrNull("code", code).ToString()));
                    _session.OnCliExitReported(NowMs());
                }
            }
        }

        private void PumpStderr(StreamReader stderr)
        {
            try
            {
                string line;
                while ((line = stderr.ReadLine()) != null)
                {
                    lock (_lock)
                    {
                        if (_current != null)
                        {
                            _current.Enqueue(CliRelayFrames.Err(line));
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // MCP front
        // ------------------------------------------------------------------

        private void McpAcceptLoop()
        {
            while (!_exitEvent.IsSet)
            {
                TcpClient client;
                try
                {
                    client = _mcpListener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_exitEvent.IsSet) { return; }
                    Thread.Sleep(50);
                    continue;
                }
                Thread t = new Thread(delegate() { ServeMcp(client); }) { IsBackground = true, Name = "cli-relay mcp" };
                t.Start();
            }
        }

        private void ServeMcp(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                NetworkStream stream = client.GetStream();
                MiniHttpRequest request;
                int status = MiniHttp.ReadRequest(stream, MiniHttp.MaxHeaderBytes, MiniHttp.MaxBodyBytes, out request);
                if (status < 0) { return; }
                MiniHttpResponse response = status == 0 ? _mcpFront.Handle(request) : MiniHttpResponse.StatusOnly(status);
                byte[] bytes = MiniHttp.FormatResponse(response);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                Log("mcp front connection error: " + ex.GetType().Name);
            }
            finally
            {
                try { client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                try { client.Close(); } catch (Exception) { }
            }
        }

        private static HttpClient CreateHttpClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async delegate(SocketsHttpConnectionContext context, CancellationToken token)
                {
                    Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        socket.Dispose();
                        throw;
                    }
                    StrongBox<bool> connected;
                    if (context.InitialRequestMessage != null
                        && context.InitialRequestMessage.Options.TryGetValue(ConnectedKey, out connected))
                    {
                        connected.Value = true;
                    }
                    return new NetworkStream(socket, true);
                },
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>One POST to Unity's UapOps endpoint. Never throws.</summary>
        private McpForwardResult Forward(McpBackend backend, MiniHttpRequest request, int timeoutMillis)
        {
            StrongBox<bool> connected = new StrongBox<bool>(false);
            McpForwardResult result = new McpForwardResult();
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMillis))
                using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, backend.Url + request.Target))
                {
                    message.Options.Set(ConnectedKey, connected);
                    message.Headers.ConnectionClose = true;
                    message.Content = new ByteArrayContent(request.Body ?? new byte[0]);
                    string contentType = request.Header("Content-Type");
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", string.IsNullOrEmpty(contentType) ? "application/json" : contentType);
                    CopyHeader(request, message, "Accept");
                    CopyHeader(request, message, "MCP-Protocol-Version");
                    CopyHeader(request, message, "Mcp-Session-Id");
                    if (!string.IsNullOrEmpty(backend.Token))
                    {
                        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + backend.Token);
                    }
                    using (HttpResponseMessage response = _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cts.Token).GetAwaiter().GetResult())
                    {
                        result.Status = (int)response.StatusCode;
                        result.Body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        result.ContentType = response.Content.Headers.ContentType != null ? response.Content.Headers.ContentType.ToString() : null;
                        IEnumerable<string> values;
                        if (response.Headers.TryGetValues("Mcp-Session-Id", out values))
                        {
                            foreach (string v in values) { result.McpSessionId = v; break; }
                        }
                        if (response.Headers.TryGetValues("X-Uap-Not-Run", out values))
                        {
                            foreach (string v in values) { if (v.Trim() == "1") { result.NotRunHeader = true; } }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (connected.Value)
                {
                    result.Error = ex.GetType().Name + ": " + ex.Message;
                }
                else
                {
                    result.NotDelivered = true;
                    result.Error = null;
                }
            }
            return result;
        }

        private static void CopyHeader(MiniHttpRequest request, HttpRequestMessage message, string name)
        {
            string value = request.Header(name);
            if (!string.IsNullOrEmpty(value))
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}

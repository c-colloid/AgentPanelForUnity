using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Where a running CLI relay can be reached, and the two secrets that
    /// go with it. Kept across a domain reload (SessionStateBridge.
    /// RelayAttachJson) so the next domain can attach to the same relay.
    /// </summary>
    public sealed class RelayEndpoint
    {
        public int CtlPort;
        public int McpPort;
        public string CtlToken;
        public string McpToken;
        public int RelayPid;
        /// <summary>The relay's start time as THIS runtime reads it (ZombieReaper compares like with like).</summary>
        public long RelayStartTicks;
        /// <summary>The CLI the relay runs, or 0 before the spawn.</summary>
        public int CliPid;
    }

    /// <summary>What Launch needs to start a relay process.</summary>
    public sealed class RelayLaunchOptions
    {
        public string DotnetHost;
        public string RelayDll;
        /// <summary>Folder for the ready file, the args file and the relay log (created when missing).</summary>
        public string RunDirectory;
        public int UnityPid;
        public string CtlToken;
        public string McpToken;
        public int ReadyTimeoutMillis = RelayCliTransport.DefaultReadyTimeoutMillis;
    }

    /// <summary>
    /// ICliTransport over the CLI relay (design note docs/design-notes/
    /// 2026-11-25-cli-relay.md). The CLI is a child of a small relay
    /// process that outlives Unity's domain reloads; this class is only
    /// the editor's end of a loopback TCP link to it:
    ///
    ///   relay -> Unity   "O line" (CLI stdout)  "E line" (CLI stderr)
    ///                    "C json" (control: welcome, spawned, live, exited...)
    ///   Unity -> relay   "I n line" (CLI stdin, n = running number)
    ///                    "C json" (hello, spawn, ack, backend, detach, shutdown)
    ///
    /// Every frame is one UTF-8 line. Two ways to get an instance:
    /// <see cref="Launch"/> starts a relay (the CLI itself is spawned by
    /// <see cref="Start"/>, as with any transport), <see cref="Attach"/>
    /// connects to a relay a previous domain left running -- its CLI is
    /// already up and the relay replays the open turn's lines.
    ///
    /// Engine-free on purpose (no UnityEngine/UnityEditor): the smoke
    /// tests drive it against a real relay over loopback.
    ///
    /// Threading: receive runs on socket callbacks (no blocking reader
    /// thread to survive a domain unload); they only enqueue lines and
    /// set flags. Everything else is called from the main thread.
    /// </summary>
    public sealed class RelayCliTransport : ICliTransport, IDetachableCliTransport
    {
        public const int ProtocolVersion = 1;
        public const int DefaultReadyTimeoutMillis = 8000;
        public const int HandshakeTimeoutMillis = 3000;
        public const int SpawnTimeoutMillis = 15000;
        public const string TokenEnvVar = "UAP_RELAY_TOKEN";
        public const string McpTokenEnvVar = "UAP_RELAY_MCP_TOKEN";
        /// <summary>Per-call budget the CLI is asked to give MCP tools (covers a held call across a reload).</summary>
        public const string McpToolTimeoutMillis = "120000";

        private readonly IProcessKiller _killer;
        private readonly Action<string> _log;
        private readonly object _sendLock = new object();
        private readonly object _stateLock = new object();
        private readonly LineChannel _output = new LineChannel();
        private readonly LineChannel _errorOutput = new LineChannel();
        private readonly ManualResetEventSlim _spawnEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _closedEvent = new ManualResetEventSlim(false);
        private readonly byte[] _receiveBuffer = new byte[64 * 1024];
        private readonly MemoryStream _pending = new MemoryStream();

        private Socket _socket;
        private RelayEndpoint _endpoint;
        private string _runDirectory;
        private volatile bool _linkOpen;
        private volatile bool _detached;
        private volatile bool _cliRunning;
        private volatile bool _stopping;
        // The relay closed the link (or went away): its pids are no longer ours to kill.
        private volatile bool _remoteClosed;
        private bool _exitedRaised;
        private bool _started;
        private string _spawnError;
        private long _inSeq;
        private int _resultsProcessed;

        private RelayCliTransport(IProcessKiller killer, Action<string> log)
        {
            _killer = killer;
            _log = log;
        }

        // -- ICliTransport ---------------------------------------------------------

        public bool IsRunning
        {
            get { return _linkOpen && _cliRunning; }
        }

        /// <summary>The RELAY's pid: it is what the orphan reaper has to find and kill.</summary>
        public int ProcessId
        {
            get { return _endpoint != null ? _endpoint.RelayPid : -1; }
        }

        public long ProcessStartTimeUtcTicks
        {
            get { return _endpoint != null ? _endpoint.RelayStartTicks : 0; }
        }

        public LineChannel Output
        {
            get { return _output; }
        }

        public LineChannel ErrorOutput
        {
            get { return _errorOutput; }
        }

        public event Action Exited;

        // -- Relay-specific surface --------------------------------------------------

        /// <summary>Ports, tokens and pids of the relay this transport talks to.</summary>
        public RelayEndpoint Endpoint
        {
            get { return _endpoint; }
        }

        /// <summary>Turn results the client has finished with (see <see cref="NoteResultProcessed"/>).</summary>
        public int ResultsProcessed
        {
            get { return _resultsProcessed; }
        }

        /// <summary>True while the link is up (false after Detach, Stop, Kill or a lost connection).</summary>
        public bool IsLinkOpen
        {
            get { return _linkOpen; }
        }

        /// <summary>Attach only: true once the relay has finished replaying the lines it had kept.</summary>
        public bool ReplayFinished { get; private set; }

        /// <summary>Why the last Launch/Attach failed (for the log and the one-line note).</summary>
        public static string LastFailure { get; private set; }

        /// <summary>
        /// Starts a relay process and connects to it. The CLI is not
        /// running yet: AgentClient.Start calls <see cref="Start"/> for
        /// that. Returns null (reason in <see cref="LastFailure"/>, relay
        /// killed) when any step fails, so the caller can fall back to
        /// the ordinary in-process transport.
        /// </summary>
        public static RelayCliTransport Launch(RelayLaunchOptions options, IProcessKiller killer, Action<string> log)
        {
            LastFailure = null;
            int relayPid = -1;
            string readyFile = null;
            try
            {
                Directory.CreateDirectory(options.RunDirectory);
                string stamp = Guid.NewGuid().ToString("N");
                readyFile = Path.Combine(options.RunDirectory, stamp + ".ready.json");
                string logFile = Path.Combine(options.RunDirectory, "relay.log");
                var psi = new ProcessStartInfo
                {
                    FileName = options.DotnetHost,
                    Arguments = "exec " + Quote(options.RelayDll)
                        + " --ready-file " + Quote(readyFile)
                        + " --log " + Quote(logFile)
                        + " --unity-pid " + options.UnityPid
                        // 0 = the relay reads this editor's start time itself and
                        // keeps comparing against its own reading (two runtimes
                        // do not agree on a process start time to the tick).
                        + " --unity-start-ticks 0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables[TokenEnvVar] = options.CtlToken;
                psi.EnvironmentVariables[McpTokenEnvVar] = options.McpToken;

                long relayStartTicks = 0;
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process == null)
                    {
                        LastFailure = "the relay process did not start";
                        return null;
                    }
                    relayPid = process.Id;
                    try
                    {
                        relayStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (Exception)
                    {
                        // Start time unknown: the reaper then matches on pid + name only.
                    }

                    JsonNode ready = WaitForReadyFile(readyFile, options.ReadyTimeoutMillis, process);
                    if (ready == null)
                    {
                        LastFailure = "the relay did not report ready within "
                            + options.ReadyTimeoutMillis + " ms";
                        KillQuietly(killer, relayPid);
                        return null;
                    }
                    var endpoint = new RelayEndpoint
                    {
                        CtlPort = ready["ctlPort"].AsInt(),
                        McpPort = ready["mcpPort"].AsInt(),
                        CtlToken = options.CtlToken,
                        McpToken = options.McpToken,
                        RelayPid = relayPid,
                        RelayStartTicks = relayStartTicks
                    };
                    var transport = new RelayCliTransport(killer, log);
                    transport._runDirectory = options.RunDirectory;
                    JsonNode welcome;
                    string error;
                    if (!transport.Connect(endpoint, 0, options.UnityPid, out welcome, out error))
                    {
                        LastFailure = error;
                        transport.CloseSocket();
                        KillQuietly(killer, relayPid);
                        return null;
                    }
                    transport.BeginReceiving();
                    return transport;
                }
            }
            catch (Exception ex)
            {
                LastFailure = ex.Message;
                if (relayPid > 0)
                {
                    KillQuietly(killer, relayPid);
                }
                return null;
            }
            finally
            {
                DeleteQuietly(readyFile);
            }
        }

        /// <summary>
        /// Connects to a relay a previous domain detached from. Succeeds
        /// only when the relay still runs the CLI and kept every line
        /// since result number <paramref name="resultsProcessed"/>; it
        /// then replays those lines into <see cref="Output"/>. On failure
        /// returns null with <see cref="LastFailure"/> set, having told
        /// the relay to shut down when it could be reached at all.
        /// </summary>
        public static RelayCliTransport Attach(RelayEndpoint endpoint, int resultsProcessed, int unityPid,
            string runDirectory, IProcessKiller killer, Action<string> log)
        {
            LastFailure = null;
            var transport = new RelayCliTransport(killer, log);
            transport._runDirectory = runDirectory;
            try
            {
                JsonNode welcome;
                string error;
                if (!transport.Connect(endpoint, resultsProcessed, unityPid, out welcome, out error))
                {
                    LastFailure = error;
                    transport.CloseSocket();
                    return null;
                }
                string refusal = null;
                if (!welcome["cliRunning"].AsBool())
                {
                    refusal = "the CLI exited while Unity was reloading (code "
                        + welcome["cliExitCode"].AsInt(-1) + ")";
                }
                else if (!welcome["replayComplete"].AsBool())
                {
                    refusal = "the relay could not keep everything the CLI printed during the reload";
                }
                if (refusal != null)
                {
                    LastFailure = refusal;
                    transport.ShutdownRelayAndWait(1500);
                    transport.CloseSocket();
                    return null;
                }
                transport._resultsProcessed = resultsProcessed;
                transport._inSeq = welcome["lastInSeq"].AsLong();
                transport._cliRunning = true;
                transport._started = true;
                int cliPid = welcome["cliPid"].AsInt();
                if (cliPid > 0)
                {
                    endpoint.CliPid = cliPid;
                }
                transport.BeginReceiving();
                return transport;
            }
            catch (Exception ex)
            {
                LastFailure = ex.Message;
                transport.CloseSocket();
                return null;
            }
        }

        /// <summary>
        /// Asks the relay to spawn the CLI. Same contract as
        /// ClaudeCliProcess.Start: throws when the spawn fails. The
        /// argument string goes through a file, never the relay's own
        /// command line (length limits; and nothing secret is in it).
        /// </summary>
        public void Start(string executablePath, string arguments, string workingDirectory,
            string subagentModel = null, ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto)
        {
            if (_started)
            {
                throw new InvalidOperationException("CLI process already started.");
            }
            if (string.IsNullOrEmpty(executablePath))
            {
                throw new ArgumentException("executablePath must be non-empty.", "executablePath");
            }
            if (!_linkOpen)
            {
                throw new InvalidOperationException("The CLI relay is not connected.");
            }
            _started = true;

            string argsFile = Path.Combine(_runDirectory, Guid.NewGuid().ToString("N") + ".args");
            File.WriteAllText(argsFile, arguments ?? string.Empty, new UTF8Encoding(false));

            JsonNode envRemove = JsonNode.NewArray();
            foreach (string name in ClaudeCliProcess.ComputeEnvVarsToRemove(claudeAuth))
            {
                envRemove.Add(name);
            }
            envRemove.Add(TokenEnvVar);
            envRemove.Add(McpTokenEnvVar);
            JsonNode envSet = JsonNode.NewObject();
            KeyValuePair<string, string>[] subagentEnv = ClaudeCliProcess.ComputeSubagentModelEnvEntries(subagentModel);
            for (int i = 0; i < subagentEnv.Length; i++)
            {
                envSet.Set(subagentEnv[i].Key, subagentEnv[i].Value);
            }
            envSet.Set("MCP_TOOL_TIMEOUT", McpToolTimeoutMillis);

            _spawnEvent.Reset();
            _spawnError = null;
            bool sent = SendControl(JsonNode.NewObject()
                .Set("type", "spawn")
                .Set("exe", executablePath)
                .Set("argsFile", argsFile)
                .Set("cwd", workingDirectory ?? string.Empty)
                .Set("envRemove", envRemove)
                .Set("envSet", envSet));
            try
            {
                if (!sent)
                {
                    throw new InvalidOperationException("The CLI relay connection was lost before the spawn.");
                }
                if (!_spawnEvent.Wait(SpawnTimeoutMillis))
                {
                    throw new InvalidOperationException("The CLI relay did not answer the spawn request.");
                }
                if (_spawnError != null)
                {
                    throw new InvalidOperationException("Failed to start CLI process: " + _spawnError);
                }
            }
            finally
            {
                DeleteQuietly(argsFile);
            }
        }

        public bool WriteLine(string serializedJsonLine)
        {
            if (serializedJsonLine == null || !_linkOpen)
            {
                return false;
            }
            lock (_sendLock)
            {
                // The number lets the relay drop a line it already wrote
                // to the CLI; it keeps counting across re-attaches.
                long n = _inSeq + 1;
                if (!SendRawLocked("I " + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " " + serializedJsonLine))
                {
                    return false;
                }
                _inSeq = n;
                return true;
            }
        }

        /// <summary>
        /// Not offered through the relay: the CLI's stdin belongs to the
        /// relay, which closes it as part of <see cref="Stop"/>.
        /// </summary>
        public void CloseStdin()
        {
        }

        public void Stop(string interruptLine, int graceMillis)
        {
            if (_detached)
            {
                return;
            }
            _stopping = true;
            try
            {
                if (_linkOpen)
                {
                    if (interruptLine != null)
                    {
                        WriteLine(interruptLine);
                    }
                    SendControl(JsonNode.NewObject()
                        .Set("type", "shutdown")
                        .Set("graceMillis", Math.Max(0, graceMillis)));
                    // The relay closes the link once the CLI is gone.
                    _closedEvent.Wait(Math.Max(0, graceMillis) + 500);
                }
            }
            catch (Exception)
            {
            }
            Kill();
        }

        public void Kill()
        {
            _stopping = true;
            RelayEndpoint endpoint = _endpoint;
            bool cliWasRunning = _cliRunning;
            bool relayGone = _remoteClosed;
            CloseSocket();
            _cliRunning = false;
            if (endpoint == null || _detached || relayGone)
            {
                // A relay that closed the link has ended (it ends its CLI
                // first). Its pids may belong to other processes by the
                // time a later Stop gets here, so they are left alone.
                return;
            }
            // CLI first: killing only the relay would leave the CLI to
            // notice its closed stdin, which it does at the end of a turn.
            if (cliWasRunning && endpoint.CliPid > 0)
            {
                KillQuietly(_killer, endpoint.CliPid);
            }
            KillQuietly(_killer, endpoint.RelayPid);
        }

        public void Dispose()
        {
            if (_detached)
            {
                CloseSocket();
                return;
            }
            Kill();
        }

        // -- IDetachableCliTransport -------------------------------------------------

        public void Detach()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            try
            {
                SendControl(JsonNode.NewObject()
                    .Set("type", "detach")
                    .Set("resultsProcessed", _resultsProcessed));
            }
            catch (Exception)
            {
            }
            CloseSocket();
        }

        public void NoteResultProcessed()
        {
            _resultsProcessed++;
            if (_linkOpen)
            {
                SendControl(JsonNode.NewObject()
                    .Set("type", "ack")
                    .Set("resultsProcessed", _resultsProcessed));
            }
        }

        /// <summary>
        /// Tells the relay where this domain's UapOps server listens. The
        /// CLI's MCP calls arrive at the relay's own fixed port; it
        /// forwards them here and holds them while no backend is set.
        /// </summary>
        public bool SetMcpBackend(int port, string token)
        {
            return SendControl(JsonNode.NewObject()
                .Set("type", "backend")
                .Set("url", "http://127.0.0.1:" + port)
                .Set("token", token ?? string.Empty));
        }

        // -- Link ------------------------------------------------------------------

        private bool Connect(RelayEndpoint endpoint, int resultsProcessed, int unityPid,
            out JsonNode welcome, out string error)
        {
            welcome = null;
            error = null;
            _endpoint = endpoint;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            _socket = socket;
            IAsyncResult pending = socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, endpoint.CtlPort), null, null);
            if (!pending.AsyncWaitHandle.WaitOne(HandshakeTimeoutMillis))
            {
                error = "the relay did not accept a connection";
                return false;
            }
            try
            {
                socket.EndConnect(pending);
            }
            catch (Exception ex)
            {
                error = "the relay is not reachable (" + ex.Message + ")";
                return false;
            }
            socket.SendTimeout = 5000;
            _linkOpen = true;
            if (!SendControl(JsonNode.NewObject()
                    .Set("type", "hello")
                    .Set("token", endpoint.CtlToken ?? string.Empty)
                    .Set("protocol", ProtocolVersion)
                    .Set("resultsProcessed", resultsProcessed)
                    .Set("unityPid", unityPid)
                    .Set("unityStartTicks", 0L)))
            {
                error = "the relay closed the connection";
                return false;
            }

            // The first frame back is the welcome (or a rejection).
            socket.ReceiveTimeout = HandshakeTimeoutMillis;
            string first = null;
            try
            {
                while (first == null)
                {
                    int n = socket.Receive(_receiveBuffer);
                    if (n <= 0)
                    {
                        error = "the relay closed the connection";
                        return false;
                    }
                    _pending.Write(_receiveBuffer, 0, n);
                    first = TakeLine();
                }
            }
            catch (Exception ex)
            {
                error = "the relay did not answer (" + ex.Message + ")";
                // Connected but silent: still ask it to end, so the
                // resume that follows is not sharing the session with a
                // CLI this relay keeps running.
                ShutdownRelayAndWait(1500);
                return false;
            }
            socket.ReceiveTimeout = 0;

            JsonNode node;
            string parseError;
            if (!first.StartsWith("C ", StringComparison.Ordinal)
                || !JsonParser.TryParse(first.Substring(2), out node, out parseError) || !node.IsObject)
            {
                error = "the relay answered with something unexpected";
                return false;
            }
            string type = node["type"].AsString(string.Empty);
            if (type == "mismatch")
            {
                // A relay built from another package version (the package
                // was updated across this reload). It still takes shutdown.
                error = "the relay speaks protocol " + node["protocol"].AsInt()
                    + ", this version needs " + ProtocolVersion;
                ShutdownRelayAndWait(1500);
                return false;
            }
            if (type != "welcome")
            {
                error = "the relay refused the connection (" + type + " "
                    + node["reason"].AsString(string.Empty) + ")";
                return false;
            }
            if (node["protocol"].AsInt() != ProtocolVersion)
            {
                error = "the relay speaks protocol " + node["protocol"].AsInt()
                    + ", this version needs " + ProtocolVersion;
                ShutdownRelayAndWait(1500);
                return false;
            }
            welcome = node;
            return true;
        }

        /// <summary>
        /// Handshake-time only (nothing is receiving yet): tells the relay
        /// to end its CLI and itself, and waits until it has closed the
        /// link, so whoever spawns next has the session to itself.
        /// </summary>
        private void ShutdownRelayAndWait(int millis)
        {
            Socket socket = _socket;
            if (socket == null
                || !SendControl(JsonNode.NewObject().Set("type", "shutdown").Set("graceMillis", 0)))
            {
                return;
            }
            try
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < millis)
                {
                    socket.ReceiveTimeout = Math.Max(1, millis - (int)watch.ElapsedMilliseconds);
                    if (socket.Receive(_receiveBuffer) <= 0)
                    {
                        return;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void BeginReceiving()
        {
            // Lines that came in the same packets as the welcome.
            DrainPendingLines();
            Arm();
        }

        private void Arm()
        {
            Socket socket = _socket;
            if (socket == null || !_linkOpen)
            {
                return;
            }
            try
            {
                socket.BeginReceive(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None, OnReceive, socket);
            }
            catch (Exception)
            {
                OnLinkClosed();
            }
        }

        private void OnReceive(IAsyncResult result)
        {
            var socket = (Socket)result.AsyncState;
            int n;
            try
            {
                n = socket.EndReceive(result);
            }
            catch (Exception)
            {
                OnLinkClosed();
                return;
            }
            if (n <= 0)
            {
                OnLinkClosed();
                return;
            }
            try
            {
                _pending.Write(_receiveBuffer, 0, n);
                DrainPendingLines();
            }
            catch (Exception ex)
            {
                Log("CLI relay link: " + ex.Message);
            }
            // Never re-arm after a detach: nothing may be left waiting on
            // the socket when the domain unloads.
            if (!_detached)
            {
                Arm();
            }
        }

        private void DrainPendingLines()
        {
            string line;
            while ((line = TakeLine()) != null)
            {
                Dispatch(line);
            }
        }

        /// <summary>The next complete line in the receive buffer, or null. '\n' never occurs inside a UTF-8 sequence.</summary>
        private string TakeLine()
        {
            byte[] bytes = _pending.GetBuffer();
            int length = (int)_pending.Length;
            int newline = Array.IndexOf(bytes, (byte)'\n', 0, length);
            if (newline < 0)
            {
                return null;
            }
            int lineLength = newline;
            if (lineLength > 0 && bytes[lineLength - 1] == (byte)'\r')
            {
                lineLength--;
            }
            string line = Encoding.UTF8.GetString(bytes, 0, lineLength);
            int rest = length - (newline + 1);
            Buffer.BlockCopy(bytes, newline + 1, bytes, 0, rest);
            _pending.SetLength(rest);
            _pending.Position = rest;
            return line;
        }

        private void Dispatch(string line)
        {
            if (line.Length < 2 || line[1] != ' ')
            {
                return;
            }
            char tag = line[0];
            if (tag == 'O')
            {
                _output.Enqueue(line.Substring(2));
                return;
            }
            if (tag == 'E')
            {
                _errorOutput.Enqueue(line.Substring(2));
                return;
            }
            if (tag != 'C')
            {
                return;
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(line.Substring(2), out node, out error) || !node.IsObject)
            {
                return;
            }
            switch (node["type"].AsString(string.Empty))
            {
                case "spawned":
                    _endpoint.CliPid = node["cliPid"].AsInt();
                    _cliRunning = true;
                    _spawnEvent.Set();
                    break;
                case "spawnFailed":
                    _spawnError = node["error"].AsString("unknown error");
                    _spawnEvent.Set();
                    break;
                case "live":
                    ReplayFinished = true;
                    break;
                case "exited":
                    _cliRunning = false;
                    RaiseExited();
                    break;
                case "rejected":
                    Log("CLI relay rejected a frame: " + node["reason"].AsString(string.Empty));
                    break;
            }
        }

        private void OnLinkClosed()
        {
            bool wasOpen = _linkOpen;
            if (wasOpen && _socket != null)
            {
                _remoteClosed = true;
            }
            _linkOpen = false;
            _closedEvent.Set();
            _spawnEvent.Set();
            if (!wasOpen || _detached || _stopping)
            {
                return;
            }
            // The relay went away under a live client: to the client this
            // is a dead CLI (it restarts with --resume, as always).
            _cliRunning = false;
            RaiseExited();
        }

        private void RaiseExited()
        {
            lock (_stateLock)
            {
                if (_exitedRaised || _detached)
                {
                    return;
                }
                _exitedRaised = true;
            }
            Action handler = Exited;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception)
                {
                }
            }
        }

        private bool SendControl(JsonNode payload)
        {
            lock (_sendLock)
            {
                return SendRawLocked("C " + JsonWriter.Write(payload));
            }
        }

        private bool SendRawLocked(string frame)
        {
            Socket socket = _socket;
            if (socket == null)
            {
                return false;
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(frame + "\n");
                int sent = 0;
                var watch = Stopwatch.StartNew();
                while (sent < bytes.Length)
                {
                    if (watch.ElapsedMilliseconds > SendBudgetMillis)
                    {
                        throw new TimeoutException("the relay is not reading");
                    }
                    sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                }
                return true;
            }
            catch (Exception)
            {
                // Part of the frame may be on the wire: this link can
                // never carry another frame safely. Drop it; the client
                // sees a dead CLI and restarts it the ordinary way.
                AbandonLinkLocked(socket);
                return false;
            }
        }

        /// <summary>Longest a single frame may take to send before the link is given up.</summary>
        private const int SendBudgetMillis = 10000;

        private void AbandonLinkLocked(Socket socket)
        {
            if (_socket == socket)
            {
                _socket = null;
            }
            bool wasOpen = _linkOpen;
            _linkOpen = false;
            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
            _closedEvent.Set();
            _spawnEvent.Set();
            if (wasOpen && !_detached && !_stopping)
            {
                _cliRunning = false;
                RaiseExited();
            }
        }

        private void CloseSocket()
        {
            Socket socket;
            lock (_sendLock)
            {
                socket = _socket;
                _socket = null;
            }
            _linkOpen = false;
            _closedEvent.Set();
            if (socket == null)
            {
                return;
            }
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }
            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
        }

        private static JsonNode WaitForReadyFile(string path, int timeoutMillis, System.Diagnostics.Process relay)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMillis)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        JsonNode node;
                        string error;
                        if (JsonParser.TryParse(File.ReadAllText(path), out node, out error)
                            && node.IsObject && node["ctlPort"].AsInt() > 0)
                        {
                            return node;
                        }
                    }
                    catch (Exception)
                    {
                        // Still being written or renamed: look again.
                    }
                }
                try
                {
                    if (relay.HasExited)
                    {
                        return null;
                    }
                }
                catch (Exception)
                {
                }
                Thread.Sleep(20);
            }
            return null;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void KillQuietly(IProcessKiller killer, int pid)
        {
            if (killer == null || pid <= 0)
            {
                return;
            }
            try
            {
                killer.KillTree(pid);
            }
            catch (Exception)
            {
            }
        }

        private static void DeleteQuietly(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }
}

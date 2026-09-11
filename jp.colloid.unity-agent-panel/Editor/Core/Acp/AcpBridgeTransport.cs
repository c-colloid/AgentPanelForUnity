using System;
using Colloid.AgentPanel.Core.Process;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// ICliTransport that spawns an ACP agent (Gemini CLI, codex-acp, any
    /// custom ACP command) and speaks Claude Code stream-json to
    /// AgentClient on top of it -- the "different CLI behind the same
    /// panel" seam ARCHITECTURE.md D4 reserved as IAgentTransport
    /// (design note docs/design-notes/2026-09-10-acp-backends.md section 3).
    ///
    /// Composition: the raw process is a <see cref="ClaudeCliProcess"/>
    /// (its Windows tree-kill/orphan-sweep/reader-drain lifecycle is
    /// exactly what any resident stdio child needs; the CLAUDE_* env
    /// cleanup it performs is harmless to other agents) with a stdout
    /// interceptor that hands every line to <see cref="AcpProtocolBridge"/>
    /// instead of the raw Output channel. The bridge's translated lines
    /// land in THIS transport's Output channel, which AgentClient drains as
    /// usual, and the bridge's outbound JSON-RPC goes to the child's stdin.
    ///
    /// Threading: the bridge is not thread-safe by itself, so every call
    /// into it is serialized through <c>_bridgeLock</c> -- reader thread
    /// (agent lines, Exited) and main thread (panel lines) alike. The lock
    /// is never held while blocking on the child (writes are non-blocking
    /// pipe writes; a full pipe would be the agent's own deadlock).
    ///
    /// The <c>arguments</c> passed to <see cref="Start"/> are AgentClient's
    /// Claude-shaped flags; they are IGNORED here -- everything the agent
    /// needs comes from the <see cref="AcpLaunchSpec"/>.
    /// </summary>
    public sealed class AcpBridgeTransport : ICliTransport
    {
        private readonly AcpLaunchSpec _spec;
        private readonly ClaudeCliProcess _process;
        private readonly LineChannel _output = new LineChannel();
        private readonly Action<string> _logger;
        private readonly object _bridgeLock = new object();
        private AcpProtocolBridge _bridge;
        private bool _stopping;

        public AcpBridgeTransport(AcpLaunchSpec spec, IProcessKiller killer, Action<string> logger = null)
        {
            if (spec == null)
            {
                throw new ArgumentNullException("spec");
            }
            _spec = spec;
            _logger = logger;
            _process = new ClaudeCliProcess(killer, logger, OnAgentStdoutLine);
            _process.Exited += OnProcessExited;
        }

        /// <summary>The translator (diagnostics/tests). Null before Start.</summary>
        public AcpProtocolBridge Bridge
        {
            get { return _bridge; }
        }

        public bool IsRunning
        {
            get { return _process.IsRunning; }
        }

        public int ProcessId
        {
            get { return _process.ProcessId; }
        }

        public long ProcessStartTimeUtcTicks
        {
            get { return _process.ProcessStartTimeUtcTicks; }
        }

        /// <summary>Translated Claude-shaped lines (never the agent's raw JSON-RPC).</summary>
        public LineChannel Output
        {
            get { return _output; }
        }

        public LineChannel ErrorOutput
        {
            get { return _process.ErrorOutput; }
        }

        public event Action Exited;

        /// <summary>See AcpProtocolBridge.AuthenticationStarted (raised on whichever thread delivered the agent's reply; handlers must only set flags).</summary>
        public event Action<string, string> AuthenticationStarted;

        /// <summary>See AcpProtocolBridge.AuthenticationFinished (same threading caveat).</summary>
        public event Action<bool, string> AuthenticationFinished;

        public void Start(string executablePath, string arguments, string workingDirectory,
            string subagentModel = null, ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto)
        {
            // `arguments` is AgentClient's Claude flag string: ignored by
            // design (see class doc). `subagentModel` likewise -- ACP has
            // no subagent-model concept. `claudeAuth` is Claude-Code-
            // specific (ICliTransport.Start's doc comment) -- the ACP agent
            // this bridge drives is never Claude Code, so it is ignored too
            // and the inner _process.Start below always passes the default.
            var bridge = new AcpProtocolBridge(_spec, workingDirectory, WriteToAgent, _output.Enqueue, _logger);
            bridge.HandshakeFailed += OnHandshakeFailed;
            bridge.AuthenticationStarted += delegate(string methodId, string methodName)
            {
                Action<string, string> handler = AuthenticationStarted;
                if (handler != null)
                {
                    handler(methodId, methodName);
                }
            };
            bridge.AuthenticationFinished += delegate(bool success, string error)
            {
                Action<bool, string> handler = AuthenticationFinished;
                if (handler != null)
                {
                    handler(success, error);
                }
            };
            _bridge = bridge;
            _stopping = false;
            _process.Start(executablePath, _spec.Arguments ?? string.Empty, workingDirectory, null);
        }

        public bool WriteLine(string serializedJsonLine)
        {
            if (serializedJsonLine == null || !_process.IsRunning || _bridge == null)
            {
                return false;
            }
            lock (_bridgeLock)
            {
                _bridge.OnPanelLine(serializedJsonLine);
            }
            return true;
        }

        public void CloseStdin()
        {
            _process.CloseStdin();
        }

        public void Stop(string interruptLine, int graceMillis)
        {
            _stopping = true;
            if (interruptLine != null && _bridge != null && _process.IsRunning)
            {
                // The Claude interrupt line becomes a session/cancel through
                // the bridge; the child's own stdin EOF follows inside
                // ClaudeCliProcess.Stop.
                lock (_bridgeLock)
                {
                    _bridge.OnPanelLine(interruptLine);
                }
            }
            _process.Stop(null, graceMillis);
        }

        public void Kill()
        {
            _stopping = true;
            _process.Kill();
        }

        public void Dispose()
        {
            _stopping = true;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
        }

        private void WriteToAgent(string line)
        {
            if (!_process.WriteLine(line))
            {
                Log("ACP agent stdin write failed (" + Truncate(line, 80) + ").");
            }
        }

        private void OnAgentStdoutLine(string line)
        {
            AcpProtocolBridge bridge = _bridge;
            if (bridge == null)
            {
                return;
            }
            lock (_bridgeLock)
            {
                bridge.OnAgentLine(line);
            }
        }

        private void OnProcessExited()
        {
            AcpProtocolBridge bridge = _bridge;
            if (bridge != null && !_stopping)
            {
                try
                {
                    lock (_bridgeLock)
                    {
                        bridge.OnAgentExited();
                    }
                }
                catch (Exception ex)
                {
                    Log("ACP bridge exit settle failed: " + ex.Message);
                }
            }
            Action handler = Exited;
            if (handler != null)
            {
                handler();
            }
        }

        private void OnHandshakeFailed(string reason)
        {
            // Called under _bridgeLock from whichever thread delivered the
            // failing response. Do not stop the process here (that would
            // block on WaitForExit while holding the lock); the child is
            // useless now, so just close its stdin -- every ACP agent exits
            // on EOF -- and AgentClient's death path takes over once the
            // Output channel (which already holds the error result) drains.
            _process.CloseStdin();
        }

        private static string Truncate(string text, int max)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private void Log(string message)
        {
            if (_logger != null)
            {
                _logger(message);
            }
        }
    }
}

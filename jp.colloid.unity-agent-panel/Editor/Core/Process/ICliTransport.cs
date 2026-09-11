using System;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Transport seam between AgentClient and the real CLI process.
    /// ClaudeCliProcess is the production implementation; tests use
    /// FakeCliProcess (scripted line source). This is also the future
    /// extension seam toward a WS bridge (ARCHITECTURE.md appendix,
    /// IAgentTransport idea) without committing to it in v1.
    ///
    /// Threading contract:
    /// - Output/ErrorOutput are filled from arbitrary reader threads and
    ///   drained by the caller (AgentClient.Pump on the main thread).
    /// - Exited may fire on any thread; handlers must only set flags.
    /// - WriteLine/CloseStdin/Stop/Kill are called from the main thread.
    /// </summary>
    public interface ICliTransport : IDisposable
    {
        /// <summary>True after a successful Start until exit/kill.</summary>
        bool IsRunning { get; }

        /// <summary>OS process id, or -1 when not running / not applicable.</summary>
        int ProcessId { get; }

        /// <summary>
        /// Process start time in UTC ticks (for ZombieReaper PID-reuse
        /// checks), or 0 when unknown.
        /// </summary>
        long ProcessStartTimeUtcTicks { get; }

        /// <summary>Stdout lines (one stream-json message per line).</summary>
        LineChannel Output { get; }

        /// <summary>Stderr lines (log view / diagnostics).</summary>
        LineChannel ErrorOutput { get; }

        /// <summary>Raised once when the process exits, on an arbitrary thread.</summary>
        event Action Exited;

        /// <summary>
        /// Spawns the process. Throws InvalidOperationException when already
        /// running and Exception subtypes on spawn failure.
        /// <paramref name="subagentModel"/> is the blanket subagent model
        /// alias (AgentClientOptions.SubagentModel); null/empty leaves the
        /// CLAUDE_CODE_SUBAGENT_MODEL environment variable completely
        /// untouched (ClaudeCliProcess.ComputeSubagentModelEnvEntries),
        /// never force-clearing an operator-set inherited value.
        /// <paramref name="claudeAuth"/> (PanelSettings.claudeAuth,
        /// docs/design-notes/2026-09-10-claude-api-key-auth-passthrough.md)
        /// is Claude-Code-specific: ClaudeCliProcess is the only
        /// implementation that reads it (ClaudeCliProcess.
        /// ComputeEnvVarsToRemove) to decide whether ANTHROPIC_API_KEY is
        /// stripped from the child environment; every other implementation
        /// ignores it, the same way AcpBridgeTransport already ignores
        /// <paramref name="subagentModel"/>.
        /// </summary>
        void Start(string executablePath, string arguments, string workingDirectory,
            string subagentModel = null, ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto);

        /// <summary>
        /// Writes one pre-serialized single-line JSON payload (produced by
        /// OutboundMessages) as UTF-8 bytes plus '\n', then flushes. This is
        /// the ONLY stdin write path. Returns false when the write failed
        /// (broken pipe / process dead); never throws.
        /// </summary>
        bool WriteLine(string serializedJsonLine);

        /// <summary>Closes stdin (EOF lets the CLI shut down gracefully).</summary>
        void CloseStdin();

        /// <summary>
        /// Graceful stop: best-effort write of <paramref name="interruptLine"/>
        /// (pass null to skip), close stdin, wait up to
        /// <paramref name="graceMillis"/> for exit, then fall back to a tree
        /// kill. Never throws.
        /// </summary>
        void Stop(string interruptLine, int graceMillis);

        /// <summary>Immediate tree kill (no grace period). Never throws.</summary>
        void Kill();
    }
}

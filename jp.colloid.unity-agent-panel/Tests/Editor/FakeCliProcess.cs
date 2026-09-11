using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Process;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Scripted ICliTransport for EditMode tests (the MockBridge strategy,
    /// Unity flavor). Tests script inbound lines -- typically real captured
    /// fixture lines -- into Output and inspect everything AgentClient
    /// wrote to stdin via WrittenLines. No process is ever spawned.
    /// </summary>
    public sealed class FakeCliProcess : ICliTransport
    {
        private bool _running;

        /// <summary>Everything WriteLine received, in order.</summary>
        public readonly List<string> WrittenLines = new List<string>();

        /// <summary>Arguments captured from Start.</summary>
        public string StartedExecutablePath { get; private set; }
        public string StartedArguments { get; private set; }
        public string StartedWorkingDirectory { get; private set; }
        public string StartedSubagentModel { get; private set; }
        public ClaudeAuthMode StartedClaudeAuth { get; private set; }
        public int StartCallCount { get; private set; }

        /// <summary>Set true to make every WriteLine fail (broken pipe).</summary>
        public bool FailWrites { get; set; }

        public bool StopCalled { get; private set; }
        public string StopInterruptLine { get; private set; }
        public bool KillCalled { get; private set; }
        public bool StdinClosed { get; private set; }
        public bool Disposed { get; private set; }

        // -- ICliTransport -----------------------------------------------------

        public bool IsRunning
        {
            get { return _running; }
        }

        public int ProcessId { get; set; } = 4242;

        public long ProcessStartTimeUtcTicks { get; set; } = 637_000_000_000_000_000L;

        public LineChannel Output { get; } = new LineChannel();

        public LineChannel ErrorOutput { get; } = new LineChannel();

        public event Action Exited;

        public void Start(string executablePath, string arguments, string workingDirectory,
            string subagentModel = null, ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto)
        {
            if (_running)
            {
                throw new InvalidOperationException("FakeCliProcess already running.");
            }
            StartCallCount++;
            StartedExecutablePath = executablePath;
            StartedArguments = arguments;
            StartedWorkingDirectory = workingDirectory;
            StartedSubagentModel = subagentModel;
            StartedClaudeAuth = claudeAuth;
            _running = true;
        }

        public bool WriteLine(string serializedJsonLine)
        {
            if (!_running || FailWrites)
            {
                return false;
            }
            WrittenLines.Add(serializedJsonLine);
            return true;
        }

        public void CloseStdin()
        {
            StdinClosed = true;
        }

        public void Stop(string interruptLine, int graceMillis)
        {
            StopCalled = true;
            StopInterruptLine = interruptLine;
            if (interruptLine != null && _running)
            {
                WrittenLines.Add(interruptLine);
            }
            StdinClosed = true;
            _running = false;
        }

        public void Kill()
        {
            KillCalled = true;
            _running = false;
        }

        public void Dispose()
        {
            Disposed = true;
            _running = false;
        }

        // -- Scripting helpers ---------------------------------------------------

        /// <summary>Queues one inbound stdout line.</summary>
        public void ScriptLine(string line)
        {
            Output.Enqueue(line);
        }

        /// <summary>Queues many inbound stdout lines.</summary>
        public void ScriptLines(IEnumerable<string> lines)
        {
            foreach (string line in lines)
            {
                Output.Enqueue(line);
            }
        }

        /// <summary>Simulates the process dying (raises Exited like the real transport).</summary>
        public void SimulateExit()
        {
            _running = false;
            Action handler = Exited;
            if (handler != null)
            {
                handler();
            }
        }
    }
}

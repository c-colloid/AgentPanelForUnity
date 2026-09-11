using System;
using System.IO;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Orphan CLI process cleanup (ARCHITECTURE.md D4 step 4, risk #14).
    /// The live PID plus its start time are recorded to a small JSON state
    /// file (path supplied by the caller -- e.g. UserSettings/AgentPanel/,
    /// never Library/). On the next initialization ReapOrphans() kills a
    /// recorded process only when ALL of these match, preventing PID-reuse
    /// friendly fire:
    /// - the PID is still alive,
    /// - the process start time matches the recorded one (small tolerance),
    /// - the process name is "claude".
    /// Pure C#: file I/O + System.Diagnostics only.
    /// </summary>
    public sealed class ZombieReaper
    {
        /// <summary>Allowed drift between recorded and observed start time.</summary>
        private const long StartTimeToleranceTicks = TimeSpan.TicksPerSecond;

        private readonly string _stateFilePath;
        private readonly IProcessKiller _killer;
        private readonly Action<string> _logger;

        public ZombieReaper(string stateFilePath, IProcessKiller killer, Action<string> logger = null)
        {
            if (string.IsNullOrEmpty(stateFilePath))
            {
                throw new ArgumentException("stateFilePath must be non-empty.", "stateFilePath");
            }
            if (killer == null)
            {
                throw new ArgumentNullException("killer");
            }
            _stateFilePath = stateFilePath;
            _killer = killer;
            _logger = logger;
        }

        /// <summary>
        /// Records the currently spawned CLI process. Overwrites any previous
        /// record (the panel owns exactly one process at a time). Never throws.
        /// </summary>
        public void Record(int pid, long startTimeUtcTicks)
        {
            Record(pid, startTimeUtcTicks, DefaultProcessName);
        }

        /// <summary>The process name recorded when none is given (the Claude Code CLI).</summary>
        public const string DefaultProcessName = "claude";

        /// <summary>
        /// As <see cref="Record(int, long)"/>, naming the expected process
        /// (design note 2026-09-10-acp-backends.md section 3.5: an ACP
        /// backend's process is "gemini"/"codex-acp"/..., and the reap
        /// check must compare against what was actually spawned, never
        /// against a hard-coded "claude").
        /// </summary>
        public void Record(int pid, long startTimeUtcTicks, string processName)
        {
            try
            {
                var payload = JsonNode.NewObject()
                    .Set("pid", pid)
                    .Set("startTimeUtcTicks", startTimeUtcTicks)
                    .Set("name", string.IsNullOrEmpty(processName) ? DefaultProcessName : processName);
                string dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(_stateFilePath, JsonWriter.Write(payload));
            }
            catch (Exception ex)
            {
                Log("ZombieReaper.Record failed: " + ex.Message);
            }
        }

        /// <summary>Deletes the record after a clean shutdown. Never throws.</summary>
        public void ClearRecord()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                }
            }
            catch (Exception ex)
            {
                Log("ZombieReaper.ClearRecord failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Kills the recorded orphan when it is verifiably the same claude
        /// process, then clears the record. Returns true when a kill was
        /// issued. Never throws.
        /// </summary>
        public bool ReapOrphans()
        {
            int pid;
            long recordedTicks;
            string recordedName;
            if (!TryReadRecord(out pid, out recordedTicks, out recordedName))
            {
                return false;
            }
            // The record is consumed either way: a stale or mismatched entry
            // must not linger and get re-checked forever.
            ClearRecord();

            if (pid <= 0)
            {
                return false;
            }
            if (recordedTicks == 0)
            {
                // A record without a verifiable start time cannot be safely
                // matched against a possibly reused PID (it could be the
                // user's own interactive claude session). Refuse to kill;
                // the record has already been cleared above.
                Log("ZombieReaper: record for PID " + pid
                    + " has no start time; refusing to kill (record cleared).");
                return false;
            }
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                {
                    string name;
                    long observedTicks;
                    try
                    {
                        name = process.ProcessName;
                        observedTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (Exception)
                    {
                        // Exited between lookup and query, or access denied.
                        return false;
                    }
                    if (!string.Equals(name, recordedName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("ZombieReaper: PID " + pid + " is now '" + name + "', not " + recordedName + ". Skipping.");
                        return false;
                    }
                    if (Math.Abs(observedTicks - recordedTicks) > StartTimeToleranceTicks)
                    {
                        Log("ZombieReaper: PID " + pid + " start time mismatch (reused PID). Skipping.");
                        return false;
                    }
                    Log("ZombieReaper: killing orphaned " + recordedName + " process, PID " + pid + ".");
                    return _killer.KillTree(pid);
                }
            }
            catch (ArgumentException)
            {
                // No process with that PID: nothing to reap.
                return false;
            }
            catch (Exception ex)
            {
                Log("ZombieReaper.ReapOrphans failed: " + ex.Message);
                return false;
            }
        }

        private bool TryReadRecord(out int pid, out long startTimeUtcTicks, out string processName)
        {
            pid = 0;
            startTimeUtcTicks = 0;
            processName = DefaultProcessName;
            try
            {
                if (!File.Exists(_stateFilePath))
                {
                    return false;
                }
                string text = File.ReadAllText(_stateFilePath);
                JsonNode node;
                string error;
                if (!JsonParser.TryParse(text, out node, out error))
                {
                    Log("ZombieReaper: unreadable state file (" + error + "). Clearing.");
                    return true; // pid==0 -> record gets cleared, nothing reaped
                }
                pid = node["pid"].AsInt(0);
                startTimeUtcTicks = node["startTimeUtcTicks"].AsLong(0);
                string name = node["name"].AsString(DefaultProcessName);
                processName = string.IsNullOrEmpty(name) ? DefaultProcessName : name;
                return true;
            }
            catch (Exception ex)
            {
                Log("ZombieReaper.TryReadRecord failed: " + ex.Message);
                return false;
            }
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

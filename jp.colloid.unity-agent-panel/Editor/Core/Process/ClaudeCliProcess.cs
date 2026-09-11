using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Production ICliTransport: spawns claude.exe directly (no shims, no
    /// shells) per ARCHITECTURE.md D1.
    ///
    /// Key constraints implemented here:
    /// - UseShellExecute=false, CreateNoWindow=true, all three stdio pipes
    ///   redirected, WorkingDirectory = Unity project root.
    /// - stdout/stderr decoded as UTF-8 (BOM-less encoding objects).
    /// - stdin: Unity 2022.3 Mono has no StandardInputEncoding, so writes go
    ///   as UTF-8 bytes straight to StandardInput.BaseStream with a flush per
    ///   line. This is the only write path; payloads must come from
    ///   OutboundMessages (one malformed line kills the CLI).
    /// - Environment cleanup: CLAUDECODE, CLAUDE_CODE_ENTRYPOINT and
    ///   CLAUDE_CODE_SESSION_ID are always removed so the child cannot
    ///   inherit a stale session. ANTHROPIC_API_KEY is left untouched by
    ///   default (docs/design-notes/2026-09-10-claude-api-key-auth-
    ///   passthrough.md): Claude Code's own auth selection must not be
    ///   blocked, per Anthropic's Claude Code legal terms. It is only
    ///   removed when PanelSettings.claudeAuth is explicitly set to
    ///   SubscriptionOnly (see <see cref="ComputeEnvVarsToRemove"/>).
    /// - Stop: interrupt line + stdin close + short WaitForExit, then
    ///   IProcessKiller tree kill (claude.exe owns bash/ripgrep/MCP children).
    /// </summary>
    public sealed class ClaudeCliProcess : ICliTransport
    {
        private static readonly string[] BaseEnvVarsToRemove =
        {
            "CLAUDECODE",
            "CLAUDE_CODE_ENTRYPOINT",
            "CLAUDE_CODE_SESSION_ID"
        };

        private static readonly string[] BaseEnvVarsToRemovePlusApiKey =
        {
            "CLAUDECODE",
            "CLAUDE_CODE_ENTRYPOINT",
            "CLAUDE_CODE_SESSION_ID",
            "ANTHROPIC_API_KEY"
        };

        /// <summary>
        /// Pure seam (docs/design-notes/2026-09-10-claude-api-key-auth-
        /// passthrough.md), mirroring ComputeSubagentModelEnvEntries below:
        /// the environment variable NAMES Start should remove from
        /// ProcessStartInfo for a given PanelSettings.claudeAuth value.
        /// ClaudeAuthMode.Auto (the default) never includes ANTHROPIC_API_KEY
        /// -- the CLI must pick its own auth exactly as it would in a
        /// terminal. Only ClaudeAuthMode.SubscriptionOnly (explicit opt-in)
        /// strips it, restoring the panel's previous (pre-v0.40.0)
        /// unconditional behaviour. Exposed as a static method (no
        /// ProcessStartInfo dependency) so tests can verify this without
        /// spawning a process.
        /// </summary>
        public static string[] ComputeEnvVarsToRemove(ClaudeAuthMode claudeAuth)
        {
            return claudeAuth == ClaudeAuthMode.SubscriptionOnly
                ? BaseEnvVarsToRemovePlusApiKey
                : BaseEnvVarsToRemove;
        }

        /// <summary>
        /// The blanket-subagent-model environment variable name (docs/
        /// research/07-model-configuration.md section 11, measured against
        /// CLI v2.1.220).
        /// </summary>
        public const string SubagentModelEnvVarName = "CLAUDE_CODE_SUBAGENT_MODEL";

        private static readonly KeyValuePair<string, string>[] NoEnvEntries =
            new KeyValuePair<string, string>[0];

        /// <summary>
        /// Pure seam (docs/design-notes/2026-08-01-model-settings-rework.md
        /// section 4.2): the environment variable entries Start should set
        /// on ProcessStartInfo for a given PanelSettings.subagentModel
        /// value. Null/empty yields NO entries at all -- the variable must
        /// be left completely untouched, never force-cleared, so an
        /// operator-set machine-wide value survives when the panel's own
        /// setting is "inherit". Exposed as a static method (no
        /// ProcessStartInfo/System.Diagnostics dependency) so tests can
        /// verify the env-computation logic without spawning a process.
        /// </summary>
        public static KeyValuePair<string, string>[] ComputeSubagentModelEnvEntries(string subagentModel)
        {
            if (string.IsNullOrEmpty(subagentModel))
            {
                return NoEnvEntries;
            }
            return new[] { new KeyValuePair<string, string>(SubagentModelEnvVarName, subagentModel) };
        }

        /// <summary>
        /// CORE-5: how long OnProcessExited waits for the async readers to
        /// drain before raising Exited. Unity's Mono WaitForExit(int)
        /// includes the async output/error readers' EOF in its timeout
        /// budget, so this bounds "final result line is enqueued before the
        /// consumer can observe the death" without the unbounded
        /// parameterless WaitForExit() (whose grandchild-inherits-stdout
        /// hang CORE-3 documents on OneShotCli).
        /// </summary>
        internal const int OutputFlushMillis = 1500;

        private readonly IProcessKiller _killer;
        private readonly Action<string> _logger;
        private readonly Action<string> _stdoutInterceptor;
        private readonly LineChannel _output = new LineChannel();
        private readonly LineChannel _errorOutput = new LineChannel();
        private readonly object _stdinLock = new object();

        /// <summary>
        /// CORE-4 (SR lifecycle-lock): serializes Start's post-spawn field
        /// assignments against OnProcessExited (which fires on a ThreadPool
        /// thread and, for a process that dies instantly, can fire BEFORE
        /// Start's assignments run). Ordering rules: take this before
        /// _stdinLock is never needed (no site holds both); never call out
        /// (events, killer) while holding it.
        /// </summary>
        private readonly object _lifecycleLock = new object();

        private System.Diagnostics.Process _process;
        private Stream _stdin;
        private volatile bool _running;
        private int _processId = -1;
        private long _startTimeUtcTicks;
        private bool _stdinClosed;

        public ClaudeCliProcess(IProcessKiller killer, Action<string> logger = null)
            : this(killer, logger, null)
        {
        }

        /// <summary>
        /// Design note 2026-09-10-acp-backends.md section 3.3: with a
        /// non-null <paramref name="stdoutInterceptor"/>, every stdout line
        /// is handed to it ON THE READER THREAD instead of being enqueued
        /// into <see cref="Output"/> -- AcpBridgeTransport uses this to
        /// translate an ACP agent's JSON-RPC before AgentClient ever sees
        /// it, while keeping this class's spawn/kill/orphan-sweep lifecycle
        /// verbatim. An interceptor that throws is logged and the line is
        /// dropped; the reader is never torn down by it.
        /// </summary>
        public ClaudeCliProcess(IProcessKiller killer, Action<string> logger, Action<string> stdoutInterceptor)
        {
            if (killer == null)
            {
                throw new ArgumentNullException("killer");
            }
            _killer = killer;
            _logger = logger;
            _stdoutInterceptor = stdoutInterceptor;
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public int ProcessId
        {
            get { return _processId; }
        }

        public long ProcessStartTimeUtcTicks
        {
            get { return _startTimeUtcTicks; }
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

        public void Start(string executablePath, string arguments, string workingDirectory,
            string subagentModel = null, ClaudeAuthMode claudeAuth = ClaudeAuthMode.Auto)
        {
            if (_process != null)
            {
                throw new InvalidOperationException("ClaudeCliProcess is single-use: already started.");
            }
            if (string.IsNullOrEmpty(executablePath))
            {
                throw new ArgumentException("executablePath must be non-empty.", "executablePath");
            }

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string name in ComputeEnvVarsToRemove(claudeAuth))
            {
                try
                {
                    psi.EnvironmentVariables.Remove(name);
                }
                catch (Exception)
                {
                    // StringDictionary.Remove on a missing key is a no-op,
                    // but stay defensive against exotic Mono behavior.
                }
            }
            KeyValuePair<string, string>[] subagentEnv = ComputeSubagentModelEnvEntries(subagentModel);
            for (int i = 0; i < subagentEnv.Length; i++)
            {
                try
                {
                    psi.EnvironmentVariables[subagentEnv[i].Key] = subagentEnv[i].Value;
                }
                catch (Exception ex)
                {
                    Log("Failed to set " + subagentEnv[i].Key + " environment variable: " + ex.Message);
                }
            }

            var process = new System.Diagnostics.Process();
            process.StartInfo = psi;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Failed to start CLI process: " + executablePath);
            }

            // CORE-4: Exited is subscribed (and EnableRaisingEvents set)
            // before Start, so a process that dies instantly can run
            // OnProcessExited on a ThreadPool thread CONCURRENTLY with the
            // assignments below. Without the lock, its _running=false could
            // be overwritten by an unconditional _running=true here,
            // leaving "IsRunning yet dead" plus a -1 _processId that skips
            // the orphan sweep. Under the lock, either OnProcessExited ran
            // first (HasExited is true -> _running stays false) or it
            // blocks until the fields are consistent.
            lock (_lifecycleLock)
            {
                _process = process;
                _stdinClosed = false;
                try
                {
                    _processId = process.Id;
                }
                catch (Exception)
                {
                    _processId = -1;
                }
                try
                {
                    _startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                catch (Exception)
                {
                    _startTimeUtcTicks = 0;
                }
                _stdin = process.StandardInput.BaseStream;
                bool hasExited;
                try
                {
                    hasExited = process.HasExited;
                }
                catch (Exception)
                {
                    hasExited = false;
                }
                _running = ShouldMarkRunning(hasExited);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        /// <summary>
        /// CORE-4's one decision, pure for tests: a transport whose process
        /// already exited must never be marked running -- OnProcessExited
        /// (which may already have run) is the only writer of the death.
        /// </summary>
        internal static bool ShouldMarkRunning(bool hasExited)
        {
            return !hasExited;
        }

        public bool WriteLine(string serializedJsonLine)
        {
            if (serializedJsonLine == null)
            {
                return false;
            }
            lock (_stdinLock)
            {
                if (!_running || _stdin == null || _stdinClosed)
                {
                    return false;
                }
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(serializedJsonLine + "\n");
                    _stdin.Write(bytes, 0, bytes.Length);
                    _stdin.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    Log("CLI stdin write failed: " + ex.Message);
                    return false;
                }
            }
        }

        public void CloseStdin()
        {
            lock (_stdinLock)
            {
                if (_stdin == null || _stdinClosed)
                {
                    return;
                }
                _stdinClosed = true;
                try
                {
                    _stdin.Close();
                }
                catch (Exception ex)
                {
                    Log("CLI stdin close failed: " + ex.Message);
                }
            }
        }

        public void Stop(string interruptLine, int graceMillis)
        {
            System.Diagnostics.Process process = _process;
            if (process == null || !_running)
            {
                return;
            }
            if (interruptLine != null)
            {
                WriteLine(interruptLine);
            }
            CloseStdin();
            bool exited = false;
            try
            {
                exited = process.WaitForExit(graceMillis > 0 ? graceMillis : 0);
            }
            catch (Exception ex)
            {
                Log("WaitForExit failed: " + ex.Message);
            }
            if (!exited)
            {
                Kill();
            }
        }

        public void Kill()
        {
            int pid = _processId;
            if (pid > 0 && _running)
            {
                _killer.KillTree(pid);
            }
        }

        public void Dispose()
        {
            System.Diagnostics.Process process = _process;
            if (process == null)
            {
                return;
            }
            // CORE-10: send EOF first, mirroring Stop() -- if Kill below is
            // a no-op (pid<=0, access denied), a child waiting on stdin
            // still gets its shutdown signal instead of lingering. CloseStdin
            // is idempotent (_stdinClosed guard), so the Stop->Dispose
            // sequence stays safe.
            CloseStdin();
            if (_running)
            {
                // Dispose without Stop is a hard teardown path.
                Kill();
                // Design note 2026-09-10 section 5: the orphan sweep below
                // used to run ONLY from OnProcessExited, but this method
                // unsubscribes Exited a few lines further down -- and on
                // the Stop -> Dispose teardown (every domain reload) the
                // Exited callback may still be queued on the ThreadPool when
                // that happens (taskkill returns as soon as the kill is
                // issued; Mono raises Exited from a registered wait on the
                // process handle), so whether the sweep ran on the path
                // where it matters most was a race. A grandchild that outlived
                // taskkill /T (its parent died first, so the tree walk no
                // longer reached it) keeps an inherited stdout/stderr write
                // handle open, which keeps our async reader parked in a
                // native ReadFile across the domain unload. Sweep here,
                // synchronously and bounded (Toolhelp snapshot + taskkill
                // per child), while _processId/_startTimeUtcTicks are
                // still intact. Idempotent with the OnProcessExited sweep.
                try
                {
                    KillOrphanedChildren();
                }
                catch (Exception ex)
                {
                    Log("Orphaned-children sweep on dispose failed: " + ex.Message);
                }
            }
            // Defensive: cancel the async readers so no pending native pipe
            // read can survive into a domain unload (a blocked reader thread
            // is the classic "Completing Domain" freeze signature).
            try
            {
                process.CancelOutputRead();
            }
            catch (Exception)
            {
            }
            try
            {
                process.CancelErrorRead();
            }
            catch (Exception)
            {
            }
            try
            {
                process.OutputDataReceived -= OnOutputDataReceived;
                process.ErrorDataReceived -= OnErrorDataReceived;
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            catch (Exception)
            {
            }
            _process = null;
            _stdin = null;
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            if (_stdoutInterceptor == null)
            {
                _output.Enqueue(e.Data);
                return;
            }
            try
            {
                _stdoutInterceptor(e.Data);
            }
            catch (Exception ex)
            {
                Log("stdout interceptor threw: " + ex.Message);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                _errorOutput.Enqueue(e.Data);
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            // CORE-4: see the Start-side comment on _lifecycleLock. The
            // fallback pid covers the exit-beat-Start ordering, where
            // _processId is still -1 -- without it the orphan sweep below
            // would silently skip.
            lock (_lifecycleLock)
            {
                _running = false;
                if (_processId <= 0)
                {
                    try
                    {
                        _processId = ((System.Diagnostics.Process)sender).Id;
                    }
                    catch (Exception)
                    {
                        // Keep -1; the sweep guards on it.
                    }
                }
            }
            // CORE-5: drain the async readers BEFORE raising Exited.
            // Process.Exited does not wait for the output flush, so the
            // final `result` line of a successful turn could still be in
            // flight when the consumer observes the death and declares the
            // turn Errored -- dropping a success that arrives a beat later.
            // Bounded (see OutputFlushMillis) so a grandchild holding the
            // stdout handle cannot park this ThreadPool thread forever.
            try
            {
                var process = sender as System.Diagnostics.Process;
                if (process != null)
                {
                    process.WaitForExit(OutputFlushMillis);
                }
            }
            catch (Exception ex)
            {
                Log("Post-exit output flush wait failed: " + ex.Message);
            }
            // The CLI died on its own (malformed-stdin exit 1, crash mid
            // tool, ...): taskkill /T on the already-dead root cannot
            // enumerate its children anymore, so bash/ripgrep/MCP
            // grandchildren would leak forever -- and any child holding an
            // inherited stdout/stderr write handle keeps our reader threads
            // alive across domain unload. Sweep them here while the parent
            // PID linkage is still fresh.
            try
            {
                KillOrphanedChildren();
            }
            catch (Exception ex)
            {
                Log("Orphaned-children sweep failed: " + ex.Message);
            }
            Action handler = Exited;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Log("Exited handler threw: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Kills processes whose parent is the (now dead) recorded root PID.
        /// PID-reuse guard: a candidate is only killed when it started at or
        /// after our root did -- a process created before our CLI cannot be
        /// its child, so a recycled parent PID pointing at an old process is
        /// never matched. The sweep runs immediately on exit, which keeps
        /// the reuse window practically zero. Windows-only (the Unix seam
        /// uses process groups; ARCHITECTURE.md D1).
        /// </summary>
        private void KillOrphanedChildren()
        {
            int rootPid = _processId;
            long rootStartTicks = _startTimeUtcTicks;
            if (rootPid <= 0 || Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }
            List<int> children = WindowsChildScanner.FindChildPids(rootPid);
            for (int i = 0; i < children.Count; i++)
            {
                int childPid = children[i];
                if (childPid <= 0 || childPid == rootPid)
                {
                    continue;
                }
                try
                {
                    using (var child = System.Diagnostics.Process.GetProcessById(childPid))
                    {
                        long childStartTicks;
                        try
                        {
                            childStartTicks = child.StartTime.ToUniversalTime().Ticks;
                        }
                        catch (Exception)
                        {
                            // Exited between snapshot and query, or access
                            // denied: do not kill what cannot be verified.
                            continue;
                        }
                        if (rootStartTicks != 0
                            && childStartTicks + TimeSpan.TicksPerSecond < rootStartTicks)
                        {
                            continue; // Older than our root: PID-reuse false match.
                        }
                    }
                }
                catch (ArgumentException)
                {
                    continue; // Already gone.
                }
                catch (Exception)
                {
                    continue;
                }
                Log("Killing orphaned CLI child process, PID " + childPid + ".");
                _killer.KillTree(childPid);
            }
        }

        /// <summary>
        /// Minimal Toolhelp32 snapshot walker used to find children of a
        /// given PID. Kept private to this transport; pure Win32 P/Invoke,
        /// no WMI/System.Management dependency (not available on Unity
        /// 2022.3 Mono).
        /// </summary>
        private static class WindowsChildScanner
        {
            private const uint Th32csSnapProcess = 0x00000002;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct ProcessEntry32
            {
                public uint dwSize;
                public uint cntUsage;
                public uint th32ProcessID;
                public IntPtr th32DefaultHeapID;
                public uint th32ModuleID;
                public uint cntThreads;
                public uint th32ParentProcessID;
                public int pcPriClassBase;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szExeFile;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
            private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
            private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            public static List<int> FindChildPids(int parentPid)
            {
                var result = new List<int>();
                IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                {
                    return result;
                }
                try
                {
                    var entry = new ProcessEntry32();
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                    if (Process32First(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.th32ParentProcessID == (uint)parentPid)
                            {
                                result.Add((int)entry.th32ProcessID);
                            }
                        }
                        while (Process32Next(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
                return result;
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

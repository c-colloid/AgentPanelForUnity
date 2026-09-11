using System;
using System.IO;
using System.Text;
using UnityEditor;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Interactive "&lt;cli&gt; auth login" session (docs/design-notes/
    /// 2026-08-02-auth-in-panel.md). Unlike AuthCli's one-shot status/logout
    /// helpers this stays alive across many editor frames: it streams stdout
    /// looking for the OAuth URL and the CLI's own "paste code" prompt, and
    /// accepts the pasted authorization code via <see cref="SubmitCode"/>.
    ///
    /// The prompt line ("Paste code here if prompted &gt; ") is NEVER
    /// newline-terminated -- the CLI sits reading stdin right after printing
    /// it -- so the usual `Process.BeginOutputReadLine`/`OutputDataReceived`
    /// pattern (a StreamReader.ReadLine()-based reader under the hood) would
    /// never deliver it while the child is blocked waiting: ReadLine()
    /// itself would still be blocked inside the .NET pipe-reader thread.
    /// This class instead reads process.StandardOutput.BaseStream directly
    /// on a dedicated background thread with a plain StreamReader.Read(...)
    /// loop, which returns as soon as ANY data is available (no newline
    /// required), and re-scans the whole accumulated buffer after every
    /// chunk for the URL / prompt tail.
    ///
    /// The process exit code is NEVER trusted as a login success/failure
    /// signal (unverified fact, see the design note section 1) -- the owner
    /// must re-run AuthCli.QueryStatus after <see cref="Exited"/> fires, for
    /// ANY exit code.
    /// </summary>
    public sealed class AuthLoginSession : IDisposable
    {
        private const string LoginArguments = "auth login";
        private const int ReadBufferChars = 512;

        /// <summary>Fired synchronously, on the caller's own thread, the moment the process is confirmed spawned.</summary>
        public event Action Starting;
        /// <summary>Fired once (main thread) with the first OAuth URL found in stdout.</summary>
        public event Action<string> UrlAvailable;
        /// <summary>Fired once (main thread) when the promptless "paste code" tail is detected.</summary>
        public event Action WaitingForCode;
        /// <summary>Fired once (main thread) after the process exits, for ANY exit code.</summary>
        public event Action<int> Exited;

        private readonly IProcessKiller _killer;
        private readonly Action<string> _logger;
        private readonly object _stdinLock = new object();
        private readonly object _outputLock = new object();
        private readonly StringBuilder _outputBuffer = new StringBuilder();

        private System.Diagnostics.Process _process;
        private Stream _stdin;
        private volatile bool _running;
        private volatile bool _urlRaised;
        private volatile bool _waitingRaised;
        private volatile bool _exitedRaised;
        private int _exitCode = -1;
        private string _oauthUrl;
        private bool _reloadHooked;
        private bool _disposed;

        /// <summary>The extracted OAuth URL, or null until stdout has produced one.</summary>
        public string OAuthUrl
        {
            get { return _oauthUrl; }
        }

        /// <summary>True once the promptless "paste code" tail has been observed.</summary>
        public bool IsWaitingForCode
        {
            get { return _waitingRaised; }
        }

        /// <summary>True once <see cref="Exited"/> has fired (or the initial spawn failed).</summary>
        public bool HasExited
        {
            get { return _exitedRaised; }
        }

        /// <summary>Valid only once <see cref="HasExited"/> is true.</summary>
        public int ExitCode
        {
            get { return _exitCode; }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        private AuthLoginSession(IProcessKiller killer, Action<string> logger)
        {
            _killer = killer;
            _logger = logger;
        }

        /// <summary>
        /// Spawns "&lt;cliPath&gt; auth login" and returns the session
        /// immediately -- <see cref="Starting"/> has already fired
        /// synchronously by the time this returns on success. On an
        /// immediate spawn failure (bad path, permission denied, ...) the
        /// returned session's <see cref="Exited"/> fires -1 on the NEXT
        /// pump drain, not synchronously -- deferred on purpose so a caller
        /// that subscribes to Exited right after Begin() returns (the
        /// normal pattern) can never miss it. Returns null only for invalid
        /// arguments (empty cliPath / null killer).
        /// </summary>
        public static AuthLoginSession Begin(string cliPath, IProcessKiller killer, Action<string> logger = null)
        {
            if (string.IsNullOrEmpty(cliPath) || killer == null)
            {
                return null;
            }
            var session = new AuthLoginSession(killer, logger);
            session.Start(cliPath);
            return session;
        }

        private void Start(string cliPath)
        {
            AuthCli.EnsurePumpHooked();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = LoginArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            };
            var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;

            bool started;
            try
            {
                started = process.Start();
            }
            catch (Exception ex)
            {
                Log("Failed to start auth login: " + ex.Message);
                started = false;
            }
            if (!started)
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }
                // Deferred through the SAME pump the success path uses (not
                // called synchronously here): Begin() returns the session
                // before its caller has had a chance to subscribe to
                // Exited, so raising it immediately would silently drop
                // the notification for every caller that subscribes right
                // after Begin() returns -- exactly the pattern AgentHub's
                // BeginLogin uses.
                AuthCli.EnqueueCallback(delegate { RaiseExited(-1); });
                return;
            }

            _process = process;
            _running = true;
            _stdin = process.StandardInput.BaseStream;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            _reloadHooked = true;

            RaiseStarting();
            StartReadThread(process.StandardOutput.BaseStream);
        }

        // -- Output reading (dedicated background thread; see the class doc
        // comment for why BeginOutputReadLine cannot be used here) -------------

        private void StartReadThread(Stream stdoutStream)
        {
            var thread = new System.Threading.Thread(delegate() { ReadLoop(stdoutStream); });
            thread.IsBackground = true;
            thread.Name = "AuthLoginSession-stdout";
            thread.Start();
        }

        private void ReadLoop(Stream stdoutStream)
        {
            try
            {
                using (var reader = new StreamReader(stdoutStream, new UTF8Encoding(false)))
                {
                    var buffer = new char[ReadBufferChars];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        OnOutputChunk(new string(buffer, 0, read));
                    }
                }
            }
            catch (Exception ex)
            {
                Log("auth login stdout read failed: " + ex.Message);
            }
        }

        private void OnOutputChunk(string chunk)
        {
            string accumulated;
            lock (_outputLock)
            {
                _outputBuffer.Append(chunk);
                accumulated = _outputBuffer.ToString();
            }
            if (!_urlRaised)
            {
                string url = AuthCli.ExtractOAuthUrl(accumulated);
                if (!string.IsNullOrEmpty(url))
                {
                    _urlRaised = true;
                    _oauthUrl = url;
                    AuthCli.EnqueueCallback(delegate { RaiseUrlAvailable(url); });
                }
            }
            if (!_waitingRaised && AuthCli.EndsWithCodePrompt(accumulated))
            {
                _waitingRaised = true;
                AuthCli.EnqueueCallback(delegate { RaiseWaitingForCode(); });
            }
        }

        // -- User actions --------------------------------------------------------

        /// <summary>
        /// Writes the pasted authorization code (trimmed) + a newline as
        /// UTF-8 bytes straight to stdin's BaseStream (same CP932-avoidance
        /// rule as ClaudeCliProcess.WriteLine -- Unity Mono has no
        /// StandardInputEncoding). Returns false without writing anything
        /// for a blank code or once the process is no longer running.
        /// </summary>
        public bool SubmitCode(string code)
        {
            string trimmed = (code ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }
            lock (_stdinLock)
            {
                if (!_running || _stdin == null)
                {
                    return false;
                }
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(trimmed + "\n");
                    _stdin.Write(bytes, 0, bytes.Length);
                    _stdin.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    Log("auth login stdin write failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Kills the login process tree. Credentials are left untouched --
        /// the OAuth exchange never completed, so the CLI never wrote
        /// anything. <see cref="Exited"/> fires once the OS finishes tearing
        /// the process down (exit code is meaningless here, per the class
        /// doc comment, and is never treated as a decision either way).
        /// </summary>
        public void Cancel()
        {
            KillProcess();
        }

        private void KillProcess()
        {
            System.Diagnostics.Process process = _process;
            if (process == null || !_running)
            {
                return;
            }
            int pid;
            try
            {
                pid = process.Id;
            }
            catch (Exception)
            {
                pid = -1;
            }
            if (pid > 0)
            {
                _killer.KillTree(pid);
            }
        }

        // -- Process exit ---------------------------------------------------------

        private void OnProcessExited(object sender, EventArgs e)
        {
            _running = false;
            int code = SafeExitCode();
            AuthCli.EnqueueCallback(delegate { RaiseExited(code); });
        }

        private int SafeExitCode()
        {
            try
            {
                return _process != null ? _process.ExitCode : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // -- Domain reload teardown (design note section 2.1: "killing from
        // AssemblyReloadEvents.beforeAssemblyReload directly is acceptable")
        // so a pending login can never outlive the editor state that started
        // it -- AgentHub's own static session reference is wiped by the
        // reload regardless; this only makes sure the OS-level process dies
        // too, even if the old AppDomain never gets to run OnProcessExited. --

        private void OnBeforeAssemblyReload()
        {
            KillProcess();
        }

        // -- Event raising ---------------------------------------------------------

        private void RaiseStarting()
        {
            Action handler = Starting;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Log("Starting handler threw: " + ex.Message);
                }
            }
        }

        private void RaiseUrlAvailable(string url)
        {
            Action<string> handler = UrlAvailable;
            if (handler != null)
            {
                try
                {
                    handler(url);
                }
                catch (Exception ex)
                {
                    Log("UrlAvailable handler threw: " + ex.Message);
                }
            }
        }

        private void RaiseWaitingForCode()
        {
            Action handler = WaitingForCode;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Log("WaitingForCode handler threw: " + ex.Message);
                }
            }
        }

        private void RaiseExited(int exitCode)
        {
            if (_exitedRaised)
            {
                return; // Already raised (e.g. via the immediate spawn-failure path).
            }
            _exitedRaised = true;
            _exitCode = exitCode;
            UnhookReload();
            Action<int> handler = Exited;
            if (handler != null)
            {
                try
                {
                    handler(exitCode);
                }
                catch (Exception ex)
                {
                    Log("Exited handler threw: " + ex.Message);
                }
            }
        }

        private void UnhookReload()
        {
            if (_reloadHooked)
            {
                _reloadHooked = false;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            }
        }

        /// <summary>Kills the process (if still running), unhooks every
        /// event subscription and releases the Process handle. Idempotent.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            UnhookReload();
            KillProcess();
            System.Diagnostics.Process process = _process;
            _process = null;
            _stdin = null;
            if (process != null)
            {
                try
                {
                    process.Exited -= OnProcessExited;
                }
                catch (Exception)
                {
                }
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void Log(string message)
        {
            if (_logger != null)
            {
                _logger(message);
            }
        }

        // -- Test-only seam (Colloid.AgentPanel.Editor.Tests via
        // InternalsVisibleTo, same precedent as AgentHub.WireClientForTests)
        // for docs/design-notes/2026-08-02-auth-in-panel.md section 4's
        // "Login state machine transitions as pure logic (no process)"
        // regression guard: lets AuthCliTests drive the SAME OnOutputChunk
        // buffering/URL-extraction/prompt-detection state machine the
        // background read thread runs in production, via directly-fed
        // stdout chunks, without spawning any "claude auth login" process. --

        /// <summary>
        /// Test-only: constructs a session with NO backing process. Only
        /// <see cref="FeedOutputChunkForTests"/> may be used against a
        /// session created this way -- Starting/Exited never fire, and
        /// SubmitCode/Cancel/Dispose are no-ops (no process to act on).
        /// </summary>
        internal static AuthLoginSession CreateForTests(Action<string> logger = null)
        {
            return new AuthLoginSession(null, logger);
        }

        /// <summary>
        /// Test-only: feeds a chunk of stdout text through the exact
        /// production OnOutputChunk logic (accumulate -&gt; ExtractOAuthUrl -&gt;
        /// EndsWithCodePrompt -&gt; UrlAvailable/WaitingForCode), without any
        /// process or background thread. UrlAvailable/WaitingForCode are
        /// raised through AuthCli's shared callback queue exactly like
        /// production -- callers must drain with
        /// AuthCli.DrainPendingForTests() to observe them.
        /// </summary>
        internal void FeedOutputChunkForTests(string chunk)
        {
            OnOutputChunk(chunk);
        }

        /// <summary>
        /// Test-only: true once <see cref="Dispose"/> has actually run.
        /// Lets AgentHub-level tests confirm a session was disposed (not
        /// merely reference-cleared) without relying on any externally
        /// observable side effect of a no-process CreateForTests() session,
        /// which has nothing else to show for it.
        /// </summary>
        internal bool WasDisposedForTests
        {
            get { return _disposed; }
        }
    }
}

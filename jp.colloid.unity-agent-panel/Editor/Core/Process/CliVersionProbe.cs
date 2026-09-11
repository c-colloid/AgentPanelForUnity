using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Reads the CLI's own version directly from the resolved binary
    /// ("&lt;cliPath&gt; --version"), independent of the stream-json
    /// protocol -- unlike <c>system/init</c> (which the CLI only emits once
    /// the FIRST user message of a connection has actually been processed;
    /// an idle spawned/resumed connection sits Ready forever with no
    /// system/init at all), the binary itself can be asked at any time,
    /// including for a connection that has never sent a message yet. See
    /// docs/design-notes/2026-08-01-cli-binary-version-probe.md.
    ///
    /// Runs the subprocess on a ThreadPool worker (never the editor main
    /// thread) and marshals the result back onto the main thread through a
    /// <see cref="ConcurrentQueue{T}"/> drained on
    /// <see cref="EditorApplication.update"/> -- the same
    /// producer/consumer idiom <c>LineChannel</c>/<c>EditorUpdatePump</c>
    /// already use for the live CLI transport's stdout/stderr, reused here
    /// instead of introducing a second threading pattern for one more
    /// subprocess.
    /// </summary>
    public static class CliVersionProbe
    {
        private const int TimeoutMillis = 10000;

        private static readonly ConcurrentQueue<Action> PendingCallbacks = new ConcurrentQueue<Action>();
        private static readonly object StateLock = new object();
        private static readonly HashSet<string> InFlightPaths = new HashSet<string>();
        /// <summary>
        /// Paths a probe has already been attempted for (success or
        /// failure) this domain load. Callers (SettingsView) call
        /// <see cref="BeginProbe"/> unconditionally on every refresh --
        /// including from `AgentHub.Changed`, which can fire many times a
        /// second while a turn streams -- so this guard is what keeps that
        /// from spawning a new `--version` subprocess every frame; only an
        /// actually-different resolved path (a real cache miss) starts a
        /// new probe.
        /// </summary>
        private static readonly HashSet<string> AttemptedPaths = new HashSet<string>();
        private static bool _pumpHooked;

        /// <summary>
        /// Extracts the stored form of a "claude --version" line: the
        /// first non-blank line, trimmed, normalizing CRLF first. Real
        /// stdout looks like "2.1.218 (Claude Code)\n" -- the whole first
        /// line is kept (not just the leading number) since the About row
        /// displays it verbatim. Returns empty for null/blank/whitespace-only
        /// input; never throws.
        /// </summary>
        public static string ParseVersionOutput(string rawStdout)
        {
            if (string.IsNullOrEmpty(rawStdout))
            {
                return string.Empty;
            }
            string[] lines = rawStdout.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Starts "&lt;cliPath&gt; --version" on a background thread
        /// (CreateNoWindow, redirected stdout only, ~10s timeout) unless a
        /// probe for this EXACT path is already in flight or was already
        /// attempted this domain load (see <see cref="AttemptedPaths"/>).
        /// <paramref name="onComplete"/> runs on the editor main thread with
        /// the parsed version, or an empty string on failure/timeout/
        /// missing path. Safe to call every time the caller re-resolves the
        /// CLI path (including from a hot refresh loop); never throws.
        /// </summary>
        public static void BeginProbe(string cliPath, Action<string> onComplete)
        {
            if (string.IsNullOrEmpty(cliPath) || onComplete == null)
            {
                return;
            }
            lock (StateLock)
            {
                if (InFlightPaths.Contains(cliPath) || AttemptedPaths.Contains(cliPath))
                {
                    return;
                }
                InFlightPaths.Add(cliPath);
            }
            EnsurePumpHooked();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string version = RunProbeBlocking(cliPath);
                PendingCallbacks.Enqueue(delegate
                {
                    lock (StateLock)
                    {
                        InFlightPaths.Remove(cliPath);
                        AttemptedPaths.Add(cliPath);
                    }
                    onComplete(version);
                });
            });
        }

        /// <summary>
        /// Reads stdout via the SAME async-event pattern
        /// <c>ClaudeCliProcess</c> uses for the live CLI transport
        /// (`BeginOutputReadLine`/`OutputDataReceived`) rather than a
        /// synchronous `StandardOutput.ReadToEnd()` -- `ReadToEnd()` blocks
        /// until the child closes stdout with NO timeout of its own, so if
        /// the child ever hung without exiting, the timed WaitForExit would
        /// never even be reached and this worker thread would leak forever.
        /// With async reading, every blocking wait in the runner is bounded
        /// (<see cref="OneShotCli"/>: the timeout, then the bounded flush),
        /// regardless of whether the child produces output, hangs, or both.
        /// </summary>
        private static string RunProbeBlocking(string cliPath)
        {
            // CORE-3: the runner body (previously duplicated line-for-line
            // with AuthCli's) lives in OneShotCli, which also replaced the
            // UNBOUNDED post-exit WaitForExit() flush with a bounded one --
            // see its class doc for the grandchild-inherits-stdout hang
            // this closes.
            try
            {
                int ignoredExitCode;
                string output = OneShotCli.Run(cliPath, "--version", TimeoutMillis, out ignoredExitCode);
                return output == null ? string.Empty : ParseVersionOutput(output);
            }
            catch (Exception)
            {
                // Missing binary, permission denied, not actually
                // executable, ... -- this is a best-effort diagnostics
                // probe, never a reason to throw into the caller.
                return string.Empty;
            }
        }

        private static void EnsurePumpHooked()
        {
            if (_pumpHooked)
            {
                return;
            }
            _pumpHooked = true;
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            Action callback;
            while (PendingCallbacks.TryDequeue(out callback))
            {
                try
                {
                    callback();
                }
                catch (Exception)
                {
                    // A listener throwing must not wedge the pump for
                    // every other queued (or future) probe callback.
                }
            }
        }
    }
}

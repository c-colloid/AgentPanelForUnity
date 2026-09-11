using System;
using System.Text;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Shared short-lived-CLI runner behind CliVersionProbe ("--version")
    /// and AuthCli ("auth status") -- the two were line-for-line duplicates
    /// (CORE-3's sharedRefactor). Async stdout capture (never a blocking
    /// ReadToEnd), one hard timeout that kills the child, then a BOUNDED
    /// post-exit flush wait.
    ///
    /// CORE-3, the bounded flush: the old code paired the timed
    /// WaitForExit with a PARAMETERLESS WaitForExit() to drain in-flight
    /// OutputDataReceived events. That wait blocks until stdout reaches
    /// EOF -- and a child that leaked its stdout handle to a grandchild
    /// keeps the pipe open after the child itself exits, so the unbounded
    /// wait could park this worker thread until the grandchild dies,
    /// potentially forever. WaitForExit(OutputFlushMillis) keeps the drain
    /// (Unity's Mono waits for the async readers' EOF within the timeout
    /// budget before/alongside the process wait) while bounding the worst
    /// case; the price is that a line still in flight after the bound can
    /// be lost, which for these tiny one-shot outputs means a failed
    /// probe/status parse (retryable), never a leaked thread.
    ///
    /// stderr is intentionally left un-redirected (goes nowhere) --
    /// redirecting it without also draining it asynchronously would
    /// reintroduce a deadlock risk on a chatty child.
    /// </summary>
    internal static class OneShotCli
    {
        /// <summary>Bounded post-exit flush wait (see class doc). Finite by contract -- a test pins it.</summary>
        internal const int OutputFlushMillis = 1500;

        /// <summary>
        /// Runs to completion and returns captured stdout ('\n'-joined), or
        /// null on spawn failure or timeout. <paramref name="exitCode"/> is
        /// only meaningful when the return value is non-null. Throws only
        /// what Process.Start throws (missing binary, access denied) --
        /// callers decide whether that is an error or a soft empty result.
        /// </summary>
        internal static string Run(string cliPath, string arguments, int timeoutMillis, out int exitCode)
        {
            exitCode = -1;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            var stdout = new StringBuilder();
            using (var process = new System.Diagnostics.Process { StartInfo = psi })
            {
                process.OutputDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stdout.Append(e.Data).Append('\n');
                    }
                };
                if (!process.Start())
                {
                    return null;
                }
                process.BeginOutputReadLine();
                if (!process.WaitForExit(timeoutMillis))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // Best-effort: already exited between the timeout
                        // check and this Kill, or access denied.
                    }
                    return null;
                }
                process.WaitForExit(OutputFlushMillis);
                exitCode = SafeExitCode(process);
                return stdout.ToString();
            }
        }

        private static int SafeExitCode(System.Diagnostics.Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}

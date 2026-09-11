using System;
using System.Text;
using Colloid.AgentPanel.Core.Acp;
using Colloid.AgentPanel.Core.Process;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>Outcome of one <see cref="CliInstaller.Run"/>.</summary>
    public sealed class CliInstallResult
    {
        public AgentBackend Backend;
        public bool Success;
        public CliInstallFailureKind Failure;
        public int ExitCode;
        /// <summary>Last non-empty line of the combined stdout+stderr (the installer's own verdict).</summary>
        public string LastLine = string.Empty;
        public double ElapsedSeconds;
    }

    /// <summary>
    /// Runs a <see cref="CliInstallPlan"/> on a worker thread and reports
    /// back on the main thread (design note
    /// docs/design-notes/2026-09-10-in-panel-install-and-sign-in.md section
    /// 1). Same shape as UnityPluginInstaller, with two differences an
    /// installer needs: stderr is captured too (npm and the native
    /// installer report failures there), and the timeout is minutes, not
    /// seconds. The process gets no stdin and no window; a tree kill ends
    /// a run that outlives the timeout.
    /// </summary>
    public static class CliInstaller
    {
        private const int OutputFlushMillis = 1500;

        public static void Run(CliInstallPlan plan, IProcessKiller killer, Action<CliInstallResult> onComplete,
            Action<string> log = null)
        {
            if (plan == null || onComplete == null)
            {
                return;
            }
            AuthCli.EnsurePumpHooked();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                CliInstallResult result;
                try
                {
                    result = RunBlocking(plan, killer, log);
                }
                catch (Exception ex)
                {
                    result = new CliInstallResult
                    {
                        Backend = plan.Backend,
                        Success = false,
                        Failure = CliInstallFailureKind.ShellMissing,
                        ExitCode = -1,
                        LastLine = ex.GetType().Name + ": " + ex.Message
                    };
                }
                CliInstallResult captured = result;
                AuthCli.EnqueueCallback(delegate { onComplete(captured); });
            });
        }

        private static CliInstallResult RunBlocking(CliInstallPlan plan, IProcessKiller killer, Action<string> log)
        {
            var started = DateTime.UtcNow;
            var output = new StringBuilder();
            var outputLock = new object();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            // The native installer and npm both talk to the network; make
            // sure nothing inherited from the editor forces API billing or
            // an interactive prompt.
            try
            {
                psi.EnvironmentVariables["CI"] = "1";
            }
            catch (Exception)
            {
            }
            bool launched;
            int exitCode = -1;
            bool timedOut = false;
            using (var process = new System.Diagnostics.Process { StartInfo = psi })
            {
                System.Diagnostics.DataReceivedEventHandler onLine = delegate(object sender,
                    System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                    {
                        return;
                    }
                    lock (outputLock)
                    {
                        output.Append(e.Data).Append('\n');
                    }
                    if (log != null)
                    {
                        log("[install] " + e.Data);
                    }
                };
                process.OutputDataReceived += onLine;
                process.ErrorDataReceived += onLine;
                try
                {
                    launched = process.Start();
                }
                catch (Exception ex)
                {
                    if (log != null)
                    {
                        log("Install could not start '" + plan.FileName + "': " + ex.Message);
                    }
                    launched = false;
                }
                if (launched)
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(CliInstallPlan.TimeoutMillis))
                    {
                        timedOut = true;
                        try
                        {
                            if (killer != null)
                            {
                                killer.KillTree(process.Id);
                            }
                            else
                            {
                                process.Kill();
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                    else
                    {
                        process.WaitForExit(OutputFlushMillis);
                        try
                        {
                            exitCode = process.ExitCode;
                        }
                        catch (Exception)
                        {
                            exitCode = -1;
                        }
                    }
                }
            }
            string text;
            lock (outputLock)
            {
                text = output.ToString();
            }
            CliInstallFailureKind failure = CliInstallPlan.Classify(plan.RequiresNpm, launched, timedOut, exitCode, text);
            return new CliInstallResult
            {
                Backend = plan.Backend,
                Success = failure == CliInstallFailureKind.None,
                Failure = failure,
                ExitCode = exitCode,
                LastLine = LastNonEmptyLine(text),
                ElapsedSeconds = (DateTime.UtcNow - started).TotalSeconds
            };
        }

        internal static string LastNonEmptyLine(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return string.Empty;
            }
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return string.Empty;
        }
    }
}

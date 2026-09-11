using System;
using Colloid.AgentPanel.Core.Process;

namespace Colloid.AgentPanel.Ops.UnityPlugin
{
    /// <summary>Which of the two CLI stages a result refers to.</summary>
    public enum UnityPluginInstallStage
    {
        MarketplaceAdd,
        PluginInstall
    }

    public sealed class UnityPluginInstallResult
    {
        public bool Success;

        /// <summary>The stage that ended the run (the failing one, or PluginInstall on success).</summary>
        public UnityPluginInstallStage Stage;

        public int ExitCode;

        /// <summary>Last non-empty stdout line of the ending stage (the CLI prints its verdict there); empty when nothing was captured.</summary>
        public string LastLine = string.Empty;

        /// <summary>True when the install stage was re-run without `--yes` (an older CLI rejecting the flag).</summary>
        public bool RetriedWithoutYes;
    }

    /// <summary>
    /// One-click install of Unity's official plugin (design note section
    /// 2.3): two `claude plugin ...` invocations run back to back on a
    /// ThreadPool worker, completion delivered on the main thread through
    /// the same pump AuthCli's logout uses. Measured 2026-09-10 on CLI
    /// 2.1.267 with stdin at /dev/null: marketplace add 3.0s, install 1.1s,
    /// both exit 0; re-running either is exit 0 with an "already" line, so
    /// there is no already-installed failure to classify -- exit code is
    /// the verdict.
    /// </summary>
    public static class UnityPluginInstaller
    {
        public const string MarketplaceAddArguments =
            "plugin marketplace add " + UnityPluginIdentity.MarketplaceSource;

        /// <summary>`--yes` is "required when stdin or stdout is not a TTY" per the CLI's own help, which a Unity-spawned child always is.</summary>
        public const string InstallArguments =
            "plugin install " + UnityPluginIdentity.QualifiedName + " --scope user --yes";

        /// <summary>Fallback for a CLI old enough to reject `--yes` (the panel's minimum 2.1.218 is unverified for it).</summary>
        public const string InstallArgumentsWithoutYes =
            "plugin install " + UnityPluginIdentity.QualifiedName + " --scope user";

        /// <summary>Per stage. The marketplace stage git-clones the repository; 3s measured, tens of seconds on a slow link.</summary>
        public const int StageTimeoutMillis = 120000;

        /// <summary>Test seam: the same shape as OneShotCli.Run.</summary>
        internal delegate string OneShotRunner(string cliPath, string arguments, int timeoutMillis, out int exitCode);

        /// <summary>
        /// Spawns both stages on a ThreadPool worker and invokes
        /// <paramref name="onComplete"/> on the main thread (next editor
        /// update). Ignored when the path or callback is missing.
        /// </summary>
        public static void Run(string cliPath, Action<UnityPluginInstallResult> onComplete)
        {
            if (string.IsNullOrEmpty(cliPath) || onComplete == null)
            {
                return;
            }
            AuthCli.EnsurePumpHooked();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                UnityPluginInstallResult result;
                try
                {
                    result = RunStages(cliPath, OneShotCli.Run);
                }
                catch (Exception ex)
                {
                    result = new UnityPluginInstallResult
                    {
                        Success = false,
                        Stage = UnityPluginInstallStage.MarketplaceAdd,
                        ExitCode = -1,
                        LastLine = ex.GetType().Name + ": " + ex.Message
                    };
                }
                UnityPluginInstallResult captured = result;
                AuthCli.EnqueueCallback(delegate { onComplete(captured); });
            });
        }

        /// <summary>
        /// The stage sequence, synchronous, with the runner injected so the
        /// decision table (design note section 2.3 items 2 and 5) is unit
        /// tested without a process: marketplace add must exit 0 (an
        /// already-registered marketplace does), then install with `--yes`;
        /// a non-zero install exit is retried ONCE without `--yes` and that
        /// second verdict is final.
        /// </summary>
        internal static UnityPluginInstallResult RunStages(string cliPath, OneShotRunner runner)
        {
            int exitCode;
            string output = runner(cliPath, MarketplaceAddArguments, StageTimeoutMillis, out exitCode);
            if (output == null || exitCode != 0)
            {
                return new UnityPluginInstallResult
                {
                    Success = false,
                    Stage = UnityPluginInstallStage.MarketplaceAdd,
                    ExitCode = exitCode,
                    LastLine = LastNonEmptyLine(output)
                };
            }

            output = runner(cliPath, InstallArguments, StageTimeoutMillis, out exitCode);
            bool retried = false;
            if (output == null || exitCode != 0)
            {
                retried = true;
                output = runner(cliPath, InstallArgumentsWithoutYes, StageTimeoutMillis, out exitCode);
            }
            return new UnityPluginInstallResult
            {
                Success = output != null && exitCode == 0,
                Stage = UnityPluginInstallStage.PluginInstall,
                ExitCode = exitCode,
                LastLine = LastNonEmptyLine(output),
                RetriedWithoutYes = retried
            };
        }

        /// <summary>Last non-blank line of <paramref name="output"/>, trimmed; empty for null/blank input. The CLI's success/already lines are the last thing it prints.</summary>
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

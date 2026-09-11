using System;
using System.Diagnostics;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Windows tree kill via "taskkill /PID x /T /F" (ARCHITECTURE.md D1).
    /// Runs hidden (CreateNoWindow) and waits briefly for taskkill itself
    /// to finish so callers can rely on the tree being gone right after.
    /// </summary>
    public sealed class WindowsProcessKiller : IProcessKiller
    {
        // taskkill /F normally returns well under a second; 2000 ms keeps
        // the worst-case beforeAssemblyReload teardown inside the ~3 s
        // domain-reload budget (short grace + this wait).
        private const int TaskkillWaitMillis = 2000;

        private readonly Action<string> _logger;

        public WindowsProcessKiller(Action<string> logger = null)
        {
            _logger = logger;
        }

        public bool KillTree(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var taskkill = System.Diagnostics.Process.Start(psi))
                {
                    if (taskkill == null)
                    {
                        return false;
                    }
                    taskkill.WaitForExit(TaskkillWaitMillis);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger("taskkill for PID " + pid + " failed: " + ex.Message);
                }
                return false;
            }
        }
    }
}

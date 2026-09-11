using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Colloid.AgentPanel.Core.Process;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Design note 2026-09-10 section 5: the orphaned-grandchild sweep must
    /// run on the Stop -> Dispose teardown (the domain-reload path), not
    /// only from the Exited callback that Dispose unsubscribes. Real
    /// processes are involved, so this is gated like the live CLI suite
    /// (UAP_LIVE_CLI=1) and Windows-only (the sweep is Windows-only;
    /// ARCHITECTURE.md D1). No claude.exe is needed: cmd.exe stands in for
    /// the CLI and ping.exe for a child that inherits its stdout.
    ///
    /// Shape of the race being pinned: the root (cmd) exits on its own
    /// when it reads the "interrupt" line -- inside Stop's grace, like a
    /// CLI that honours interrupt + stdin EOF -- leaving ping as an orphan
    /// whose recorded parent is the dead root. Whether OnProcessExited
    /// (ThreadPool) or Dispose (main thread) runs first is not controlled
    /// here; the assertion is the invariant both must uphold: after
    /// Dispose returns, the orphan is gone within a short bound. Before the
    /// fix, the ordering where Dispose won left ping running for its full
    /// 90 seconds, holding the inherited stdout write handle.
    /// </summary>
    [TestFixture]
    public class ClaudeCliProcessDisposeSweepLiveTests
    {
        private const int PingSeconds = 90;
        private const int AppearTimeoutMillis = 8000;
        private const int GoneTimeoutMillis = 8000;

        [Test]
        public void StopThenDispose_KillsOrphanedGrandchild_WhoseParentExitedOnItsOwn()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live process test skipped (set UAP_LIVE_CLI=1 to enable).");
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Ignore("Windows-only: the orphan sweep uses Toolhelp32 + taskkill.");
            }

            HashSet<int> before = SnapshotPids("ping");
            var log = new List<string>();
            var transport = new ClaudeCliProcess(new WindowsProcessKiller(log.Add), log.Add);
            int orphanPid = 0;
            try
            {
                // start /b: ping becomes a child of cmd and inherits cmd's
                // stdout (our pipe). set /p: cmd blocks on stdin until a
                // line arrives, then exits -- so the "interrupt" line Stop
                // writes is exactly what lets the root die on its own.
                // No outer quotes around the /c payload: cmd keeps them
                // when the payload contains '&', and then tries to run a
                // program literally named "start /b ...".
                transport.Start("cmd.exe",
                    "/d /c start /b ping.exe -n " + PingSeconds + " 127.0.0.1 & set /p uap_line=",
                    Path.GetTempPath());
                Assert.IsTrue(transport.IsRunning, "cmd.exe did not start");

                orphanPid = WaitForNewPid("ping", before, AppearTimeoutMillis);
                Assert.Greater(orphanPid, 0, "ping.exe never appeared as a child of cmd.exe (root pid "
                    + transport.ProcessId + ", running=" + transport.IsRunning + "). Names seen: "
                    + DescribeProcessesContaining("ping") + ". Log: " + string.Join(" | ", log.ToArray()));

                transport.Stop("interrupt", 2000);
                transport.Dispose();

                Assert.IsTrue(WaitForPidGone(orphanPid, GoneTimeoutMillis),
                    "ping.exe (PID " + orphanPid + ") outlived Stop+Dispose: the orphan sweep did not run"
                    + " on the teardown path. Log: " + string.Join(" | ", log.ToArray()));
            }
            finally
            {
                transport.Dispose();
                if (orphanPid > 0)
                {
                    TryKill(orphanPid);
                }
            }
        }

        /// <summary>
        /// Case-insensitive and extension-tolerant by name: on Unity's Mono
        /// the Windows image reports as "PING.EXE" (measured 2026-09-10 --
        /// .NET Framework would say "PING"), and GetProcessesByName is not
        /// reliably case-insensitive there either.
        /// </summary>
        private static List<int> PidsNamed(string name)
        {
            var list = new List<int>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    string processName;
                    try
                    {
                        processName = p.ProcessName;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(processName, name + ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(p.Id);
                    }
                }
            }
            return list;
        }

        private static string DescribeProcessesContaining(string fragment)
        {
            var names = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    string processName;
                    try
                    {
                        processName = p.ProcessName;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (processName != null
                        && processName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        names.Add(processName + "#" + p.Id);
                    }
                }
            }
            return names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray());
        }

        private static HashSet<int> SnapshotPids(string name)
        {
            return new HashSet<int>(PidsNamed(name));
        }

        private static int WaitForNewPid(string name, HashSet<int> before, int timeoutMillis)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMillis)
            {
                foreach (int pid in PidsNamed(name))
                {
                    if (!before.Contains(pid))
                    {
                        return pid;
                    }
                }
                Thread.Sleep(100);
            }
            return 0;
        }

        private static bool WaitForPidGone(int pid, int timeoutMillis)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMillis)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited)
                        {
                            return true;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private static void TryKill(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    p.Kill();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}

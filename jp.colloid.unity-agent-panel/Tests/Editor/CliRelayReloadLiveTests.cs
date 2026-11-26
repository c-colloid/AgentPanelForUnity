using System;
using System.Collections;
using System.IO;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The CLI relay across a REAL domain reload (design note 2026-11-25-
    /// cli-relay.md), end to end: the relay program is compiled by this
    /// editor's own toolchain, started as a real process, and runs the
    /// stand-in CLI (ci/FakeCli/claude) as its child. With the setting
    /// on, a reload must NOT restart anything: same relay, same CLI
    /// process, no interrupt, no --resume, and the turn that was
    /// streaming carries on with every line shown exactly once.
    ///
    /// The mirror image of ReloadLifecycleLiveTests (setting off: the old
    /// process is killed and the session resumed). Guarded the same way:
    /// self-ignores unless UAP_FAKE_CLI names the stand-in. Locals do not
    /// survive the reload, so the second half reads SessionState.
    /// </summary>
    [Category("LiveReload")]
    public class CliRelayReloadLiveTests
    {
        private const string Prefix = "Colloid.AgentPanel.Tests.RelayReload.";
        private const string KeySid = Prefix + "Sid";
        private const string KeyRelayPid = Prefix + "RelayPid";
        private const string KeyCliPid = Prefix + "CliPid";
        private const string KeyLog = Prefix + "Log";
        private const string KeyOrigPath = Prefix + "OrigCliPath";
        private const string KeyOrigRelay = Prefix + "OrigRelay";
        private const string KeyOrigAutoContinue = Prefix + "OrigAutoContinue";
        private const string KeyKillRelay = Prefix + "KillRelayAtReload";
        private const double StateTimeoutSeconds = 60.0;
        private const int StreamLines = 60;

        private static string FakeCliPath
        {
            get { return Environment.GetEnvironmentVariable("UAP_FAKE_CLI"); }
        }

        /// <summary>
        /// A failed assertion must not leave the relay setting on for the
        /// tests that run after this fixture (they expect the default).
        /// </summary>
        [TearDown]
        public void RestoreAfterFailure()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(KeyLog, string.Empty)))
            {
                AgentHub.Shutdown();
                RestoreSettings();
            }
        }

        // -- A streaming turn carries on across the reload ---------------------------

        [UnityTest]
        public IEnumerator StreamingTurn_ScriptReload_KeepsTheSameCli_AndShowsEveryLineOnce()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live relay test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }

            // ---- before the reload ----
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_STREAM_N", StreamLines.ToString());
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_STREAM_INTERVAL_MS", "250");
            yield return StartThroughRelay();

            CompileGate.SendOrQueue("hello");
            yield return WaitUntil(delegate { return CountInTranscript("line 3\n") > 0; }, "the third streamed line");
            Assert.IsTrue(AgentHub.Client.TurnActive, "sanity: the turn must be open before the reload");
            RememberProcesses();

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            // ---- after the reload ----
            yield return WaitUntil(delegate { return AgentHub.Client != null; }, "a client after the reload");
            AssertSameProcesses("after the reload");
            Assert.IsFalse(AgentHub.ResumedMidTurn, "nothing was interrupted, so nothing is 'resumed mid-turn'");

            // The stand-in finishes its stream by itself: the turn ends
            // without anybody sending "continue".
            yield return WaitUntil(delegate
            {
                return AgentHub.Client != null && AgentHub.Client.State == AgentClientState.Ready;
            }, "the streamed turn's result");
            for (int k = 1; k <= StreamLines; k++)
            {
                Assert.AreEqual(1, CountInTranscript("line " + k + "\n"),
                    "line " + k + " must be in the transcript exactly once\n" + DumpTranscript());
            }
            string log = ReadLog();
            Assert.AreEqual(1, Count(log, "start pid="), "one CLI process for the whole test\n" + log);
            Assert.AreEqual(1, Count(log, "user pid="), "the turn was never re-sent\n" + log);
            StringAssert.DoesNotContain("interrupt pid=", log);
            StringAssert.DoesNotContain("resumed=1", log);

            // And the same CLI takes the next message.
            CompileGate.SendOrQueue("again");
            yield return WaitUntil(delegate { return Count(ReadLog(), "user pid=") == 2; }, "the second message");
            yield return WaitUntil(delegate { return Count(ReadLog(), "result pid=") == 2; }, "the second result");
            AssertSameProcesses("after the second turn");

            yield return CleanupAndAssertProcessesGone();
        }

        // -- A permission prompt asked during the reload is shown once ---------------

        [UnityTest]
        public IEnumerator PermissionPrompt_ScriptReload_IsStillPending_AndAnsweredOnce()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live relay test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }

            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_ASK_PERMISSION", "1");
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_COMPLETE_TURN", "1");
            yield return StartThroughRelay();

            CompileGate.SendOrQueue("hello");
            yield return WaitUntil(delegate { return AgentHub.PendingPermission != null; }, "the permission prompt");
            RememberProcesses();

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            yield return WaitUntil(delegate { return AgentHub.Client != null; }, "a client after the reload");
            AssertSameProcesses("after the reload");
            yield return WaitUntil(delegate { return AgentHub.PendingPermission != null; },
                "the prompt again after the reload");
            Assert.AreEqual(0, AgentHub.PendingPermissionQueueDepth, "the prompt must not be doubled");
            Assert.AreEqual(1, Count(ReadLog(), "permission-asked"), "the CLI asked once");

            Assert.IsTrue(AgentHub.RespondToPendingPermission(AgentHub.PendingPermission.RequestId,
                PermissionDecision.AllowTool()), "the answer must be accepted");
            yield return WaitUntil(delegate { return Count(ReadLog(), "result pid=") == 1; },
                "the turn to finish once the prompt is answered");
            string log = ReadLog();
            Assert.AreEqual(1, Count(log, "permission-answered"), "answered once\n" + log);
            StringAssert.Contains("behavior=allow", log);
            Assert.AreEqual(1, Count(log, "start pid="), "one CLI process for the whole test\n" + log);

            yield return CleanupAndAssertProcessesGone();
        }

        // -- A relay that is gone after the reload: the old behaviour takes over -----

        [UnityTest]
        public IEnumerator RelayLostDuringReload_FallsBackToResume()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live relay test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }

            yield return StartThroughRelay();
            CompileGate.SendOrQueue("hello");
            yield return WaitUntil(delegate
            {
                return AgentHub.Client != null && AgentHub.Client.State == AgentClientState.ToolRunning;
            }, "tool_use (mid-turn)");
            RememberProcesses();

            // Registered after ReloadLifecycle's own handler, so it runs
            // after the detach: the relay dies while no domain is attached.
            SessionState.SetBool(KeyKillRelay, true);
            AssemblyReloadEvents.beforeAssemblyReload += KillRelayAtReload;
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            // No locals in the lambdas below: after a reload the test
            // framework restores the coroutine's position, not the closure
            // object the compiler would keep captured locals in.
            yield return WaitUntil(delegate
            {
                return AgentHub.Client != null && AgentHub.Client.State == AgentClientState.Ready;
            }, "the resumed client");
            Assert.IsTrue(AgentHub.ResumedMidTurn, "the turn WAS cut short this time");
            Assert.AreEqual(SessionState.GetString(KeySid, string.Empty), AgentHub.Client.SessionId,
                "same session, resumed");
            Assert.AreNotEqual(SessionState.GetInt(KeyRelayPid, 0), SessionStateBridge.Pid,
                "a new process was started");
            StringAssert.Contains("resumed=1 session=" + SessionState.GetString(KeySid, string.Empty), ReadLog());
            yield return WaitUntil(delegate { return !IsAlive(SessionState.GetInt(KeyCliPid, 0)); },
                "the old CLI to end");

            yield return CleanupAndAssertProcessesGone();
        }

        private static void KillRelayAtReload()
        {
            if (!SessionState.GetBool(KeyKillRelay, false))
            {
                return;
            }
            SessionState.EraseBool(KeyKillRelay);
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(SessionState.GetInt(KeyRelayPid, 0)))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
            }
        }

        // -- Shared pieces -----------------------------------------------------------

        private static IEnumerator StartThroughRelay()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            SessionState.SetString(KeyOrigPath, settings.cliManualPath ?? string.Empty);
            SessionState.SetBool(KeyOrigRelay, settings.cliRelayEnabled);
            SessionState.SetBool(KeyOrigAutoContinue, settings.autoContinueInterruptedTurn);
            settings.cliManualPath = FakeCliPath;
            settings.cliRelayEnabled = true;
            settings.autoContinueInterruptedTurn = false;
            PanelStateStore.instance.SaveNow();

            string log = Path.Combine(Path.GetTempPath(),
                "uap-fake-cli-relay-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            SessionState.SetString(KeyLog, log);
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_LOG", log);

            // The first spawn of an editor session may have to wait for
            // the relay program to compile.
            AgentHub.RelayBuildWaitMillis = 120000;
            AgentHub.StartFresh();
            AgentHub.RelayBuildWaitMillis = AgentHub.DefaultRelayBuildWaitMillis;
            Assert.IsNull(AgentHub.LastError, "spawn failed: " + AgentHub.LastError);
            if (!AgentHub.UsingCliRelay)
            {
                RestoreSettings();
                Assert.Ignore("The CLI relay could not be built or started in this editor: "
                    + Colloid.AgentPanel.Core.Process.RelayCliTransport.LastFailure);
            }
            yield return WaitUntil(delegate
            {
                return AgentHub.Client != null && AgentHub.Client.State == AgentClientState.Ready;
            }, "initial Ready");
            Assert.IsFalse(string.IsNullOrEmpty(AgentHub.Client.SessionId), "system/init must carry a session id");
            SessionState.SetString(KeySid, AgentHub.Client.SessionId);
        }

        private static void RememberProcesses()
        {
            int relayPid = SessionStateBridge.Pid;
            Assert.Greater(relayPid, 0, "the relay's pid must be recorded");
            SessionState.SetInt(KeyRelayPid, relayPid);
            SessionState.SetInt(KeyCliPid, CliPidFromLog());
            Assert.Greater(SessionState.GetInt(KeyCliPid, 0), 0, "the stand-in must have logged its pid");
        }

        private static void AssertSameProcesses(string phase)
        {
            int relayPid = SessionState.GetInt(KeyRelayPid, 0);
            int cliPid = SessionState.GetInt(KeyCliPid, 0);
            Assert.IsTrue(AgentHub.UsingCliRelay, phase + ": the client must be on the relay");
            Assert.AreEqual(relayPid, SessionStateBridge.Pid, phase + ": the same relay process");
            Assert.IsTrue(IsAlive(relayPid), phase + ": the relay must be alive");
            Assert.IsTrue(IsAlive(cliPid), phase + ": the CLI must be alive");
            Assert.AreEqual(SessionState.GetString(KeySid, string.Empty), SessionStateBridge.CurrentSessionId,
                phase + ": the same session");
        }

        private static IEnumerator CleanupAndAssertProcessesGone()
        {
            int relayPid = SessionStateBridge.Pid;
            int cliPid = CliPidFromLog();
            AgentHub.Shutdown();
            RestoreSettings();
            yield return WaitUntil(delegate { return !IsAlive(relayPid) && !IsAlive(cliPid); },
                "the relay and the CLI to end with the shutdown");
        }

        private static void RestoreSettings()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.cliManualPath = SessionState.GetString(KeyOrigPath, string.Empty);
            settings.cliRelayEnabled = SessionState.GetBool(KeyOrigRelay, false);
            settings.autoContinueInterruptedTurn = SessionState.GetBool(KeyOrigAutoContinue, false);
            PanelStateStore.instance.SaveNow();
            // "0", not null: clearing a variable did not reach the next
            // child process on Unity's Mono, and a leftover value changes
            // what the stand-in does for every later live test.
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_STREAM_N", "0");
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_STREAM_INTERVAL_MS", "100");
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_ASK_PERMISSION", "0");
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_COMPLETE_TURN", "0");
            SessionState.EraseString(KeyOrigPath);
            SessionState.EraseBool(KeyOrigRelay);
            SessionState.EraseBool(KeyOrigAutoContinue);
            SessionState.EraseBool(KeyKillRelay);
            SessionState.EraseString(KeySid);
            SessionState.EraseInt(KeyRelayPid);
            SessionState.EraseInt(KeyCliPid);
            SessionState.EraseString(KeyLog);
        }

        private static string ReadLog()
        {
            string log = SessionState.GetString(KeyLog, string.Empty);
            try
            {
                return File.Exists(log) ? File.ReadAllText(log) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        /// <summary>The pid on the stand-in's LAST "start pid=..." log line.</summary>
        private static int CliPidFromLog()
        {
            string log = ReadLog();
            int at = log.LastIndexOf("start pid=", StringComparison.Ordinal);
            if (at < 0)
            {
                return 0;
            }
            at += "start pid=".Length;
            int end = at;
            while (end < log.Length && char.IsDigit(log[end]))
            {
                end++;
            }
            int pid;
            return int.TryParse(log.Substring(at, end - at), out pid) ? pid : 0;
        }

        private static int Count(string text, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        private static int CountInTranscript(string needle)
        {
            int count = 0;
            var messages = AgentHub.Session.messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].role != ChatMessage.RoleAssistant)
                {
                    continue;
                }
                for (int b = 0; b < messages[i].blocks.Count; b++)
                {
                    string text = messages[i].blocks[b].text;
                    if (messages[i].blocks[b].kind == ChatBlockKind.Text && text != null)
                    {
                        count += Count(text, needle);
                    }
                }
            }
            return count;
        }

        private static string DumpTranscript()
        {
            var sb = new System.Text.StringBuilder("--- transcript ---\n");
            var messages = AgentHub.Session.messages;
            for (int i = 0; i < messages.Count; i++)
            {
                sb.Append(messages[i].role).Append(" t").Append(messages[i].turnId).Append(": ");
                for (int b = 0; b < messages[i].blocks.Count; b++)
                {
                    sb.Append('[').Append(messages[i].blocks[b].kind).Append("] ")
                        .Append((messages[i].blocks[b].text ?? string.Empty).Replace("\n", "\\n")).Append(' ');
                }
                sb.Append('\n');
            }
            sb.Append("--- stand-in log ---\n").Append(ReadLog());
            return sb.ToString();
        }

        private static IEnumerator WaitUntil(Func<bool> condition, string what)
        {
            double deadline = EditorApplication.timeSinceStartup + StateTimeoutSeconds;
            while (!condition())
            {
                if (AgentHub.Client != null && AgentHub.Client.State == AgentClientState.Errored)
                {
                    Assert.Fail("waiting for " + what + ": client errored (LastError="
                        + AgentHub.LastError + ")\n" + DumpTranscript());
                }
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail("waiting for " + what + ": timed out after " + StateTimeoutSeconds
                        + "s (state=" + (AgentHub.Client != null ? AgentHub.Client.State.ToString() : "null")
                        + ", LastError=" + AgentHub.LastError + ")\n" + DumpTranscript());
                }
                yield return null;
            }
        }

        private static bool IsAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                // An orphan that has exited stays in the process table as
                // a zombie until its new parent reaps it, and in a
                // container whose PID 1 never does, it stays for good.
                // It is not running: read the state from /proc when there.
                string stat = "/proc/" + pid + "/stat";
                if (File.Exists(stat))
                {
                    string text = File.ReadAllText(stat);
                    int close = text.LastIndexOf(')');
                    if (close >= 0 && close + 2 < text.Length && text[close + 2] == 'Z')
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                // Gone between the check and the read, or no /proc: fall through.
            }
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

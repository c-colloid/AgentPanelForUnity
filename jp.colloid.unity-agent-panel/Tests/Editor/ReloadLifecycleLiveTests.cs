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
    /// REAL domain-reload survival, end to end, against a stand-in CLI
    /// (ci/FakeCli/claude) spawned as a genuine child process through
    /// ClaudeCliProcess: spawn -> Ready -> open a turn (the stand-in
    /// answers with one tool_use and then stays silent, so the turn is
    /// mid-flight) -> a real domain reload (EditorUtility.RequestScriptReload,
    /// or entering Play Mode) -> the panel must kill the old process,
    /// respawn with --resume for the SAME session id, and flag the resume
    /// as mid-turn so the "interrupted" nudge / auto-continue can act.
    ///
    /// Guarded like LiveCliIntegrationTests: self-ignores unless the
    /// environment variable UAP_FAKE_CLI names the stand-in's path, so an
    /// ordinary CI/editor run never spawns anything. The test coroutine
    /// is resumed after the reload with its locals reset (Unity Test
    /// Framework restores only the enumerator's position), so everything
    /// the post-reload half needs travels through SessionState.
    /// </summary>
    [Category("LiveReload")]
    public class ReloadLifecycleLiveTests
    {
        private const string KeySid = "Colloid.AgentPanel.Tests.Reload.Sid";
        private const string KeyPid = "Colloid.AgentPanel.Tests.Reload.Pid";
        private const string KeyLog = "Colloid.AgentPanel.Tests.Reload.Log";
        private const string KeyOrigPath = "Colloid.AgentPanel.Tests.Reload.OrigCliPath";
        private const string KeyOrigAutoContinue = "Colloid.AgentPanel.Tests.Reload.OrigAutoContinue";
        private const double StateTimeoutSeconds = 30.0;

        private static string FakeCliPath
        {
            get { return Environment.GetEnvironmentVariable("UAP_FAKE_CLI"); }
        }

        private static string NewLogPath()
        {
            return Path.Combine(Path.GetTempPath(),
                "uap-fake-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
        }

        // -- Script reload (the "saved a .cs in the IDE and came back" case) --

        [UnityTest]
        public IEnumerator MidTurn_ScriptReload_KillsOldProcess_ResumesSameSession_FlagsMidTurn()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live reload test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }

            // ---- before the reload ----
            yield return BeginMidTurn();

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            // ---- after the reload (locals are gone; read SessionState) ----
            yield return AssertResumedMidTurn("script reload");
            Cleanup();
        }

        // -- Interrupted-turn auto-continue (PanelSettings.autoContinueInterruptedTurn) --

        [UnityTest]
        public IEnumerator MidTurn_ScriptReload_WithAutoContinueOn_SendsContinueByItself()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live reload test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }

            yield return BeginMidTurn();
            PanelStateStore.instance.Settings.autoContinueInterruptedTurn = true;
            PanelStateStore.instance.SaveNow();

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            string sid = SessionState.GetString(KeySid, string.Empty);
            string log = SessionState.GetString(KeyLog, string.Empty);
            yield return WaitUntilConnected("auto-continue after script reload");
            int newPid = SessionStateBridge.Pid;
            Assert.AreEqual(sid, AgentHub.Client.SessionId, "same session id");

            // The continuation is queued by ReloadLifecycle and released by
            // the drain tick once the resumed client is Ready: the stand-in
            // logs the user message it receives.
            double deadline = EditorApplication.timeSinceStartup + StateTimeoutSeconds;
            string marker = "user pid=" + newPid;
            while (!(File.Exists(log) && File.ReadAllText(log).Contains(marker)))
            {
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail("the resumed stand-in never received the automatic continue"
                        + " (state=" + AgentHub.Client.State + ")\n--- stand-in log ---\n"
                        + (File.Exists(log) ? File.ReadAllText(log) : "<none>"));
                }
                yield return null;
            }
            yield return WaitForState(AgentClientState.ToolRunning, "the continued turn's tool_use");

            Assert.IsFalse(AgentHub.ResumedMidTurn,
                "once the continuation is sent the banner must stop offering Continue");
            Assert.IsTrue(TranscriptHasSystemNote(Colloid.AgentPanel.UI.L10n.S.HubAutoContinueInterruptedResuming),
                "the automatic send must be announced in the transcript");
            Cleanup();
        }

        private static bool TranscriptHasSystemNote(string text)
        {
            var messages = AgentHub.Session.messages;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].role != ChatMessage.RoleSystem)
                {
                    continue;
                }
                for (int b = 0; b < messages[i].blocks.Count; b++)
                {
                    if (messages[i].blocks[b].text != null && messages[i].blocks[b].text.Contains(text))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // -- Play Mode enter/exit (the "hit Play to test the game" case) --

        [UnityTest]
        public IEnumerator MidTurn_EnterPlayMode_ResumesSameSession_FlagsMidTurn_ThenExitKeepsIt()
        {
            if (string.IsNullOrEmpty(FakeCliPath))
            {
                Assert.Ignore("Live reload test skipped (set UAP_FAKE_CLI=<path to ci/FakeCli/claude>).");
                yield break;
            }
            Assert.IsFalse(EditorSettings.enterPlayModeOptionsEnabled
                    && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0,
                "this test needs the default 'domain reload on Play' project setting");

            yield return BeginMidTurn();

            yield return new EnterPlayMode();

            // Entering Play Mode reloaded the domain: same contract as a
            // script reload.
            yield return AssertResumedMidTurn("enter play mode");

            // Open a turn again in Play Mode so the exit is also observed
            // mid-turn, then leave. With the default settings the exit does
            // NOT reload the domain, so the same process must still be
            // there; if a reload does happen, the resume contract applies.
            string sid = SessionState.GetString(KeySid, string.Empty);
            int playPid = SessionStateBridge.Pid;
            CompileGate.SendOrQueue("hello again from play mode");
            yield return WaitForState(AgentClientState.ToolRunning, "tool_use in play mode");

            yield return new ExitPlayMode();

            yield return WaitUntilConnected("exit play mode");
            Assert.AreEqual(sid, AgentHub.Client.SessionId,
                "exiting Play Mode must keep the same session id");
            if (SessionStateBridge.Pid != playPid)
            {
                Assert.IsTrue(AgentHub.ResumedMidTurn,
                    "exit reloaded the domain, so the resume must be flagged mid-turn");
            }
            else
            {
                Assert.AreEqual(AgentClientState.ToolRunning, AgentHub.Client.State,
                    "no reload on exit: the open turn must still be open on the same process");
            }
            Cleanup();
        }

        // -- Shared halves --------------------------------------------------------

        /// <summary>
        /// Points the panel at the stand-in, spawns a fresh session, waits
        /// for Ready, opens a turn and waits for ToolRunning; then parks
        /// session id / pid / log path in SessionState for the other half.
        /// </summary>
        private IEnumerator BeginMidTurn()
        {
            PanelSettings settings = PanelStateStore.instance.Settings;
            SessionState.SetString(KeyOrigPath, settings.cliManualPath ?? string.Empty);
            SessionState.SetBool(KeyOrigAutoContinue, settings.autoContinueInterruptedTurn);
            settings.cliManualPath = FakeCliPath;
            PanelStateStore.instance.SaveNow();

            string log = NewLogPath();
            SessionState.SetString(KeyLog, log);
            Environment.SetEnvironmentVariable("UAP_FAKE_CLI_LOG", log);

            AgentHub.StartFresh();
            Assert.IsNull(AgentHub.LastError, "spawn failed: " + AgentHub.LastError);
            yield return WaitForState(AgentClientState.Ready, "initial Ready");

            string sid = AgentHub.Client.SessionId;
            Assert.IsFalse(string.IsNullOrEmpty(sid), "system/init must have carried a session id");

            CompileGate.SendOrQueue("hello");
            yield return WaitForState(AgentClientState.ToolRunning, "tool_use (mid-turn)");
            Assert.IsTrue(AgentHub.Client.TurnActive, "sanity: the turn must be open before the reload");

            int pid = SessionStateBridge.Pid;
            Assert.Greater(pid, 0, "the live pid must be recorded before the reload");
            SessionState.SetString(KeySid, sid);
            SessionState.SetInt(KeyPid, pid);
        }

        /// <summary>
        /// The post-reload contract (ReloadLifecycle -> AgentHub.RestoreAfterReload):
        /// a client is back, it is the SAME session id, it was started with
        /// --resume, the pre-reload process is dead, and ResumedMidTurn is
        /// set (what ResumeBanner's "interrupted" nudge and the auto-continue
        /// path key off).
        /// </summary>
        private IEnumerator AssertResumedMidTurn(string phase)
        {
            string sid = SessionState.GetString(KeySid, string.Empty);
            int oldPid = SessionState.GetInt(KeyPid, 0);
            string log = SessionState.GetString(KeyLog, string.Empty);
            Assert.IsFalse(string.IsNullOrEmpty(sid), phase + ": SessionState lost the session id");

            yield return WaitUntilConnected(phase);

            Assert.IsTrue(AgentHub.ResumedMidTurn,
                phase + ": the resume must be flagged mid-turn (TurnRunning survived the reload "
                + "and nothing clobbered it before RestoreAfterReload read it)");
            Assert.AreEqual(sid, AgentHub.Client.SessionId,
                phase + ": the resumed client must carry the same session id");
            Assert.AreEqual(sid, SessionStateBridge.CurrentSessionId,
                phase + ": SessionStateBridge must still point at that session");

            int newPid = SessionStateBridge.Pid;
            Assert.Greater(newPid, 0, phase + ": the resumed process must be recorded");
            Assert.AreNotEqual(oldPid, newPid, phase + ": the resume must be a NEW process");
            Assert.IsFalse(IsAlive(oldPid), phase + ": the pre-reload process must be dead");

            string logText = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
            StringAssert.Contains("resumed=1 session=" + sid, logText,
                phase + ": the stand-in must have been respawned with --resume " + sid
                + "\n--- stand-in log ---\n" + logText);
            StringAssert.Contains("interrupt pid=" + oldPid, logText,
                phase + ": the old process must have received the interrupt before the kill"
                + "\n--- stand-in log ---\n" + logText);
        }

        private static IEnumerator WaitUntilConnected(string phase)
        {
            double deadline = EditorApplication.timeSinceStartup + StateTimeoutSeconds;
            while (AgentHub.Client == null
                || AgentHub.Client.State == AgentClientState.Starting
                || AgentHub.Client.State == AgentClientState.NotStarted)
            {
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail(phase + ": no connected client within " + StateTimeoutSeconds
                        + "s (state=" + (AgentHub.Client != null ? AgentHub.Client.State.ToString() : "null")
                        + ", LastError=" + AgentHub.LastError + ")");
                }
                yield return null;
            }
            Assert.AreNotEqual(AgentClientState.Errored, AgentHub.Client.State,
                phase + ": client errored (LastError=" + AgentHub.LastError + ")");
        }

        private static IEnumerator WaitForState(AgentClientState wanted, string what)
        {
            double deadline = EditorApplication.timeSinceStartup + StateTimeoutSeconds;
            while (AgentHub.Client == null || AgentHub.Client.State != wanted)
            {
                if (AgentHub.Client != null && AgentHub.Client.State == AgentClientState.Errored)
                {
                    Assert.Fail("waiting for " + what + ": client errored (LastError="
                        + AgentHub.LastError + ")");
                }
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail("waiting for " + what + ": timed out after " + StateTimeoutSeconds
                        + "s (state=" + (AgentHub.Client != null ? AgentHub.Client.State.ToString() : "null")
                        + ", LastError=" + AgentHub.LastError + ")");
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

        private static void Cleanup()
        {
            AgentHub.Shutdown();
            PanelSettings settings = PanelStateStore.instance.Settings;
            settings.cliManualPath = SessionState.GetString(KeyOrigPath, string.Empty);
            settings.autoContinueInterruptedTurn = SessionState.GetBool(KeyOrigAutoContinue, false);
            PanelStateStore.instance.SaveNow();
            SessionState.EraseBool(KeyOrigAutoContinue);
            SessionState.EraseString(KeySid);
            SessionState.EraseInt(KeyPid);
            SessionState.EraseString(KeyLog);
            SessionState.EraseString(KeyOrigPath);
        }
    }
}

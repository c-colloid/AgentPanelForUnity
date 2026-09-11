using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Colloid.AgentPanel.Core.Client;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Process;
using Colloid.AgentPanel.Core.Protocol;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// End-to-end proof against the real, logged-in claude CLI:
    /// spawn -> initialize -> one full turn -> result -> clean shutdown.
    ///
    /// Guarded twice so normal CI/editor runs never touch the network:
    /// - [Category("LiveCli")] lets runners exclude it wholesale.
    /// - Unless the environment variable UAP_LIVE_CLI=1 is set for the
    ///   Unity process, the test self-ignores.
    ///
    /// The test also asserts process hygiene: the claude.exe it spawned is
    /// gone afterwards and no new claude.exe (started from the resolved CLI
    /// path) is left behind. Pre-existing claude processes (for example the
    /// Claude Desktop app) are snapshotted first and never touched.
    /// </summary>
    [Category("LiveCli")]
    public class LiveCliIntegrationTests
    {
        private const int InitTimeoutSeconds = 90;
        // 420s, not 180s: on API 529 "overloaded" the CLI walks a retry
        // ladder of up to 10 attempts with roughly doubling delays (observed
        // live 2026-07-30: attempt 7 waited 38s; attempts 8/9 extrapolate to
        // ~76s/~152s), so a transient overload storm can legitimately hold a
        // turn open for 6-7 minutes before succeeding. 180s failed twice on
        // exactly this, with the very next session completing in ~10s.
        private const int TurnTimeoutSeconds = 420;
        private const int ExitTimeoutSeconds = 15;

        [Test]
        public void FullTurn_SpawnInitializeSendResultShutdown_Succeeds()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live CLI test skipped (set UAP_LIVE_CLI=1 to enable).");
            }

            string cliPath = new WindowsCliPathProbe(null).Resolve();
            Assert.IsNotNull(cliPath, "WindowsCliPathProbe could not resolve claude.exe.");

            // Snapshot claude.exe PIDs before we spawn anything. Anything in
            // this set (e.g. the Claude Desktop app) is out of scope.
            HashSet<int> pidsBefore = SnapshotClaudePids();

            string workDir = Path.Combine(Path.GetTempPath(),
                "uap-live-cli-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workDir);

            var log = new StringBuilder();
            Action<string> logger = delegate(string s) { log.AppendLine(s); };

            var transport = new ClaudeCliProcess(new WindowsProcessKiller(logger), logger);
            var client = new AgentClient(transport, logger);

            var assistantText = new StringBuilder();
            bool turnCompleted = false;
            ResultMessage result = null;
            string diedReason = null;

            client.TextDelta += delegate(string t) { assistantText.Append(t); };
            client.AssistantMessageCompleted += delegate(AssistantMessage m)
            {
                for (int i = 0; i < m.Content.Length; i++)
                {
                    if (m.Content[i].Type == ContentBlockType.Text
                        && !string.IsNullOrEmpty(m.Content[i].Text))
                    {
                        // Finalized text is authoritative; deltas may repeat it.
                        assistantText.Append('\n').Append(m.Content[i].Text);
                    }
                }
            };
            client.TurnCompleted += delegate(ResultMessage r)
            {
                turnCompleted = true;
                result = r;
            };
            client.ProcessDied += delegate(string reason) { diedReason = reason; };
            client.RawLineForLog += delegate(string line)
            {
                if (line == null)
                {
                    return;
                }
                log.AppendLine(line.Length > 400
                    ? "[stdout] " + line.Substring(0, 400) + "...(truncated)"
                    : "[stdout] " + line);
            };

            int spawnedPid = -1;
            try
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = cliPath,
                    WorkingDirectory = workDir
                });
                spawnedPid = transport.ProcessId;
                Assert.Greater(spawnedPid, 0, "CLI process id not captured after Start.");

                // Phase 1: pump until the session is initialized (Ready).
                // Ready flips on the initialize control_response; system/init
                // (which carries the session id) follows moments later, so we
                // give it a short non-fatal grace window here and hard-assert
                // the session id only after the turn completed.
                PumpUntil(client,
                    delegate { return client.State == AgentClientState.Ready; },
                    InitTimeoutSeconds,
                    "initialize handshake (state=Ready)",
                    log,
                    delegate { return diedReason; });
                DateTime initGrace = DateTime.UtcNow.AddSeconds(10);
                while (client.SessionId == null && DateTime.UtcNow < initGrace)
                {
                    client.Pump(50, 5.0);
                    Thread.Sleep(25);
                }

                // Phase 2: one full turn.
                client.SendUserText("Reply with exactly: OK");
                PumpUntil(client,
                    delegate { return turnCompleted; },
                    TurnTimeoutSeconds,
                    "turn result",
                    log,
                    delegate { return diedReason; });

                Assert.IsNotNull(result, "TurnCompleted fired without a ResultMessage.");
                Assert.IsNotNull(client.SessionId,
                    "SessionId still unset after a completed turn (system/init never arrived). Log:\n"
                    + Tail(log));
                StringAssert.Contains("OK", assistantText.ToString(),
                    "Assistant text did not contain OK. Log:\n" + Tail(log));

                // Phase 3: clean shutdown.
                client.Stop();
                WaitFor(delegate { return !transport.IsRunning; }, ExitTimeoutSeconds);
                Assert.IsFalse(transport.IsRunning,
                    "CLI process still running " + ExitTimeoutSeconds + "s after Stop().");
                Assert.IsFalse(IsProcessAlive(spawnedPid),
                    "Spawned claude.exe PID " + spawnedPid + " is still alive after Stop().");
            }
            finally
            {
                client.Dispose();
                // Hygiene sweep: kill anything WE leaked before asserting, so a
                // red test never strands a live CLI process. Only processes
                // that (a) did not exist before and (b) run our resolved
                // claude.exe are candidates -- Claude Desktop is never touched.
                List<int> leaked = FindLeakedClaudePids(pidsBefore, cliPath);
                if (leaked.Count > 0)
                {
                    var killer = new WindowsProcessKiller(logger);
                    for (int i = 0; i < leaked.Count; i++)
                    {
                        killer.KillTree(leaked[i]);
                    }
                }
                TryDeleteDirectory(workDir);
                if (leaked.Count > 0)
                {
                    Assert.Fail("Leaked claude.exe PID(s) after the test: "
                        + string.Join(", ", leaked)
                        + " (killed by the hygiene sweep). Log:\n" + Tail(log));
                }
            }
        }

        /// <summary>
        /// Live permission round trip: prompt the model into a Write tool
        /// call, receive the can_use_tool control_request, answer allow with
        /// the original input, and verify the file really appears on disk.
        /// Same double gating and process hygiene as the full-turn test.
        /// If the model answers in text without calling the tool (observed
        /// flakiness), the test retries once in the same session with a more
        /// forceful prompt before failing.
        /// </summary>
        [Test]
        public void PermissionRoundTrip_AllowWriteTool_CreatesFile()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live CLI test skipped (set UAP_LIVE_CLI=1 to enable).");
            }

            string cliPath = new WindowsCliPathProbe(null).Resolve();
            Assert.IsNotNull(cliPath, "WindowsCliPathProbe could not resolve claude.exe.");

            HashSet<int> pidsBefore = SnapshotClaudePids();

            string workDir = Path.Combine(Path.GetTempPath(),
                "uap-live-perm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workDir);

            var log = new StringBuilder();
            Action<string> logger = delegate(string s) { log.AppendLine(s); };

            var transport = new ClaudeCliProcess(new WindowsProcessKiller(logger), logger);
            var client = new AgentClient(transport, logger);

            bool turnCompleted = false;
            string diedReason = null;
            ControlRequestMessage pendingRequest = null;
            var permissionToolNames = new List<string>();

            client.TurnCompleted += delegate(ResultMessage r) { turnCompleted = true; };
            client.ProcessDied += delegate(string reason) { diedReason = reason; };
            client.PermissionRequested += delegate(ControlRequestMessage m)
            {
                pendingRequest = m;
                if (m.CanUseTool != null)
                {
                    permissionToolNames.Add(m.CanUseTool.ToolName);
                }
            };
            client.RawLineForLog += delegate(string line)
            {
                if (line == null)
                {
                    return;
                }
                log.AppendLine(line.Length > 400
                    ? "[stdout] " + line.Substring(0, 400) + "...(truncated)"
                    : "[stdout] " + line);
            };

            int spawnedPid = -1;
            try
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = cliPath,
                    WorkingDirectory = workDir
                });
                spawnedPid = transport.ProcessId;
                Assert.Greater(spawnedPid, 0, "CLI process id not captured after Start.");

                PumpUntil(client,
                    delegate { return client.State == AgentClientState.Ready; },
                    InitTimeoutSeconds,
                    "initialize handshake (state=Ready)",
                    log,
                    delegate { return diedReason; });

                // Attempt 1, then one forceful retry if the model answered in
                // plain text without ever calling the tool.
                string[] prompts = new string[]
                {
                    "Use the Write tool to create exactly the file "
                        + "live_perm_test.txt containing OK, then reply DONE.",
                    "You MUST call the Write tool. Do not reply with any text "
                        + "before the tool call. Call the Write tool now to "
                        + "create the file live_perm_test.txt with the exact "
                        + "content OK. Only after the tool call succeeds, "
                        + "reply DONE."
                };

                bool sawPermission = false;
                for (int attempt = 0; attempt < prompts.Length && !sawPermission; attempt++)
                {
                    turnCompleted = false;
                    pendingRequest = null;
                    Assert.IsTrue(client.SendUserText(prompts[attempt]),
                        "SendUserText refused the prompt (attempt " + (attempt + 1)
                        + ", state=" + client.State + ").");

                    // Pump the whole turn; answer every can_use_tool prompt
                    // with allow(original input) as it arrives.
                    DateTime deadline = DateTime.UtcNow.AddSeconds(TurnTimeoutSeconds);
                    while (!turnCompleted)
                    {
                        if (diedReason != null)
                        {
                            Assert.Fail("CLI process died during the permission turn: "
                                + diedReason + "\nLog:\n" + Tail(log));
                        }
                        if (DateTime.UtcNow > deadline)
                        {
                            Assert.Fail("Timed out (" + TurnTimeoutSeconds
                                + "s) waiting for the permission turn to complete."
                                + "\nLog:\n" + Tail(log));
                        }
                        if (pendingRequest != null)
                        {
                            ControlRequestMessage request = pendingRequest;
                            pendingRequest = null;
                            sawPermission = true;
                            Assert.IsNotNull(request.CanUseTool,
                                "PermissionRequested fired without a CanUseTool payload."
                                + "\nLog:\n" + Tail(log));
                            Assert.AreEqual("Write", request.CanUseTool.ToolName,
                                "Expected a Write tool permission prompt but got '"
                                + request.CanUseTool.ToolName + "'.\nLog:\n" + Tail(log));
                            client.RespondToPermission(request.RequestId,
                                PermissionDecision.AllowTool(request.CanUseTool.Input));
                        }
                        client.Pump(50, 5.0);
                        Thread.Sleep(25);
                    }
                }

                Assert.IsTrue(sawPermission,
                    "PermissionRequested never fired: the model completed "
                    + prompts.Length + " turn(s) without calling the Write tool."
                    + " Tool prompts seen: [" + string.Join(", ", permissionToolNames)
                    + "]\nLog:\n" + Tail(log));

                string expectedFile = Path.Combine(workDir, "live_perm_test.txt");
                Assert.IsTrue(File.Exists(expectedFile),
                    "live_perm_test.txt was not created in " + workDir
                    + " after the allowed Write.\nLog:\n" + Tail(log));
                StringAssert.Contains("OK", File.ReadAllText(expectedFile),
                    "live_perm_test.txt exists but does not contain OK.");

                client.Stop();
                WaitFor(delegate { return !transport.IsRunning; }, ExitTimeoutSeconds);
                Assert.IsFalse(transport.IsRunning,
                    "CLI process still running " + ExitTimeoutSeconds + "s after Stop().");
                Assert.IsFalse(IsProcessAlive(spawnedPid),
                    "Spawned claude.exe PID " + spawnedPid + " is still alive after Stop().");
            }
            finally
            {
                client.Dispose();
                List<int> leaked = FindLeakedClaudePids(pidsBefore, cliPath);
                if (leaked.Count > 0)
                {
                    var killer = new WindowsProcessKiller(logger);
                    for (int i = 0; i < leaked.Count; i++)
                    {
                        killer.KillTree(leaked[i]);
                    }
                }
                TryDeleteDirectory(workDir);
                if (leaked.Count > 0)
                {
                    Assert.Fail("Leaked claude.exe PID(s) after the test: "
                        + string.Join(", ", leaked)
                        + " (killed by the hygiene sweep). Log:\n" + Tail(log));
                }
            }
        }

        /// <summary>
        /// Live set_model round trip: spawn a real session, wait for the
        /// initialize handshake (Ready), pick a real model id off
        /// InitializeResponse's models[] list, send set_model, and assert
        /// the matching control_response comes back success=true with no
        /// error via the new AgentClient.ControlRequestResolved event
        /// (added alongside this test so the CLI-generated request id never
        /// needs to be guessed by the test). Same double gating and process
        /// hygiene as the other two live tests.
        /// </summary>
        [Test]
        public void SetModel_LiveRoundTrip()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live CLI test skipped (set UAP_LIVE_CLI=1 to enable).");
            }

            string cliPath = new WindowsCliPathProbe(null).Resolve();
            Assert.IsNotNull(cliPath, "WindowsCliPathProbe could not resolve claude.exe.");

            HashSet<int> pidsBefore = SnapshotClaudePids();

            string workDir = Path.Combine(Path.GetTempPath(),
                "uap-live-setmodel-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workDir);

            var log = new StringBuilder();
            Action<string> logger = delegate(string s) { log.AppendLine(s); };

            var transport = new ClaudeCliProcess(new WindowsProcessKiller(logger), logger);
            var client = new AgentClient(transport, logger);

            string diedReason = null;
            bool resolved = false;
            bool resolvedSuccess = false;
            string resolvedError = null;
            string resolvedKind = null;

            client.ProcessDied += delegate(string reason) { diedReason = reason; };
            client.ControlRequestResolved += delegate(string kind, bool success, string error)
            {
                if (kind != "set_model")
                {
                    return;
                }
                resolved = true;
                resolvedSuccess = success;
                resolvedError = error;
                resolvedKind = kind;
            };
            client.RawLineForLog += delegate(string line)
            {
                if (line == null)
                {
                    return;
                }
                log.AppendLine(line.Length > 400
                    ? "[stdout] " + line.Substring(0, 400) + "...(truncated)"
                    : "[stdout] " + line);
            };

            int spawnedPid = -1;
            try
            {
                client.Start(new AgentClientOptions
                {
                    CliPath = cliPath,
                    WorkingDirectory = workDir
                });
                spawnedPid = transport.ProcessId;
                Assert.Greater(spawnedPid, 0, "CLI process id not captured after Start.");

                PumpUntil(client,
                    delegate { return client.State == AgentClientState.Ready; },
                    InitTimeoutSeconds,
                    "initialize handshake (state=Ready)",
                    log,
                    delegate { return diedReason; });

                Assert.IsNotNull(client.InitializeResponse,
                    "State reached Ready but InitializeResponse is still null.\nLog:\n" + Tail(log));
                JsonNode models = client.InitializeResponse.Response["models"];
                Assert.IsTrue(models.IsArray && models.Count > 0,
                    "initialize response has no non-empty models[] array to pick from.\nLog:\n"
                    + Tail(log));
                string modelId = null;
                for (int i = 0; i < models.Count && modelId == null; i++)
                {
                    // Observed shapes across fixtures: either a plain string
                    // entry or an object with an "id"/"model" field.
                    JsonNode entry = models[i];
                    if (entry.IsString)
                    {
                        modelId = entry.AsString(null);
                    }
                    else if (entry.IsObject)
                    {
                        // Real initialize response shape (verified against
                        // Fixtures/out_bidi.jsonl and HeaderView.ParseModels):
                        // each entry is {"value": "<model id to send>",
                        // "resolvedModel": "...", "displayName": "...", ...}
                        // -- there is no "id"/"model" key.
                        modelId = entry["value"].AsString(null);
                    }
                }
                Assert.IsFalse(string.IsNullOrEmpty(modelId),
                    "Could not extract a model id from initialize response models[].\nLog:\n"
                    + Tail(log));

                client.SetModel(modelId);
                PumpUntil(client,
                    delegate { return resolved; },
                    InitTimeoutSeconds,
                    "set_model control_response",
                    log,
                    delegate { return diedReason; });

                Assert.AreEqual("set_model", resolvedKind);
                Assert.IsTrue(resolvedSuccess,
                    "set_model control_response was not success for model '" + modelId
                    + "': " + (resolvedError ?? "<no error text>") + "\nLog:\n" + Tail(log));
                Assert.IsNull(resolvedError,
                    "set_model succeeded but still carried an error string.\nLog:\n" + Tail(log));

                client.Stop();
                WaitFor(delegate { return !transport.IsRunning; }, ExitTimeoutSeconds);
                Assert.IsFalse(transport.IsRunning,
                    "CLI process still running " + ExitTimeoutSeconds + "s after Stop().");
                Assert.IsFalse(IsProcessAlive(spawnedPid),
                    "Spawned claude.exe PID " + spawnedPid + " is still alive after Stop().");
            }
            finally
            {
                client.Dispose();
                List<int> leaked = FindLeakedClaudePids(pidsBefore, cliPath);
                if (leaked.Count > 0)
                {
                    var killer = new WindowsProcessKiller(logger);
                    for (int i = 0; i < leaked.Count; i++)
                    {
                        killer.KillTree(leaked[i]);
                    }
                }
                TryDeleteDirectory(workDir);
                if (leaked.Count > 0)
                {
                    Assert.Fail("Leaked claude.exe PID(s) after the test: "
                        + string.Join(", ", leaked)
                        + " (killed by the hygiene sweep). Log:\n" + Tail(log));
                }
            }
        }

        /// <summary>
        /// Live settings-auto-apply round trip (docs/design-notes/2026-08-01-
        /// settings-auto-apply.md section 5): a custom-instructions change is
        /// applied exactly the way AgentHub.Reconnect() applies ANY
        /// next-spawn-only settings change (model, tool lists,
        /// dangerouslySkipPermissions, custom instructions -- see
        /// SettingsChangeDetector) -- kill the CLI process and respawn it
        /// with --resume the SAME session id, this time with a new
        /// --append-system-prompt. Proves two things end to end against the
        /// real CLI: (1) the session id survives the respawn (the
        /// conversation is not lost), and (2) the new custom instruction
        /// actually takes effect on the resumed session (the model's very
        /// next reply reflects it). AgentHub's own debounce/idle-gate timing
        /// (AutoApplySettingsPolicy, the EditorApplication.update pump) is
        /// Unity-editor state machinery covered by
        /// AutoApplySettingsPolicyTests instead; this test only proves the
        /// underlying CLI protocol round trip that AgentHub.Reconnect()
        /// relies on still behaves with a changed AppendSystemPrompt.
        /// </summary>
        [Test]
        public void AutoApplyReconnect_CustomInstructionsChange_PreservesSessionId()
        {
            if (Environment.GetEnvironmentVariable("UAP_LIVE_CLI") != "1")
            {
                Assert.Ignore("Live CLI test skipped (set UAP_LIVE_CLI=1 to enable).");
            }

            string cliPath = new WindowsCliPathProbe(null).Resolve();
            Assert.IsNotNull(cliPath, "WindowsCliPathProbe could not resolve claude.exe.");

            HashSet<int> pidsBefore = SnapshotClaudePids();

            string workDir = Path.Combine(Path.GetTempPath(),
                "uap-live-autoapply-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workDir);

            var log = new StringBuilder();
            Action<string> logger = delegate(string s) { log.AppendLine(s); };
            const string magicWord = "BANANA-AUTOAPPLY-42";

            ClaudeCliProcess transport1 = null;
            AgentClient client1 = null;
            ClaudeCliProcess transport2 = null;
            AgentClient client2 = null;
            int spawnedPid2 = -1;

            try
            {
                // -- First spawn: no custom instructions. ------------------------
                transport1 = new ClaudeCliProcess(new WindowsProcessKiller(logger), logger);
                client1 = new AgentClient(transport1, logger);
                bool turn1Completed = false;
                string diedReason1 = null;
                client1.TurnCompleted += delegate(ResultMessage r) { turn1Completed = true; };
                client1.ProcessDied += delegate(string reason) { diedReason1 = reason; };
                client1.RawLineForLog += delegate(string line)
                {
                    if (line == null)
                    {
                        return;
                    }
                    log.AppendLine(line.Length > 400
                        ? "[stdout1] " + line.Substring(0, 400) + "...(truncated)"
                        : "[stdout1] " + line);
                };

                client1.Start(new AgentClientOptions { CliPath = cliPath, WorkingDirectory = workDir });
                int spawnedPid1 = transport1.ProcessId;
                Assert.Greater(spawnedPid1, 0, "CLI process id not captured after the first Start.");

                PumpUntil(client1,
                    delegate { return client1.State == AgentClientState.Ready; },
                    InitTimeoutSeconds, "first initialize handshake (state=Ready)", log,
                    delegate { return diedReason1; });

                client1.SendUserText("Reply with exactly: FIRST");
                PumpUntil(client1,
                    delegate { return turn1Completed; },
                    TurnTimeoutSeconds, "first turn result", log,
                    delegate { return diedReason1; });

                string sessionId = client1.SessionId;
                Assert.IsFalse(string.IsNullOrEmpty(sessionId),
                    "SessionId still unset after the first completed turn.\nLog:\n" + Tail(log));

                // Simulates AgentHub.Reconnect(): kill+Dispose the first
                // client, then respawn a NEW client with --resume + the
                // changed AppendSystemPrompt -- exactly what the settings
                // auto-apply feature triggers once the client goes idle.
                client1.Stop();
                WaitFor(delegate { return !transport1.IsRunning; }, ExitTimeoutSeconds);
                Assert.IsFalse(transport1.IsRunning,
                    "First CLI process still running " + ExitTimeoutSeconds + "s after Stop().");
                client1.Dispose();
                client1 = null;

                // -- Second spawn: --resume same session id, NEW custom instructions. --
                transport2 = new ClaudeCliProcess(new WindowsProcessKiller(logger), logger);
                client2 = new AgentClient(transport2, logger);
                var assistantText2 = new StringBuilder();
                bool turn2Completed = false;
                string diedReason2 = null;
                client2.AssistantMessageCompleted += delegate(AssistantMessage m)
                {
                    for (int i = 0; i < m.Content.Length; i++)
                    {
                        if (m.Content[i].Type == ContentBlockType.Text
                            && !string.IsNullOrEmpty(m.Content[i].Text))
                        {
                            assistantText2.Append('\n').Append(m.Content[i].Text);
                        }
                    }
                };
                client2.TurnCompleted += delegate(ResultMessage r) { turn2Completed = true; };
                client2.ProcessDied += delegate(string reason) { diedReason2 = reason; };
                client2.RawLineForLog += delegate(string line)
                {
                    if (line == null)
                    {
                        return;
                    }
                    log.AppendLine(line.Length > 400
                        ? "[stdout2] " + line.Substring(0, 400) + "...(truncated)"
                        : "[stdout2] " + line);
                };

                client2.Start(new AgentClientOptions
                {
                    CliPath = cliPath,
                    WorkingDirectory = workDir,
                    ResumeSessionId = sessionId,
                    AppendSystemPrompt = "Whenever you reply to the user, include the exact "
                        + "token " + magicWord + " somewhere in your reply."
                });
                spawnedPid2 = transport2.ProcessId;
                Assert.Greater(spawnedPid2, 0,
                    "CLI process id not captured after the second (resume) Start.");

                PumpUntil(client2,
                    delegate { return client2.State == AgentClientState.Ready; },
                    InitTimeoutSeconds, "resumed initialize handshake (state=Ready)", log,
                    delegate { return diedReason2; });

                client2.SendUserText("Reply with exactly one short sentence.");
                PumpUntil(client2,
                    delegate { return turn2Completed; },
                    TurnTimeoutSeconds, "second (resumed) turn result", log,
                    delegate { return diedReason2; });

                // (1) Same session id: the conversation was preserved across
                // the kill+respawn, exactly like AgentHub.Reconnect().
                Assert.AreEqual(sessionId, client2.SessionId,
                    "Resumed session id changed after the auto-apply-style reconnect.\nLog:\n"
                    + Tail(log));

                // (2) The NEW custom instructions actually took effect on the
                // resumed session -- the whole point of respawning instead of
                // just trusting --resume alone to pick up a live config
                // change.
                StringAssert.Contains(magicWord, assistantText2.ToString(),
                    "Resumed reply did not reflect the newly appended system prompt.\nLog:\n"
                    + Tail(log));

                client2.Stop();
                WaitFor(delegate { return !transport2.IsRunning; }, ExitTimeoutSeconds);
                Assert.IsFalse(transport2.IsRunning,
                    "Second CLI process still running " + ExitTimeoutSeconds + "s after Stop().");
                Assert.IsFalse(IsProcessAlive(spawnedPid2),
                    "Spawned (resumed) claude.exe PID " + spawnedPid2 + " is still alive after Stop().");
            }
            finally
            {
                if (client1 != null)
                {
                    client1.Dispose();
                }
                if (client2 != null)
                {
                    client2.Dispose();
                }
                List<int> leaked = FindLeakedClaudePids(pidsBefore, cliPath);
                if (leaked.Count > 0)
                {
                    var killer = new WindowsProcessKiller(logger);
                    for (int i = 0; i < leaked.Count; i++)
                    {
                        killer.KillTree(leaked[i]);
                    }
                }
                TryDeleteDirectory(workDir);
                if (leaked.Count > 0)
                {
                    Assert.Fail("Leaked claude.exe PID(s) after the test: "
                        + string.Join(", ", leaked)
                        + " (killed by the hygiene sweep). Log:\n" + Tail(log));
                }
            }
        }

        // -- Pump helpers -----------------------------------------------------

        private static void PumpUntil(AgentClient client, Func<bool> condition,
            int timeoutSeconds, string what, StringBuilder log, Func<string> diedReason)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!condition())
            {
                string died = diedReason();
                if (died != null)
                {
                    Assert.Fail("CLI process died while waiting for " + what
                        + ": " + died + "\nLog:\n" + Tail(log));
                }
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Timed out (" + timeoutSeconds + "s) waiting for "
                        + what + ".\nLog:\n" + Tail(log));
                }
                client.Pump(50, 5.0);
                Thread.Sleep(25);
            }
        }

        private static void WaitFor(Func<bool> condition, int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }

        // -- Process bookkeeping ------------------------------------------------

        private static HashSet<int> SnapshotClaudePids()
        {
            var pids = new HashSet<int>();
            System.Diagnostics.Process[] processes =
                System.Diagnostics.Process.GetProcessesByName("claude");
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    pids.Add(processes[i].Id);
                }
                finally
                {
                    processes[i].Dispose();
                }
            }
            return pids;
        }

        /// <summary>
        /// claude.exe processes that appeared since the snapshot AND whose
        /// main module is the CLI exe we spawned. Processes whose module path
        /// cannot be read (access denied - e.g. elevated or other-user
        /// processes, never ours) are skipped.
        /// </summary>
        private static List<int> FindLeakedClaudePids(HashSet<int> pidsBefore, string cliPath)
        {
            var leaked = new List<int>();
            System.Diagnostics.Process[] processes =
                System.Diagnostics.Process.GetProcessesByName("claude");
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    if (pidsBefore.Contains(processes[i].Id))
                    {
                        continue;
                    }
                    string modulePath = null;
                    try
                    {
                        modulePath = processes[i].MainModule.FileName;
                    }
                    catch (Exception)
                    {
                        continue; // Not inspectable => not spawned by this test.
                    }
                    if (string.Equals(Path.GetFullPath(modulePath), Path.GetFullPath(cliPath),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        leaked.Add(processes[i].Id);
                    }
                }
                finally
                {
                    processes[i].Dispose();
                }
            }
            return leaked;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                using (System.Diagnostics.Process p =
                    System.Diagnostics.Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false; // No such process.
            }
            catch (Exception)
            {
                return false;
            }
        }

        // -- Misc ----------------------------------------------------------------

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
                // Scratch dir cleanup is best-effort.
            }
        }

        private static string Tail(StringBuilder log)
        {
            string text = log.ToString();
            const int max = 4000;
            if (text.Length <= max)
            {
                return text;
            }
            return "..." + text.Substring(text.Length - max);
        }
    }
}

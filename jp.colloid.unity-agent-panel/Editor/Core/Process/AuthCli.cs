using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Parsed "&lt;cli&gt; auth status --json" result (docs/design-notes/
    /// 2026-08-02-auth-in-panel.md). IsAvailable is false for a spawn
    /// failure, timeout, or malformed/unparsable stdout -- AuthCli never
    /// throws into its caller, mirroring CliVersionProbe's "best-effort
    /// diagnostics" contract.
    /// </summary>
    public sealed class AuthStatus
    {
        public bool IsAvailable;
        public bool LoggedIn;
        public string Email = string.Empty;
        public string SubscriptionType = string.Empty;
        public string AuthMethod = string.Empty;

        public static AuthStatus Unavailable()
        {
            return new AuthStatus { IsAvailable = false };
        }
    }

    /// <summary>
    /// One-shot "auth status --json" / "auth logout" helpers, plus the pure
    /// parsing/regex seams shared with <see cref="AuthLoginSession"/>
    /// (the long-lived "auth login" process). Structured after
    /// CliVersionProbe.cs: subprocess work runs on a ThreadPool worker
    /// (never the editor main thread) and every callback is marshaled back
    /// through a shared <see cref="ConcurrentQueue{T}"/> drained on
    /// <see cref="EditorApplication.update"/>.
    /// </summary>
    public static class AuthCli
    {
        private const int StatusTimeoutMillis = 10000;
        private const int LogoutTimeoutMillis = 15000;

        /// <summary>
        /// The exact (no trailing newline) prompt tail captured from a real
        /// "auth login" run (docs/design-notes/2026-08-02-auth-in-panel.md
        /// section 1). The CLI never terminates this line -- it sits reading
        /// stdin right after printing it -- so nothing downstream may rely
        /// on a trailing "\n" ever arriving for it.
        /// </summary>
        public const string WaitingForCodePromptTail = "Paste code here if prompted > ";

        private static readonly Regex OAuthUrlPattern = new Regex(@"https://\S+", RegexOptions.Compiled);

        private static readonly ConcurrentQueue<Action> PendingCallbacks = new ConcurrentQueue<Action>();
        private static bool _pumpHooked;

        // -- Status ----------------------------------------------------------

        /// <summary>
        /// Tolerant parse of one "auth status --json" stdout capture.
        /// Malformed/empty/non-object JSON all yield
        /// <see cref="AuthStatus.Unavailable"/> rather than throwing.
        /// </summary>
        public static AuthStatus ParseStatusJson(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
            {
                return AuthStatus.Unavailable();
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(rawJson.Trim(), out node, out error) || node == null || !node.IsObject)
            {
                return AuthStatus.Unavailable();
            }
            return new AuthStatus
            {
                IsAvailable = true,
                LoggedIn = node["loggedIn"].AsBool(false),
                Email = node["email"].AsString(string.Empty),
                SubscriptionType = node["subscriptionType"].AsString(string.Empty),
                AuthMethod = node["authMethod"].AsString(string.Empty)
            };
        }

        /// <summary>
        /// Spawns "&lt;cliPath&gt; auth status --json" on a ThreadPool
        /// worker with a 10s timeout; <paramref name="onComplete"/> runs on
        /// the editor main thread with a parsed (possibly Unavailable)
        /// result. Never throws.
        /// </summary>
        public static void QueryStatus(string cliPath, Action<AuthStatus> onComplete)
        {
            if (string.IsNullOrEmpty(cliPath) || onComplete == null)
            {
                return;
            }
            EnsurePumpHooked();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                AuthStatus status;
                try
                {
                    int exitCode;
                    string stdout = RunOneShot(cliPath, "auth status --json", StatusTimeoutMillis, out exitCode);
                    status = stdout == null ? AuthStatus.Unavailable() : ParseStatusJson(stdout);
                }
                catch (Exception)
                {
                    status = AuthStatus.Unavailable();
                }
                AuthStatus captured = status;
                PendingCallbacks.Enqueue(delegate { onComplete(captured); });
            });
        }

        // -- Logout ------------------------------------------------------------

        /// <summary>
        /// Spawns "&lt;cliPath&gt; auth logout" on a ThreadPool worker with
        /// a 15s timeout; <paramref name="onComplete"/> runs on the editor
        /// main thread with true only on a clean exit 0. Never throws.
        /// </summary>
        public static void Logout(string cliPath, Action<bool> onComplete)
        {
            if (string.IsNullOrEmpty(cliPath) || onComplete == null)
            {
                return;
            }
            EnsurePumpHooked();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool success;
                try
                {
                    int exitCode;
                    string ignoredStdout = RunOneShot(cliPath, "auth logout", LogoutTimeoutMillis, out exitCode);
                    success = ignoredStdout != null && exitCode == 0;
                }
                catch (Exception)
                {
                    success = false;
                }
                bool captured = success;
                PendingCallbacks.Enqueue(delegate { onComplete(captured); });
            });
        }

        // -- OAuth URL extraction / prompt detection (pure seams) ---------------

        /// <summary>
        /// Returns the first "https://" token in <paramref name="text"/>
        /// (whitespace-terminated), or null when none is present. Pure and
        /// allocation-light so both the one-shot fixture tests and the live
        /// login session's per-chunk scan share the exact same rule.
        /// </summary>
        public static string ExtractOAuthUrl(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            Match match = OAuthUrlPattern.Match(text);
            return match.Success ? match.Value : null;
        }

        /// <summary>
        /// True when <paramref name="text"/> ends with the exact (no
        /// trailing newline) "Paste code here if prompted &gt; " tail --
        /// the only signal available that the CLI is now blocked reading
        /// stdin, since the line itself is never newline-terminated.
        /// </summary>
        public static bool EndsWithCodePrompt(string text)
        {
            return !string.IsNullOrEmpty(text)
                && text.EndsWith(WaitingForCodePromptTail, StringComparison.Ordinal);
        }

        // -- Shared one-shot process runner ---------------------------------

        /// <summary>
        /// CORE-3: the runner body (previously duplicated line-for-line
        /// with CliVersionProbe.RunProbeBlocking) lives in
        /// <see cref="OneShotCli"/>, which also replaced the UNBOUNDED
        /// post-exit WaitForExit() flush with a bounded one -- see its
        /// class doc for the grandchild-inherits-stdout hang this closes.
        /// </summary>
        private static string RunOneShot(string cliPath, string arguments, int timeoutMillis, out int exitCode)
        {
            return OneShotCli.Run(cliPath, arguments, timeoutMillis, out exitCode);
        }

        // -- Shared main-thread pump (also used by AuthLoginSession) ------------

        internal static void EnsurePumpHooked()
        {
            if (_pumpHooked)
            {
                return;
            }
            _pumpHooked = true;
            EditorApplication.update += Pump;
        }

        /// <summary>Lets AuthLoginSession marshal its own background-thread
        /// events through the exact same drain loop as the one-shot helpers
        /// above, rather than running a second pump.</summary>
        internal static void EnqueueCallback(Action callback)
        {
            if (callback != null)
            {
                PendingCallbacks.Enqueue(callback);
            }
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
                    // A listener throwing must not wedge the pump for every
                    // other queued (or future) callback.
                }
            }
        }

        /// <summary>
        /// Test-only synchronous pump drain, bypassing the
        /// EditorApplication.update subscription entirely. This codebase's
        /// EditMode tests are plain (non-coroutine) [Test] methods, which
        /// run to completion inside a single tick of the editor's own
        /// update loop -- a blocking Thread.Sleep poll loop inside one would
        /// never actually let EditorApplication.update fire again, so the
        /// one UAP_LIVE_CLI-gated live test needs a way to drain queued
        /// QueryStatus/Logout/AuthLoginSession callbacks without relying on
        /// that. Internal (AssemblyInfo.cs InternalsVisibleTo), never called
        /// from production code.
        /// </summary>
        internal static void DrainPendingForTests()
        {
            Pump();
        }
    }
}

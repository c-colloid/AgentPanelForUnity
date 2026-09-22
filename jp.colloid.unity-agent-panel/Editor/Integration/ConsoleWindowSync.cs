using System;
using System.Reflection;
using UnityEditor;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Keeps the error chip in step with the Console window's Clear
    /// (design note 2026-09-07 decision E2). Unity 2022.3 has no public
    /// read/clear hook for the Console, so this polls the internal
    /// UnityEditor.LogEntries.GetCountsByType through reflection twice a
    /// second and, when the error count DROPS (Clear, Clear on Play /
    /// Recompile / Build), forgets the provider's captured errors -- the
    /// Console no longer shows them, so the chip must not either. Rises
    /// and unchanged counts are ignored: capture stays on the log
    /// callback. Collapse and search filters do not change the per-type
    /// totals, so they cannot trigger a false clear.
    ///
    /// Reflection can fail on a future editor; then <see cref="Available"/>
    /// is false and nothing polls -- the pre-2026-09-07 behavior, never an
    /// exception. The clear decision itself is a pure function
    /// (<see cref="ShouldClear"/>) pinned by ConsoleWindowSyncTests.
    /// </summary>
    public static class ConsoleWindowSync
    {
        public const double PollIntervalSeconds = 0.5;

        private static MethodInfo _getCountsByType;
        private static MethodInfo _clear;
        private static bool _resolved;
        private static bool _clearResolved;
        private static bool _installed;
        private static int _lastErrorCount = -1;
        private static double _nextPoll;

        /// <summary>True when the internal Console API resolved on this editor.</summary>
        public static bool Available
        {
            get
            {
                Resolve();
                return _getCountsByType != null;
            }
        }

        [InitializeOnLoadMethod]
        private static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            if (!Available)
            {
                return;
            }
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;
            try
            {
                Type type = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                if (type == null)
                {
                    return;
                }
                MethodInfo method = type.GetMethod("GetCountsByType",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null || method.GetParameters().Length != 3)
                {
                    return;
                }
                _getCountsByType = method;
            }
            catch (Exception)
            {
                _getCountsByType = null;
            }
        }

        /// <summary>
        /// Clears the Console window's entries (uap_console_clear, design
        /// note docs/design-notes/2026-09-21-console-clear-and-game-view-
        /// size.md). False when the internal API is not reachable on this
        /// editor -- the caller says so rather than reporting a clear that
        /// did not happen.
        ///
        /// The LogEntries reflection lives here, beside the count read, so
        /// this package has exactly ONE place that depends on that internal
        /// type. Clearing also resets this sync's baseline: the drop is ours,
        /// and re-reporting it as "the user pressed Clear" on the next poll
        /// would only make the panel do the same work twice.
        /// </summary>
        public static bool TryClearConsole()
        {
            ResolveClear();
            if (_clear == null)
            {
                return false;
            }
            try
            {
                _clear.Invoke(null, null);
            }
            catch (Exception)
            {
                _clear = null;
                return false;
            }
            _lastErrorCount = 0;
            return true;
        }

        private static void ResolveClear()
        {
            if (_clearResolved)
            {
                return;
            }
            _clearResolved = true;
            try
            {
                Type type = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                if (type == null)
                {
                    return;
                }
                MethodInfo method = type.GetMethod("Clear",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                _clear = method;
            }
            catch (Exception)
            {
                _clear = null;
            }
        }

        /// <summary>Reads the Console's error count (errors + exceptions + asserts). False when unavailable.</summary>
        public static bool TryReadErrorCount(out int errors)
        {
            errors = 0;
            if (!Available)
            {
                return false;
            }
            try
            {
                var args = new object[] { 0, 0, 0 };
                _getCountsByType.Invoke(null, args);
                errors = (int)args[0];
                return true;
            }
            catch (Exception)
            {
                _getCountsByType = null;
                return false;
            }
        }

        /// <summary>
        /// The decision: clear only when a baseline exists and the count
        /// went DOWN. The first observation (no baseline) never clears --
        /// a fresh domain has no idea what the Console held before.
        /// </summary>
        public static bool ShouldClear(int previousErrors, int currentErrors)
        {
            return previousErrors >= 0 && currentErrors < previousErrors;
        }

        private static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextPoll)
            {
                return;
            }
            _nextPoll = now + PollIntervalSeconds;
            int errors;
            if (!TryReadErrorCount(out errors))
            {
                EditorApplication.update -= Poll;
                return;
            }
            ApplyCount(errors);
        }

        /// <summary>One observation (internal so tests can drive it without the update loop).</summary>
        internal static void ApplyCount(int errors)
        {
            if (ShouldClear(_lastErrorCount, errors))
            {
                ConsoleErrorProvider.Clear();
            }
            _lastErrorCount = errors;
        }

        internal static int LastErrorCountForTests
        {
            get { return _lastErrorCount; }
        }

        internal static void ResetForTests()
        {
            _lastErrorCount = -1;
        }
    }
}

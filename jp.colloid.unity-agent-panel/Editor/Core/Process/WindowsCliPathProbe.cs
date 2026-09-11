using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Windows CLI path resolution (ARCHITECTURE.md D1). Order:
    /// 1. The manual path injected by the caller (EditorPrefs escape hatch).
    /// 2. %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe
    /// 3. PATH shim tracing: find claude.cmd on PATH and resolve the
    ///    node_modules\@anthropic-ai\claude-code\bin\claude.exe next to it
    ///    (the npm global prefix keeps the shim and node_modules siblings).
    /// 4. %USERPROFILE%\.local\bin\claude.exe (native installer).
    /// The shims themselves are never returned: only the real exe is
    /// spawnable with UseShellExecute=false and tree-killable.
    /// </summary>
    public sealed class WindowsCliPathProbe : ICliPathProbe
    {
        private const string NpmRelativeExePath =
            "node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe";

        private readonly string _manualPath;

        /// <param name="manualPath">
        /// User-specified executable path (may be null or empty). The caller
        /// reads it from EditorPrefs; this class stays UnityEditor-free.
        /// </param>
        public WindowsCliPathProbe(string manualPath)
        {
            _manualPath = manualPath;
        }

        public string Resolve()
        {
            foreach (string candidate in EnumerateCandidates())
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception)
                {
                    // Malformed path or access failure: try the next candidate.
                }
            }
            return null;
        }

        public string[] DescribeCandidates()
        {
            var list = new List<string>(EnumerateCandidates());
            return list.ToArray();
        }

        private IEnumerable<string> EnumerateCandidates()
        {
            if (!string.IsNullOrEmpty(_manualPath))
            {
                yield return _manualPath;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                yield return Path.Combine(appData, "npm", NpmRelativeExePath);
            }

            foreach (string traced in TracePathShims())
            {
                yield return traced;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                yield return Path.Combine(userProfile, ".local", "bin", "claude.exe");
            }
        }

        /// <summary>
        /// Finds claude.cmd on PATH and yields the real exe next to each
        /// shim's node_modules directory.
        /// </summary>
        private static IEnumerable<string> TracePathShims()
        {
            string pathVar;
            try
            {
                pathVar = Environment.GetEnvironmentVariable("PATH");
            }
            catch (Exception)
            {
                yield break;
            }
            if (string.IsNullOrEmpty(pathVar))
            {
                yield break;
            }

            foreach (string rawDir in pathVar.Split(Path.PathSeparator))
            {
                string dir = rawDir == null ? null : rawDir.Trim().Trim('"');
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                bool hasShim;
                try
                {
                    hasShim = File.Exists(Path.Combine(dir, "claude.cmd"));
                }
                catch (Exception)
                {
                    continue;
                }
                if (hasShim)
                {
                    yield return Path.Combine(dir, NpmRelativeExePath);
                }
            }
        }
    }
}

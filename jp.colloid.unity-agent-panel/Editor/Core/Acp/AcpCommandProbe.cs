using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Process;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// ICliPathProbe for ACP backends: resolves the configured command
    /// (an absolute/relative path, or a bare name looked up on PATH) to a
    /// launchable file. Unlike WindowsCliPathProbe it deliberately accepts
    /// npm's `.cmd` shims on Windows -- there is no single well-known
    /// `node_modules` layout to trace through across Gemini CLI, codex-acp
    /// and arbitrary custom agents, and CreateProcess runs a `.cmd`
    /// through cmd.exe on its own; the tree kill covers the extra layer.
    /// Pure C#, no Unity APIs.
    /// </summary>
    public sealed class AcpCommandProbe : ICliPathProbe
    {
        private static readonly string[] WindowsExtensions = { ".exe", ".cmd", ".bat", ".com" };

        private readonly string _command;
        private readonly bool _isWindows;
        private readonly string _pathVariable;
        private readonly string _home;
        private readonly string _appData;

        public AcpCommandProbe(string command)
            : this(command, Environment.OSVersion.Platform == PlatformID.Win32NT, ReadPath(),
                ReadFolder(Environment.SpecialFolder.UserProfile), ReadFolder(Environment.SpecialFolder.ApplicationData))
        {
        }

        /// <summary>Test seam: platform, PATH and the two home-style folders injected.</summary>
        internal AcpCommandProbe(string command, bool isWindows, string pathVariable,
            string home = null, string appData = null)
        {
            _command = command != null ? command.Trim() : string.Empty;
            _isWindows = isWindows;
            _pathVariable = pathVariable ?? string.Empty;
            _home = home ?? string.Empty;
            _appData = appData ?? string.Empty;
        }

        /// <summary>
        /// Directories the vendor installers write to, tried AFTER PATH so a
        /// binary installed a minute ago from the panel is found even
        /// though the editor's PATH was captured at launch: Grok Build's
        /// ~/.grok/bin, Claude's ~/.local/bin, npm's global bin folders.
        /// </summary>
        internal IEnumerable<string> WellKnownDirectories()
        {
            if (_isWindows)
            {
                if (_home.Length > 0)
                {
                    yield return Path.Combine(_home, ".grok", "bin");
                    yield return Path.Combine(_home, ".local", "bin");
                }
                if (_appData.Length > 0)
                {
                    yield return Path.Combine(_appData, "npm");
                }
                yield break;
            }
            yield return "/usr/local/bin";
            yield return "/opt/homebrew/bin";
            if (_home.Length > 0)
            {
                yield return Path.Combine(_home, ".grok", "bin");
                yield return Path.Combine(_home, ".local", "bin");
                yield return Path.Combine(_home, ".npm-global", "bin");
            }
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
                    // Try the next candidate.
                }
            }
            return null;
        }

        public string[] DescribeCandidates()
        {
            var list = new List<string>(EnumerateCandidates());
            // A bare name on a long PATH would list dozens of entries; the
            // first few are the informative ones for a "not found" message.
            if (list.Count > 8)
            {
                list.RemoveRange(8, list.Count - 8);
                list.Add("...");
            }
            return list.ToArray();
        }

        /// <summary>True when the command carries a directory component (so PATH is not consulted).</summary>
        internal static bool IsPathLike(string command)
        {
            return !string.IsNullOrEmpty(command)
                && (command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0
                    || (command.Length > 1 && command[1] == ':'));
        }

        internal IEnumerable<string> EnumerateCandidates()
        {
            if (_command.Length == 0)
            {
                yield break;
            }
            if (IsPathLike(_command))
            {
                yield return _command;
                if (_isWindows && !Path.HasExtension(_command))
                {
                    for (int i = 0; i < WindowsExtensions.Length; i++)
                    {
                        yield return _command + WindowsExtensions[i];
                    }
                }
                yield break;
            }
            char separator = _isWindows ? ';' : ':';
            var dirs = new List<string>(_pathVariable.Split(separator));
            dirs.AddRange(WellKnownDirectories());
            for (int d = 0; d < dirs.Count; d++)
            {
                string dir = dirs[d].Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }
                if (_isWindows)
                {
                    bool hasExtension = Path.HasExtension(_command);
                    if (hasExtension)
                    {
                        yield return Path.Combine(dir, _command);
                        continue;
                    }
                    for (int i = 0; i < WindowsExtensions.Length; i++)
                    {
                        yield return Path.Combine(dir, _command + WindowsExtensions[i]);
                    }
                }
                else
                {
                    yield return Path.Combine(dir, _command);
                }
            }
        }

        private static string ReadFolder(Environment.SpecialFolder folder)
        {
            try
            {
                return Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string ReadPath()
        {
            try
            {
                return Environment.GetEnvironmentVariable("PATH");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Minimal macOS/Linux CLI path resolution (ARCHITECTURE.md D1 seam).
    /// Order: manual path, then the common install locations a
    /// "which claude" would normally find. v1 keeps this deliberately
    /// simple; a real `which` subprocess is not worth spawning here.
    /// </summary>
    public sealed class UnixCliPathProbe : ICliPathProbe
    {
        private readonly string _manualPath;

        public UnixCliPathProbe(string manualPath)
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
                    // Try the next candidate.
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

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return "/usr/local/bin/claude";
            yield return "/opt/homebrew/bin/claude";
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".local", "bin", "claude");
                yield return Path.Combine(home, ".npm-global", "bin", "claude");
            }

            // PATH scan as a final net (covers nvm-style installs).
            string pathVar = null;
            try
            {
                pathVar = Environment.GetEnvironmentVariable("PATH");
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(':'))
                {
                    if (!string.IsNullOrEmpty(dir))
                    {
                        yield return Path.Combine(dir, "claude");
                    }
                }
            }
        }
    }
}

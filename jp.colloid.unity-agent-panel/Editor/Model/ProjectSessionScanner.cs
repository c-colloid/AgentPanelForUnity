using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Cross-project session discovery for History's "group by project"
    /// mode: walks every directory under "~/.claude/projects", resolves the
    /// working directory each one belongs to, and keeps only the ones that
    /// are real Unity projects.
    ///
    /// Why the Unity-project filter is not optional: on a machine that has
    /// ever run the CLI from a scratch directory, this root fills up with
    /// throwaway entries (30+ of them on the development machine, one per
    /// probe working directory). Listing them all would bury the handful of
    /// directories the user actually cares about, which is the exact
    /// problem this whole view is being reworked to solve.
    ///
    /// Why cwd has to be read from the transcript rather than derived from
    /// the directory name: the CLI's name transform
    /// (<see cref="SessionIndex.TransformCwdToProjectDirName"/>) replaces
    /// every non-alphanumeric character with '-', which is not invertible
    /// ("C--Unity-Foo" could be "C:\Unity\Foo" or "C:/Unity/Foo" or
    /// "C_-Unity_Foo"). The transcript records the real absolute path.
    ///
    /// Pure C# + Core/Json (no Unity API) so it stays EditMode-testable
    /// against a fake directory tree, matching <see cref="SessionIndex"/>.
    /// </summary>
    public static class ProjectSessionScanner
    {
        /// <summary>
        /// How far into a transcript the cwd probe reads before giving up.
        /// Measured across 18 real transcripts: the first line carrying a
        /// top-level "cwd" was ALWAYS line index 2, so this cap is roughly
        /// 10x the observed worst case and still bounds a pathological file.
        /// </summary>
        private const int CwdScanLineCap = 32;

        /// <summary>One discovered Unity project and the sessions recorded under it.</summary>
        public sealed class ProjectSessions
        {
            /// <summary>Absolute working directory as recorded in the transcripts.</summary>
            public string Cwd = string.Empty;
            /// <summary>Last path segment of <see cref="Cwd"/> -- what the group header shows.</summary>
            public string DisplayName = string.Empty;
            public List<SessionIndexEntry> Entries = new List<SessionIndexEntry>();
        }

        /// <summary>
        /// Every Unity project with at least one session, newest project
        /// first (by its newest session). Tolerant throughout: an
        /// unreadable root, directory or file is skipped, never thrown.
        /// Callers should treat this as an on-demand operation -- it stats
        /// every session file on the machine and reads a few lines of one
        /// file per project.
        /// </summary>
        public static List<ProjectSessions> ScanUnityProjects(string projectsRootOverride = null)
        {
            var results = new List<ProjectSessions>();
            string root = string.IsNullOrEmpty(projectsRootOverride)
                ? SessionIndex.DefaultProjectsRoot()
                : projectsRootOverride;

            string[] directories;
            try
            {
                if (!Directory.Exists(root))
                {
                    return results;
                }
                directories = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                return results;
            }

            for (int i = 0; i < directories.Length; i++)
            {
                // MODEL-14: one directory can yield SEVERAL projects -- the
                // CLI's cwd->directory-name transform is many-to-one, so
                // sessions from different working directories can share a
                // directory. See AppendProjects.
                AppendProjects(directories[i], projectsRootOverride, results);
            }

            results.Sort(delegate (ProjectSessions a, ProjectSessions b)
            {
                return NewestOf(b).CompareTo(NewestOf(a));
            });
            return results;
        }

        /// <summary>
        /// True when the path looks like a Unity project root. Checks
        /// ProjectSettings/ProjectVersion.txt specifically rather than just
        /// an Assets folder: "Assets" is a common directory name in
        /// non-Unity repositories, while ProjectVersion.txt is written by
        /// the editor itself and is present in every project it has opened.
        /// </summary>
        public static bool IsUnityProject(string cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return false;
            }
            try
            {
                return File.Exists(Path.Combine(cwd, "ProjectSettings", "ProjectVersion.txt"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AppendProjects(string sessionDir, string projectsRootOverride,
            List<ProjectSessions> results)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(sessionDir, "*.jsonl");
            }
            catch (Exception)
            {
                return;
            }
            if (files.Length == 0)
            {
                return;
            }

            // The single directory-level probe survives as the cheap EARLY
            // SKIP only. MODEL-14 disproved its old justification ("every
            // session in a directory shares the working directory by
            // construction"): the cwd->name transform is many-to-one
            // (every non-alphanumeric becomes '-'), so distinct working
            // directories can collide into one directory, and stamping the
            // first file's cwd onto every entry misattributed the rest.
            // Attribution now comes from each entry's OWN transcript
            // (SessionIndexEntry.Cwd, lazily read and cached alongside the
            // title/preview History reads anyway).
            string probedCwd = null;
            for (int i = 0; i < files.Length && string.IsNullOrEmpty(probedCwd); i++)
            {
                probedCwd = ReadCwd(files[i]);
            }
            if (!IsUnityProject(probedCwd))
            {
                return;
            }

            // Reuse SessionIndex rather than re-implementing the stat/sort:
            // it already maps cwd -> directory and yields entries newest
            // first, and going through it keeps exactly one definition of
            // what a listed session is.
            var index = new SessionIndex(projectsRootOverride);
            index.Refresh(probedCwd);
            if (index.Entries.Count == 0)
            {
                return;
            }

            // Bucket by each entry's own cwd. The common case (no
            // collision) yields exactly one bucket, identical to the old
            // output; a collided directory splits into one ProjectSessions
            // per distinct Unity-project cwd. A distinct cwd other than the
            // probed one must prove it is a Unity project itself -- the
            // probe only vouched for ONE of them -- and non-Unity cwds
            // (scratch runs colliding into this name) are dropped, matching
            // the scanner's overall filter.
            var buckets = new Dictionary<string, ProjectSessions>(StringComparer.Ordinal);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<ProjectSessions>();
            for (int i = 0; i < index.Entries.Count; i++)
            {
                SessionIndexEntry entry = index.Entries[i];
                string entryCwd = entry.Cwd;
                if (string.IsNullOrEmpty(entryCwd))
                {
                    // Unreadable/capped transcript: keep the session,
                    // attributed to the probed cwd rather than dropped.
                    entryCwd = probedCwd;
                }
                if (rejected.Contains(entryCwd))
                {
                    continue;
                }
                ProjectSessions bucket;
                if (!buckets.TryGetValue(entryCwd, out bucket))
                {
                    if (!string.Equals(entryCwd, probedCwd, StringComparison.Ordinal)
                        && !IsUnityProject(entryCwd))
                    {
                        rejected.Add(entryCwd);
                        continue;
                    }
                    bucket = new ProjectSessions
                    {
                        Cwd = entryCwd,
                        DisplayName = LastSegment(entryCwd)
                    };
                    buckets[entryCwd] = bucket;
                    order.Add(bucket);
                }
                bucket.Entries.Add(entry);
            }
            results.AddRange(order);
        }

        private static string ReadCwd(string filePath)
        {
            try
            {
                int scanned = 0;
                foreach (string rawLine in File.ReadLines(filePath))
                {
                    if (scanned++ >= CwdScanLineCap)
                    {
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        continue;
                    }
                    JsonNode node;
                    try
                    {
                        node = JsonParser.Parse(rawLine);
                    }
                    catch (JsonParseException)
                    {
                        // Malformed or truncated line: keep scanning. A
                        // crash-time transcript's last line is routinely
                        // half-written and must not blank out the project.
                        continue;
                    }
                    if (!node.IsObject)
                    {
                        continue;
                    }
                    string cwd = node["cwd"].AsString(string.Empty);
                    if (!string.IsNullOrEmpty(cwd))
                    {
                        return cwd;
                    }
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string LastSegment(string cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return string.Empty;
            }
            string trimmed = cwd.TrimEnd('/', '\\');
            int slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            return slash >= 0 && slash + 1 < trimmed.Length
                ? trimmed.Substring(slash + 1)
                : trimmed;
        }

        private static DateTime NewestOf(ProjectSessions project)
        {
            DateTime newest = DateTime.MinValue;
            if (project == null || project.Entries == null)
            {
                return newest;
            }
            for (int i = 0; i < project.Entries.Count; i++)
            {
                if (project.Entries[i] != null
                    && project.Entries[i].LastModifiedUtc > newest)
                {
                    newest = project.Entries[i].LastModifiedUtc;
                }
            }
            return newest;
        }
    }
}

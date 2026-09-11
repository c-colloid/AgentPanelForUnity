using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// INFRA-2d: ProjectSessionScanner is pure C# with a
    /// projectsRootOverride, so the whole cross-project discovery is
    /// hermetic against a fake directory tree (the SessionIndexTests
    /// temp + Guid pattern) -- it just never had a suite. Covers the
    /// Unity-project filter, cwd probing (cap, malformed leading lines),
    /// newest-first ordering, display naming and the missing-root case.
    /// </summary>
    [TestFixture]
    public class ProjectSessionScannerTests
    {
        private string _root;
        private string _projectsRoot;
        private string _workRoot;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "AgentPanelProjectScannerTests_" + Guid.NewGuid().ToString("N"));
            _projectsRoot = Path.Combine(_root, "projects");
            _workRoot = Path.Combine(_root, "work");
            Directory.CreateDirectory(_projectsRoot);
            Directory.CreateDirectory(_workRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
            }
        }

        private string MakeUnityProject(string name)
        {
            string cwd = Path.Combine(_workRoot, name);
            Directory.CreateDirectory(Path.Combine(cwd, "ProjectSettings"));
            File.WriteAllText(Path.Combine(cwd, "ProjectSettings", "ProjectVersion.txt"),
                "m_EditorVersion: 2022.3.22f1\n");
            return cwd;
        }

        private string MakePlainDir(string name)
        {
            string cwd = Path.Combine(_workRoot, name);
            Directory.CreateDirectory(cwd);
            return cwd;
        }

        private static string CwdLine(string cwd)
        {
            // Through JsonWriter so a Windows path's backslashes are
            // escaped exactly like the CLI writes them.
            return JsonWriter.Write(JsonNode.NewObject()
                .Set("type", "user").Set("cwd", cwd));
        }

        private string WriteSession(string cwd, string fileName,
            DateTime lastWriteUtc, params string[] lines)
        {
            string dir = Path.Combine(_projectsRoot,
                SessionIndex.TransformCwdToProjectDirName(cwd));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllLines(path, lines);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        [Test]
        public void Scan_KeepsUnityProjects_DropsScratchDirectories()
        {
            string unity = MakeUnityProject("UnityA");
            string scratch = MakePlainDir("Scratch");
            WriteSession(unity, "s1.jsonl", DateTime.UtcNow, CwdLine(unity));
            WriteSession(scratch, "s2.jsonl", DateTime.UtcNow, CwdLine(scratch));
            // An empty session directory must be skipped, not crash.
            Directory.CreateDirectory(Path.Combine(_projectsRoot, "empty-dir"));

            List<ProjectSessionScanner.ProjectSessions> results =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(1, results.Count,
                "only the directory whose cwd has ProjectSettings/ProjectVersion.txt survives");
            Assert.AreEqual(unity, results[0].Cwd);
            Assert.AreEqual("UnityA", results[0].DisplayName,
                "the group header shows the last path segment");
            Assert.AreEqual(1, results[0].Entries.Count);
        }

        [Test]
        public void Scan_OrdersProjectsByTheirNewestSession()
        {
            string older = MakeUnityProject("OlderProject");
            string newer = MakeUnityProject("NewerProject");
            DateTime now = DateTime.UtcNow;
            WriteSession(older, "old.jsonl", now.AddHours(-5), CwdLine(older));
            WriteSession(newer, "new.jsonl", now.AddMinutes(-1), CwdLine(newer));

            List<ProjectSessionScanner.ProjectSessions> results =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("NewerProject", results[0].DisplayName);
            Assert.AreEqual("OlderProject", results[1].DisplayName);
        }

        [Test]
        public void Scan_MalformedLeadingLine_StillFindsTheCwd()
        {
            // A crash-time transcript's last line is routinely half-written;
            // the probe must skip garbage lines, not blank out the project.
            string unity = MakeUnityProject("UnityTolerant");
            WriteSession(unity, "s.jsonl", DateTime.UtcNow,
                "{ this is not json at all",
                "",
                CwdLine(unity));

            List<ProjectSessionScanner.ProjectSessions> results =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(unity, results[0].Cwd);
        }

        [Test]
        public void Scan_CwdBeyondTheScanCap_ExcludesTheDirectory()
        {
            string unity = MakeUnityProject("UnityBuried");
            var lines = new List<string>();
            for (int i = 0; i < 40; i++)
            {
                lines.Add(JsonWriter.Write(JsonNode.NewObject().Set("type", "meta")));
            }
            lines.Add(CwdLine(unity));
            WriteSession(unity, "s.jsonl", DateTime.UtcNow, lines.ToArray());

            List<ProjectSessionScanner.ProjectSessions> results =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(0, results.Count,
                "the cwd probe is line-capped so a pathological transcript stays cheap; "
                + "a cwd only beyond the cap reads as no cwd at all");
        }

        /// <summary>
        /// MODEL-14: the CLI's cwd->directory-name transform is many-to-one
        /// (every non-alphanumeric becomes '-'), so 'Col_lide' and
        /// 'Col-lide' share one directory. The old scanner stamped the
        /// first file's cwd onto every session in the directory; it must
        /// split into one ProjectSessions per distinct cwd, each holding
        /// only its own sessions.
        /// </summary>
        [Test]
        public void Scan_CollidingCwds_SplitIntoOneProjectPerCwd()
        {
            string underscore = MakeUnityProject("Col_lide");
            string dash = MakeUnityProject("Col-lide");
            Assert.AreEqual(SessionIndex.TransformCwdToProjectDirName(underscore),
                SessionIndex.TransformCwdToProjectDirName(dash),
                "precondition: the two cwds must collide into one directory");
            WriteSession(underscore, "u.jsonl", DateTime.UtcNow.AddMinutes(-2), CwdLine(underscore));
            WriteSession(dash, "d.jsonl", DateTime.UtcNow.AddMinutes(-1), CwdLine(dash));

            List<ProjectSessionScanner.ProjectSessions> projects =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(2, projects.Count, "one project per distinct cwd, not one per directory");
            foreach (ProjectSessionScanner.ProjectSessions project in projects)
            {
                Assert.AreEqual(1, project.Entries.Count);
                StringAssert.Contains(project.Cwd == underscore ? "u.jsonl" : "d.jsonl",
                    project.Entries[0].FilePath,
                    "each bucket must hold only the sessions recorded under its own cwd");
            }
        }

        /// <summary>MODEL-14 regression guard: a directory with one cwd keeps producing exactly one project.</summary>
        [Test]
        public void Scan_SingleCwdDirectory_StaysOneProject()
        {
            string unity = MakeUnityProject("Solo");
            WriteSession(unity, "a.jsonl", DateTime.UtcNow.AddMinutes(-2), CwdLine(unity));
            WriteSession(unity, "b.jsonl", DateTime.UtcNow.AddMinutes(-1), CwdLine(unity));

            List<ProjectSessionScanner.ProjectSessions> projects =
                ProjectSessionScanner.ScanUnityProjects(_projectsRoot);

            Assert.AreEqual(1, projects.Count);
            Assert.AreEqual(2, projects[0].Entries.Count);
            Assert.AreEqual(unity, projects[0].Cwd);
        }

        [Test]
        public void Scan_MissingRoot_ReturnsEmpty_NeverThrows()
        {
            List<ProjectSessionScanner.ProjectSessions> results =
                ProjectSessionScanner.ScanUnityProjects(
                    Path.Combine(_root, "does-not-exist"));

            Assert.IsNotNull(results);
            Assert.AreEqual(0, results.Count);
        }

        [Test]
        public void IsUnityProject_RequiresProjectVersionTxt()
        {
            string unity = MakeUnityProject("UnityCheck");
            string assetsOnly = MakePlainDir("AssetsOnly");
            Directory.CreateDirectory(Path.Combine(assetsOnly, "Assets"));

            Assert.IsTrue(ProjectSessionScanner.IsUnityProject(unity));
            Assert.IsFalse(ProjectSessionScanner.IsUnityProject(assetsOnly),
                "an Assets folder alone is a common non-Unity layout; "
                + "ProjectVersion.txt is editor-written and decisive");
            Assert.IsFalse(ProjectSessionScanner.IsUnityProject(null));
            Assert.IsFalse(ProjectSessionScanner.IsUnityProject(string.Empty));
        }
    }
}

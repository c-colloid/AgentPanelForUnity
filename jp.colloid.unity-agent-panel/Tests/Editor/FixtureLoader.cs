using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Resolves and reads the captured CLI output fixtures under
    /// Packages/jp.colloid.unity-agent-panel/Tests/Editor/Fixtures/.
    /// Unity maps the "Packages/&lt;id&gt;/" virtual path onto the embedded
    /// package folder, and Path.GetFullPath resolves it relative to the
    /// project root, which is the Editor's working directory.
    /// </summary>
    public static class FixtureLoader
    {
        private const string FixtureRoot =
            "Packages/jp.colloid.unity-agent-panel/Tests/Editor/Fixtures/";

        /// <summary>
        /// The CLI version every fixture in Fixtures/ was captured from.
        /// FixtureProvenanceTests asserts _fixtures.meta.json agrees; bump
        /// BOTH (after re-capturing) when the fixtures are refreshed
        /// against a newer CLI.
        /// </summary>
        public const string ExpectedCliVersion = "2.1.218";

        /// <summary>The original six protocol captures (real claude
        /// v2.1.218 output) -- NOT the whole Fixtures/ directory;
        /// FixtureProvenanceTests enumerates the directory itself.</summary>
        public static readonly string[] AllFixtureNames =
        {
            "out1.jsonl",
            "out2.jsonl",
            "out3.jsonl",
            "out_bidi.jsonl",
            "init_flags.json",
            "init_plugins.json",
            "resume_out.json"
        };

        /// <summary>Absolute path of a fixture file. Throws when the file does not exist.</summary>
        public static string GetPath(string name)
        {
            string path = Path.GetFullPath(FixtureRoot + name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Fixture not found: " + name + " (resolved to " + path
                    + "). Are the Tests/Editor/Fixtures files present in the package?",
                    path);
            }
            return path;
        }

        /// <summary>Reads the whole fixture as UTF-8 text (BOM stripped by the reader).</summary>
        public static string ReadAllText(string name)
        {
            return File.ReadAllText(GetPath(name));
        }

        /// <summary>
        /// Reads the fixture as JSONL: one entry per non-empty line.
        /// File.ReadAllLines detects and strips the UTF-8 BOM some captures
        /// carry; the parser tolerates a residual BOM either way.
        /// </summary>
        public static List<string> ReadLines(string name)
        {
            var lines = new List<string>();
            foreach (string line in File.ReadAllLines(GetPath(name)))
            {
                if (line != null && line.Trim().Length > 0)
                {
                    lines.Add(line);
                }
            }
            return lines;
        }
    }
}

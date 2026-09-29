using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// One MCP server the user added in Settings (design note docs/design-
    /// notes/2026-09-27-mcp-servers-in-panel.md section 2). The panel
    /// spawns the CLI with `--strict-mcp-config`, so the CLI's own config
    /// files are never read; this list is the ONLY way an extra server
    /// reaches a panel session, and it rides in the same `--mcp-config`
    /// file as the UapOps server (UapOpsMcpConfig.BuildConfigJson).
    ///
    /// Unity-serializable (public fields, JsonUtility through PanelSettings):
    /// args / env / headers are line lists rather than nested objects so
    /// JsonUtility can hold them. Wire shape (docs/research/08-mcp-
    /// transport.md section 2, measured): stdio = {type, command, args[],
    /// env{}}, http / sse = {type, url, headers{}}.
    /// </summary>
    [Serializable]
    public class McpServerConfig
    {
        public const string TransportStdio = "stdio";
        public const string TransportHttp = "http";
        public const string TransportSse = "sse";
        public static readonly string[] Transports = { TransportStdio, TransportHttp, TransportSse };

        public string name = string.Empty;
        /// <summary>"stdio" | "http" | "sse"; anything else is treated as stdio.</summary>
        public string transport = TransportStdio;
        public bool enabled = true;
        /// <summary>stdio: the executable.</summary>
        public string command = string.Empty;
        /// <summary>stdio: one argument per entry.</summary>
        public List<string> args = new List<string>();
        /// <summary>stdio: "KEY=VALUE" per entry.</summary>
        public List<string> env = new List<string>();
        /// <summary>http / sse: the endpoint.</summary>
        public string url = string.Empty;
        /// <summary>http / sse: "Name: value" per entry.</summary>
        public List<string> headers = new List<string>();

        public bool IsRemote
        {
            get { return IsRemoteTransport(transport); }
        }

        public static bool IsRemoteTransport(string transport)
        {
            return string.Equals(transport, TransportHttp, StringComparison.Ordinal)
                || string.Equals(transport, TransportSse, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the entry can be handed to the CLI: a name, and a
        /// command (stdio) or a URL (http / sse). An invalid entry is left
        /// out of the config rather than failing the whole spawn.
        /// </summary>
        public bool IsComplete
        {
            get
            {
                if (string.IsNullOrEmpty((name ?? string.Empty).Trim()))
                {
                    return false;
                }
                return IsRemote
                    ? !string.IsNullOrEmpty((url ?? string.Empty).Trim())
                    : !string.IsNullOrEmpty((command ?? string.Empty).Trim());
            }
        }

        /// <summary>The CLI's mcpServers[name] object for this entry.</summary>
        public JsonNode ToJson()
        {
            JsonNode node = JsonNode.NewObject();
            if (IsRemote)
            {
                node.Set("type", transport).Set("url", (url ?? string.Empty).Trim());
                JsonNode headerObject = JsonNode.NewObject();
                int count = 0;
                foreach (KeyValuePair<string, string> pair in ParsePairs(headers, ':'))
                {
                    headerObject.Set(pair.Key, pair.Value);
                    count++;
                }
                if (count > 0)
                {
                    node.Set("headers", headerObject);
                }
                return node;
            }
            node.Set("type", TransportStdio).Set("command", (command ?? string.Empty).Trim());
            JsonNode argArray = JsonNode.NewArray();
            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if (!string.IsNullOrEmpty(args[i]))
                    {
                        argArray.Add(args[i]);
                    }
                }
            }
            node.Set("args", argArray);
            JsonNode envObject = JsonNode.NewObject();
            int envCount = 0;
            foreach (KeyValuePair<string, string> pair in ParsePairs(env, '='))
            {
                envObject.Set(pair.Key, pair.Value);
                envCount++;
            }
            if (envCount > 0)
            {
                node.Set("env", envObject);
            }
            return node;
        }

        /// <summary>
        /// "KEY&lt;sep&gt;VALUE" lines -> pairs, in order. A line without the
        /// separator, or with an empty key, is skipped (never a spawn
        /// failure). Whitespace around key and value is trimmed.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ParsePairs(List<string> lines, char separator)
        {
            if (lines == null)
            {
                yield break;
            }
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                int at = line.IndexOf(separator);
                if (at <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, at).Trim();
                if (key.Length == 0)
                {
                    continue;
                }
                yield return new KeyValuePair<string, string>(key, line.Substring(at + 1).Trim());
            }
        }

        /// <summary>
        /// Reads one CLI-format server object (as found under "mcpServers"
        /// in ~/.claude.json or .mcp.json) into an entry. Returns null when
        /// the node is not an object.
        /// </summary>
        public static McpServerConfig FromJson(string name, JsonNode node)
        {
            if (node == null || !node.IsObject)
            {
                return null;
            }
            var config = new McpServerConfig { name = name ?? string.Empty };
            string type = node["type"].AsString(string.Empty);
            config.transport = IsRemoteTransport(type) ? type : TransportStdio;
            if (config.IsRemote)
            {
                config.url = node["url"].AsString(string.Empty);
                foreach (KeyValuePair<string, JsonNode> pair in node["headers"].Properties)
                {
                    config.headers.Add(pair.Key + ": " + pair.Value.AsString(string.Empty));
                }
            }
            else
            {
                config.command = node["command"].AsString(string.Empty);
                string[] argValues = node["args"].AsStringArray();
                if (argValues != null)
                {
                    config.args.AddRange(argValues);
                }
                foreach (KeyValuePair<string, JsonNode> pair in node["env"].Properties)
                {
                    config.env.Add(pair.Key + "=" + pair.Value.AsString(string.Empty));
                }
            }
            return config;
        }

        /// <summary>
        /// Every server under a config document's "mcpServers" (the shape
        /// of `.mcp.json` and of the top level of `~/.claude.json`), plus,
        /// when <paramref name="projectRoot"/> is given, the ones under
        /// `projects[projectRoot].mcpServers` (where `claude mcp add`
        /// stores project-scoped servers, measured in docs/research/08-mcp-
        /// transport.md section 2). Never null; unparsable text yields an
        /// empty list.
        /// </summary>
        public static List<McpServerConfig> ReadAll(string jsonText, string projectRoot)
        {
            var result = new List<McpServerConfig>();
            if (string.IsNullOrEmpty(jsonText))
            {
                return result;
            }
            JsonNode root;
            try
            {
                root = JsonParser.Parse(jsonText);
            }
            catch (Exception)
            {
                return result;
            }
            if (root == null || !root.IsObject)
            {
                return result;
            }
            AppendServers(root["mcpServers"], result);
            if (!string.IsNullOrEmpty(projectRoot))
            {
                JsonNode projects = root["projects"];
                if (projects.IsObject)
                {
                    foreach (KeyValuePair<string, JsonNode> pair in projects.Properties)
                    {
                        if (SamePath(pair.Key, projectRoot))
                        {
                            AppendServers(pair.Value["mcpServers"], result);
                        }
                    }
                }
            }
            return result;
        }

        private static void AppendServers(JsonNode servers, List<McpServerConfig> into)
        {
            if (servers == null || !servers.IsObject)
            {
                return;
            }
            foreach (KeyValuePair<string, JsonNode> pair in servers.Properties)
            {
                McpServerConfig config = FromJson(pair.Key, pair.Value);
                if (config != null)
                {
                    into.Add(config);
                }
            }
        }

        /// <summary>Path equality loose enough for the CLI's project keys (separator and trailing-slash agnostic, case-insensitive).</summary>
        internal static bool SamePath(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        }

        public McpServerConfig Clone()
        {
            return new McpServerConfig
            {
                name = name ?? string.Empty,
                transport = transport ?? TransportStdio,
                enabled = enabled,
                command = command ?? string.Empty,
                args = args != null ? new List<string>(args) : new List<string>(),
                env = env != null ? new List<string>(env) : new List<string>(),
                url = url ?? string.Empty,
                headers = headers != null ? new List<string>(headers) : new List<string>()
            };
        }

        public static List<McpServerConfig> CloneList(List<McpServerConfig> source)
        {
            var result = new List<McpServerConfig>();
            if (source == null)
            {
                return result;
            }
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    result.Add(source[i].Clone());
                }
            }
            return result;
        }

        /// <summary>Field-wise equality of two lists, in order (SettingsChangeDetector).</summary>
        public static bool ListsEqual(List<McpServerConfig> a, List<McpServerConfig> b)
        {
            int countA = a == null ? 0 : a.Count;
            int countB = b == null ? 0 : b.Count;
            if (countA != countB)
            {
                return false;
            }
            for (int i = 0; i < countA; i++)
            {
                if (!SameAs(a[i], b[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool SameAs(McpServerConfig a, McpServerConfig b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            return string.Equals(a.name ?? string.Empty, b.name ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.transport ?? string.Empty, b.transport ?? string.Empty, StringComparison.Ordinal)
                && a.enabled == b.enabled
                && string.Equals(a.command ?? string.Empty, b.command ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.url ?? string.Empty, b.url ?? string.Empty, StringComparison.Ordinal)
                && LinesEqual(a.args, b.args) && LinesEqual(a.env, b.env) && LinesEqual(a.headers, b.headers);
        }

        private static bool LinesEqual(List<string> a, List<string> b)
        {
            int countA = a == null ? 0 : a.Count;
            int countB = b == null ? 0 : b.Count;
            if (countA != countB)
            {
                return false;
            }
            for (int i = 0; i < countA; i++)
            {
                if (!string.Equals(a[i] ?? string.Empty, b[i] ?? string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
    }
}

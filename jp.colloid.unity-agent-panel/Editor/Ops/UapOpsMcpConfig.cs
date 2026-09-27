using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Model;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Builds the `--mcp-config` JSON payload for the UapOps server
    /// (docs/research/08-mcp-transport.md section 1.2 -- the CLI accepts
    /// both an inline JSON string and a JSON file path, auto-detected).
    /// The payload carries the server's Bearer token, so the normal spawn
    /// path writes it to a FILE (<see cref="EnsureConfigFileWritten"/>)
    /// and puts only the path on the command line -- an inline argument
    /// would expose the token to every local process via the world-readable
    /// process command line (`ps` / WMI / Task Manager), which is SEC-2.
    /// The builders are pure functions with no HTTP/Unity dependency, so
    /// the exact wire shape is unit tested without spawning a real CLI
    /// process or HTTP server.
    /// </summary>
    public static class UapOpsMcpConfig
    {
        /// <summary>
        /// The mcpServers key AND the mcp_reconnect serverName (design
        /// section 1.1's control_request, R08 section 3.4) -- these two
        /// MUST always agree, since mcp_reconnect addresses a server by
        /// this exact name.
        /// </summary>
        public const string ServerName = "unity-ops";

        /// <summary>
        /// The CLI's wire prefix for every tool THIS server exposes, per
        /// the verified `mcp__&lt;server&gt;__&lt;tool&gt;` naming (docs/research/
        /// 08-mcp-transport.md section 1.2/5: a real CLI round trip named a
        /// server "toy" tool "ping" as `mcp__toy__ping`) -- used to resolve
        /// a can_use_tool/tool_use wire name back to the registered
        /// <see cref="IUapTool"/> (design section 8.2 B2's "not undoable"
        /// permission-card badge and turn-level warning).
        /// </summary>
        public const string ToolNamePrefix = "mcp__" + ServerName + "__";

        /// <summary>
        /// Strips <see cref="ToolNamePrefix"/> from a wire tool_use/
        /// can_use_tool name, returning the bare registry name (e.g.
        /// "asset_create") -- null when <paramref name="wireToolName"/> is
        /// null/empty or does not start with the prefix (not one of ours,
        /// e.g. a plain "Write" or a different MCP server's tool).
        /// </summary>
        public static string StripToolNamePrefix(string wireToolName)
        {
            if (string.IsNullOrEmpty(wireToolName)
                || !wireToolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal))
            {
                return null;
            }
            return wireToolName.Substring(ToolNamePrefix.Length);
        }

        /// <summary>
        /// Builds `{"mcpServers":{"unity-ops":{"type":"http","url":"http://127.0.0.1:&lt;port&gt;/mcp","headers":{"Authorization":"Bearer &lt;token&gt;"}}}}`.
        /// </summary>
        public static string BuildConfigJson(int port, string token)
        {
            return BuildConfigJson(true, port, token, null);
        }

        /// <summary>
        /// The same document with the user's own servers (design note
        /// docs/design-notes/2026-09-27-mcp-servers-in-panel.md section 2)
        /// after the UapOps entry: each enabled, complete entry under its
        /// name, in list order. An entry named like the UapOps server is
        /// skipped (the panel's own server must stay the one the CLI
        /// reaches under that name), as is a later duplicate of a name.
        /// <paramref name="includeUapOps"/> false leaves the UapOps entry
        /// out (UapOps switched off, user servers still wanted).
        /// </summary>
        public static string BuildConfigJson(bool includeUapOps, int port, string token,
            IList<McpServerConfig> extraServers)
        {
            JsonNode servers = JsonNode.NewObject();
            if (includeUapOps)
            {
                servers.Set(ServerName, JsonNode.NewObject()
                    .Set("type", "http")
                    .Set("url", "http://127.0.0.1:" + port + "/mcp")
                    .Set("headers", JsonNode.NewObject()
                        .Set("Authorization", "Bearer " + (token ?? string.Empty))));
            }
            if (extraServers != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { ServerName };
                for (int i = 0; i < extraServers.Count; i++)
                {
                    McpServerConfig server = extraServers[i];
                    if (server == null || !server.enabled || !server.IsComplete)
                    {
                        continue;
                    }
                    string name = server.name.Trim();
                    if (!seen.Add(name))
                    {
                        continue;
                    }
                    servers.Set(name, server.ToJson());
                }
            }
            return JsonWriter.Write(JsonNode.NewObject().Set("mcpServers", servers));
        }

        /// <summary>True when at least one entry would be written by BuildConfigJson.</summary>
        public static bool HasUsableServer(IList<McpServerConfig> extraServers)
        {
            if (extraServers == null)
            {
                return false;
            }
            for (int i = 0; i < extraServers.Count; i++)
            {
                McpServerConfig server = extraServers[i];
                if (server != null && server.enabled && server.IsComplete
                    && !string.Equals(server.name.Trim(), ServerName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Config filename, under <see cref="GateHookInstaller.DefaultRelativeDirectory"/>
        /// (the package's one well-known generated-file directory,
        /// git-ignored by Unity's own default template).
        /// </summary>
        public const string ConfigFileName = "uap-mcp-config.json";

        /// <summary>
        /// SEC-2: writes <see cref="BuildConfigJson"/>'s payload to
        /// `&lt;projectRoot&gt;/UserSettings/AgentPanel/uap-mcp-config.json` so the
        /// spawn can pass `--mcp-config &lt;path&gt;` instead of the inline JSON --
        /// the payload carries the Bearer token, and a command-line argument
        /// is readable by every local process (`/proc/*/cmdline` is
        /// world-readable on Linux regardless of umask; WMI/Task Manager on
        /// Windows), while a file inherits the project directory's own
        /// ACLs. docs/research/08-mcp-transport.md section 1.2 measured that
        /// the CLI auto-detects file path vs inline string and produces the
        /// identical `system/init` connection either way.
        ///
        /// Rewrites only when the freshly computed content differs from
        /// what is on disk (token changes per server start, not per spawn,
        /// so most reconnect spawns skip the write). Returns the ABSOLUTE
        /// path (the CLI resolves `--mcp-config` relative to its own cwd)
        /// on success, or null when the directory/file could not be written
        /// (logged; the caller decides the fallback).
        ///
        /// Tamper note: the file is agent-readable and (in acceptEdits)
        /// agent-writable, and that is accepted -- it is regenerated from
        /// the live server state BEFORE every spawn, so an edit never
        /// survives to a process start, and the token itself grants the
        /// agent nothing it does not already have through the CLI's own
        /// authorized MCP connection.
        /// </summary>
        public static string EnsureConfigFileWritten(string projectRoot, int port, string token, Action<string> log = null)
        {
            return EnsureConfigFileWritten(projectRoot, true, port, token, null, log);
        }

        /// <summary>The extra-servers form of <see cref="EnsureConfigFileWritten(string,int,string,Action{string})"/>.</summary>
        public static string EnsureConfigFileWritten(string projectRoot, bool includeUapOps, int port, string token,
            IList<McpServerConfig> extraServers, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                return null;
            }
            string directory = Path.Combine(projectRoot,
                GateHookInstaller.DefaultRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            string configPath = Path.Combine(directory, ConfigFileName);
            string json = BuildConfigJson(includeUapOps, port, token, extraServers);

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to create '" + directory + "': " + ex.Message);
                return null;
            }
            if (!WriteIfChanged(configPath, json, log))
            {
                return null;
            }
            try
            {
                return Path.GetFullPath(configPath);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to resolve '" + configPath + "': " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Write-if-changed via the shared MODEL-2 helper (UTF-8 no BOM,
        /// atomic staged-tmp swap, skip when the on-disk content already
        /// matches -- the token rotates per server start, not per spawn, so
        /// most reconnect spawns skip the write).
        /// </summary>
        private static bool WriteIfChanged(string path, string content, Action<string> log)
        {
            return AtomicFile.WriteAllTextIfChanged(
                path, content, delegate (string message) { Log(log, message); });
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[UapOpsMcpConfig] " + message);
            }
        }
    }
}

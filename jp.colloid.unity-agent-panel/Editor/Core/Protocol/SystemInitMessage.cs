using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Core.Protocol
{
    /// <summary>Status entry for one MCP server as reported in system/init.</summary>
    public sealed class McpServerStatus
    {
        /// <summary>Server name.</summary>
        public string Name;
        /// <summary>"pending" | "connected" | "failed" (observed values).</summary>
        public string Status;
    }

    /// <summary>
    /// One loaded plugin as reported in system/init `plugins[]`. Fields
    /// measured on CLI 2.1.267 (docs/verify/2026-09-10-unity-plugin-init-event.json):
    /// name, path, source ("plugin@marketplace"), version. Older captures
    /// (2.1.218) carry an empty array, so every field is optional.
    /// </summary>
    public sealed class PluginEntry
    {
        public string Name;
        public string Path;
        public string Source;
        public string Version;
    }

    /// <summary>One plugin that failed to load, from system/init `plugin_errors[]` (documented shape: plugin, type, message; the key is omitted when there are none).</summary>
    public sealed class PluginError
    {
        public string Plugin;
        public string ErrorType;
        public string Message;
    }

    /// <summary>
    /// type=system, subtype=init -- always the first line of a session.
    /// Captured example fields (CLI v2.1.218): cwd, session_id, tools[],
    /// mcp_servers[], model, permissionMode, slash_commands[], apiKeySource,
    /// claude_code_version, output_style, agents[], skills[], plugins[],
    /// capabilities[], uuid, fast_mode_state.
    /// Required: session_id (needed for --resume); everything else optional.
    /// </summary>
    public sealed class SystemInitMessage : StreamJsonMessage
    {
        public string Cwd { get; private set; }
        public string Model { get; private set; }
        public string PermissionMode { get; private set; }
        public string ClaudeCodeVersion { get; private set; }
        public string ApiKeySource { get; private set; }
        public string OutputStyle { get; private set; }
        /// <summary>
        /// Display name of the ACP backend whose bridge synthesized this
        /// init (`acp_backend`, set only by AcpProtocolBridge); null for a
        /// system/init the Claude Code CLI itself emitted. Claude-only
        /// readers (the Account card's apiKeySource note) use
        /// <see cref="IsAcpSynthesized"/> to skip it (design note
        /// 2026-09-17-account-card-agent-picker-and-acp-init.md section 2).
        /// </summary>
        public string AcpBackend { get; private set; }
        /// <summary>True when an ACP bridge, not the Claude Code CLI, produced this init.</summary>
        public bool IsAcpSynthesized
        {
            get { return !string.IsNullOrEmpty(AcpBackend); }
        }
        public string[] Tools { get; private set; }
        public string[] SlashCommands { get; private set; }
        public string[] Agents { get; private set; }
        public string[] Capabilities { get; private set; }
        public McpServerStatus[] McpServers { get; private set; }
        /// <summary>Plugins this session loaded (never null; empty when the key is absent or empty).</summary>
        public PluginEntry[] Plugins { get; private set; }
        /// <summary>Plugins that failed to load (never null; empty when the key is absent).</summary>
        public PluginError[] PluginErrors { get; private set; }

        private SystemInitMessage()
        {
            Type = InboundType.SystemInit;
        }

        /// <summary>Maps a system/init line. Returns null (logged) when session_id is missing.</summary>
        public static SystemInitMessage FromJson(JsonNode node, Action<string> logger)
        {
            string sessionId = node["session_id"].AsString();
            if (string.IsNullOrEmpty(sessionId))
            {
                Log(logger, "Dropped system/init message: missing required field 'session_id'.");
                return null;
            }

            var msg = new SystemInitMessage();
            msg.ReadCommonFields(node);
            msg.Cwd = node["cwd"].AsString();
            msg.Model = node["model"].AsString();
            msg.PermissionMode = node["permissionMode"].AsString();
            msg.ClaudeCodeVersion = node["claude_code_version"].AsString();
            msg.ApiKeySource = node["apiKeySource"].AsString();
            msg.OutputStyle = node["output_style"].AsString();
            msg.AcpBackend = node["acp_backend"].AsString();
            msg.Tools = node["tools"].AsStringArray();
            msg.SlashCommands = node["slash_commands"].AsStringArray();
            msg.Agents = node["agents"].AsStringArray();
            msg.Capabilities = node["capabilities"].AsStringArray();

            var servers = new List<McpServerStatus>();
            foreach (var entry in node["mcp_servers"].Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                servers.Add(new McpServerStatus
                {
                    Name = entry["name"].AsString(),
                    Status = entry["status"].AsString()
                });
            }
            msg.McpServers = servers.ToArray();

            var plugins = new List<PluginEntry>();
            foreach (var entry in node["plugins"].Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                plugins.Add(new PluginEntry
                {
                    Name = entry["name"].AsString(),
                    Path = entry["path"].AsString(),
                    Source = entry["source"].AsString(),
                    Version = entry["version"].AsString()
                });
            }
            msg.Plugins = plugins.ToArray();

            var pluginErrors = new List<PluginError>();
            foreach (var entry in node["plugin_errors"].Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                pluginErrors.Add(new PluginError
                {
                    Plugin = entry["plugin"].AsString(),
                    ErrorType = entry["type"].AsString(),
                    Message = entry["message"].AsString()
                });
            }
            msg.PluginErrors = pluginErrors.ToArray();
            return msg;
        }
    }
}

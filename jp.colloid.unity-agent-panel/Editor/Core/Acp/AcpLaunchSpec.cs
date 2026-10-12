using System;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// Everything the ACP bridge needs that Claude Code would otherwise get
    /// as command-line flags (design note docs/design-notes/2026-09-10-acp-
    /// backends.md section 3.2). AgentHub fills this from PanelSettings
    /// right before spawning; AgentClient.Start's Claude-shaped argument
    /// string is ignored by AcpBridgeTransport, which reads this instead.
    /// Plain data, no behavior, Unity-free.
    /// </summary>
    public sealed class AcpLaunchSpec
    {
        /// <summary>Which backend this is (for the process-name bookkeeping and diagnostics text).</summary>
        public AgentBackend Backend = AgentBackend.AcpCustom;

        /// <summary>Argument string passed verbatim to the agent executable (e.g. "--experimental-acp").</summary>
        public string Arguments = string.Empty;

        /// <summary>
        /// ACP session id to `session/load` instead of creating a new one.
        /// Null/empty = fresh session. Silently falls back to session/new
        /// when the agent does not advertise loadSession.
        /// </summary>
        public string ResumeSessionId;

        /// <summary>Model id to select after the session exists (session/set_model). Null/empty = agent default.</summary>
        public string Model;

        /// <summary>
        /// The panel's Claude-style permission mode (default/plan/
        /// acceptEdits/bypassPermissions). Mapped onto the agent's own
        /// session modes by AcpProtocolBridge.ResolveModeId when one
        /// matches; otherwise ignored.
        /// </summary>
        public string PermissionMode;

        /// <summary>
        /// The composed custom-instructions text (Settings "Custom
        /// instructions" + cost policy + UapOps steering + extension
        /// profiles). ACP has no system-prompt parameter, so the bridge
        /// prepends this to the FIRST prompt of a NEW session as a clearly
        /// delimited block; a loaded session already saw it.
        /// </summary>
        public string SystemPrompt;

        /// <summary>UapOps MCP server name as registered with the agent (UapOpsMcpConfig.ServerName). Null = do not register.</summary>
        public string McpServerName;

        /// <summary>UapOps MCP endpoint URL (http://127.0.0.1:port/mcp).</summary>
        public string McpUrl;

        /// <summary>Bearer token for the UapOps endpoint; sent as an Authorization header in session/new.</summary>
        public string McpBearerToken;

        /// <summary>
        /// Section 2.7's fallback for an ACP agent that does not advertise
        /// mcpCapabilities.http: builds the stdio relay launch spec on
        /// demand. Called at most once per <c>BuildMcpServers</c> (never
        /// during session/load's replay, and never when the agent DOES
        /// speak http -- the bridge only reaches for this after
        /// OnInitializeResult found <c>!_capMcpHttp</c>), from whichever
        /// thread the bridge runs OpenSession/OpenNewSession on -- per
        /// AcpProtocolBridge's own class doc, that is the ACP reader
        /// thread, not the Unity main thread, so a slow first-time compile
        /// inside this delegate never freezes the editor. AgentHub sets it
        /// (from UapStdioRelayBuilder, AFTER capturing Unity's toolchain
        /// paths on the main thread -- see that class's doc comment for
        /// why) only when McpUrl above is also set; leaving it null keeps
        /// today's http-only behavior (no stdio fallback offered).
        /// MUST NOT throw -- AcpProtocolBridge wraps every call in a
        /// try/catch regardless, but a well-behaved factory never lets an
        /// exception reach it. Null return = no relay available this spawn
        /// (unresolved toolchain, compile failure, or UapOps disabled);
        /// the bridge then reports the agent as unable to reach UapOps at
        /// all, same as before this factory existed.
        /// </summary>
        public Func<AcpStdioServer> McpStdioRelayFactory;

        /// <summary>
        /// Preferred `authenticate` method id when the agent reports that
        /// authentication is required. Null/empty = the first method the
        /// agent advertised.
        /// </summary>
        public string AuthMethodId;

        /// <summary>Name reported in clientInfo.name.</summary>
        public string ClientName = "unity-agent-panel";

        /// <summary>Version reported in clientInfo.version.</summary>
        public string ClientVersion = string.Empty;
    }
}

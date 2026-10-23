using System.Collections.Generic;

namespace Colloid.AgentPanel.Core.Acp
{
    /// <summary>
    /// A local stdio MCP server launch spec (design note docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 2.7): the command/args an ACP
    /// agent should spawn as a CHILD process to reach UapOps when the agent
    /// does not advertise mcpCapabilities.http -- the relay then forwards
    /// whatever the agent writes to its stdin on to UapOps over the same
    /// HTTP loopback the http path already uses, and relays the answer
    /// back on stdout (Editor/Tools~/UapMcpStdioRelay/RelayCore.cs +
    /// RelayMain.cs).
    ///
    /// Built by UapStdioRelayBuilder (Unity-coupled: it resolves and
    /// compiles the relay against Unity's own bundled dotnet + Roslyn,
    /// which needs EditorApplication/PackageManager APIs this Unity-free
    /// Acp assembly split must not reference) and handed to
    /// AcpProtocolBridge through <see cref="AcpLaunchSpec.McpStdioRelayFactory"/>.
    /// This type itself is plain data with no behavior, so it can travel
    /// through both the Unity-coupled builder and the Unity-free bridge
    /// (and, like AcpLaunchSpec/AcpProtocolBridge, compiles verbatim into
    /// ci/SmokeTests).
    ///
    /// Deliberately does NOT carry the bearer token: AcpProtocolBridge
    /// already holds it (AcpLaunchSpec.McpBearerToken, the same field the
    /// http path's Authorization header reads) and adds it to the env
    /// array itself when it serializes this into the ACP McpServer::Stdio
    /// shape -- UapStdioRelayBuilder never needs to know it, keeping the
    /// toolchain-resolution/compile code (this task's riskiest surface)
    /// free of anything security-sensitive.
    /// </summary>
    public sealed class AcpStdioServer
    {
        /// <summary>Absolute path to the executable the agent should spawn (Unity's bundled dotnet host).</summary>
        public string Command;

        /// <summary>
        /// Command-line arguments, in order -- e.g. ["exec", "&lt;path to
        /// the compiled relay.dll&gt;", "&lt;UapOps MCP URL&gt;"] (RelayMain.Main
        /// reads the URL as its one positional argument; the bearer token
        /// travels as the UAP_MCP_TOKEN environment variable instead, never
        /// here -- see this class's own doc comment).
        /// </summary>
        public List<string> Args = new List<string>();

        /// <summary>
        /// Extra environment variables the relay process needs BESIDES the
        /// bearer token. Normally empty -- AcpProtocolBridge appends
        /// UAP_MCP_TOKEN itself (see this class's doc comment), so a
        /// builder that has no other environment requirement can leave
        /// this untouched.
        /// </summary>
        public Dictionary<string, string> Env = new Dictionary<string, string>();
    }
}

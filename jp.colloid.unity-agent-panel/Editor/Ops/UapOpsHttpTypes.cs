namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Transport-agnostic view of one inbound HTTP request to the UapOps
    /// `/mcp` endpoint. UapOpsHttpServer (the real HttpListener-backed
    /// server) builds this from an HttpListenerContext; tests build it by
    /// hand -- neither needs a real socket, which is the point (design's
    /// "structure the server so the request handler is a pure function
    /// (bytes/JsonNode in -&gt; response out)").
    /// </summary>
    public sealed class UapOpsHttpRequest
    {
        public string Method;
        /// <summary>Absolute path, trailing slash trimmed (e.g. "/mcp", "" normalized to "/" by the caller).</summary>
        public string Path;
        /// <summary>Raw "Authorization" header value, or null when absent.</summary>
        public string AuthorizationHeader;
        /// <summary>Raw request body text (POST only; ignored for GET/DELETE).</summary>
        public string Body;
    }

    /// <summary>Transport-agnostic HTTP response produced by UapOpsRequestHandler.Handle.</summary>
    public sealed class UapOpsHttpResponse
    {
        public int StatusCode;
        /// <summary>Null when there is no body (e.g. 202/405 responses).</summary>
        public string ContentType;
        /// <summary>Null when there is no body.</summary>
        public string Body;
        /// <summary>Set only on a successful "initialize" response (Mcp-Session-Id header).</summary>
        public string McpSessionId;

        public static UapOpsHttpResponse StatusOnly(int statusCode)
        {
            return new UapOpsHttpResponse { StatusCode = statusCode };
        }
    }
}

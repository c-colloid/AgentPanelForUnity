using System;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>One completed GET as uap_web_fetch consumes it.</summary>
    public sealed class UapWebFetchResponse
    {
        public int StatusCode;
        /// <summary>Raw Content-Type header (may carry a charset parameter), or null.</summary>
        public string ContentType;
        /// <summary>URL the body came from after redirects.</summary>
        public Uri FinalUrl;
        /// <summary>The first URL when at least one redirect was followed; null otherwise.</summary>
        public Uri RedirectedFrom;
        public byte[] Body = new byte[0];
    }

    /// <summary>
    /// The network seam of uap_web_fetch: production uses
    /// <see cref="UapHttpWebFetcher"/>, tests hand the tool canned
    /// responses. Implementations enforce <see cref="UapWebFetchPolicy"/>
    /// (destination check per hop, byte and time caps) and throw
    /// <see cref="UapWebFetchException"/> with an agent-readable message
    /// for anything that is not a 2xx body.
    /// </summary>
    public interface IUapWebFetcher
    {
        UapWebFetchResponse Fetch(Uri url);
    }

    /// <summary>A fetch that produced no usable body; the message is quoted to the agent verbatim.</summary>
    public sealed class UapWebFetchException : Exception
    {
        public UapWebFetchException(string message) : base(message)
        {
        }

        public UapWebFetchException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}

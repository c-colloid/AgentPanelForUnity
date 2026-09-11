using System;
using System.Security.Cryptography;
using System.Text;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Bearer-token generation and check for the UapOps HTTP server (design
    /// section 1.1: loopback + per-editor-session token, R08's measured
    /// "Authorization: Bearer &lt;token&gt;" header shape). The check is a
    /// pure function -- no HttpListener/request object involved -- so it is
    /// unit tested directly without a real socket.
    /// </summary>
    public static class UapOpsAuth
    {
        private const int TokenByteLength = 24;

        /// <summary>Generates a fresh random token (48 lowercase hex chars), one per editor session/server start.</summary>
        public static string GenerateToken()
        {
            var bytes = new byte[TokenByteLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// True only when <paramref name="authorizationHeaderValue"/> is
        /// EXACTLY "Bearer " + <paramref name="expectedToken"/> (ordinal,
        /// case-sensitive -- Bearer tokens are opaque values, not
        /// case-insensitive identifiers). False when either the header or
        /// the expected token is null/empty, so a server that has not
        /// generated a token yet (or a request with no header at all) can
        /// never authorize.
        /// </summary>
        public static bool IsAuthorized(string authorizationHeaderValue, string expectedToken)
        {
            if (string.IsNullOrEmpty(expectedToken) || string.IsNullOrEmpty(authorizationHeaderValue))
            {
                return false;
            }
            return string.Equals(authorizationHeaderValue, "Bearer " + expectedToken, StringComparison.Ordinal);
        }
    }
}

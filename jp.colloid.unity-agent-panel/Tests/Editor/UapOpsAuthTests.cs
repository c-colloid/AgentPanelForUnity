using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Bearer-token pure-function tests (design section 1.1: loopback +
    /// per-session token; the check itself is exercised here directly, no
    /// HttpListener/socket involved -- see UapOpsRequestHandlerTests for
    /// the 401 response shape this feeds into).
    /// </summary>
    [TestFixture]
    public class UapOpsAuthTests
    {
        [Test]
        public void IsAuthorized_ExactBearerHeader_ReturnsTrue()
        {
            Assert.IsTrue(UapOpsAuth.IsAuthorized("Bearer secret123", "secret123"));
        }

        [Test]
        public void IsAuthorized_MissingHeader_ReturnsFalse()
        {
            Assert.IsFalse(UapOpsAuth.IsAuthorized(null, "secret123"));
            Assert.IsFalse(UapOpsAuth.IsAuthorized(string.Empty, "secret123"));
        }

        [Test]
        public void IsAuthorized_WrongToken_ReturnsFalse()
        {
            Assert.IsFalse(UapOpsAuth.IsAuthorized("Bearer wrong", "secret123"));
        }

        [Test]
        public void IsAuthorized_MissingBearerPrefix_ReturnsFalse()
        {
            Assert.IsFalse(UapOpsAuth.IsAuthorized("secret123", "secret123"));
        }

        [Test]
        public void IsAuthorized_CaseSensitiveToken_ReturnsFalse()
        {
            Assert.IsFalse(UapOpsAuth.IsAuthorized("Bearer SECRET123", "secret123"));
        }

        [Test]
        public void IsAuthorized_LowercaseBearerScheme_ReturnsFalse()
        {
            // "Bearer" (capital B) is the exact measured wire form (R08
            // section 1.1) -- this is not a case-insensitive scheme match.
            Assert.IsFalse(UapOpsAuth.IsAuthorized("bearer secret123", "secret123"));
        }

        [Test]
        public void IsAuthorized_NoExpectedTokenYet_AlwaysReturnsFalse()
        {
            // A server that has not generated a token yet must never
            // authorize anything, even an empty-header request.
            Assert.IsFalse(UapOpsAuth.IsAuthorized("Bearer anything", null));
            Assert.IsFalse(UapOpsAuth.IsAuthorized(null, null));
        }

        [Test]
        public void GenerateToken_ReturnsNonEmpty_AndDiffersAcrossCalls()
        {
            string a = UapOpsAuth.GenerateToken();
            string b = UapOpsAuth.GenerateToken();
            Assert.IsFalse(string.IsNullOrEmpty(a));
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void GenerateToken_ProducesRoundTrippableAuthorization()
        {
            string token = UapOpsAuth.GenerateToken();
            Assert.IsTrue(UapOpsAuth.IsAuthorized("Bearer " + token, token));
        }

        [Test]
        public void GenerateToken_IsLowercaseHex_NoSeparators()
        {
            string token = UapOpsAuth.GenerateToken();
            var allowed = new HashSet<char>("0123456789abcdef");
            foreach (char c in token)
            {
                Assert.IsTrue(allowed.Contains(c), "Unexpected character '" + c + "' in generated token.");
            }
        }
    }
}

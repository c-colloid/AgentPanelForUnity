using System.Collections.Generic;
using System.Text;
using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// <see cref="ExtensionProfileTrust"/> (design section 8.2 B3, C6 "hash
    /// pin + tamper invalidation"; OPS-11 machine binding): a bundled
    /// profile is always trusted; a user profile is trusted only by an
    /// approval TOKEN recomputed from this machine's salt and the file's
    /// content hash -- any content edit invalidates (the hash feeds the
    /// MAC), and a token minted under another machine's salt never
    /// verifies, which is exactly what stops a malicious project shipping
    /// a pre-approved State.asset.
    /// </summary>
    public class ExtensionProfileTrustTests
    {
        private const string Salt = "test-salt";

        private static string Token(string contentHash, string salt = Salt)
        {
            return ExtensionProfileTrust.ComputeApprovalToken(salt, contentHash);
        }

        [Test]
        public void Bundled_AlwaysTrusted_EvenWithNoApprovedTokensAtAll()
        {
            Assert.IsTrue(ExtensionProfileTrust.IsTrusted(true, null, null, Salt));
            Assert.IsTrue(ExtensionProfileTrust.IsTrusted(true, "any-hash", new List<string>(), Salt));
        }

        [Test]
        public void UserProfile_TokenFromThisSalt_IsTrusted()
        {
            var approved = new List<string> { Token("abc123") };
            Assert.IsTrue(ExtensionProfileTrust.IsTrusted(false, "abc123", approved, Salt));
        }

        [Test]
        public void UserProfile_TokenMatch_IsCaseInsensitive_AndHashCasingStable()
        {
            // Tokens are lowercase hex; a store that upper-cased them must
            // still verify, and the CONTENT hash's own casing must not
            // change the token (the old raw-hash comparison was
            // case-insensitive; the token keeps that by lowercasing the
            // hash before MACing).
            var approved = new List<string> { Token("abc123").ToUpperInvariant() };
            Assert.IsTrue(ExtensionProfileTrust.IsTrusted(false, "ABC123", approved, Salt));
        }

        [Test]
        public void UserProfile_RawContentHashInList_IsNotTrusted()
        {
            // OPS-11's migration rule: a legacy store that still carries
            // the RAW content hash (or a State.asset a project shipped
            // with raw hashes) fails verification -- re-approval required.
            var approved = new List<string> { "abc123" };
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, "abc123", approved, Salt));
        }

        [Test]
        public void UserProfile_TokenFromADifferentSalt_IsNotTrusted()
        {
            // The transplanted-State.asset attack: a token minted under
            // some other machine's salt must never verify here.
            var approved = new List<string> { Token("abc123", "attacker-salt") };
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, "abc123", approved, Salt));
        }

        [Test]
        public void UserProfile_NullOrEmptyApprovedList_IsNotTrusted()
        {
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, "abc123", null, Salt));
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, "abc123", new List<string>(), Salt));
        }

        [Test]
        public void UserProfile_NullOrEmptyContentHash_IsNotTrusted_EvenWithMatchingEntries()
        {
            var approved = new List<string> { Token(string.Empty) };
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, null, approved, Salt));
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, string.Empty, approved, Salt));
        }

        [Test]
        public void TamperInvalidation_ApprovingOriginal_ThenTamperingContent_NoLongerTrusted()
        {
            byte[] original = Encoding.UTF8.GetBytes("{ \"id\": \"x\", \"instructionLines\": [\"a\"] }");
            byte[] tampered = Encoding.UTF8.GetBytes("{ \"id\": \"x\", \"instructionLines\": [\"a-plus-one-char\"] }");
            string originalHash = UserExtensionProfileStore.ComputeContentHash(original);
            string tamperedHash = UserExtensionProfileStore.ComputeContentHash(tampered);

            // The user approved the ORIGINAL content -- its token is pinned.
            var approvedTokens = new List<string> { Token(originalHash) };
            Assert.IsTrue(ExtensionProfileTrust.IsTrusted(false, originalHash, approvedTokens, Salt));

            // The file on disk was then edited (tampered). Its hash -- and
            // therefore its token -- changes, so trust silently drops
            // without any extra bookkeeping, exactly design section 8.2
            // B3's requirement, machine binding included.
            Assert.IsFalse(ExtensionProfileTrust.IsTrusted(false, tamperedHash, approvedTokens, Salt));
        }

        [Test]
        public void ComputeApprovalToken_IsDeterministic_AndSaltSensitive()
        {
            Assert.AreEqual(Token("h1"), Token("h1"), "same salt + hash must be stable");
            Assert.AreNotEqual(Token("h1"), Token("h2"), "different hashes must differ");
            Assert.AreNotEqual(Token("h1", "salt-a"), Token("h1", "salt-b"),
                "different salts must differ");
            StringAssert.IsMatch("^[0-9a-f]{64}$", Token("h1"),
                "lowercase hex HMAC-SHA256");
        }
    }
}

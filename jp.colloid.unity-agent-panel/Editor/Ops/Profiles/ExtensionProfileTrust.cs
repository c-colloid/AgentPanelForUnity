using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// Pure trust decision behind design section 8.2 B3: "v1 is bundled-only
    /// auto-injection ... a user/third-party profile is only injected once
    /// its content hash is pinned via explicit approval; any change
    /// invalidates it". Deliberately factored out of
    /// <see cref="ExtensionProfileService"/> so both halves of that
    /// contract -- bundled is unconditionally trusted, a user profile is
    /// trusted only by an exact approval-token match -- are unit-tested
    /// without touching disk or PanelStateStore.
    ///
    /// OPS-11 (SR-machine-approval): the approved list stores
    /// HMAC-SHA256(machine salt, content hash) TOKENS, not raw content
    /// hashes. The raw-hash scheme let a malicious project ship a
    /// pre-populated State.asset (it lives INSIDE the project folder)
    /// alongside matching .uap-profiles/*.json and have them injected
    /// into the system prompt with the victim never approving anything.
    /// The salt lives in EditorPrefs (<see cref="MachineApprovalSalt"/> --
    /// per OS user, outside every project tree), so a token minted on one
    /// machine verifies only there: content edits still invalidate
    /// (content hash feeds the MAC) and now transplanted approvals do
    /// too. Legacy raw-hash entries simply fail the token comparison and
    /// surface as pending-approval again -- the one-time migration is
    /// re-approving.
    /// </summary>
    public static class ExtensionProfileTrust
    {
        /// <summary>
        /// The machine-bound approval token for one profile content hash:
        /// lowercase hex HMAC-SHA256 keyed by <paramref name="approvalSalt"/>
        /// over the LOWERCASED content hash (so the token is stable across
        /// hash-casing differences, matching the old case-insensitive hash
        /// comparison). Pure -- callers supply the salt.
        /// </summary>
        public static string ComputeApprovalToken(string approvalSalt, string contentHashHex)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(approvalSalt ?? string.Empty)))
            {
                byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(
                    (contentHashHex ?? string.Empty).ToLowerInvariant()));
                var sb = new StringBuilder(mac.Length * 2);
                for (int i = 0; i < mac.Length; i++)
                {
                    sb.Append(mac[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// True when a profile may be injected: always true for a bundled
        /// profile (reviewed as part of this repository); for a user
        /// profile, true only when the token recomputed from THIS machine's
        /// salt and <paramref name="contentHashHex"/> is present
        /// (case-insensitively) in <paramref name="approvedTokens"/>. ANY
        /// edit to an already-approved file changes its content hash and
        /// therefore its token (re-approval required), and a token minted
        /// under a different machine's salt never matches.
        /// </summary>
        public static bool IsTrusted(bool isBundled, string contentHashHex,
            IEnumerable<string> approvedTokens, string approvalSalt)
        {
            if (isBundled)
            {
                return true;
            }
            if (string.IsNullOrEmpty(contentHashHex) || approvedTokens == null)
            {
                return false;
            }
            string expected = ComputeApprovalToken(approvalSalt, contentHashHex);
            foreach (string approved in approvedTokens)
            {
                if (string.Equals(approved, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

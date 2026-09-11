using System;
using UnityEditor;

namespace Colloid.AgentPanel.Ops.Profiles
{
    /// <summary>
    /// The machine-local secret behind Extension Profile approval tokens
    /// (OPS-11, SR-machine-approval; see ExtensionProfileTrust's class doc
    /// for the threat this closes). A random GUID minted on first use and
    /// stored in EditorPrefs -- per OS user, OUTSIDE every project tree --
    /// so nothing a project or package ships can contain it. Losing the
    /// pref (new machine, cleared prefs) merely invalidates approvals; the
    /// user re-approves, which is the safe failure direction.
    /// </summary>
    public static class MachineApprovalSalt
    {
        internal const string EditorPrefsKey = "Colloid.AgentPanel.ProfileApprovalSalt";

        private static string _cached;

        /// <summary>Test seam: when set, Get() returns this instead of
        /// touching EditorPrefs (and the cache is bypassed), so trust
        /// tests run against a deterministic salt.</summary>
        internal static Func<string> OverrideForTests;

        public static string Get()
        {
            if (OverrideForTests != null)
            {
                return OverrideForTests();
            }
            if (!string.IsNullOrEmpty(_cached))
            {
                return _cached;
            }
            string salt = EditorPrefs.GetString(EditorPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(salt))
            {
                salt = Guid.NewGuid().ToString("N");
                EditorPrefs.SetString(EditorPrefsKey, salt);
            }
            _cached = salt;
            return salt;
        }

        /// <summary>Test-only: drops the in-memory cache so the next Get()
        /// re-reads EditorPrefs (used by MachineApprovalSaltTests around
        /// their save/restore of the real pref).</summary>
        internal static void ResetCacheForTests()
        {
            _cached = null;
        }
    }
}

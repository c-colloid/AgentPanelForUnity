using Colloid.AgentPanel.Ops.Profiles;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// OPS-11: the machine-local salt behind Extension Profile approval
    /// tokens. Save/restore the REAL EditorPrefs value around each test so
    /// the developer's (or CI's) actual approvals survive the suite.
    /// </summary>
    [TestFixture]
    public class MachineApprovalSaltTests
    {
        private string _savedPref;
        private bool _hadPref;

        [SetUp]
        public void SetUp()
        {
            _hadPref = EditorPrefs.HasKey(MachineApprovalSalt.EditorPrefsKey);
            _savedPref = _hadPref
                ? EditorPrefs.GetString(MachineApprovalSalt.EditorPrefsKey)
                : null;
            MachineApprovalSalt.OverrideForTests = null;
            MachineApprovalSalt.ResetCacheForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadPref)
            {
                EditorPrefs.SetString(MachineApprovalSalt.EditorPrefsKey, _savedPref);
            }
            else
            {
                EditorPrefs.DeleteKey(MachineApprovalSalt.EditorPrefsKey);
            }
            MachineApprovalSalt.OverrideForTests = null;
            MachineApprovalSalt.ResetCacheForTests();
        }

        [Test]
        public void FirstGet_MintsAndPersistsASalt()
        {
            EditorPrefs.DeleteKey(MachineApprovalSalt.EditorPrefsKey);
            MachineApprovalSalt.ResetCacheForTests();

            string salt = MachineApprovalSalt.Get();

            Assert.IsFalse(string.IsNullOrEmpty(salt));
            Assert.AreEqual(salt, EditorPrefs.GetString(MachineApprovalSalt.EditorPrefsKey),
                "the minted salt must persist OUTSIDE the project tree (EditorPrefs)");
        }

        [Test]
        public void Get_IsStableAcrossCallsAndCacheDrops()
        {
            EditorPrefs.DeleteKey(MachineApprovalSalt.EditorPrefsKey);
            MachineApprovalSalt.ResetCacheForTests();

            string first = MachineApprovalSalt.Get();
            string second = MachineApprovalSalt.Get();
            MachineApprovalSalt.ResetCacheForTests();
            string third = MachineApprovalSalt.Get();

            Assert.AreEqual(first, second);
            Assert.AreEqual(first, third,
                "re-reading the pref after a cache drop must yield the same salt");
        }

        [Test]
        public void Get_ReusesAnExistingPref_NeverReplacesIt()
        {
            EditorPrefs.SetString(MachineApprovalSalt.EditorPrefsKey, "pre-existing-salt");
            MachineApprovalSalt.ResetCacheForTests();

            Assert.AreEqual("pre-existing-salt", MachineApprovalSalt.Get(),
                "replacing an existing salt would silently invalidate every approval");
        }

        [Test]
        public void OverrideForTests_BypassesPrefsEntirely()
        {
            EditorPrefs.SetString(MachineApprovalSalt.EditorPrefsKey, "real-salt");
            MachineApprovalSalt.ResetCacheForTests();
            MachineApprovalSalt.OverrideForTests = delegate { return "override-salt"; };

            Assert.AreEqual("override-salt", MachineApprovalSalt.Get());
        }
    }
}

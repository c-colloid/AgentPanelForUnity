using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Prefab-stage guard tests (design section 1.2/8.2 B2). The
    /// stage-open branch requires actually opening prefab mode, which is
    /// heavier/flakier than this stream's scope warrants -- these cover
    /// the "no stage open" default (every write tool's common case) and
    /// the null-target short-circuit.
    /// </summary>
    [TestFixture]
    public class UapPrefabStageGuardTests
    {
        [SetUp]
        public void SetUp()
        {
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage(), "test assumes no prefab stage is open");
        }

        [Test]
        public void CheckScene_NoStageOpen_ReturnsTrue()
        {
            string error;
            bool ok = UapPrefabStageGuard.CheckScene(EditorSceneManager.GetActiveScene(), out error);
            Assert.IsTrue(ok);
            Assert.IsNull(error);
        }

        [Test]
        public void Check_NullTarget_ReturnsTrue()
        {
            string error;
            bool ok = UapPrefabStageGuard.Check(null, out error);
            Assert.IsTrue(ok);
            Assert.IsNull(error);
        }

        [Test]
        public void Check_RealGameObject_NoStageOpen_ReturnsTrue()
        {
            var go = new GameObject("UapPrefabGuardTestObject");
            try
            {
                string error;
                bool ok = UapPrefabStageGuard.Check(go, out error);
                Assert.IsTrue(ok);
                Assert.IsNull(error);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}

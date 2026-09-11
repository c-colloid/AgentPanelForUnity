using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Capability-matrix + registry wiring tests (design section 7.2:
    /// "when uLoop is present, do NOT register UapOps tools it already
    /// covers"). The 5a core set declares zero real overlap, so this
    /// exercises the MECHANISM with a fake matrix entry, per the stream's
    /// task line.
    /// </summary>
    [TestFixture]
    public class UloopCapabilityMatrixTests
    {
        [Test]
        public void IsCoveredByUloop_UnknownName_ReturnsFalse()
        {
            Assert.IsFalse(UloopCapabilityMatrix.IsCoveredByUloop("uap_ping"));
        }

        [Test]
        public void IsCoveredByUloop_NullName_ReturnsFalse()
        {
            Assert.IsFalse(UloopCapabilityMatrix.IsCoveredByUloop(null));
        }

        [Test]
        public void OverrideForTests_MarksCovered_UntilDisposed()
        {
            using (UloopCapabilityMatrix.OverrideForTests("uap_ping"))
            {
                Assert.IsTrue(UloopCapabilityMatrix.IsCoveredByUloop("uap_ping"));
            }
            Assert.IsFalse(UloopCapabilityMatrix.IsCoveredByUloop("uap_ping"));
        }

        [Test]
        public void CreateDefault_UloopNotDetected_RegistersEveryTool_EvenIfMatrixCoversOne()
        {
            using (UloopCapabilityMatrix.OverrideForTests("uap_ping"))
            {
                ToolRegistry registry = ToolRegistry.CreateDefault(false);
                Assert.IsNotNull(registry.Find("uap_ping"), "uloopDetected:false must never skip registration.");
            }
        }

        [Test]
        public void CreateDefault_UloopDetected_SkipsRegisteringTheCoveredFakeEntry()
        {
            using (UloopCapabilityMatrix.OverrideForTests("uap_ping"))
            {
                ToolRegistry registry = ToolRegistry.CreateDefault(true);
                Assert.IsNull(registry.Find("uap_ping"), "a uLoop-covered tool name must not be registered.");
                Assert.IsNotNull(registry.Find("uap_scene_create_object"),
                    "only the covered tool is skipped -- everything else still registers.");
            }
        }

        [Test]
        public void CreateDefault_UloopDetected_NoOverlapDeclared_RegistersEveryCoreTool()
        {
            ToolRegistry registry = ToolRegistry.CreateDefault(true);
            Assert.IsNotNull(registry.Find("uap_ping"));
            Assert.IsNotNull(registry.Find("uap_scene_create_object"));
            Assert.IsNotNull(registry.Find("uap_component_add"));
            Assert.IsNotNull(registry.Find("uap_property_set"));
            Assert.IsNotNull(registry.Find("uap_component_list"));
            Assert.IsNotNull(registry.Find("uap_object_inspect"));
            Assert.IsNotNull(registry.Find("uap_query_component_types"));
            Assert.IsNotNull(registry.Find("uap_asset_create"));
            Assert.IsNotNull(registry.Find("uap_asset_delete"));
            Assert.IsNotNull(registry.Find("uap_asset_find"));
            Assert.IsNotNull(registry.Find("uap_scripts_commit"));
        }
    }
}

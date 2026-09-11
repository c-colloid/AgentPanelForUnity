using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests.UapDupA
{
    /// <summary>Deliberately duplicate short name (see UapDupB) to exercise UapComponentTypeResolver's ambiguity path deterministically.</summary>
    internal sealed class UapDupNameComponent : MonoBehaviour
    {
    }
}

namespace Colloid.AgentPanel.Tests.UapDupB
{
    /// <summary>See Colloid.AgentPanel.Tests.UapDupA.UapDupNameComponent.</summary>
    internal sealed class UapDupNameComponent : MonoBehaviour
    {
    }
}

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Type-resolution tests for uap_component_add/uap_asset_create/uap_query_component_types' shared resolver.</summary>
    [TestFixture]
    public class UapComponentTypeResolverTests
    {
        [Test]
        public void ResolveComponentType_ExactShortName_ResolvesUnityBuiltin()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveComponentType("Transform", out error);
            Assert.IsNull(error);
            Assert.AreEqual(typeof(Transform), type);
        }

        [Test]
        public void ResolveComponentType_FullyQualifiedName_Resolves()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveComponentType("UnityEngine.BoxCollider", out error);
            Assert.IsNull(error);
            Assert.AreEqual(typeof(BoxCollider), type);
        }

        [Test]
        public void ResolveComponentType_UnknownName_ReturnsError()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveComponentType("ThisComponentDoesNotExist12345", out error);
            Assert.IsNull(type);
            StringAssert.Contains("Unknown", error);
        }

        [Test]
        public void ResolveComponentType_NullOrEmpty_ReturnsError()
        {
            string error;
            Assert.IsNull(UapComponentTypeResolver.ResolveComponentType(null, out error));
            Assert.IsNotNull(error);
            Assert.IsNull(UapComponentTypeResolver.ResolveComponentType(string.Empty, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void ResolveComponentType_AmbiguousShortName_ReturnsErrorListingBothFullNames()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveComponentType("UapDupNameComponent", out error);
            Assert.IsNull(type);
            StringAssert.Contains("Ambiguous", error);
            StringAssert.Contains("Colloid.AgentPanel.Tests.UapDupA.UapDupNameComponent", error);
            StringAssert.Contains("Colloid.AgentPanel.Tests.UapDupB.UapDupNameComponent", error);
        }

        [Test]
        public void ResolveComponentType_AmbiguousName_ResolvedByFullyQualifiedName()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveComponentType(
                "Colloid.AgentPanel.Tests.UapDupA.UapDupNameComponent", out error);
            Assert.IsNull(error);
            Assert.AreEqual(typeof(Colloid.AgentPanel.Tests.UapDupA.UapDupNameComponent), type);
        }

        [Test]
        public void ResolveScriptableObjectType_UnknownName_ReturnsError()
        {
            string error;
            var type = UapComponentTypeResolver.ResolveScriptableObjectType("NoSuchScriptableObject12345", out error);
            Assert.IsNull(type);
            StringAssert.Contains("ScriptableObject", error);
        }
    }
}

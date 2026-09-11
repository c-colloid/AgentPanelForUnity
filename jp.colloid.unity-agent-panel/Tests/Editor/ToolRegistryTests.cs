using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Minimal stub tool for registry tests -- module and name are settable per test case.</summary>
    internal sealed class StubUapTool : IUapTool
    {
        public string Name { get; set; } = "stub_tool";
        public string Description { get; set; } = "A stub tool.";
        public string Module { get; set; } = "core";
        public bool Undoable { get; set; }
        public bool ReadOnly { get; set; }
        public JsonNode InputSchema
        {
            get { return JsonNode.NewObject().Set("type", "object"); }
        }

        public int ExecuteCallCount;
        public JsonNode LastInput;
        public Func<JsonNode, JsonNode> ExecuteImpl;

        public JsonNode Execute(JsonNode input)
        {
            ExecuteCallCount++;
            LastInput = input;
            return ExecuteImpl != null
                ? ExecuteImpl(input)
                : JsonNode.NewArray().Add(JsonNode.NewObject().Set("type", "text").Set("text", "ok"));
        }
    }

    /// <summary>
    /// ToolRegistry unit tests (design section 7.1: module-filtered
    /// tools/list). No HTTP/server involved -- pure registry behavior.
    /// </summary>
    [TestFixture]
    public class ToolRegistryTests
    {
        [Test]
        public void Register_ThenFind_ReturnsTheSameInstance()
        {
            var registry = new ToolRegistry();
            var tool = new StubUapTool { Name = "a" };
            registry.Register(tool);
            Assert.AreSame(tool, registry.Find("a"));
        }

        [Test]
        public void Find_UnknownName_ReturnsNull()
        {
            var registry = new ToolRegistry();
            Assert.IsNull(registry.Find("nope"));
        }

        [Test]
        public void Find_NullOrEmptyName_ReturnsNull()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "a" });
            Assert.IsNull(registry.Find(null));
            Assert.IsNull(registry.Find(string.Empty));
        }

        [Test]
        public void Register_NullTool_Throws()
        {
            var registry = new ToolRegistry();
            Assert.Throws<ArgumentNullException>(delegate { registry.Register(null); });
        }

        [Test]
        public void Register_EmptyName_Throws()
        {
            var registry = new ToolRegistry();
            Assert.Throws<ArgumentException>(delegate
            {
                registry.Register(new StubUapTool { Name = string.Empty });
            });
        }

        [Test]
        public void Register_DuplicateName_Throws()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "dup" });
            Assert.Throws<InvalidOperationException>(delegate
            {
                registry.Register(new StubUapTool { Name = "dup" });
            });
        }

        [Test]
        public void ListEnabled_OnlyReturnsToolsWhoseModuleIsEnabled()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "core_a", Module = "core" });
            registry.Register(new StubUapTool { Name = "prefab_a", Module = "prefab" });
            registry.Register(new StubUapTool { Name = "core_b", Module = "core" });

            List<IUapTool> enabled = registry.ListEnabled(new[] { "core" });

            Assert.AreEqual(2, enabled.Count);
            Assert.AreEqual("core_a", enabled[0].Name);
            Assert.AreEqual("core_b", enabled[1].Name);
        }

        [Test]
        public void ListEnabled_PreservesRegistrationOrder_AcrossModules()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "prefab_a", Module = "prefab" });
            registry.Register(new StubUapTool { Name = "core_a", Module = "core" });
            registry.Register(new StubUapTool { Name = "prefab_b", Module = "prefab" });

            List<IUapTool> enabled = registry.ListEnabled(new[] { "core", "prefab" });

            CollectionAssert.AreEqual(
                new[] { "prefab_a", "core_a", "prefab_b" },
                enabled.ConvertAll(t => t.Name));
        }

        [Test]
        public void ListEnabled_NullModules_ReturnsEmpty()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "a", Module = "core" });
            Assert.AreEqual(0, registry.ListEnabled(null).Count);
        }

        [Test]
        public void ListEnabled_EmptyModules_ReturnsEmpty()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "a", Module = "core" });
            Assert.AreEqual(0, registry.ListEnabled(new string[0]).Count);
        }

        [Test]
        public void CreateDefault_RegistersUapPing_InTheCoreModule()
        {
            ToolRegistry registry = ToolRegistry.CreateDefault();
            IUapTool ping = registry.Find("uap_ping");
            Assert.IsNotNull(ping);
            Assert.AreEqual("core", ping.Module);
            Assert.IsFalse(ping.Undoable);
        }

        [Test]
        public void Count_ReflectsRegisteredTools_RegardlessOfModuleEnablement()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "a", Module = "core" });
            registry.Register(new StubUapTool { Name = "b", Module = "prefab" });
            Assert.AreEqual(2, registry.Count);
        }

        /// <summary>Test/production seam for provider discovery (2026-09-11 core/pro split, design note "seam 1") -- an explicit provider list, no TypeCache involved.</summary>
        private sealed class StubToolProvider : IUapToolProvider
        {
            public IEnumerable<IUapTool> Tools = new IUapTool[0];
            public IEnumerable<IUapTool> CreateTools() { return Tools; }
        }

        [Test]
        public void RegisterProviderTools_RegistersEveryToolFromEveryProvider()
        {
            var registry = new ToolRegistry();
            var providerA = new StubToolProvider { Tools = new IUapTool[] { new StubUapTool { Name = "pro_a", Module = "prefab" } } };
            var providerB = new StubToolProvider { Tools = new IUapTool[] { new StubUapTool { Name = "pro_b", Module = "anim" } } };

            ToolRegistry.RegisterProviderTools(registry, new IUapToolProvider[] { providerA, providerB }, false);

            Assert.IsNotNull(registry.Find("pro_a"));
            Assert.IsNotNull(registry.Find("pro_b"));
            Assert.AreEqual(2, registry.Count);
        }

        [Test]
        public void RegisterProviderTools_DuplicateNameAgainstAnExistingTool_ThrowsLikeAnyOtherCollision()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "dup", Module = "core" });
            var provider = new StubToolProvider { Tools = new IUapTool[] { new StubUapTool { Name = "dup", Module = "prefab" } } };

            Assert.Throws<InvalidOperationException>(delegate
            {
                ToolRegistry.RegisterProviderTools(registry, new IUapToolProvider[] { provider }, false);
            });
        }

        [Test]
        public void RegisterProviderTools_ThrowingProvider_IsSkipped_OtherProvidersStillRegister()
        {
            var registry = new ToolRegistry();
            var throwing = new ThrowingToolProvider();
            var ok = new StubToolProvider { Tools = new IUapTool[] { new StubUapTool { Name = "pro_ok", Module = "anim" } } };
            // The skip is logged as an error on purpose (a broken add-on
            // must be visible in the Console); the runner treats an
            // unexpected error log as a failure, so declare it.
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("ThrowingToolProvider' threw while creating tools"));

            ToolRegistry.RegisterProviderTools(registry, new IUapToolProvider[] { throwing, ok }, false);

            Assert.IsNotNull(registry.Find("pro_ok"));
            Assert.AreEqual(1, registry.Count);
        }

        private sealed class ThrowingToolProvider : IUapToolProvider
        {
            public IEnumerable<IUapTool> CreateTools() { throw new InvalidOperationException("boom"); }
        }

        [Test]
        public void HasToolsInModule_TrueOnlyWhenAToolOfThatModuleIsRegistered()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubUapTool { Name = "a", Module = "core" });
            Assert.IsTrue(registry.HasToolsInModule("core"));
            Assert.IsFalse(registry.HasToolsInModule("prefab"));
            Assert.IsFalse(registry.HasToolsInModule(null));
        }
    }
}

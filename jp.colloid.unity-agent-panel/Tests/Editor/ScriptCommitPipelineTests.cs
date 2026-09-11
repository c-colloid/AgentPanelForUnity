using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>Pure state-machine tests for uap_scripts_commit's pipeline (design section 7.4/8.1 P1), with no UnityEditor.Compilation dependency at all.</summary>
    [TestFixture]
    public class ScriptCommitPipelineTests
    {
        [Test]
        public void InitialPhase_IsIdle()
        {
            var pipeline = new ScriptCommitPipeline();
            Assert.AreEqual(ScriptCommitPhase.Idle, pipeline.Phase);
            Assert.IsFalse(pipeline.ShouldMoveFilesToAssets);
        }

        [Test]
        public void MarkNothingStaged_TransitionsToNothingStaged()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkNothingStaged();
            Assert.AreEqual(ScriptCommitPhase.NothingStaged, pipeline.Phase);
            Assert.IsFalse(pipeline.ShouldMoveFilesToAssets);
        }

        [Test]
        public void CompleteCompilation_NoMessages_Succeeds()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            pipeline.CompleteCompilation(null);
            Assert.AreEqual(ScriptCommitPhase.Succeeded, pipeline.Phase);
            Assert.IsTrue(pipeline.ShouldMoveFilesToAssets);
            Assert.AreEqual(0, pipeline.Errors.Count);
        }

        [Test]
        public void CompleteCompilation_OnlyWarnings_StillSucceeds()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            pipeline.CompleteCompilation(new List<ScriptCompileMessage>
            {
                new ScriptCompileMessage { File = "Foo.cs", Line = 3, Column = 1, Message = "unused var", IsError = false }
            });
            Assert.AreEqual(ScriptCommitPhase.Succeeded, pipeline.Phase);
            Assert.IsTrue(pipeline.ShouldMoveFilesToAssets);
            Assert.AreEqual(0, pipeline.Errors.Count);
        }

        [Test]
        public void CompleteCompilation_WithErrors_Fails_AndCollectsOnlyErrors()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            pipeline.CompleteCompilation(new List<ScriptCompileMessage>
            {
                new ScriptCompileMessage { File = "Foo.cs", Line = 5, Column = 2, Message = "CS1002: ; expected", IsError = true },
                new ScriptCompileMessage { File = "Foo.cs", Line = 6, Column = 1, Message = "unused var", IsError = false }
            });
            Assert.AreEqual(ScriptCommitPhase.Failed, pipeline.Phase);
            Assert.IsFalse(pipeline.ShouldMoveFilesToAssets);
            Assert.AreEqual(1, pipeline.Errors.Count);
            Assert.AreEqual("CS1002: ; expected", pipeline.Errors[0].Message);
        }

        [Test]
        public void CompleteCompilation_BeforeMarkCompiling_Throws()
        {
            var pipeline = new ScriptCommitPipeline();
            Assert.Throws<InvalidOperationException>(delegate { pipeline.CompleteCompilation(null); });
        }

        [Test]
        public void CompleteCompilation_CalledTwice_Throws()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            pipeline.CompleteCompilation(null);
            Assert.Throws<InvalidOperationException>(delegate { pipeline.CompleteCompilation(null); });
        }

        [Test]
        public void MarkCompiling_CalledTwice_Throws()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            Assert.Throws<InvalidOperationException>(delegate { pipeline.MarkCompiling(); });
        }

        [Test]
        public void MarkNothingStaged_AfterMarkCompiling_Throws()
        {
            var pipeline = new ScriptCommitPipeline();
            pipeline.MarkCompiling();
            Assert.Throws<InvalidOperationException>(delegate { pipeline.MarkNothingStaged(); });
        }

        [Test]
        public void ScriptCompileMessage_ToString_IncludesFileLineAndMessage()
        {
            var message = new ScriptCompileMessage { File = "Foo.cs", Line = 7, Column = 3, Message = "boom", IsError = true };
            string text = message.ToString();
            StringAssert.Contains("Foo.cs", text);
            StringAssert.Contains("7", text);
            StringAssert.Contains("boom", text);
            StringAssert.Contains("error", text);
        }
    }
}

using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure rules behind PanelSettings.uapToolListMode (design note
    /// docs/design-notes/2026-09-22-uloop-phase1.md section 2.8 B) -- no
    /// server/registry involved, so these run identically here and in the
    /// license-free smoke gate (UapToolListModePolicySmoke).
    /// </summary>
    [TestFixture]
    public class UapToolListModePolicyTests
    {
        [Test]
        public void ResolveCompact_Full_IsAlwaysFalse()
        {
            Assert.IsFalse(UapToolListModePolicy.ResolveCompact(UapToolListMode.Full, true));
            Assert.IsFalse(UapToolListModePolicy.ResolveCompact(UapToolListMode.Full, false));
        }

        [Test]
        public void ResolveCompact_Compact_IsAlwaysTrue()
        {
            Assert.IsTrue(UapToolListModePolicy.ResolveCompact(UapToolListMode.Compact, true));
            Assert.IsTrue(UapToolListModePolicy.ResolveCompact(UapToolListMode.Compact, false));
        }

        [Test]
        public void ResolveCompact_Auto_FollowsTheBackend_ClaudeCodeStaysFull()
        {
            // isAcpBackend:false is Claude Code (AgentHub's isAcp) -- the
            // only backend with client-side ToolSearch, so Auto stays Full.
            Assert.IsFalse(UapToolListModePolicy.ResolveCompact(UapToolListMode.Auto, false));
            Assert.IsTrue(UapToolListModePolicy.ResolveCompact(UapToolListMode.Auto, true));
        }

        [Test]
        public void EndpointPath_MatchesCompactFlag()
        {
            Assert.AreEqual(UapToolListModePolicy.McpPath, UapToolListModePolicy.EndpointPath(false));
            Assert.AreEqual(UapToolListModePolicy.McpCompactPath, UapToolListModePolicy.EndpointPath(true));
        }

        /// <summary>
        /// The literal names UapToolListModePolicy duplicates on purpose
        /// (see its own doc comment on why it does not reference
        /// UapToolGateway's constants directly) must still equal the real
        /// ones -- this is the "kept in sync BY CONSTRUCTION" promise that
        /// comment makes: a rename of any of the three on either side
        /// breaks this assertion, not just that comment's promise.
        /// </summary>
        [Test]
        public void SteeringSentence_TheThreeDuplicatedLiterals_MatchUapToolGateways()
        {
            string sentence = UapToolListModePolicy.SteeringSentence();
            StringAssert.Contains(UapToolGateway.DirectlyListedModule, sentence);
            StringAssert.Contains(UapToolGateway.ToolsToolName, sentence);
            StringAssert.Contains(UapToolGateway.CallToolName, sentence);
            // 2026-10-02: the lean-schema clause names uap_help too.
            StringAssert.Contains(UapHelpTool.ToolName, sentence);
        }

        [Test]
        public void SteeringSentence_SaysListedSchemasAreLean_AndWhereTheFullOneIs()
        {
            string sentence = UapToolListModePolicy.SteeringSentence();
            StringAssert.Contains("listed schemas carry one-line field descriptions", sentence);
            StringAssert.Contains(UapHelpTool.ToolName + " names:[...] returns the full schema", sentence);
        }
    }
}

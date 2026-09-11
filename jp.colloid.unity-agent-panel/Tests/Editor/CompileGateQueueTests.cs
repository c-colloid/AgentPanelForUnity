using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// INFRA-2c: round-trip and tolerance table for the reload-surviving
    /// send queue's (de)serialization, extracted from CompileGate into the
    /// pure CompileGateQueue so it finally has direct coverage -- the
    /// queue crosses a domain reload as a SessionState JSON string, and a
    /// shape bug here silently drops user text.
    /// </summary>
    [TestFixture]
    public class CompileGateQueueTests
    {
        [Test]
        public void RoundTrip_WireDisplayAndAttachments_Survive()
        {
            var entries = new List<CompileGateQueue.Entry>
            {
                new CompileGateQueue.Entry
                {
                    WireText = "wire-1\nwith newline",
                    DisplayText = "display-1",
                    Attachments = new List<ContextAttachment>
                    {
                        new ContextAttachment("Console errors", "payload text")
                    }
                },
                new CompileGateQueue.Entry { WireText = "wire-2" }
            };

            string json = CompileGateQueue.Serialize(entries);
            string discardError;
            List<CompileGateQueue.Entry> back = CompileGateQueue.Deserialize(json, out discardError);

            Assert.IsNull(discardError);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual("wire-1\nwith newline", back[0].WireText);
            Assert.AreEqual("display-1", back[0].DisplayText);
            Assert.AreEqual(1, back[0].Attachments.Count);
            Assert.AreEqual("Console errors", back[0].Attachments[0].title);
            Assert.AreEqual("payload text", back[0].Attachments[0].payload);
            Assert.AreEqual("wire-2", back[1].WireText);
            Assert.IsNull(back[1].Attachments);
        }

        [Test]
        public void Serialize_EmptyOrNull_IsTheEmptyStoreValue()
        {
            Assert.AreEqual(string.Empty,
                CompileGateQueue.Serialize(new List<CompileGateQueue.Entry>()));
            Assert.AreEqual(string.Empty, CompileGateQueue.Serialize(null));
        }

        [Test]
        public void Deserialize_EmptyString_IsAnEmptyQueue_NoError()
        {
            string discardError;
            Assert.AreEqual(0, CompileGateQueue.Deserialize(string.Empty, out discardError).Count);
            Assert.IsNull(discardError);
            Assert.AreEqual(0, CompileGateQueue.Deserialize(null, out discardError).Count);
            Assert.IsNull(discardError);
        }

        [Test]
        public void Deserialize_LegacyPlainStringEntries_BecomeWireOnlySends()
        {
            string discardError;
            List<CompileGateQueue.Entry> back = CompileGateQueue.Deserialize(
                "[\"legacy text\", \"\", \"second\"]", out discardError);

            Assert.IsNull(discardError);
            Assert.AreEqual(2, back.Count, "empty legacy strings are dropped");
            Assert.AreEqual("legacy text", back[0].WireText);
            Assert.IsNull(back[0].DisplayText);
            Assert.AreEqual("second", back[1].WireText);
        }

        [Test]
        public void Deserialize_InvalidItems_AreSkipped_NotFatal()
        {
            string discardError;
            List<CompileGateQueue.Entry> back = CompileGateQueue.Deserialize(
                "[{\"wire\":\"keep\"}, {\"display\":\"no wire\"}, 42, [1,2]]",
                out discardError);

            Assert.IsNull(discardError);
            Assert.AreEqual(1, back.Count);
            Assert.AreEqual("keep", back[0].WireText);
        }

        [Test]
        public void Deserialize_MalformedJsonOrNonArray_ReportsDiscard()
        {
            string discardError;
            Assert.AreEqual(0, CompileGateQueue.Deserialize("{not json", out discardError).Count);
            Assert.IsNotNull(discardError, "the caller logs and clears the store off this signal");

            Assert.AreEqual(0, CompileGateQueue.Deserialize("{\"a\":1}", out discardError).Count);
            Assert.IsNotNull(discardError, "a non-array root is a discard, not a queue");
        }

        [Test]
        public void Deserialize_AttachmentArray_KeepsOnlyObjectItems()
        {
            string discardError;
            List<CompileGateQueue.Entry> back = CompileGateQueue.Deserialize(
                "[{\"wire\":\"w\",\"attachments\":[{\"title\":\"t\",\"payload\":\"p\"},\"junk\",7]}]",
                out discardError);

            Assert.IsNull(discardError);
            Assert.AreEqual(1, back[0].Attachments.Count);
            Assert.AreEqual("t", back[0].Attachments[0].title);
        }
    }
}

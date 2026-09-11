using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Pure marker model/store/renderer logic (docs/design-notes/2026-09-07-
    /// scene-markers-image-attachments-error-chip.md section 1.3 and the
    /// corrections in 1.5): label normalization, colour/kind parsing, JSON
    /// round trip, the 32-marker cap that evicts agent markers before user
    /// pins, origin-scoped clear, TTL expiry, and label de-overlap.
    /// </summary>
    [TestFixture]
    public class SceneMarkerTests
    {
        private static SceneMarker M(int id, SceneMarkerOrigin origin = SceneMarkerOrigin.Agent)
        {
            return new SceneMarker { Id = id, Origin = origin, Label = "m" + id };
        }

        [Test]
        public void NormalizeLabel_TrimsCollapsesNewlines_AndCaps()
        {
            Assert.AreEqual(string.Empty, SceneMarker.NormalizeLabel(null));
            Assert.AreEqual("a b", SceneMarker.NormalizeLabel("  a\nb \r\n"));
            string longLabel = new string('x', SceneMarker.MaxLabelChars + 10);
            string normalized = SceneMarker.NormalizeLabel(longLabel);
            Assert.AreEqual(SceneMarker.MaxLabelChars, normalized.Length);
            StringAssert.EndsWith("...", normalized);
        }

        [Test]
        public void DisplayLabel_PrefixesNumber_AndPForUserPins()
        {
            Assert.AreEqual("3  Spawn", new SceneMarker { Id = 3, Label = "Spawn" }.DisplayLabel);
            Assert.AreEqual("P2  pin", new SceneMarker { Id = 2, Label = "pin", Origin = SceneMarkerOrigin.User }.DisplayLabel);
            Assert.AreEqual("4", new SceneMarker { Id = 4 }.DisplayLabel);
        }

        [TestCase("red", true)]
        [TestCase("Cyan", true)]
        [TestCase("#33FF66", true)]
        [TestCase("#33FF66AA", true)]
        [TestCase("purple-ish", false)]
        [TestCase("", false)]
        public void TryParseColor_NamedAndHex(string text, bool expected)
        {
            Color color;
            Assert.AreEqual(expected, SceneMarker.TryParseColor(text, out color));
        }

        [TestCase("point", true)]
        [TestCase("ARROW", true)]
        [TestCase("box", true)]
        [TestCase("sphere", false)]
        public void TryParseKind(string text, bool expected)
        {
            SceneMarkerKind kind;
            Assert.AreEqual(expected, SceneMarker.TryParseKind(text, out kind));
        }

        [Test]
        public void Json_RoundTrips_EveryField()
        {
            var marker = new SceneMarker
            {
                Id = 7, Kind = SceneMarkerKind.Arrow, Origin = SceneMarkerOrigin.User,
                Position = new Vector3(1.5f, -2f, 3.25f), To = new Vector3(0f, 1f, 0f),
                Target = "Env/Ground", Label = "here", Color = new Color(0.2f, 0.4f, 0.6f, 1f),
                Size = 0.75f, TtlSeconds = 12f, CreatedAt = 123.5
            };

            SceneMarker back = SceneMarker.FromJson(marker.ToJson());

            Assert.AreEqual(7, back.Id);
            Assert.AreEqual(SceneMarkerKind.Arrow, back.Kind);
            Assert.AreEqual(SceneMarkerOrigin.User, back.Origin);
            Assert.AreEqual(marker.Position, back.Position);
            Assert.AreEqual(marker.To, back.To);
            Assert.AreEqual("Env/Ground", back.Target);
            Assert.AreEqual("here", back.Label);
            Assert.AreEqual(0.75f, back.Size);
            Assert.AreEqual(12f, back.TtlSeconds);
            Assert.AreEqual(123.5, back.CreatedAt);
            Assert.AreEqual(ColorUtility.ToHtmlStringRGBA(marker.Color), ColorUtility.ToHtmlStringRGBA(back.Color));
        }

        [Test]
        public void NextPinNumber_ReusesTheLowestFreeNumber()
        {
            var list = new List<SceneMarker>
            {
                new SceneMarker { Id = 1, Origin = SceneMarkerOrigin.User, PinNumber = 1 },
                new SceneMarker { Id = 2, Origin = SceneMarkerOrigin.Agent },
                new SceneMarker { Id = 3, Origin = SceneMarkerOrigin.User, PinNumber = 3 }
            };
            Assert.AreEqual(2, SceneMarkerStore.NextPinNumber(list), "P2 was removed, so it is free again");
            list.RemoveAt(0);
            Assert.AreEqual(1, SceneMarkerStore.NextPinNumber(list));
            Assert.AreEqual(1, SceneMarkerStore.NextPinNumber(new List<SceneMarker>()));
        }

        [Test]
        public void Json_RoundTrips_PinNumberAndNote()
        {
            var pin = new SceneMarker { Id = 9, Origin = SceneMarkerOrigin.User, PinNumber = 2, Note = "hit object: Ground" };
            SceneMarker back = SceneMarker.FromJson(pin.ToJson());
            Assert.AreEqual(2, back.PinNumber);
            Assert.AreEqual("hit object: Ground", back.Note);
            Assert.AreEqual("P2", back.DisplayLabel);
        }

        [Test]
        public void Store_Serialize_Deserialize_RoundTrip_AndInvalidPayloadIsEmpty()
        {
            var list = new List<SceneMarker> { M(1), M(2, SceneMarkerOrigin.User) };
            int nextId;

            List<SceneMarker> back = SceneMarkerStore.Deserialize(SceneMarkerStore.Serialize(list, 3), out nextId);

            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(3, nextId);
            Assert.AreEqual(SceneMarkerOrigin.User, back[1].Origin);
            Assert.AreEqual(0, SceneMarkerStore.Deserialize("not json", out nextId).Count);
            Assert.AreEqual(1, nextId);
            Assert.AreEqual(0, SceneMarkerStore.Deserialize(null, out nextId).Count);
        }

        [Test]
        public void AddWithCap_EvictsOldestAgentMarker_NeverAUserPin()
        {
            var list = new List<SceneMarker> { M(1, SceneMarkerOrigin.User), M(2), M(3) };

            SceneMarker evicted = SceneMarkerStore.AddWithCap(list, M(4), 3);

            Assert.AreEqual(2, evicted.Id, "the oldest AGENT marker goes, not the older user pin");
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(4, list[2].Id);
        }

        [Test]
        public void AddWithCap_OnlyPins_EvictsOldestPinSoAddNeverFails()
        {
            var list = new List<SceneMarker> { M(1, SceneMarkerOrigin.User), M(2, SceneMarkerOrigin.User) };
            SceneMarker evicted = SceneMarkerStore.AddWithCap(list, M(3), 2);
            Assert.AreEqual(1, evicted.Id);
            Assert.AreEqual(2, list.Count);
        }

        [Test]
        public void AddWithCap_UnderCap_EvictsNothing()
        {
            var list = new List<SceneMarker> { M(1) };
            Assert.IsNull(SceneMarkerStore.AddWithCap(list, M(2), 32));
            Assert.AreEqual(2, list.Count);
        }

        [Test]
        public void ClearByOrigin_AgentOnly_KeepsUserPins()
        {
            var list = new List<SceneMarker> { M(1), M(2, SceneMarkerOrigin.User), M(3) };
            Assert.AreEqual(2, SceneMarkerStore.ClearByOrigin(list, false));
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(SceneMarkerOrigin.User, list[0].Origin);
            Assert.AreEqual(1, SceneMarkerStore.ClearByOrigin(list, true));
            Assert.AreEqual(0, list.Count);
        }

        [Test]
        public void RemoveExpired_UsesTtlAgainstCreatedAt()
        {
            var list = new List<SceneMarker>
            {
                new SceneMarker { Id = 1, TtlSeconds = 5f, CreatedAt = 100 },
                new SceneMarker { Id = 2, TtlSeconds = 0f, CreatedAt = 100 },
                new SceneMarker { Id = 3, TtlSeconds = 50f, CreatedAt = 100 }
            };
            Assert.AreEqual(1, SceneMarkerStore.RemoveExpired(list, 106));
            CollectionAssert.AreEqual(new[] { 2, 3 }, list.ConvertAll(m => m.Id));
        }

        [Test]
        public void ComputeLabelOffsets_StacksCollidingLabels_LeavesOthersAlone()
        {
            var anchors = new List<Vector2>
            {
                new Vector2(100, 100),
                new Vector2(110, 104),   // collides with 0
                new Vector2(105, 101),   // collides with 0 and (after its offset) 1
                new Vector2(600, 100)    // far away
            };

            float[] offsets = SceneMarkerRenderer.ComputeLabelOffsets(anchors, 140f, 18f);

            Assert.AreEqual(0f, offsets[0]);
            Assert.AreEqual(118f - 104f, offsets[1], 0.001f, "second label sits 18px below the first");
            Assert.AreEqual(136f - 101f, offsets[2], 0.001f, "third label sits 18px below the second");
            Assert.AreEqual(0f, offsets[3]);
        }

        [Test]
        public void ComputeLabelOffsets_NullOrEmpty_IsEmpty()
        {
            Assert.AreEqual(0, SceneMarkerRenderer.ComputeLabelOffsets(null, 1f, 1f).Length);
            Assert.AreEqual(0, SceneMarkerRenderer.ComputeLabelOffsets(new List<Vector2>(), 1f, 1f).Length);
        }
    }
}

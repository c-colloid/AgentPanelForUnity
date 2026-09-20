using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The sketch strokes' pure pieces (design note 2026-09-17-scene-
    /// sketch-strokes.md section 8): polyline geometry, Douglas-Peucker
    /// and decimation, the plane-vs-mesh section behind the depth contour,
    /// the store's JSON round trip / cap / numbering, and the raw-sample
    /// to stored-stroke build plus the chip payload. English is pinned
    /// because the payload expectations are English strings.
    /// </summary>
    [TestFixture]
    public class SceneStrokeTests
    {
        [SetUp]
        public void SetUp()
        {
            UI.L10n.OverrideForTests(Model.PanelLanguage.English);
            SceneStrokeStore.ResetForTests();
            SceneStrokeSketch.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UI.L10n.OverrideForTests(null);
            SceneStrokeSketch.ResetForTests();
            SceneStrokeStore.ResetForTests();
        }

        private static List<Vector3> Line(params float[] xs)
        {
            var points = new List<Vector3>();
            for (int i = 0; i < xs.Length; i++)
            {
                points.Add(new Vector3(xs[i], 0f, 0f));
            }
            return points;
        }

        // -- Geometry ---------------------------------------------------------------------

        [Test]
        public void Length_SumsSegments_AndIsZeroForFewerThanTwoPoints()
        {
            Assert.AreEqual(0f, SceneStrokeGeometry.Length(null));
            Assert.AreEqual(0f, SceneStrokeGeometry.Length(Line(1f)));
            Assert.AreEqual(3f, SceneStrokeGeometry.Length(Line(0f, 1f, 3f)), 1e-5f);
        }

        [Test]
        public void Bounds_EncapsulatesEveryPoint()
        {
            var points = new List<Vector3> { new Vector3(1f, 2f, 3f), new Vector3(-1f, 5f, 0f) };
            Bounds bounds = SceneStrokeGeometry.Bounds(points);
            Assert.AreEqual(new Vector3(-1f, 2f, 0f), bounds.min);
            Assert.AreEqual(new Vector3(1f, 5f, 3f), bounds.max);
            Assert.AreEqual(Vector3.zero, SceneStrokeGeometry.Bounds(new List<Vector3>()).size);
        }

        [Test]
        public void IsClosed_NeedsThreePoints_AndEndsWithinTwoPercentOfLength()
        {
            var square = new List<Vector3>
            {
                Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0.05f, 0f)
            };
            Assert.IsTrue(SceneStrokeGeometry.IsClosed(square), "3.95 long, ends 0.05 apart (< 2% = 0.079)");
            square[square.Count - 1] = new Vector3(0f, 0.2f, 0f);
            Assert.IsFalse(SceneStrokeGeometry.IsClosed(square));
            Assert.IsFalse(SceneStrokeGeometry.IsClosed(new List<Vector3> { Vector3.zero, Vector3.zero }), "two points never close");
        }

        [Test]
        public void SimplifyIndices_KeepsEndpoints_DropsCollinear_KeepsCorners()
        {
            var points = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0.001f, 0f), new Vector3(2f, 0f, 0f),
                new Vector3(2f, 1f, 0f), new Vector3(2f, 2f, 0f)
            };
            List<int> keep = SceneStrokeGeometry.SimplifyIndices(points, 0.01f);
            CollectionAssert.AreEqual(new[] { 0, 2, 4 }, keep, "corner at index 2 survives, near-collinear 1 and 3 go");
            Assert.AreEqual(3, SceneStrokeGeometry.Simplify(points, 0.01f).Count);
        }

        [Test]
        public void SimplifyIndices_ZeroTolerance_OrTwoPoints_KeepsEverything()
        {
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, SceneStrokeGeometry.SimplifyIndices(Line(0f, 1f, 2f), 0f));
            CollectionAssert.AreEqual(new[] { 0, 1 }, SceneStrokeGeometry.SimplifyIndices(Line(0f, 1f), 5f));
            Assert.AreEqual(0, SceneStrokeGeometry.SimplifyIndices(new List<Vector3>(), 1f).Count);
        }

        [Test]
        public void DecimateIndices_SpreadsEvenly_AndKeepsBothEnds()
        {
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, SceneStrokeGeometry.DecimateIndices(3, 10), "under the cap: every index");
            List<int> five = SceneStrokeGeometry.DecimateIndices(101, 5);
            CollectionAssert.AreEqual(new[] { 0, 25, 50, 75, 100 }, five);
            CollectionAssert.AreEqual(new[] { 0 }, SceneStrokeGeometry.DecimateIndices(9, 1));
            Assert.AreEqual(0, SceneStrokeGeometry.DecimateIndices(0, 4).Count);
            Assert.AreEqual(0, SceneStrokeGeometry.DecimateIndices(4, 0).Count);
        }

        [Test]
        public void SectionTriangles_CutsAStraddlingTriangle_OnceAndSkipsOthers()
        {
            // One triangle straddling y = 0 (vertex 0 below, 1 and 2 above),
            // one entirely above, one lying in the plane.
            Vector3[] vertices =
            {
                new Vector3(0f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
                new Vector3(0f, 2f, 0f), new Vector3(1f, 3f, 0f), new Vector3(-1f, 3f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 2f)
            };
            int[] triangles = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            var segments = new List<Vector3>();
            int added = SceneStrokeGeometry.SectionTriangles(vertices, triangles, Matrix4x4.identity, new Plane(Vector3.up, 0f), segments);
            Assert.AreEqual(1, added);
            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(0f, segments[0].y, 1e-5f);
            Assert.AreEqual(0f, segments[1].y, 1e-5f);
            Assert.AreEqual(1f, Vector3.Distance(segments[0], segments[1]), 1e-4f, "the cut of the straddling triangle at y=0 is 1 wide");
        }

        [Test]
        public void SectionTriangles_AppliesTheMatrix_AndIgnoresBadIndices()
        {
            Vector3[] vertices = { new Vector3(0f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
            int[] triangles = { 0, 1, 2, 0, 1, 9 };
            var segments = new List<Vector3>();
            Matrix4x4 up = Matrix4x4.Translate(new Vector3(0f, 5f, 0f));
            int added = SceneStrokeGeometry.SectionTriangles(vertices, triangles, up, new Plane(Vector3.up, -5f), segments);
            Assert.AreEqual(1, added, "the out-of-range triangle is skipped, the moved one is cut at y=5");
            Assert.AreEqual(5f, segments[0].y, 1e-5f);
        }

        [Test]
        public void BoundsCrossesPlane_UsesTheProjectedExtent()
        {
            var bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(2f, 2f, 2f));
            Assert.IsTrue(SceneStrokeGeometry.BoundsCrossesPlane(bounds, new Plane(Vector3.up, -1.5f)), "plane y=1.5 is inside [1,3]");
            Assert.IsFalse(SceneStrokeGeometry.BoundsCrossesPlane(bounds, new Plane(Vector3.up, 0f)), "plane y=0 is below [1,3]");
        }

        // -- Store -----------------------------------------------------------------------

        private static SceneStroke Surface(int number, params Vector3[] points)
        {
            var stroke = new SceneStroke { Number = number, Mode = SceneStrokeMode.Surface };
            stroke.Points.AddRange(points);
            for (int i = 0; i < points.Length; i++)
            {
                stroke.Normals.Add(Vector3.up);
                stroke.HitIndices.Add(i == 0 ? 0 : -1);
            }
            stroke.HitPaths.Add("Env/Wall");
            return stroke;
        }

        [Test]
        public void Serialize_RoundTrips_PointsNormalsHitsPlaneAndFlags()
        {
            SceneStroke a = Surface(1, new Vector3(0f, 0f, 0f), new Vector3(1f, 2f, 3f));
            a.Id = 7;
            a.Closed = true;
            a.Note = "note";
            var b = new SceneStroke { Id = 8, Number = 2, Mode = SceneStrokeMode.Plane, PlaneOrigin = new Vector3(1f, 1f, 1f), PlaneNormal = Vector3.forward };
            b.Points.Add(Vector3.zero);
            b.Points.Add(Vector3.one);
            int nextId;
            List<SceneStroke> back = SceneStrokeStore.Deserialize(SceneStrokeStore.Serialize(new List<SceneStroke> { a, b }, 9), out nextId);
            Assert.AreEqual(9, nextId);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(7, back[0].Id);
            Assert.AreEqual(SceneStrokeMode.Surface, back[0].Mode);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), back[0].Points[1]);
            Assert.AreEqual(2, back[0].Normals.Count);
            CollectionAssert.AreEqual(new[] { 0, -1 }, back[0].HitIndices);
            Assert.AreEqual("Env/Wall", back[0].HitPathAt(0));
            Assert.AreEqual(string.Empty, back[0].HitPathAt(1));
            Assert.IsTrue(back[0].Closed);
            Assert.AreEqual("note", back[0].Note);
            Assert.AreEqual(SceneStrokeMode.Plane, back[1].Mode);
            Assert.AreEqual(Vector3.forward, back[1].PlaneNormal);
            Assert.AreEqual(new Vector3(1f, 1f, 1f), back[1].PlaneOrigin);
            Assert.AreEqual(0, SceneStrokeStore.Deserialize("not json", out nextId).Count);
            Assert.AreEqual(1, nextId);
            Assert.AreEqual(0, SceneStrokeStore.Deserialize(null, out nextId).Count);
        }

        [Test]
        public void AddWithCap_EvictsTheOldest_AndNumbersAreReused()
        {
            var list = new List<SceneStroke> { Surface(1, Vector3.zero, Vector3.one), Surface(2, Vector3.zero, Vector3.one) };
            SceneStroke evicted = SceneStrokeStore.AddWithCap(list, Surface(3, Vector3.zero, Vector3.one), 2);
            Assert.AreEqual(1, evicted.Number);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(1, SceneStrokeStore.NextNumber(list), "S1 was evicted, so 1 is free again");
            list.Add(Surface(1, Vector3.zero, Vector3.one));
            Assert.AreEqual(4, SceneStrokeStore.NextNumber(list));
        }

        [Test]
        public void Store_Add_AssignsIdAndNumber_AndRejectsEmpty()
        {
            Assert.AreEqual(1, SceneStrokeStore.NextId);
            SceneStroke added = SceneStrokeStore.Add(Surface(0, Vector3.zero, Vector3.one));
            Assert.AreEqual(1, added.Id);
            Assert.AreEqual(1, added.Number);
            Assert.AreEqual(2, SceneStrokeStore.NextId);
            Assert.AreSame(added, SceneStrokeStore.Find(1));
            Assert.Throws<System.ArgumentException>(delegate { SceneStrokeStore.Add(new SceneStroke()); });
            Assert.IsTrue(SceneStrokeStore.Remove(1));
            Assert.IsFalse(SceneStrokeStore.Remove(1));
            Assert.AreEqual(0, SceneStrokeStore.Count);
        }

        // -- Build + payload -------------------------------------------------------------

        [Test]
        public void BuildStroke_Surface_KeepsNormalsAndDistinctHitPaths_PerKeptPoint()
        {
            var points = new List<Vector3> { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), new Vector3(2f, 1f, 0f) };
            var normals = new List<Vector3> { Vector3.up, Vector3.up, Vector3.forward, Vector3.forward };
            var hits = new List<string> { "A", "A", "B", "B" };
            SceneStroke stroke = SceneStrokeSketch.BuildStroke(SceneStrokeMode.Surface, points, normals, hits, Vector3.zero, Vector3.up);
            Assert.IsNotNull(stroke);
            Assert.AreEqual(3, stroke.Points.Count, "the collinear middle point goes");
            Assert.AreEqual(3, stroke.Normals.Count);
            Assert.AreEqual(Vector3.forward, stroke.Normals[1], "the corner keeps its own normal");
            CollectionAssert.AreEqual(new[] { "A", "B" }, stroke.HitPaths);
            CollectionAssert.AreEqual(new[] { 0, 1, 1 }, stroke.HitIndices);
            Assert.IsFalse(stroke.Closed);
        }

        [Test]
        public void BuildStroke_Plane_RecordsThePlane_AndCarriesNoNormals()
        {
            var points = new List<Vector3>();
            for (int i = 0; i < 40; i++)
            {
                // A wide zigzag (1 m steps, 1 m amplitude): every corner
                // sits ~0.7 off any chord, far over the 0.5%-of-length
                // tolerance (~0.28), so every point survives. (A narrow
                // zigzag would not: it reads as a near-vertical line.)
                points.Add(new Vector3(i, i % 2, 0f));
            }
            SceneStroke stroke = SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, points, null, null, new Vector3(0f, 0f, 3f), new Vector3(0f, 0f, 2f));
            Assert.IsNotNull(stroke);
            Assert.AreEqual(40, stroke.Points.Count);
            Assert.AreEqual(points[0], stroke.Points[0]);
            Assert.AreEqual(points[39], stroke.Points[39]);
            Assert.AreEqual(0, stroke.Normals.Count);
            Assert.AreEqual(0, stroke.HitIndices.Count);
            Assert.AreEqual(new Vector3(0f, 0f, 3f), stroke.PlaneOrigin);
            Assert.AreEqual(Vector3.forward, stroke.PlaneNormal, "normalized");
        }

        [Test]
        public void BuildStroke_RejectsDegenerateInput()
        {
            Assert.IsNull(SceneStrokeSketch.BuildStroke(SceneStrokeMode.Surface, null, null, null, Vector3.zero, Vector3.up));
            Assert.IsNull(SceneStrokeSketch.BuildStroke(SceneStrokeMode.Surface, Line(1f), null, null, Vector3.zero, Vector3.up));
            Assert.IsNull(SceneStrokeSketch.BuildStroke(SceneStrokeMode.Surface, Line(1f, 1f), null, null, Vector3.zero, Vector3.up), "zero length");
        }

        [Test]
        public void FormatPayload_NamesNumberModeObjectsAndTheListTool()
        {
            SceneStroke stroke = Surface(3, new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f));
            string payload = SceneStrokeSketch.FormatPayload(3, stroke, 12);
            StringAssert.Contains("Scene sketch S3", payload);
            StringAssert.Contains("mode: surface", payload);
            StringAssert.Contains("points: 2, length: 1.00 m, closed: no", payload);
            StringAssert.Contains("on objects: Env/Wall", payload);
            StringAssert.Contains("(0.00, 0.00, 0.00), (1.00, 0.00, 0.00)", payload);
            StringAssert.Contains("uap_stroke_list id=12", payload);
            StringAssert.DoesNotContain("plane:", payload);
        }

        [Test]
        public void FormatPayload_PlaneStroke_ShowsThePlane_AndTruncatesLongPointLists()
        {
            var stroke = new SceneStroke { Mode = SceneStrokeMode.Plane, PlaneOrigin = Vector3.zero, PlaneNormal = Vector3.up };
            for (int i = 0; i < 100; i++)
            {
                stroke.Points.Add(new Vector3(i, 0f, 0f));
            }
            string payload = SceneStrokeSketch.FormatPayload(1, stroke, 5);
            StringAssert.Contains("mode: plane", payload);
            StringAssert.Contains("plane: origin (0.00, 0.00, 0.00), normal (0.00, 1.00, 0.00)", payload);
            StringAssert.Contains("(" + SceneStrokeSketch.PayloadPointCount + " of 100 points shown; uap_stroke_list id=5 returns all)", payload);
        }

        [Test]
        public void StepDirection_AxisPlanesMoveAlongTheirNormal_AwayFromTheCamera()
        {
            Vector3 lookingDown = Vector3.down;
            Assert.AreEqual(Vector3.forward, SceneStrokeSketch.StepDirection(SceneStrokePlaneAxis.Z, Vector3.forward, lookingDown),
                "a Z plane seen from above still moves along z (it used to fall back to the view direction)");
            Assert.AreEqual(Vector3.back, SceneStrokeSketch.StepDirection(SceneStrokePlaneAxis.Z, Vector3.forward, Vector3.back),
                "looking toward -z, farther is -z");
            Assert.AreEqual(Vector3.up, SceneStrokeSketch.StepDirection(SceneStrokePlaneAxis.Y, Vector3.up, new Vector3(0.1f, 0.5f, 0.8f).normalized));
            Vector3 view = new Vector3(1f, 2f, 3f).normalized;
            Assert.AreEqual(view, SceneStrokeSketch.StepDirection(SceneStrokePlaneAxis.CameraFacing, -view, view), "camera-facing: the view direction");
        }

        [Test]
        public void Arm_SetsModeAndArmed_AndRaisesOnce()
        {
            int raised = 0;
            System.Action handler = delegate { raised++; };
            SceneStrokeSketch.ArmedChanged += handler;
            try
            {
                SceneStrokeSketch.Arm(SceneStrokeMode.Plane);
                Assert.IsTrue(SceneStrokeSketch.IsArmedIn(SceneStrokeMode.Plane));
                Assert.IsFalse(SceneStrokeSketch.IsArmedIn(SceneStrokeMode.Surface));
                Assert.AreEqual(1, raised);
                SceneStrokeSketch.Arm(SceneStrokeMode.Plane);
                Assert.AreEqual(1, raised, "re-arming the same mode is a no-op");
                SceneStrokeSketch.Armed = false;
                Assert.IsFalse(SceneStrokeSketch.Armed);
                Assert.AreEqual(2, raised);
            }
            finally
            {
                SceneStrokeSketch.ArmedChanged -= handler;
            }
        }

        [Test]
        public void Arm_DisarmsThePin_AndArmingThePinDisarmsTheSketch()
        {
            SceneMarkerPin.ResetForTests();
            try
            {
                SceneMarkerPin.Armed = true;
                SceneStrokeSketch.Arm(SceneStrokeMode.Surface);
                Assert.IsFalse(SceneMarkerPin.Armed, "arming a sketch mode disarms the pin (one tool per Scene-view click)");
                Assert.IsTrue(SceneStrokeSketch.Armed);

                SceneMarkerPin.Armed = true;
                Assert.IsFalse(SceneStrokeSketch.Armed, "arming the pin disarms the sketch");
                Assert.IsTrue(SceneMarkerPin.Armed);

                SceneMarkerPin.Armed = false;
                SceneStrokeSketch.Armed = true;
                Assert.IsFalse(SceneMarkerPin.Armed, "the pin was already off; disarming it again touches nothing");
                Assert.IsTrue(SceneStrokeSketch.Armed);
            }
            finally
            {
                SceneMarkerPin.ResetForTests();
            }
        }
    }
}

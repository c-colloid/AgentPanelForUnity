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

        // -- Shapes and regions (design note 2026-10-09-sketch-shapes-navigation-regions.md) --

        [Test]
        public void PlaneBasis_IsOrthonormal_AndFollowsThePreferredRight()
        {
            Vector3 right;
            Vector3 up;
            SceneStrokeGeometry.PlaneBasis(Vector3.up, new Vector3(1f, 5f, 0f), out right, out up);
            Assert.AreEqual(0f, Vector3.Dot(right, Vector3.up), 1e-5f);
            Assert.AreEqual(0f, Vector3.Dot(right, up), 1e-5f);
            Assert.AreEqual(1f, right.magnitude, 1e-5f);
            Assert.AreEqual(1f, up.magnitude, 1e-5f);
            Assert.AreEqual(1f, Vector3.Dot(right, Vector3.right), 1e-5f, "the preferred right projected into the plane");
            // A preferred right along the normal falls back to a world axis.
            SceneStrokeGeometry.PlaneBasis(Vector3.up, Vector3.up, out right, out up);
            Assert.AreEqual(1f, right.magnitude, 1e-5f);
            Assert.AreEqual(0f, Vector3.Dot(right, Vector3.up), 1e-5f);
        }

        [Test]
        public void RectangleAndEllipse_SpanTheDrag_AndShiftMakesThemUniform()
        {
            List<Vector3> rect = SceneStrokeGeometry.RectanglePoints(Vector3.zero, new Vector3(4f, 0f, 2f), Vector3.right, Vector3.forward, false);
            Assert.AreEqual(4, rect.Count);
            Assert.AreEqual(new Vector3(4f, 0f, 0f), rect[1]);
            Assert.AreEqual(new Vector3(4f, 0f, 2f), rect[2]);
            Assert.AreEqual(new Vector3(0f, 0f, 2f), rect[3]);
            Assert.AreEqual(8f, SceneStrokeGeometry.PolygonArea(rect), 1e-4f);
            List<Vector3> square = SceneStrokeGeometry.RectanglePoints(Vector3.zero, new Vector3(4f, 0f, -2f), Vector3.right, Vector3.forward, true);
            Assert.AreEqual(new Vector3(2f, 0f, -2f), square[2], "Shift clamps to the shorter extent, keeping the drag's direction");
            List<Vector3> ellipse = SceneStrokeGeometry.EllipsePoints(Vector3.zero, new Vector3(4f, 0f, 2f), Vector3.right, Vector3.forward, false, 48);
            Assert.AreEqual(48, ellipse.Count);
            Assert.AreEqual(Mathf.PI * 2f * 1f, SceneStrokeGeometry.PolygonArea(ellipse), 0.05f, "pi * a * b for a 2 x 1 ellipse");
            List<Vector3> circle = SceneStrokeGeometry.EllipsePoints(Vector3.zero, new Vector3(4f, 0f, 2f), Vector3.right, Vector3.forward, true, 48);
            Assert.AreEqual(Mathf.PI, SceneStrokeGeometry.PolygonArea(circle), 0.05f, "a circle of radius 1");
        }

        [Test]
        public void Subdivide_InsertsPointsAlongLongEdges_AndRespectsTheCap()
        {
            List<Vector3> dense = SceneStrokeGeometry.Subdivide(Line(0f, 10f), false, 1f, 256);
            Assert.AreEqual(11, dense.Count);
            Assert.AreEqual(new Vector3(0f, 0f, 0f), dense[0]);
            Assert.AreEqual(new Vector3(10f, 0f, 0f), dense[10]);
            Assert.AreEqual(new Vector3(5f, 0f, 0f), dense[5]);
            List<Vector3> closed = SceneStrokeGeometry.Subdivide(
                new List<Vector3> { Vector3.zero, new Vector3(4f, 0f, 0f), new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f) }, true, 1f, 256);
            Assert.AreEqual(16, closed.Count, "four edges of four pieces, the closing edge included, no repeated start");
            List<Vector3> capped = SceneStrokeGeometry.Subdivide(Line(0f, 1000f), false, 0.01f, 20);
            Assert.LessOrEqual(capped.Count, 20);
            Assert.AreEqual(Line(0f, 10f), SceneStrokeGeometry.Subdivide(Line(0f, 10f), false, 20f, 256), "a short edge stays as it is");
        }

        [Test]
        public void NewellNormal_AndPolygonArea_HandleAnyStartVertexAndOrientation()
        {
            var square = new List<Vector3> { Vector3.zero, new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 2f), new Vector3(0f, 0f, 2f) };
            Vector3 n = SceneStrokeGeometry.NewellNormal(square);
            Assert.AreEqual(1f, Mathf.Abs(n.y), 1e-5f);
            Assert.AreEqual(4f, SceneStrokeGeometry.PolygonArea(square), 1e-5f);
            square.Reverse();
            Assert.AreEqual(4f, SceneStrokeGeometry.PolygonArea(square), 1e-5f);
            Assert.AreEqual(Vector3.zero, SceneStrokeGeometry.NewellNormal(Line(0f, 1f)));
            Assert.AreEqual(0f, SceneStrokeGeometry.PolygonArea(Line(0f, 1f)));
            Assert.AreEqual(new Vector3(1f, 0f, 1f), SceneStrokeGeometry.Centroid(square));
        }

        [Test]
        public void PointInPolygon_Triangulate_WorkOnAConcaveOutline()
        {
            // An L shape: 3 x 3 with the top-right 2 x 2 cut away (area 5).
            var l = new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(3f, 0f), new Vector2(3f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 3f), new Vector2(0f, 3f)
            };
            Assert.IsTrue(SceneStrokeGeometry.PointInPolygon(l, new Vector2(0.5f, 2.5f)));
            Assert.IsTrue(SceneStrokeGeometry.PointInPolygon(l, new Vector2(2.5f, 0.5f)));
            Assert.IsFalse(SceneStrokeGeometry.PointInPolygon(l, new Vector2(2f, 2f)), "inside the bounds but in the notch");
            Assert.IsFalse(SceneStrokeGeometry.PointInPolygon(l, new Vector2(-1f, 1f)));
            Assert.AreEqual(5f, SceneStrokeGeometry.SignedArea(l), 1e-5f);
            List<int> triangles = SceneStrokeGeometry.Triangulate(l);
            Assert.AreEqual(4 * 3, triangles.Count, "n - 2 triangles");
            float area = 0f;
            for (int t = 0; t < triangles.Count; t += 3)
            {
                Vector2 a = l[triangles[t]];
                Vector2 b = l[triangles[t + 1]];
                Vector2 c = l[triangles[t + 2]];
                area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
            }
            Assert.AreEqual(5f, area, 1e-4f, "the triangles tile the outline exactly");
            l.Reverse();
            Assert.AreEqual(12, SceneStrokeGeometry.Triangulate(l).Count, "clockwise input works too");
            Assert.AreEqual(0, SceneStrokeGeometry.Triangulate(new List<Vector2> { Vector2.zero, Vector2.one }).Count);
        }

        [Test]
        public void Region_FrameAndContainment_ExcludeTheFloorAndOutsidePoints()
        {
            var stroke = new SceneStroke { Mode = SceneStrokeMode.Surface, Shape = SceneStrokeShape.Rectangle, Closed = true };
            stroke.Points.AddRange(new[] { new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f), new Vector3(4f, 0f, 4f), new Vector3(0f, 0f, 4f) });
            Assert.IsTrue(stroke.IsRegion);
            SceneStrokeRegion.Frame frame = SceneStrokeRegion.FrameFor(stroke);
            Assert.AreEqual(1f, Mathf.Abs(frame.Normal.y), 1e-5f, "a ground outline measures in the XZ plane");
            Assert.AreEqual(new Vector3(2f, 0f, 2f), frame.Origin);
            List<Vector2> outline = SceneStrokeRegion.Outline(stroke, frame);
            Rect bounds = SceneStrokeRegion.OutlineBounds(outline);
            Assert.AreEqual(4f, bounds.width, 1e-5f);
            Assert.IsTrue(SceneStrokeRegion.Contains(outline, bounds, frame, new Vector3(1f, 0.5f, 1f), new Vector3(0.5f, 0.5f, 0.5f)), "a crate inside");
            Assert.IsTrue(SceneStrokeRegion.Contains(outline, bounds, frame, new Vector3(1f, 7f, 1f), new Vector3(0.5f, 0.5f, 0.5f)), "height above the plane does not matter");
            Assert.IsFalse(SceneStrokeRegion.Contains(outline, bounds, frame, new Vector3(6f, 0f, 1f), new Vector3(0.5f, 0.5f, 0.5f)), "outside");
            Assert.IsFalse(SceneStrokeRegion.Contains(outline, bounds, frame, new Vector3(2f, 0f, 2f), new Vector3(50f, 0.1f, 50f)), "the floor covers the whole outline");
            var open = new SceneStroke { Mode = SceneStrokeMode.Plane, PlaneNormal = Vector3.forward };
            open.Points.AddRange(new[] { Vector3.zero, Vector3.right });
            Assert.IsFalse(open.IsRegion);
            Assert.AreEqual(Vector3.forward, SceneStrokeRegion.FrameFor(open).Normal, "a plane stroke measures in its sketch plane");
            Assert.AreEqual(0, SceneStrokeRegion.FillTriangles(open).Count);
            Assert.AreEqual(6, SceneStrokeRegion.FillTriangles(stroke).Count);
        }

        [Test]
        public void BuildStroke_Shapes_KeepTheirVerticesAndClosedFlag()
        {
            var rect = new List<Vector3> { Vector3.zero, new Vector3(3f, 0f, 0f), new Vector3(3f, 0f, 3f), new Vector3(0f, 0f, 3f) };
            SceneStroke stroke = SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, SceneStrokeShape.Rectangle, rect, null, null, Vector3.zero, Vector3.up, true);
            Assert.AreEqual(SceneStrokeShape.Rectangle, stroke.Shape);
            Assert.AreEqual(4, stroke.Points.Count, "no simplification for a shape");
            Assert.IsTrue(stroke.Closed, "a rectangle is closed although its ends are 3 m apart");
            // A collinear polyline keeps its middle vertex (freehand would drop it).
            var poly = new List<Vector3> { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) };
            Assert.AreEqual(3, SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, SceneStrokeShape.Polyline, poly, null, null, Vector3.zero, Vector3.up, false).Points.Count);
            Assert.AreEqual(2, SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, SceneStrokeShape.Freehand, poly, null, null, Vector3.zero, Vector3.up, false).Points.Count);
            // closed:true needs three points to mean anything.
            Assert.IsFalse(SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, SceneStrokeShape.Line, Line(0f, 1f), null, null, Vector3.zero, Vector3.up, true).Closed);
            // The pre-shape overload is a freehand build.
            Assert.AreEqual(SceneStrokeShape.Freehand, SceneStrokeSketch.BuildStroke(SceneStrokeMode.Plane, poly, null, null, Vector3.zero, Vector3.up).Shape);
        }

        [Test]
        public void ShapeKeys_PaletteAndShapeNames()
        {
            Assert.AreEqual((int)SceneStrokeShape.Freehand, SceneStrokeSketch.ShapeKeyIndex(KeyCode.Alpha1));
            Assert.AreEqual((int)SceneStrokeShape.Ellipse, SceneStrokeSketch.ShapeKeyIndex(KeyCode.Keypad5));
            Assert.AreEqual(-1, SceneStrokeSketch.ShapeKeyIndex(KeyCode.Alpha6));
            Assert.AreEqual(-1, SceneStrokeSketch.ShapeKeyIndex(KeyCode.X));
            Assert.AreEqual("rectangle", SceneStroke.ShapeName(SceneStrokeShape.Rectangle));
            Assert.AreEqual("freehand", SceneStroke.ShapeName(SceneStrokeShape.Freehand));
            Assert.AreEqual(SceneStroke.PaletteColor(1), SceneStroke.PaletteColor(9), "eight colours cycle");
            Assert.AreNotEqual(SceneStroke.PaletteColor(1), SceneStroke.PaletteColor(2));
            Assert.AreEqual(SceneStroke.PaletteColor(1), SceneStroke.PaletteColor(0), "no number: the first colour");
        }

        [Test]
        public void Store_Rename_SetsTheNameRebuildsTheNoteAndPersists()
        {
            SceneStroke stroke = SceneStrokeStore.Add(Surface(0, Vector3.zero, Vector3.one));
            Assert.IsTrue(SceneStrokeStore.Rename(stroke.Id, "  plaza "));
            Assert.AreEqual("plaza", stroke.Name);
            Assert.AreEqual("S1 plaza", stroke.DisplayLabel);
            StringAssert.Contains("name: plaza", stroke.Note);
            Assert.IsFalse(SceneStrokeStore.Rename(99, "x"));
            Assert.IsTrue(SceneStrokeStore.Rename(stroke.Id, string.Empty));
            Assert.AreEqual("S1", stroke.DisplayLabel);
            StringAssert.DoesNotContain("name:", stroke.Note);
        }

        [Test]
        public void Serialize_RoundTrips_ShapeAndName()
        {
            var a = new SceneStroke { Id = 3, Mode = SceneStrokeMode.Plane, Shape = SceneStrokeShape.Ellipse, Name = "pond", Closed = true };
            a.Points.AddRange(new[] { Vector3.zero, Vector3.right, Vector3.forward });
            int nextId;
            List<SceneStroke> back = SceneStrokeStore.Deserialize(SceneStrokeStore.Serialize(new List<SceneStroke> { a }, 4), out nextId);
            Assert.AreEqual(SceneStrokeShape.Ellipse, back[0].Shape);
            Assert.AreEqual("pond", back[0].Name);
            // A payload from before shapes existed reads as a freehand, unnamed stroke.
            SceneStroke old = SceneStroke.FromJson(Core.Json.JsonParser.Parse("{\"id\":1,\"mode\":1,\"p\":[0,0,0,1,1,1]}"));
            Assert.AreEqual(SceneStrokeShape.Freehand, old.Shape);
            Assert.AreEqual(string.Empty, old.Name);
        }

        [Test]
        public void FormatPayload_Region_NamesShapeNameAndArea()
        {
            var stroke = new SceneStroke { Mode = SceneStrokeMode.Plane, Shape = SceneStrokeShape.Rectangle, Name = "plaza", Closed = true, PlaneNormal = Vector3.up };
            stroke.Points.AddRange(new[] { Vector3.zero, new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 3f), new Vector3(0f, 0f, 3f) });
            string payload = SceneStrokeSketch.FormatPayload(2, stroke, 5);
            StringAssert.Contains("name: plaza", payload);
            StringAssert.Contains("mode: plane, shape: rectangle", payload);
            StringAssert.Contains("closed: yes", payload);
            StringAssert.Contains("area: 6.00 m^2", payload);
            StringAssert.Contains("uap_stroke_list id=5 lists the objects inside it", payload);
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

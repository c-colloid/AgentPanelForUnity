using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_editor_screenshot's annotate_ui (design note docs/design-notes/
    /// 2026-09-22-uloop-phase1.md section 2.1): the Unity-free ordering/
    /// labeling/coordinate math and JSON shape (also pinned verbatim in
    /// ci/SmokeTests), plus the small Unity-touching helpers
    /// (UapUguiElementCatalog.SiblingPath / IsUnderActiveCanvas) that need
    /// no com.unity.ugui at all -- plain GameObjects are enough for those.
    ///
    /// What is NOT tested here, on purpose: an actual Selectable/Canvas
    /// being discovered by Collect(). Neither CI host project installs
    /// com.unity.ugui, so a real Button/Canvas cannot be built here; a
    /// developer sandbox that does have it is covered by hand (design
    /// note section 6).
    /// </summary>
    [TestFixture]
    public class UapUiAnnotationPolicyTests
    {
        [Test]
        public void LabelForIndex_SpreadsheetStyle_AZ_ThenAA()
        {
            Assert.AreEqual("A", UapUiAnnotationPolicy.LabelForIndex(0));
            Assert.AreEqual("Z", UapUiAnnotationPolicy.LabelForIndex(25));
            Assert.AreEqual("AA", UapUiAnnotationPolicy.LabelForIndex(26));
            Assert.AreEqual("AZ", UapUiAnnotationPolicy.LabelForIndex(51));
            Assert.AreEqual("BA", UapUiAnnotationPolicy.LabelForIndex(52));
            // The default cap (59 is the 60th, 0-based) must still resolve to a short, unambiguous label.
            Assert.AreEqual("BH", UapUiAnnotationPolicy.LabelForIndex(UapUiAnnotationPolicy.DefaultElementCap - 1));
        }

        [Test]
        public void LabelForIndex_Negative_Throws()
        {
            Assert.Throws<ArgumentException>(delegate { UapUiAnnotationPolicy.LabelForIndex(-1); });
        }

        [Test]
        public void CompareOrder_HigherSortingOrderFirst_ThenHierarchyOrderAscending()
        {
            // Higher canvas sortingOrder sorts first (front-most / the one
            // a raycast would actually hit when things overlap).
            Assert.Less(UapUiAnnotationPolicy.CompareOrder(5, new[] { 0 }, 1, new[] { 0 }), 0);
            Assert.Greater(UapUiAnnotationPolicy.CompareOrder(1, new[] { 0 }, 5, new[] { 0 }), 0);
            // Same canvas: ascending document order.
            Assert.Less(UapUiAnnotationPolicy.CompareOrder(0, new[] { 0, 1 }, 0, new[] { 0, 2 }), 0);
            Assert.Greater(UapUiAnnotationPolicy.CompareOrder(0, new[] { 1 }, 0, new[] { 0, 9 }), 0);
            Assert.AreEqual(0, UapUiAnnotationPolicy.CompareOrder(3, new[] { 2, 1 }, 3, new[] { 2, 1 }));
        }

        [Test]
        public void FlipToTopLeft_FlipsYOnly()
        {
            double px, py;
            UapUiAnnotationPolicy.FlipToTopLeft(100, 10, 1080, out px, out py);
            Assert.AreEqual(100, px, 1e-9);
            Assert.AreEqual(1070, py, 1e-9);
            UapUiAnnotationPolicy.FlipToTopLeft(0, 0, 1080, out px, out py);
            Assert.AreEqual(1080, py, 1e-9);
        }

        [Test]
        public void PixelToNormalized_ScalesByView_AndHedgesZeroSize()
        {
            double nx, ny;
            UapUiAnnotationPolicy.PixelToNormalized(400, 450, 800, 600, out nx, out ny);
            Assert.AreEqual(0.5, nx, 1e-9);
            Assert.AreEqual(0.75, ny, 1e-9);
            UapUiAnnotationPolicy.PixelToNormalized(10, 10, 0, 0, out nx, out ny);
            Assert.AreEqual(0.0, nx, 1e-9);
            Assert.AreEqual(0.0, ny, 1e-9);
        }

        private static UapUiElementCandidate MakeCandidate(int sortingOrder, int[] siblingPath, string path,
            double centreX, double centreY, bool hasRect)
        {
            double[] cornersX = hasRect
                ? new[] { centreX - 10, centreX - 10, centreX + 10, centreX + 10 }
                : new[] { centreX, centreX, centreX, centreX };
            double[] cornersY = hasRect
                ? new[] { centreY - 5, centreY + 5, centreY + 5, centreY - 5 }
                : new[] { centreY, centreY, centreY, centreY };
            return new UapUiElementCandidate
            {
                CanvasSortingOrder = sortingOrder,
                SiblingPath = siblingPath,
                HierarchyPath = path,
                ComponentType = "UnityEngine.UI.Button",
                Interactable = true,
                Text = "Go",
                ScreenCentreX = centreX,
                ScreenCentreY = centreY,
                CornerScreenX = cornersX,
                CornerScreenY = cornersY,
                SupportsClick = true
            };
        }

        [Test]
        public void BuildResult_LabelsInSortedOrder_AndBuildsARectFromTheCorners()
        {
            var candidates = new List<UapUiElementCandidate>
            {
                MakeCandidate(0, new[] { 1 }, "Canvas/Back", 100, 100, true),
                MakeCandidate(5, new[] { 0 }, "Overlay/Start", 200, 200, true),
            };
            List<UapUiAnnotationMark> marks;
            bool truncated;
            JsonNode elements = UapUiAnnotationPolicy.BuildResult(candidates, 1920, 1080, 60, out truncated, out marks);

            Assert.IsFalse(truncated);
            Assert.AreEqual(2, elements.Count);
            Assert.AreEqual(2, marks.Count);
            // sortingOrder 5 (Overlay/Start) sorts before sortingOrder 0 (Canvas/Back).
            Assert.AreEqual("A", elements[0]["label"].AsString());
            Assert.AreEqual("Overlay/Start", elements[0]["path"].AsString());
            Assert.AreEqual("B", elements[1]["label"].AsString());
            Assert.AreEqual("Canvas/Back", elements[1]["path"].AsString());
            Assert.AreEqual("A", marks[0].Label);
            Assert.AreEqual("Go", elements[0]["text"].AsString());

            // centre (200,200) bottom-left-origin in an 1080-tall view -> top-left y = 880.
            Assert.AreEqual(200, elements[0]["x"].AsDouble(), 1e-6);
            Assert.AreEqual(880, elements[0]["y"].AsDouble(), 1e-6);
            // corners (190,195)-(210,205) bottom-left -> rect top-left (190, 875), 20x10.
            JsonNode rect = elements[0]["rect"];
            Assert.AreEqual(190, rect["x"].AsDouble(), 1e-6);
            Assert.AreEqual(875, rect["y"].AsDouble(), 1e-6);
            Assert.AreEqual(20, rect["width"].AsDouble(), 1e-6);
            Assert.AreEqual(10, rect["height"].AsDouble(), 1e-6);
        }

        [Test]
        public void BuildResult_CapsAndFlagsTruncation()
        {
            var candidates = new List<UapUiElementCandidate>();
            for (int i = 0; i < 5; i++)
            {
                candidates.Add(MakeCandidate(0, new[] { i }, "Item" + i, i, i, false));
            }
            List<UapUiAnnotationMark> marks;
            bool truncated;
            JsonNode elements = UapUiAnnotationPolicy.BuildResult(candidates, 1920, 1080, 3, out truncated, out marks);
            Assert.IsTrue(truncated);
            Assert.AreEqual(3, elements.Count);
            Assert.AreEqual(3, marks.Count);
        }

        [Test]
        public void BuildResult_NonPositiveCap_FallsBackToDefault()
        {
            var candidates = new List<UapUiElementCandidate> { MakeCandidate(0, new[] { 0 }, "X", 0, 0, false) };
            List<UapUiAnnotationMark> marks;
            bool truncated;
            JsonNode elements = UapUiAnnotationPolicy.BuildResult(candidates, 100, 100, 0, out truncated, out marks);
            Assert.IsFalse(truncated);
            Assert.AreEqual(1, elements.Count);
        }

        [Test]
        public void ScaleMarks_ScalesByRatio_AndHedgesZeroFromSize()
        {
            var marks = new List<UapUiAnnotationMark>
            {
                new UapUiAnnotationMark { Label = "A", X = 100, Y = 50, Width = 20, Height = 10 }
            };
            List<UapUiAnnotationMark> scaled = UapUiAnnotationPolicy.ScaleMarks(marks, 1000, 500, 500, 1000);
            Assert.AreEqual(50, scaled[0].X, 1e-9);
            Assert.AreEqual(100, scaled[0].Y, 1e-9);
            Assert.AreEqual(10, scaled[0].Width, 1e-9);
            Assert.AreEqual(20, scaled[0].Height, 1e-9);

            List<UapUiAnnotationMark> unscaled = UapUiAnnotationPolicy.ScaleMarks(marks, 0, 0, 500, 1000);
            Assert.AreEqual(100, unscaled[0].X, 1e-9, "a zero 'from' size must not divide by zero");
        }

        [Test]
        public void TryGetGlyph_EveryLetterIsFiveWideBySevenTall_UsingOnlyHashAndDot()
        {
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string[] rows;
                Assert.IsTrue(UapUiAnnotationPolicy.TryGetGlyph(c, out rows), "missing glyph for " + c);
                Assert.AreEqual(UapUiAnnotationPolicy.GlyphHeight, rows.Length, "row count for " + c);
                foreach (string row in rows)
                {
                    Assert.AreEqual(UapUiAnnotationPolicy.GlyphWidth, row.Length, "row width for " + c);
                    foreach (char pixel in row)
                    {
                        Assert.IsTrue(pixel == '#' || pixel == '.', "unexpected glyph pixel for " + c);
                    }
                }
            }
            // Case-insensitive, and something outside A-Z is simply absent (labels never contain digits).
            string[] lower;
            Assert.IsTrue(UapUiAnnotationPolicy.TryGetGlyph('a', out lower));
            string[] digit;
            Assert.IsFalse(UapUiAnnotationPolicy.TryGetGlyph('5', out digit));
        }
    }

    /// <summary>The small Collect() helpers that need real GameObjects/Transforms but no com.unity.ugui.</summary>
    [TestFixture]
    public class UapUguiElementCatalogTests
    {
        private const string Prefix = "UapUguiElementCatalogTest";

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith(Prefix))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void SiblingPath_Root_IsASingleElementArray_MatchingItsOwnIndex()
        {
            var root = new GameObject(Prefix + "Root");
            root.transform.SetSiblingIndex(0);
            int[] path = UapUguiElementCatalog.SiblingPath(root.transform);
            Assert.AreEqual(1, path.Length, "a root has exactly one level");
            Assert.AreEqual(0, path[0]);
        }

        [Test]
        public void SiblingPath_Nested_ReflectsEachLevelsIndex()
        {
            var root = new GameObject(Prefix + "Root2");
            var childA = new GameObject(Prefix + "ChildA");
            var childB = new GameObject(Prefix + "ChildB");
            childA.transform.SetParent(root.transform, false);
            childB.transform.SetParent(root.transform, false);
            var grandchild = new GameObject(Prefix + "Grandchild");
            grandchild.transform.SetParent(childB.transform, false);

            int[] path = UapUguiElementCatalog.SiblingPath(grandchild.transform);
            Assert.AreEqual(3, path.Length);
            Assert.AreEqual(root.transform.GetSiblingIndex(), path[0]);
            Assert.AreEqual(1, path[1], "childB is the second child of root");
            Assert.AreEqual(0, path[2], "grandchild is the only child of childB");
        }

        [Test]
        public void IsUnderActiveCanvas_NoCanvasAncestor_ReturnsFalse()
        {
            var go = new GameObject(Prefix + "NoCanvas");
            Component canvas;
            Assert.IsFalse(UapUguiElementCatalog.IsUnderActiveCanvas(go, out canvas));
            Assert.IsNull(canvas);
        }

        [Test]
        public void IsUnderActiveCanvas_InactiveGameObject_ReturnsFalse()
        {
            var go = new GameObject(Prefix + "Inactive");
            go.SetActive(false);
            Component canvas;
            Assert.IsFalse(UapUguiElementCatalog.IsUnderActiveCanvas(go, out canvas));
        }

        [Test]
        public void IsUnderActiveCanvas_NullGameObject_ReturnsFalse()
        {
            Component canvas;
            Assert.IsFalse(UapUguiElementCatalog.IsUnderActiveCanvas(null, out canvas));
        }

        [Test]
        public void Collect_LoadsOrNamesThePackage()
        {
            // Same pattern as UapGameKeyToolTests.InputSystemAccess_LoadsOrNamesThePackage:
            // neither CI host project installs com.unity.ugui, but a developer
            // sandbox may, so the answer must be usable either way.
            string warning;
            List<UapUiElementCandidate> candidates = UapUguiElementCatalog.Collect(out warning);
            Assert.IsNotNull(candidates);
            if (warning != null)
            {
                StringAssert.Contains("com.unity.ugui", warning);
            }
        }
    }
}

using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real scene RectTransform reads/writes via uap_rect_transform_set
    /// (mirrors UapTransformSetToolTests -- real GameObjects, real Undo, no
    /// mocking of the Editor). Every fixture object is built with
    /// RectTransform only: uGUI itself (com.unity.ugui) is NOT a dependency
    /// of this package and is absent from both CI host projects, so nothing
    /// here may reference a UnityEngine.UI type.
    /// </summary>
    [TestFixture]
    public class UapRectTransformSetToolTests
    {
        private const string Prefix = "UapRectTransformSetToolTest";

        private UapRectTransformSetTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new UapRectTransformSetTool();
        }

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

        private static JsonNode Vec2(double x, double y)
        {
            return JsonNode.NewObject().Set("x", x).Set("y", y);
        }

        /// <summary>A root RectTransform with deterministic starting values (never trust Unity's defaults).</summary>
        private static RectTransform MakeRect(string nameSuffix)
        {
            var go = new GameObject(Prefix + nameSuffix, typeof(RectTransform));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(100f, 50f);
            rt.anchoredPosition = Vector2.zero;
            return rt;
        }

        /// <summary>A 400x300 parent rect with a centered child, the setup every anchor-math assertion uses.</summary>
        private static RectTransform MakeChildOfSizedParent(string nameSuffix, out RectTransform parent)
        {
            parent = MakeRect(nameSuffix + "Parent");
            parent.sizeDelta = new Vector2(400f, 300f);
            RectTransform child = MakeRect(nameSuffix + "Child");
            child.SetParent(parent, false);
            child.anchorMin = new Vector2(0.5f, 0.5f);
            child.anchorMax = new Vector2(0.5f, 0.5f);
            child.pivot = new Vector2(0.5f, 0.5f);
            child.sizeDelta = new Vector2(100f, 50f);
            child.anchoredPosition = Vector2.zero;
            return child;
        }

        private static JsonNode ReadBack(JsonNode executeResult)
        {
            return JsonParser.Parse(executeResult[0]["text"].AsString());
        }

        private static void AssertVec2(Vector2 expected, Vector2 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, 0.01f, what + ".x");
            Assert.AreEqual(expected.y, actual.y, 0.01f, what + ".y");
        }

        // -----------------------------------------------------------------
        // Position and size
        // -----------------------------------------------------------------

        [Test]
        public void Execute_AnchoredPosition_WritesAnchoredPosition()
        {
            RectTransform rt = MakeRect("AnchoredPos");

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("anchoredPosition", Vec2(30.0, -12.0)));

            AssertVec2(new Vector2(30f, -12f), rt.anchoredPosition, "anchoredPosition");
        }

        [Test]
        public void Execute_PartialAnchoredPosition_OnlyOverridesGivenAxis()
        {
            RectTransform rt = MakeRect("PartialPos");
            rt.anchoredPosition = new Vector2(5f, 7f);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("anchoredPosition", JsonNode.NewObject().Set("y", 99.0)));

            AssertVec2(new Vector2(5f, 99f), rt.anchoredPosition, "anchoredPosition");
        }

        [Test]
        public void Execute_SizeDelta_WritesSizeDelta()
        {
            RectTransform rt = MakeRect("SizeDelta");

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("sizeDelta", Vec2(160.0, 32.0)));

            AssertVec2(new Vector2(160f, 32f), rt.sizeDelta, "sizeDelta");
        }

        [Test]
        public void Execute_PartialSizeDelta_OnlyOverridesGivenAxis()
        {
            RectTransform rt = MakeRect("PartialSize");
            rt.sizeDelta = new Vector2(100f, 50f);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("sizeDelta", JsonNode.NewObject().Set("x", 200.0)));

            AssertVec2(new Vector2(200f, 50f), rt.sizeDelta, "sizeDelta");
        }

        // -----------------------------------------------------------------
        // Anchors and pivot are RAW writes -- the documented contract
        // -----------------------------------------------------------------

        [Test]
        public void Execute_Anchors_AreWrittenRaw_AndCompensateNothing()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("RawAnchors", out parent);
            Vector2 positionBefore = rt.anchoredPosition;
            Vector2 sizeBefore = rt.sizeDelta;

            _tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + rt.gameObject.name)
                .Set("anchorMin", Vec2(0.0, 0.0))
                .Set("anchorMax", Vec2(1.0, 1.0)));

            AssertVec2(new Vector2(0f, 0f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(1f, 1f), rt.anchorMax, "anchorMax");
            // The whole point: without keepRect the tool touches neither, so
            // the rect visibly grows to the parent's span.
            AssertVec2(positionBefore, rt.anchoredPosition, "anchoredPosition (untouched)");
            AssertVec2(sizeBefore, rt.sizeDelta, "sizeDelta (untouched)");
        }

        [Test]
        public void Execute_Pivot_WritesPivot()
        {
            RectTransform rt = MakeRect("Pivot");

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("pivot", Vec2(0.0, 1.0)));

            AssertVec2(new Vector2(0f, 1f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_PartialPivot_OnlyOverridesGivenAxis()
        {
            RectTransform rt = MakeRect("PartialPivot");

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("pivot", JsonNode.NewObject().Set("x", 0.0)));

            AssertVec2(new Vector2(0f, 0.5f), rt.pivot, "pivot");
        }

        // -----------------------------------------------------------------
        // keepRect: re-anchor without moving
        // -----------------------------------------------------------------

        [Test]
        public void Execute_KeepRect_PreservesTheRectAcrossAnAnchorAndPivotChange()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("KeepRect", out parent);

            // Parent rect is 400x300 centred on its pivot, so its corners run
            // (-200,-150)..(200,150); the child sits centred at 100x50, i.e.
            // (-50,-25)..(50,25) in the parent's space.
            _tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + rt.gameObject.name)
                .Set("preset", "top-left")
                .Set("keepRect", true));

            AssertVec2(new Vector2(0f, 1f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(0f, 1f), rt.anchorMax, "anchorMax");
            AssertVec2(new Vector2(0f, 1f), rt.pivot, "pivot");
            // Anchor (0,1) sits at parent-space (-200,150); keeping the rect
            // means offsetMin (150,-175) / offsetMax (250,-125), which is
            // anchoredPosition (150,-125) with the size unchanged.
            AssertVec2(new Vector2(100f, 50f), rt.sizeDelta, "sizeDelta preserved");
            AssertVec2(new Vector2(150f, -125f), rt.anchoredPosition, "anchoredPosition compensated");
            AssertVec2(new Vector2(100f, 50f), rt.rect.size, "rect size preserved");
        }

        [Test]
        public void Execute_KeepRect_ThenExplicitPosition_LetsTheExplicitValueWin()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("KeepRectThenMove", out parent);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + rt.gameObject.name)
                .Set("preset", "top-left")
                .Set("keepRect", true)
                .Set("anchoredPosition", JsonNode.NewObject().Set("x", 8.0)));

            // x comes from the explicit argument, y from the compensation.
            AssertVec2(new Vector2(8f, -125f), rt.anchoredPosition, "anchoredPosition");
        }

        [Test]
        public void Execute_KeepRect_WithoutAnAnchorOrPivotChange_Throws()
        {
            RectTransform rt = MakeRect("KeepRectAlone");

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("keepRect", true)
                    .Set("anchoredPosition", Vec2(1.0, 1.0)));
            });
        }

        [Test]
        public void Execute_KeepRect_WhenParentHasNoRectTransform_Throws()
        {
            RectTransform rt = MakeRect("KeepRectNoParent");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("preset", "top-left")
                    .Set("keepRect", true));
            });
            StringAssert.Contains("keepRect", ex.Message);
        }

        // -----------------------------------------------------------------
        // Offsets
        // -----------------------------------------------------------------

        [Test]
        public void Execute_Offsets_WriteBothEdges()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("Offsets", out parent);
            string path = parent.gameObject.name + "/" + rt.gameObject.name;

            _tool.Execute(JsonNode.NewObject().Set("path", path).Set("preset", "stretch-stretch"));
            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("offsetMin", Vec2(10.0, 10.0))
                .Set("offsetMax", Vec2(-10.0, -10.0)));

            AssertVec2(new Vector2(10f, 10f), rt.offsetMin, "offsetMin");
            AssertVec2(new Vector2(-10f, -10f), rt.offsetMax, "offsetMax");
            AssertVec2(new Vector2(380f, 280f), rt.rect.size, "rect size");
        }

        [Test]
        public void Execute_PartialOffsetMax_MergesAgainstCurrentOffsets()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("PartialOffset", out parent);
            string path = parent.gameObject.name + "/" + rt.gameObject.name;

            _tool.Execute(JsonNode.NewObject().Set("path", path).Set("preset", "stretch-stretch"));
            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("offsetMin", Vec2(10.0, 10.0))
                .Set("offsetMax", Vec2(-10.0, -10.0)));

            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("offsetMax", JsonNode.NewObject().Set("x", -20.0)));

            AssertVec2(new Vector2(-20f, -10f), rt.offsetMax, "offsetMax");
            AssertVec2(new Vector2(10f, 10f), rt.offsetMin, "offsetMin untouched");
        }

        // -----------------------------------------------------------------
        // Presets
        // -----------------------------------------------------------------

        [Test]
        public void Execute_Preset_TopLeft_SetsAnchorsAndPivot()
        {
            RectTransform rt = MakeRect("PresetTopLeft");

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "top-left"));

            AssertVec2(new Vector2(0f, 1f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(0f, 1f), rt.anchorMax, "anchorMax");
            AssertVec2(new Vector2(0f, 1f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_Preset_StretchStretch_SetsSpanningAnchors()
        {
            RectTransform rt = MakeRect("PresetStretch");

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "stretch-stretch"));

            AssertVec2(new Vector2(0f, 0f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(1f, 1f), rt.anchorMax, "anchorMax");
            AssertVec2(new Vector2(0.5f, 0.5f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_Preset_StretchLeft_StretchesTheVerticalAxisOnly()
        {
            RectTransform rt = MakeRect("PresetStretchLeft");

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "stretch-left"));

            // Pins the <vertical>-<horizontal> naming: "stretch-left" is a
            // LEFT-anchored column spanning top to bottom, never the reverse.
            AssertVec2(new Vector2(0f, 0f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(0f, 1f), rt.anchorMax, "anchorMax");
            AssertVec2(new Vector2(0f, 0.5f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_Preset_BottomStretch_StretchesTheHorizontalAxisOnly()
        {
            RectTransform rt = MakeRect("PresetBottomStretch");

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "bottom-stretch"));

            AssertVec2(new Vector2(0f, 0f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(1f, 0f), rt.anchorMax, "anchorMax");
            AssertVec2(new Vector2(0.5f, 0f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_Preset_IsCaseInsensitive()
        {
            RectTransform rt = MakeRect("PresetCase");

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "TOP-LEFT"));

            AssertVec2(new Vector2(0f, 1f), rt.pivot, "pivot");
        }

        [Test]
        public void Execute_PresetWithKeepPivot_LeavesPivotAlone()
        {
            RectTransform rt = MakeRect("PresetKeepPivot");
            rt.pivot = new Vector2(0.2f, 0.3f);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("preset", "top-left")
                .Set("keepPivot", true));

            AssertVec2(new Vector2(0f, 1f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(0.2f, 0.3f), rt.pivot, "pivot kept");
        }

        [Test]
        public void Execute_EveryPresetName_IsAccepted()
        {
            RectTransform rt = MakeRect("PresetAll");
            string[] names = Registry().Find("uap_rect_transform_set")
                .InputSchema["properties"]["preset"]["enum"].AsStringArray();

            Assert.AreEqual(16, names.Length, "the schema must advertise all 16 anchor cells");
            for (int i = 0; i < names.Length; i++)
            {
                string preset = names[i];
                Assert.DoesNotThrow(delegate
                {
                    _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", preset));
                }, "schema advertises preset '" + preset + "' but Execute rejected it");
            }
        }

        private static ToolRegistry Registry()
        {
            return ToolRegistry.CreateDefault();
        }

        // -----------------------------------------------------------------
        // Rejected combinations -- all of them before any write happens
        // -----------------------------------------------------------------

        [Test]
        public void Execute_UnknownPreset_Throws()
        {
            RectTransform rt = MakeRect("BadPreset");

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "middle"));
            });
            StringAssert.Contains("Unknown preset", ex.Message);
        }

        [Test]
        public void Execute_PresetWithExplicitAnchorMin_Throws()
        {
            RectTransform rt = MakeRect("PresetPlusAnchor");

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("preset", "top-left")
                    .Set("anchorMin", Vec2(0.0, 0.0)));
            });
        }

        [Test]
        public void Execute_KeepPivotWithoutPreset_Throws()
        {
            RectTransform rt = MakeRect("KeepPivotAlone");

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("keepPivot", true)
                    .Set("anchorMin", Vec2(0.0, 0.0)));
            });
        }

        [Test]
        public void Execute_OffsetsAndAnchoredPositionTogether_Throw_AndChangeNothing()
        {
            RectTransform rt = MakeRect("OffsetsPlusPos");
            Vector2 positionBefore = rt.anchoredPosition;
            Vector2 sizeBefore = rt.sizeDelta;

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("anchoredPosition", Vec2(1.0, 2.0))
                    .Set("offsetMin", Vec2(3.0, 4.0)));
            });

            AssertVec2(positionBefore, rt.anchoredPosition, "anchoredPosition");
            AssertVec2(sizeBefore, rt.sizeDelta, "sizeDelta");
        }

        [Test]
        public void Execute_OffsetsAndSizeDeltaTogether_Throw()
        {
            RectTransform rt = MakeRect("OffsetsPlusSize");

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("sizeDelta", Vec2(1.0, 2.0))
                    .Set("offsetMax", Vec2(3.0, 4.0)));
            });
        }

        [Test]
        public void Execute_AnchorsAndPositionInOneCall_ApplyInOrder()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("Combined", out parent);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + rt.gameObject.name)
                .Set("preset", "top-left")
                .Set("anchoredPosition", Vec2(20.0, -20.0))
                .Set("sizeDelta", Vec2(100.0, 40.0)));

            AssertVec2(new Vector2(0f, 1f), rt.anchorMin, "anchorMin");
            AssertVec2(new Vector2(0f, 1f), rt.pivot, "pivot");
            AssertVec2(new Vector2(20f, -20f), rt.anchoredPosition, "anchoredPosition");
            AssertVec2(new Vector2(100f, 40f), rt.sizeDelta, "sizeDelta");
        }

        // -----------------------------------------------------------------
        // Read-back
        // -----------------------------------------------------------------

        [Test]
        public void Execute_NoFieldsGiven_ReadsCurrentValues_AndChangesNothing()
        {
            RectTransform rt = MakeRect("Read");
            rt.anchoredPosition = new Vector2(3f, 4f);
            rt.sizeDelta = new Vector2(120f, 60f);

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name)));

            AssertVec2(new Vector2(3f, 4f), rt.anchoredPosition, "anchoredPosition unchanged");
            AssertVec2(new Vector2(120f, 60f), rt.sizeDelta, "sizeDelta unchanged");
            Assert.AreEqual(3.0, read["anchoredPosition"]["x"].AsDouble(), 0.01);
            Assert.AreEqual(120.0, read["sizeDelta"]["x"].AsDouble(), 0.01);
            Assert.IsTrue(read["anchorMin"].IsObject, "anchorMin reported");
            Assert.IsTrue(read["anchorMax"].IsObject, "anchorMax reported");
            Assert.IsTrue(read["pivot"].IsObject, "pivot reported");
            Assert.IsTrue(read["offsetMin"].IsObject, "offsetMin reported");
            Assert.IsTrue(read["offsetMax"].IsObject, "offsetMax reported");
            Assert.IsTrue(read["rect"].IsObject, "rect reported");
            Assert.IsTrue(read["warnings"].IsArray, "warnings is always an array");
        }

        [Test]
        public void Execute_ReadBack_ReportsResolvedRectSize_WhenAnchorsStretch()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("StretchRead", out parent);
            string path = parent.gameObject.name + "/" + rt.gameObject.name;

            _tool.Execute(JsonNode.NewObject().Set("path", path).Set("preset", "stretch-stretch"));
            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("offsetMin", Vec2(0.0, 0.0))
                .Set("offsetMax", Vec2(0.0, 0.0)));

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", path)));

            // The whole reason 'rect' is reported alongside 'sizeDelta':
            // once the anchors stretch, sizeDelta is 0 while the element is
            // the full 400x300 of its parent.
            Assert.AreEqual(0.0, read["sizeDelta"]["x"].AsDouble(), 0.01, "sizeDelta.x");
            Assert.AreEqual(400.0, read["rect"]["width"].AsDouble(), 0.01, "rect.width");
            Assert.AreEqual(300.0, read["rect"]["height"].AsDouble(), 0.01, "rect.height");
            Assert.AreEqual(400.0, read["parentRect"]["width"].AsDouble(), 0.01, "parentRect.width");
        }

        [Test]
        public void Execute_ReadBack_ParentRectIsNull_WhenParentHasNoRectTransform()
        {
            RectTransform rt = MakeRect("NoParentRect");

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name)));

            Assert.IsTrue(read["parentRect"].IsNull, "parentRect must be null, not an empty object");
            Assert.GreaterOrEqual(read["warnings"].Count, 1, "the missing parent rect must be explained");
            StringAssert.Contains("no RectTransform", read["warnings"][0].AsString());
        }

        [Test]
        public void Execute_ReadBack_LayoutDriverIsNull_WhenNothingDrivesTheRect()
        {
            RectTransform rt = MakeRect("NoDriver");

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name)));

            Assert.IsTrue(read["layoutDriver"].IsNull);
        }

        [Test]
        public void Execute_StretchedAnchorsWithSizeDelta_WarnsThatSizeDeltaIsNotTheSize()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("StretchWarn", out parent);

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + rt.gameObject.name)
                .Set("preset", "stretch-stretch")
                .Set("sizeDelta", Vec2(10.0, 10.0))));

            bool warned = false;
            for (int i = 0; i < read["warnings"].Count; i++)
            {
                if (read["warnings"][i].AsString("").Contains("not the size"))
                {
                    warned = true;
                }
            }
            Assert.IsTrue(warned, "a sizeDelta write on stretched anchors must be explained");
        }

        [Test]
        public void Execute_SizeDeltaOnThePointAnchoredAxis_DoesNotWarn_EvenWhenTheOtherAxisStretches()
        {
            RectTransform parent;
            RectTransform rt = MakeChildOfSizedParent("AxisScopedWarn", out parent);
            string path = parent.gameObject.name + "/" + rt.gameObject.name;

            // stretch-left: stretched on Y, point-anchored on X. Writing only
            // sizeDelta.x sets a real width, so the "sizeDelta is not the
            // size" warning must NOT fire -- it is about the stretched axis.
            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("preset", "stretch-left")
                .Set("sizeDelta", JsonNode.NewObject().Set("x", 40.0))));

            for (int i = 0; i < read["warnings"].Count; i++)
            {
                StringAssert.DoesNotContain("not the size", read["warnings"][i].AsString(""));
            }
        }

        // -----------------------------------------------------------------
        // World Space canvas (VR / in-world panels)
        //
        // These use a REAL Canvas: com.unity.modules.ui is in both CI host
        // projects, unlike com.unity.ugui. Production still probes it by
        // name and reflection so the package survives a project that
        // stripped the module -- that constraint is not the test's.
        // -----------------------------------------------------------------

        private static RectTransform MakeWorldSpaceCanvas(string nameSuffix, float scale)
        {
            RectTransform rt = MakeRect(nameSuffix + "Canvas");
            Canvas canvas = rt.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            rt.sizeDelta = new Vector2(1000f, 600f);
            rt.localScale = new Vector3(scale, scale, scale);
            return rt;
        }

        [Test]
        public void Execute_WorldSpaceCanvas_ReportsRenderModeAndWorldSize()
        {
            // The VR idiom: a 1000x600 canvas at 0.001 scale is a 1m x 0.6m panel.
            RectTransform rt = MakeWorldSpaceCanvas("WorldRead", 0.001f);

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name)));

            Assert.AreEqual("WorldSpace", read["canvas"]["renderMode"].AsString());
            Assert.IsTrue(read["canvas"]["worldSpace"].AsBool(), "worldSpace flag");
            Assert.AreEqual("self", read["canvas"]["scope"].AsString());
            Assert.AreEqual(1.0, read["worldSize"]["x"].AsDouble(), 0.001, "1000 * 0.001 = 1m");
            Assert.AreEqual(0.6, read["worldSize"]["y"].AsDouble(), 0.001, "600 * 0.001 = 0.6m");
        }

        [Test]
        public void Execute_WorldSpaceRootCanvas_IsNotWarnedThatWritesWillNotStick()
        {
            // The bug this path exists to fix: the warning used to fire for
            // EVERY root Canvas. On a World Space canvas the write DOES stick,
            // so saying otherwise is worse than saying nothing.
            RectTransform rt = MakeWorldSpaceCanvas("WorldNoWarn", 0.001f);

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("sizeDelta", Vec2(800.0, 400.0))));

            for (int i = 0; i < read["warnings"].Count; i++)
            {
                StringAssert.DoesNotContain("will not stick", read["warnings"][i].AsString(""));
            }
            AssertVec2(new Vector2(800f, 400f), rt.sizeDelta, "sizeDelta actually written");
        }

        [Test]
        public void Execute_ScreenSpaceRootCanvas_IsWarnedThatWritesWillNotStick()
        {
            RectTransform rt = MakeRect("ScreenCanvas");
            Canvas canvas = rt.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("sizeDelta", Vec2(10.0, 10.0))));

            bool warned = false;
            for (int i = 0; i < read["warnings"].Count; i++)
            {
                if (read["warnings"][i].AsString("").Contains("will not stick"))
                {
                    warned = true;
                }
            }
            Assert.IsTrue(warned, "a screen-space root canvas rect IS recomputed by Unity");
            Assert.AreEqual("ScreenSpaceOverlay", read["canvas"]["renderMode"].AsString());
        }

        [Test]
        public void Execute_WorldSize_SetsSizeDeltaInMetres()
        {
            RectTransform rt = MakeWorldSpaceCanvas("WorldSizeWrite", 0.001f);

            // "Make this panel 1.2m x 0.8m" -- no lossyScale arithmetic by hand.
            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("worldSize", Vec2(1.2, 0.8)));

            AssertVec2(new Vector2(1200f, 800f), rt.sizeDelta, "sizeDelta from metres");
        }

        [Test]
        public void Execute_WorldSize_OnAStretchedChild_SubtractsTheAnchorSpan()
        {
            RectTransform parent = MakeWorldSpaceCanvas("WorldSizeChildParent", 0.001f);
            RectTransform child = MakeRect("WorldSizeChild");
            child.SetParent(parent, false);
            string path = parent.gameObject.name + "/" + child.gameObject.name;

            // Stretched to the full 1000x600 parent, then asked for 0.5m wide.
            _tool.Execute(JsonNode.NewObject().Set("path", path).Set("preset", "stretch-stretch"));
            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("offsetMin", Vec2(0.0, 0.0))
                .Set("offsetMax", Vec2(0.0, 0.0)));

            _tool.Execute(JsonNode.NewObject()
                .Set("path", path)
                .Set("worldSize", JsonNode.NewObject().Set("x", 0.5)));

            // 0.5m / 0.001 = 500 rect units; the anchors already span 1000, so
            // sizeDelta must be -500, not 500.
            Assert.AreEqual(-500f, child.sizeDelta.x, 0.01f, "sizeDelta.x");
            Assert.AreEqual(500f, child.rect.width, 0.01f, "resolved rect width");
        }

        [Test]
        public void Execute_WorldSize_PartialAxis_LeavesTheOtherAxisAlone()
        {
            RectTransform rt = MakeWorldSpaceCanvas("WorldSizePartial", 0.001f);

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("worldSize", JsonNode.NewObject().Set("x", 2.0)));

            AssertVec2(new Vector2(2000f, 600f), rt.sizeDelta, "only x resized");
        }

        [Test]
        public void Execute_WorldSizeWithSizeDelta_Throws_AndChangesNothing()
        {
            RectTransform rt = MakeWorldSpaceCanvas("WorldSizeClash", 0.001f);
            Vector2 before = rt.sizeDelta;

            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("worldSize", Vec2(1.0, 1.0))
                    .Set("sizeDelta", Vec2(10.0, 10.0)));
            });

            AssertVec2(before, rt.sizeDelta, "sizeDelta untouched");
        }

        [Test]
        public void Execute_WorldSize_WithCollapsedScale_Throws_BeforeWritingAnything()
        {
            RectTransform rt = MakeWorldSpaceCanvas("WorldSizeZeroScale", 0.001f);
            rt.localScale = new Vector3(0f, 1f, 1f);
            Vector2 before = rt.sizeDelta;

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", rt.gameObject.name)
                    .Set("preset", "top-left")
                    .Set("worldSize", Vec2(1.0, 1.0)));
            });
            StringAssert.Contains("lossyScale", ex.Message);
            // The preset in the same call must not have landed either.
            AssertVec2(before, rt.sizeDelta, "sizeDelta untouched");
            AssertVec2(new Vector2(0.5f, 0.5f), rt.anchorMin, "anchors untouched");
        }

        [Test]
        public void Execute_ChildOfWorldSpaceCanvas_ReportsTheAncestorCanvas()
        {
            RectTransform parent = MakeWorldSpaceCanvas("WorldAncestorParent", 0.001f);
            RectTransform child = MakeRect("WorldAncestorChild");
            child.SetParent(parent, false);

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject()
                .Set("path", parent.gameObject.name + "/" + child.gameObject.name)));

            Assert.AreEqual("ancestor", read["canvas"]["scope"].AsString());
            Assert.IsTrue(read["canvas"]["worldSpace"].AsBool());
        }

        [Test]
        public void Execute_NoCanvasAnywhere_ReportsCanvasNull()
        {
            RectTransform rt = MakeRect("NoCanvasAtAll");

            JsonNode read = ReadBack(_tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name)));

            Assert.IsTrue(read["canvas"].IsNull);
        }

        [Test]
        public void CanvasProbe_IsCanvasType_MatchesThroughBaseTypes_AndIgnoresOthers()
        {
            Assert.IsTrue(UapCanvasProbe.IsCanvasType(typeof(Canvas)));
            Assert.IsFalse(UapCanvasProbe.IsCanvasType(typeof(Transform)));
            Assert.IsFalse(UapCanvasProbe.IsCanvasType(null));
        }

        [Test]
        public void CanvasProbe_UnknownRenderMode_IsNeitherWorldNorKnownScreenSpace()
        {
            // null means "could not read", and must never be reported as
            // screen space -- that is what would resurrect the false warning.
            Assert.IsFalse(UapCanvasProbe.IsWorldSpace(null));
            Assert.IsFalse(UapCanvasProbe.IsKnownScreenSpace(null));
            Assert.IsTrue(UapCanvasProbe.IsKnownScreenSpace("ScreenSpaceCamera"));
            Assert.IsTrue(UapCanvasProbe.IsWorldSpace("WorldSpace"));
        }

        // -----------------------------------------------------------------
        // Addressing and failure modes
        // -----------------------------------------------------------------

        [Test]
        public void Execute_MissingRectTransform_Throws_AndPointsAtTransformSet()
        {
            var go = new GameObject(Prefix + "Plain");

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", go.name)
                    .Set("anchoredPosition", Vec2(1.0, 1.0)));
            });
            StringAssert.Contains("has no RectTransform", ex.Message);
            StringAssert.Contains("uap_transform_set", ex.Message);
        }

        [Test]
        public void Execute_MissingPath_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("anchoredPosition", Vec2(1.0, 1.0)));
            });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", "NoSuchObjectAtAll12345"));
            });
        }

        // -----------------------------------------------------------------
        // Undo
        // -----------------------------------------------------------------

        [Test]
        public void Execute_Undo_RestoresAnchoredPositionAndSizeDelta()
        {
            RectTransform rt = MakeRect("UndoPosSize");
            rt.anchoredPosition = new Vector2(1f, 2f);
            rt.sizeDelta = new Vector2(10f, 20f);
            Undo.IncrementCurrentGroup();

            _tool.Execute(JsonNode.NewObject()
                .Set("path", rt.gameObject.name)
                .Set("anchoredPosition", Vec2(99.0, 99.0))
                .Set("sizeDelta", Vec2(88.0, 88.0)));
            AssertVec2(new Vector2(99f, 99f), rt.anchoredPosition, "anchoredPosition applied");

            Undo.PerformUndo();

            AssertVec2(new Vector2(1f, 2f), rt.anchoredPosition, "anchoredPosition restored");
            AssertVec2(new Vector2(10f, 20f), rt.sizeDelta, "sizeDelta restored");
        }

        [Test]
        public void Execute_Undo_RestoresAnchorsAndPivot()
        {
            RectTransform rt = MakeRect("UndoAnchors");
            Undo.IncrementCurrentGroup();

            _tool.Execute(JsonNode.NewObject().Set("path", rt.gameObject.name).Set("preset", "top-left"));
            AssertVec2(new Vector2(0f, 1f), rt.anchorMin, "anchorMin applied");

            Undo.PerformUndo();

            // One RecordObject must cover all five fields, not just the last written one.
            AssertVec2(new Vector2(0.5f, 0.5f), rt.anchorMin, "anchorMin restored");
            AssertVec2(new Vector2(0.5f, 0.5f), rt.anchorMax, "anchorMax restored");
            AssertVec2(new Vector2(0.5f, 0.5f), rt.pivot, "pivot restored");
        }

        // -----------------------------------------------------------------
        // Layout-driver matchers (pure Type predicates -- uGUI is absent in CI)
        // -----------------------------------------------------------------

        private class ContentSizeFitter
        {
        }

        private sealed class FakeFitterSubclass : ContentSizeFitter
        {
        }

        private class LayoutGroup
        {
        }

        private sealed class HorizontalLayoutGroup : LayoutGroup
        {
        }

        [Test]
        public void LayoutDriver_IsSelfDriverType_MatchesByNameThroughBaseTypes()
        {
            Assert.IsTrue(UapRectTransformLayoutDriver.IsSelfDriverType(typeof(ContentSizeFitter)));
            Assert.IsTrue(UapRectTransformLayoutDriver.IsSelfDriverType(typeof(FakeFitterSubclass)),
                "a user subclass of ContentSizeFitter drives the rect just the same");
        }

        [Test]
        public void LayoutDriver_IsGroupDriverType_MatchesLayoutGroupSubclasses()
        {
            Assert.IsTrue(UapRectTransformLayoutDriver.IsGroupDriverType(typeof(LayoutGroup)));
            Assert.IsTrue(UapRectTransformLayoutDriver.IsGroupDriverType(typeof(HorizontalLayoutGroup)),
                "Horizontal/Vertical/Grid all derive from LayoutGroup, so one name covers them");
        }

        [Test]
        public void LayoutDriver_Matchers_IgnoreUnrelatedTypes()
        {
            Assert.IsFalse(UapRectTransformLayoutDriver.IsSelfDriverType(typeof(Transform)));
            Assert.IsFalse(UapRectTransformLayoutDriver.IsGroupDriverType(typeof(Transform)));
            Assert.IsFalse(UapRectTransformLayoutDriver.IsSelfDriverType(null));
            Assert.IsFalse(UapRectTransformLayoutDriver.IsGroupDriverType(null));
        }

        [Test]
        public void LayoutDriver_DrivenFieldsFor_NamesOnlyWhatTheDriverTakesOver()
        {
            CollectionAssert.AreEqual(new[] { "sizeDelta" },
                UapRectTransformLayoutDriver.DrivenFieldsFor("ContentSizeFitter"));
            CollectionAssert.Contains(UapRectTransformLayoutDriver.DrivenFieldsFor("LayoutGroup"),
                "anchoredPosition");
            CollectionAssert.IsEmpty(UapRectTransformLayoutDriver.DrivenFieldsFor("Rigidbody"));
        }
    }
}

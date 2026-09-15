using System;
using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One-call read-or-write for the ANCHOR-RELATIVE half of a uGUI
    /// object's layout -- anchorMin/anchorMax, pivot, anchoredPosition,
    /// sizeDelta, offsetMin/offsetMax -- by hierarchy path.
    ///
    /// uap_transform_set (2026-09-08 design note) covers every object's
    /// localPosition/rotation/scale and stops there; on a RectTransform
    /// those three say almost nothing, because Unity recomputes
    /// localPosition from the anchors every layout pass and the fields the
    /// Inspector actually shows are the six above. The only route to them
    /// was uap_property_set, one SerializedProperty per call
    /// (m_AnchorMin, m_AnchorMax, m_Pivot, m_AnchoredPosition,
    /// m_SizeDelta) -- the same call-count argument the 2026-09-08 note
    /// measured for Transform, on a field set nearly twice as large, and
    /// with layout math (anchor fractions to parent-space offsets) the
    /// agent had to do by hand. Doing it by hand is what makes an agent
    /// reach for dynamic code instead.
    ///
    /// Anchors and pivot are written RAW, exactly as the RectTransform
    /// setters behave -- unlike the Inspector's anchor widget, nothing
    /// silently compensates -- so 'keepRect' is offered for the common
    /// "re-anchor without moving it" case, and the read-back reports the
    /// resolved rect, the parent rect and any layout component that will
    /// overwrite the write. Omitting every write field turns this into a
    /// pure read. Undoable; prefab-stage guarded, same as
    /// uap_transform_set. Design note
    /// docs/design-notes/2026-09-15-rect-transform-layout-tool.md.
    /// </summary>
    public sealed class UapRectTransformSetTool : IUapTool
    {
        public string Name
        {
            get { return "uap_rect_transform_set"; }
        }

        public string Description
        {
            get
            {
                return "Sets (or reads) a uGUI RectTransform's anchors, pivot, anchoredPosition,"
                    + " sizeDelta and offsets in one call, by hierarchy path -- the anchor-relative"
                    + " layout fields uap_transform_set does NOT touch (it writes localPosition /"
                    + " rotation / scale, which Unity recomputes from the anchors on a RectTransform)."
                    + " Anchors and pivot are written raw, so changing them alone moves the visible"
                    + " rect: pass keepRect:true to re-anchor WITHOUT moving it, or give the intended"
                    + " anchoredPosition/sizeDelta (or offsetMin/offsetMax) in the same call. 'preset'"
                    + " takes the 16 standard anchor corners (top-left ... stretch-stretch) instead of"
                    + " hand-computed fractions. Omit every write field to just read the current"
                    + " layout, including the resolved rect size (not the same as sizeDelta once the"
                    + " anchors stretch), the parent rect, and any LayoutGroup / ContentSizeFitter /"
                    + " AspectRatioFitter that will overwrite what you write. For a World Space canvas"
                    + " (VR panels, in-world UI) it also reports the governing canvas and its render mode"
                    + " and gives the element's size in world units, which 'worldSize' can set directly in"
                    + " metres instead of canvas-local sizeDelta.";
            }
        }

        public string Module
        {
            get { return "core"; }
        }

        public bool Undoable
        {
            get { return true; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        /// <summary>
        /// The 16 cells of Unity's anchor-preset popup, named
        /// "&lt;vertical&gt;-&lt;horizontal&gt;". Each row is
        /// {name, anchorMinX, anchorMinY, anchorMaxX, anchorMaxY, pivotX, pivotY}:
        /// the horizontal axis contributes left (0,0,0) / center
        /// (0.5,0.5,0.5) / right (1,1,1) / stretch (0,1,0.5), the vertical
        /// axis bottom (0,0,0) / middle (0.5,0.5,0.5) / top (1,1,1) /
        /// stretch (0,1,0.5). Naming the VERTICAL half first matches the
        /// popup's rows and leaves no doubt which axis a "stretch" applies
        /// to (which "left-stretch" vs "stretch-left" would).
        /// </summary>
        private static readonly float[][] PresetValues =
        {
            //          aMinX aMinY aMaxX aMaxY pivX  pivY
            new[] { 0f, 1f, 0f, 1f, 0f, 1f },       // top-left
            new[] { 0.5f, 1f, 0.5f, 1f, 0.5f, 1f }, // top-center
            new[] { 1f, 1f, 1f, 1f, 1f, 1f },       // top-right
            new[] { 0f, 1f, 1f, 1f, 0.5f, 1f },     // top-stretch
            new[] { 0f, 0.5f, 0f, 0.5f, 0f, 0.5f },       // middle-left
            new[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f }, // middle-center
            new[] { 1f, 0.5f, 1f, 0.5f, 1f, 0.5f },       // middle-right
            new[] { 0f, 0.5f, 1f, 0.5f, 0.5f, 0.5f },     // middle-stretch
            new[] { 0f, 0f, 0f, 0f, 0f, 0f },       // bottom-left
            new[] { 0.5f, 0f, 0.5f, 0f, 0.5f, 0f }, // bottom-center
            new[] { 1f, 0f, 1f, 0f, 1f, 0f },       // bottom-right
            new[] { 0f, 0f, 1f, 0f, 0.5f, 0f },     // bottom-stretch
            new[] { 0f, 0f, 0f, 1f, 0f, 0.5f },       // stretch-left
            new[] { 0.5f, 0f, 0.5f, 1f, 0.5f, 0.5f }, // stretch-center
            new[] { 1f, 0f, 1f, 1f, 1f, 0.5f },       // stretch-right
            new[] { 0f, 0f, 1f, 1f, 0.5f, 0.5f },     // stretch-stretch
        };

        private static readonly string[] PresetNames =
        {
            "top-left", "top-center", "top-right", "top-stretch",
            "middle-left", "middle-center", "middle-right", "middle-stretch",
            "bottom-left", "bottom-center", "bottom-right", "bottom-stretch",
            "stretch-left", "stretch-center", "stretch-right", "stretch-stretch",
        };

        public JsonNode InputSchema
        {
            get
            {
                JsonNode presetEnum = JsonNode.NewArray();
                for (int i = 0; i < PresetNames.Length; i++)
                {
                    presetEnum.Add(PresetNames[i]);
                }
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("path", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the target GameObject. It must have a RectTransform -- use uap_transform_set for plain-Transform objects, and for rotation/scale of this one."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("preset", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", presetEnum)
                            .Set("description", "Standard anchor corner, named '<vertical>-<horizontal>': top/middle/bottom/stretch crossed with left/center/right/stretch. Sets anchorMin, anchorMax AND pivot ('stretch' on an axis spans the parent on that axis). It does not move or resize the rect to compensate -- pass keepRect:true for that, or give the position/size explicitly. Cannot be combined with anchorMin, anchorMax or pivot."))
                        .Set("keepPivot", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "With 'preset', keep the current pivot instead of setting the preset's own pivot. Rejected without 'preset'."))
                        .Set("keepRect", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Re-anchor WITHOUT moving the element: after the anchor/pivot change, the offsets are recomputed so the rect stays exactly where and as big as it was (what the Inspector's anchor widget does for you). This is almost always what you want when changing anchors on an element that is already placed. Needs the parent to have a RectTransform. Rejected unless 'preset', 'anchorMin', 'anchorMax' or 'pivot' is also given; any explicit anchoredPosition/sizeDelta/offset in the same call is applied afterwards and wins."))
                        .Set("anchorMin", Vec2Schema("New anchorMin (normalized 0..1 of the parent rect; (0,0) is bottom-left). Partial -- give only the axes you want to change; the rest keep their current value. Written raw, so the rect moves unless you pass keepRect:true or a compensating position/size."))
                        .Set("anchorMax", Vec2Schema("New anchorMax (normalized 0..1 of the parent rect). Partial, same merge rule as 'anchorMin'. anchorMin == anchorMax on an axis means point-anchored on that axis; anchorMin < anchorMax means stretched."))
                        .Set("pivot", Vec2Schema("New pivot (normalized within this rect; (0.5,0.5) is the center, (0,1) the top-left corner). Partial, same merge rule. Written raw -- the rect shifts, because every edge is measured from the pivot."))
                        .Set("anchoredPosition", Vec2Schema("New anchoredPosition: the offset, in parent-space units, from the anchor point to the pivot. Partial, same merge rule. Cannot be combined with offsetMin/offsetMax."))
                        .Set("sizeDelta", Vec2Schema("New sizeDelta. On a point-anchored axis this IS the size in pixels; on a stretched axis it is the size ADDED to the span the anchors already imply, so 0 means 'exactly fill the anchors' and negative values inset. Partial, same merge rule. Cannot be combined with offsetMin/offsetMax."))
                        .Set("offsetMin", Vec2Schema("New offsetMin: the left/bottom edge relative to anchorMin -- the usual left and bottom margins (y grows upward, so a bottom margin is positive). Partial, same merge rule. The same four numbers as anchoredPosition+sizeDelta seen differently, so it cannot be combined with either."))
                        .Set("offsetMax", Vec2Schema("New offsetMax: the right/top edge relative to anchorMax. NEGATIVE values are the usual right and top margins (offsetMax x of -10 means 10 in from the right edge). Partial, same merge rule. Cannot be combined with anchoredPosition/sizeDelta."))
                        .Set("worldSize", Vec2Schema("New size in WORLD units -- metres for a World Space canvas, which is how VR panels are actually specified ('make this 1.2m wide'). The tool divides by the element's lossyScale and subtracts the span the anchors already imply, so it lands the right sizeDelta whether the element is point-anchored or stretched. Partial, same merge rule. Cannot be combined with sizeDelta or offsetMin/offsetMax. Read 'worldSize' back to see the current value.")))
                    .Set("required", JsonNode.NewArray().Add("path"))
                    .Set("additionalProperties", false);
            }
        }

        private static JsonNode Vec2Schema(string description)
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("description", description)
                .Set("properties", JsonNode.NewObject()
                    .Set("x", JsonNode.NewObject().Set("type", "number"))
                    .Set("y", JsonNode.NewObject().Set("type", "number")))
                .Set("additionalProperties", false);
        }

        public JsonNode Execute(JsonNode input)
        {
            string path = input["path"].AsString(null);
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("'path' is required.");
            }

            JsonNode anchorMinNode = input["anchorMin"];
            JsonNode anchorMaxNode = input["anchorMax"];
            JsonNode pivotNode = input["pivot"];
            JsonNode anchoredPositionNode = input["anchoredPosition"];
            JsonNode sizeDeltaNode = input["sizeDelta"];
            JsonNode offsetMinNode = input["offsetMin"];
            JsonNode offsetMaxNode = input["offsetMax"];
            JsonNode worldSizeNode = input["worldSize"];
            bool hasAnchorMin = anchorMinNode != null && anchorMinNode.IsObject;
            bool hasAnchorMax = anchorMaxNode != null && anchorMaxNode.IsObject;
            bool hasPivot = pivotNode != null && pivotNode.IsObject;
            bool hasAnchoredPosition = anchoredPositionNode != null && anchoredPositionNode.IsObject;
            bool hasSizeDelta = sizeDeltaNode != null && sizeDeltaNode.IsObject;
            bool hasOffsetMin = offsetMinNode != null && offsetMinNode.IsObject;
            bool hasOffsetMax = offsetMaxNode != null && offsetMaxNode.IsObject;
            bool hasWorldSize = worldSizeNode != null && worldSizeNode.IsObject;

            string preset = input["preset"].AsString(null);
            bool hasPreset = !string.IsNullOrEmpty(preset);
            bool keepPivot = input["keepPivot"].AsBool(false);
            bool keepRect = input["keepRect"].AsBool(false);

            // Everything that can be judged from the arguments alone is
            // judged BEFORE the target is touched, so a rejected call can
            // never leave a half-written rect behind.
            if (hasPreset && (hasAnchorMin || hasAnchorMax || hasPivot))
            {
                throw new ArgumentException("'preset' already sets anchorMin, anchorMax and pivot --"
                    + " pass 'preset', or pass 'anchorMin'/'anchorMax'/'pivot', not both.");
            }
            if (input.HasKey("keepPivot") && !hasPreset)
            {
                throw new ArgumentException("'keepPivot' only means anything together with 'preset'.");
            }
            if (hasWorldSize && (hasSizeDelta || hasOffsetMin || hasOffsetMax))
            {
                throw new ArgumentException("'worldSize' sets the same thing as 'sizeDelta' (and as"
                    + " 'offsetMin'/'offsetMax'), only in world units -- pass one of them, not several.");
            }
            if ((hasAnchoredPosition || hasSizeDelta) && (hasOffsetMin || hasOffsetMax))
            {
                throw new ArgumentException("'offsetMin'/'offsetMax' and 'anchoredPosition'/'sizeDelta' are"
                    + " the same four numbers expressed two ways -- pass one pair or the other, not both"
                    + " in the same call.");
            }
            bool changesAnchorOrPivot = hasPreset || hasAnchorMin || hasAnchorMax || hasPivot;
            if (input.HasKey("keepRect") && !changesAnchorOrPivot)
            {
                throw new ArgumentException("'keepRect' preserves the rect ACROSS an anchor/pivot change,"
                    + " so it only means anything together with 'preset', 'anchorMin', 'anchorMax' or"
                    + " 'pivot'.");
            }

            Vector2 presetMin = Vector2.zero;
            Vector2 presetMax = Vector2.zero;
            Vector2 presetPivot = Vector2.zero;
            if (hasPreset && !TryResolvePreset(preset, out presetMin, out presetMax, out presetPivot))
            {
                throw new ArgumentException("Unknown preset: " + preset + " (expected one of: "
                    + string.Join(", ", PresetNames) + ").");
            }

            string error;
            GameObject go = UapAddressing.ResolveHierarchyPath(input["scene"].AsString(null), path, out error);
            if (go == null)
            {
                throw new InvalidOperationException(error);
            }
            if (!UapPrefabStageGuard.Check(go, out error))
            {
                throw new InvalidOperationException(error);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null)
            {
                throw new InvalidOperationException("GameObject at path '" + path + "' has no RectTransform"
                    + " (it has a plain Transform). uap_rect_transform_set is for uGUI objects under a"
                    + " Canvas -- use uap_transform_set for everything else.");
            }
            RectTransform parentRect = rt.parent as RectTransform;

            if (keepRect && parentRect == null)
            {
                throw new InvalidOperationException("'keepRect' needs the parent's rect to work out the"
                    + " compensating offsets, and the parent of '" + path + "' has no RectTransform."
                    + " Drop keepRect and pass the intended anchoredPosition/sizeDelta instead.");
            }

            // Checked here, with the other refusals, rather than in the write
            // block: nothing this tool writes changes lossyScale, so it is
            // knowable up front, and a throw after Undo.RecordObject would
            // leave the half-written rect every other rejection avoids.
            if (hasWorldSize)
            {
                Vector2 worldScale = LossyScale2D(rt);
                if (worldScale.x == 0f || worldScale.y == 0f)
                {
                    throw new InvalidOperationException("'worldSize' cannot be applied to '" + path
                        + "': its lossyScale is 0 on an axis, so no canvas-local size maps to the"
                        + " requested world size. Fix the scale on this object or a parent first.");
                }
            }

            var warnings = new List<string>();
            bool hasAnyWrite = changesAnchorOrPivot || hasAnchoredPosition || hasSizeDelta
                || hasOffsetMin || hasOffsetMax || hasWorldSize;

            if (hasAnyWrite)
            {
                Undo.RecordObject(rt, "uap_rect_transform_set");

                // The rect's own corners in the PARENT's local space, taken
                // before the anchors move, are what keepRect restores. They
                // are independent of pivot, so one snapshot covers an
                // anchor change and a pivot change in the same call.
                Rect parentRectRect = parentRect != null ? parentRect.rect : new Rect();
                Vector2 keptMin = Vector2.zero;
                Vector2 keptMax = Vector2.zero;
                if (keepRect)
                {
                    keptMin = AnchorPointInParent(parentRectRect, rt.anchorMin) + rt.offsetMin;
                    keptMax = AnchorPointInParent(parentRectRect, rt.anchorMax) + rt.offsetMax;
                }

                if (hasPreset)
                {
                    rt.anchorMin = presetMin;
                    rt.anchorMax = presetMax;
                    if (!keepPivot)
                    {
                        rt.pivot = presetPivot;
                    }
                }
                else
                {
                    if (hasAnchorMin)
                    {
                        rt.anchorMin = MergeVector2(anchorMinNode, rt.anchorMin);
                    }
                    if (hasAnchorMax)
                    {
                        rt.anchorMax = MergeVector2(anchorMaxNode, rt.anchorMax);
                    }
                    if (hasPivot)
                    {
                        rt.pivot = MergeVector2(pivotNode, rt.pivot);
                    }
                }

                if (keepRect)
                {
                    rt.offsetMin = keptMin - AnchorPointInParent(parentRectRect, rt.anchorMin);
                    rt.offsetMax = keptMax - AnchorPointInParent(parentRectRect, rt.anchorMax);
                }

                if (hasAnchoredPosition)
                {
                    rt.anchoredPosition = MergeVector2(anchoredPositionNode, rt.anchoredPosition);
                }
                if (hasSizeDelta)
                {
                    rt.sizeDelta = MergeVector2(sizeDeltaNode, rt.sizeDelta);
                }
                if (hasWorldSize)
                {
                    // Applied here, after the anchors are final, because the
                    // sizeDelta that yields a given rect depends on the span
                    // the anchors imply.
                    Vector2 scale = LossyScale2D(rt);
                    Vector2 targetWorld = MergeVector2(worldSizeNode, WorldSizeOf(rt));
                    Vector2 targetRect = new Vector2(targetWorld.x / scale.x, targetWorld.y / scale.y);
                    rt.sizeDelta = targetRect - AnchorSpan(rt, parentRect);
                }
                if (hasOffsetMin || hasOffsetMax)
                {
                    // Snapshot both edges before writing either: each setter
                    // leaves the OTHER edge where it is, so min-then-max
                    // lands both requested values.
                    Vector2 currentOffsetMin = rt.offsetMin;
                    Vector2 currentOffsetMax = rt.offsetMax;
                    Vector2 targetOffsetMin = hasOffsetMin
                        ? MergeVector2(offsetMinNode, currentOffsetMin) : currentOffsetMin;
                    Vector2 targetOffsetMax = hasOffsetMax
                        ? MergeVector2(offsetMaxNode, currentOffsetMax) : currentOffsetMax;
                    rt.offsetMin = targetOffsetMin;
                    rt.offsetMax = targetOffsetMax;
                }

                EditorSceneManager.MarkSceneDirty(go.scene);
            }

            Component canvas = UapCanvasProbe.FindGoverningCanvas(rt);
            string renderMode = UapCanvasProbe.ReadRenderMode(canvas);
            bool worldSpace = UapCanvasProbe.IsWorldSpace(renderMode);
            JsonNode layoutDriver = DescribeLayoutDriver(go, rt, hasAnyWrite, warnings);
            // Per AXIS, not per call: sizeDelta stays the real size on a
            // point-anchored axis even when the other one is stretched.
            AddStructuralWarnings(go, rt, parentRect,
                hasSizeDelta && sizeDeltaNode.HasKey("x"),
                hasSizeDelta && sizeDeltaNode.HasKey("y"),
                canvas, renderMode, worldSpace, hasWorldSize, warnings);

            JsonNode result = JsonNode.NewObject()
                .Set("path", path)
                .Set("anchorMin", Vector2ToNode(rt.anchorMin))
                .Set("anchorMax", Vector2ToNode(rt.anchorMax))
                .Set("pivot", Vector2ToNode(rt.pivot))
                .Set("anchoredPosition", Vector2ToNode(rt.anchoredPosition))
                .Set("sizeDelta", Vector2ToNode(rt.sizeDelta))
                .Set("offsetMin", Vector2ToNode(rt.offsetMin))
                .Set("offsetMax", Vector2ToNode(rt.offsetMax))
                .Set("rect", RectToNode(rt.rect))
                .Set("parentRect", parentRect != null ? RectToNode(parentRect.rect) : JsonNode.Null)
                .Set("canvas", DescribeCanvas(canvas, rt, renderMode))
                .Set("worldSize", Vector2ToNode(WorldSizeOf(rt)))
                .Set("layoutDriver", layoutDriver)
                .Set("warnings", StringsToNode(warnings));

            return UapToolResults.Text(JsonWriter.Write(result));
        }

        /// <summary>
        /// The anchor point, in the parent rect's own local coordinates,
        /// that a normalized anchor fraction names. Offsets are measured
        /// from here, which is what makes keepRect's restore exact.
        /// </summary>
        private static Vector2 AnchorPointInParent(Rect parentRect, Vector2 anchor)
        {
            return new Vector2(
                parentRect.xMin + anchor.x * parentRect.width,
                parentRect.yMin + anchor.y * parentRect.height);
        }

        /// <summary>
        /// Finds the layout component that will overwrite this rect (a
        /// ContentSizeFitter/AspectRatioFitter on the object itself beats a
        /// LayoutGroup on the parent), reports it, and -- when the call
        /// actually wrote something -- warns that the write will not last.
        /// </summary>
        private static JsonNode DescribeLayoutDriver(GameObject go, RectTransform rt, bool wrote,
            List<string> warnings)
        {
            string matchedName;
            string scope = "self";
            Component driver = UapRectTransformLayoutDriver.FindSelfDriver(go, out matchedName);
            if (driver == null && rt.parent != null)
            {
                driver = UapRectTransformLayoutDriver.FindGroupDriver(rt.parent.gameObject, out matchedName);
                scope = "parent";
            }
            if (driver == null)
            {
                return JsonNode.Null;
            }

            string driverType = driver.GetType().Name;
            string driverPath = UapAddressing.DescribeHierarchyPath(driver.transform);
            string[] drivenFields = UapRectTransformLayoutDriver.DrivenFieldsFor(matchedName);
            JsonNode drives = JsonNode.NewArray();
            for (int i = 0; i < drivenFields.Length; i++)
            {
                drives.Add(drivenFields[i]);
            }
            if (wrote && drivenFields.Length > 0)
            {
                warnings.Add(driverType + " on '" + driverPath + "' drives " + string.Join("/", drivenFields)
                    + " on this RectTransform: the write was applied, but the next layout rebuild will"
                    + " overwrite it. Change that component's settings, or add a LayoutElement to this"
                    + " object, instead.");
            }
            return JsonNode.NewObject()
                .Set("type", driverType)
                .Set("path", driverPath)
                .Set("scope", scope)
                .Set("drives", drives);
        }

        /// <summary>
        /// Warnings that describe the OBJECT rather than the call, so they
        /// are reported on a pure read too: no parent rect for the anchors
        /// to resolve against, a root Canvas whose rect Unity owns, and
        /// sizeDelta written on an axis where it is not the size (judged
        /// per axis -- the other axis may be point-anchored and fine).
        /// </summary>
        private static void AddStructuralWarnings(GameObject go, RectTransform rt, RectTransform parentRect,
            bool wroteSizeDeltaX, bool wroteSizeDeltaY, Component canvas, string renderMode,
            bool worldSpace, bool wroteWorldSize, List<string> warnings)
        {
            if (parentRect == null)
            {
                warnings.Add("The parent has no RectTransform, so there is no parent rect for the anchors"
                    + " to resolve against: 'parentRect' is null and the anchor fractions do not mean what"
                    + " they normally mean.");
                bool isOwnCanvas = canvas != null && canvas.gameObject == go;
                if (isOwnCanvas && UapCanvasProbe.IsKnownScreenSpace(renderMode))
                {
                    // Only for a canvas KNOWN to be screen space. A World Space
                    // canvas -- every VR panel -- is an authored object whose
                    // rect sticks, and telling its author otherwise was the
                    // single most misleading thing this tool could say.
                    warnings.Add("This is a root Canvas rendering in " + renderMode + ", so Unity"
                        + " recomputes its rect from the screen every frame: writes to the anchors,"
                        + " sizeDelta or anchoredPosition here will not stick. Set the Canvas to World"
                        + " Space first if you meant to author an in-world panel.");
                }
                else if (isOwnCanvas && string.IsNullOrEmpty(renderMode))
                {
                    warnings.Add("This object carries a Canvas whose render mode could not be read."
                        + " If it renders in screen space, Unity recomputes its rect from the screen"
                        + " every frame and writes here will not stick.");
                }
            }
            if (wroteWorldSize && canvas != null && UapCanvasProbe.IsKnownScreenSpace(renderMode))
            {
                warnings.Add("'worldSize' was applied, but the governing Canvas renders in " + renderMode
                    + " -- world units are not what a viewer sees there. It is meaningful for a World"
                    + " Space canvas (VR and in-world panels); on a screen-space one use 'sizeDelta'.");
            }
            if (worldSpace)
            {
                Vector2 scale = LossyScale2D(rt);
                if (scale.x == 0f || scale.y == 0f)
                {
                    warnings.Add("This element is under a World Space canvas but its lossyScale is 0 on"
                        + " an axis, so it has no size in the world and cannot be seen. A parent's scale"
                        + " is collapsed.");
                }
            }
            bool stretchedX = rt.anchorMin.x != rt.anchorMax.x;
            bool stretchedY = rt.anchorMin.y != rt.anchorMax.y;
            if ((wroteSizeDeltaX && stretchedX) || (wroteSizeDeltaY && stretchedY))
            {
                warnings.Add("anchorMin and anchorMax differ on an axis you wrote 'sizeDelta' for, so"
                    + " on that axis sizeDelta is not the size -- it is added to the span the anchors"
                    + " already imply. Use offsetMin/offsetMax if you meant margins.");
            }
        }

        /// <summary>
        /// The governing Canvas as reported to the caller: which object it is
        /// on, its render mode, and whether that mode is World Space -- the
        /// one bit that decides if 'worldSize' and metres mean anything here.
        /// Null when the element is not under a Canvas at all.
        /// </summary>
        private static JsonNode DescribeCanvas(Component canvas, RectTransform rt, string renderMode)
        {
            if (canvas == null)
            {
                return JsonNode.Null;
            }
            return JsonNode.NewObject()
                .Set("path", UapAddressing.DescribeHierarchyPath(canvas.transform))
                .Set("type", canvas.GetType().Name)
                .Set("renderMode", string.IsNullOrEmpty(renderMode) ? JsonNode.Null : JsonNode.Of(renderMode))
                .Set("worldSpace", UapCanvasProbe.IsWorldSpace(renderMode))
                .Set("scope", canvas.gameObject == rt.gameObject ? "self" : "ancestor");
        }

        /// <summary>
        /// The element's size in world units: the resolved rect scaled by
        /// lossyScale. For a World Space canvas these are metres, which is
        /// what a VR panel is actually specified in. It ignores rotation
        /// skew, exactly as Unity's own lossyScale does.
        /// </summary>
        private static Vector2 WorldSizeOf(RectTransform rt)
        {
            Vector2 scale = LossyScale2D(rt);
            Rect r = rt.rect;
            return new Vector2(r.width * scale.x, r.height * scale.y);
        }

        private static Vector2 LossyScale2D(RectTransform rt)
        {
            Vector3 scale = rt.lossyScale;
            return new Vector2(scale.x, scale.y);
        }

        /// <summary>
        /// The size the anchors alone already imply -- (anchorMax - anchorMin)
        /// times the parent rect. rect.size is this plus sizeDelta, so it is
        /// what a desired rect size must have subtracted to get a sizeDelta.
        /// Zero when there is no parent rect, which makes sizeDelta the size.
        /// </summary>
        private static Vector2 AnchorSpan(RectTransform rt, RectTransform parentRect)
        {
            if (parentRect == null)
            {
                return Vector2.zero;
            }
            Rect pr = parentRect.rect;
            return new Vector2(
                (rt.anchorMax.x - rt.anchorMin.x) * pr.width,
                (rt.anchorMax.y - rt.anchorMin.y) * pr.height);
        }

        private static bool TryResolvePreset(string name, out Vector2 min, out Vector2 max, out Vector2 pivot)
        {
            for (int i = 0; i < PresetNames.Length; i++)
            {
                if (string.Equals(name, PresetNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    float[] v = PresetValues[i];
                    min = new Vector2(v[0], v[1]);
                    max = new Vector2(v[2], v[3]);
                    pivot = new Vector2(v[4], v[5]);
                    return true;
                }
            }
            min = Vector2.zero;
            max = Vector2.zero;
            pivot = Vector2.zero;
            return false;
        }

        private static Vector2 MergeVector2(JsonNode node, Vector2 current)
        {
            return new Vector2(
                (float)node["x"].AsDouble(current.x),
                (float)node["y"].AsDouble(current.y));
        }

        private static JsonNode Vector2ToNode(Vector2 v)
        {
            return JsonNode.NewObject()
                .Set("x", (double)v.x)
                .Set("y", (double)v.y);
        }

        private static JsonNode RectToNode(Rect r)
        {
            return JsonNode.NewObject()
                .Set("x", (double)r.x)
                .Set("y", (double)r.y)
                .Set("width", (double)r.width)
                .Set("height", (double)r.height);
        }

        private static JsonNode StringsToNode(List<string> values)
        {
            JsonNode array = JsonNode.NewArray();
            for (int i = 0; i < values.Count; i++)
            {
                array.Add(values[i]);
            }
            return array;
        }
    }
}

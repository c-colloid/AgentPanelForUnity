using System;
using System.Reflection;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Clicks, long-presses and drags uGUI widgets in the running game
    /// through its EventSystem (design note docs/design-notes/
    /// 2026-09-22-game-input-tools.md section 3): the pointer events a
    /// real mouse would raise (pointerDown / pointerUp / pointerClick,
    /// beginDrag / drag / endDrag / drop), dispatched with ExecuteEvents to
    /// the objects a real raycast finds. It is the other half of "test the
    /// change yourself" next to uap_game_key: menus, buttons and sliders
    /// are how most games are driven, and a Canvas button responds to this
    /// whether the project uses the legacy Input Manager or the Input
    /// System, because uGUI handlers sit above both.
    ///
    /// Two ways to say where: coordinates as a screenshot is read (origin
    /// top-left, in the Game View's render resolution, or 0..1 fractions
    /// with normalized:true), or a hierarchy path in 'target', which
    /// dispatches to that widget directly -- the recommended form, since a
    /// path from uap_query_hierarchy is exact where a pixel guessed from a
    /// downscaled screenshot is not, and it reaches a widget a transparent
    /// overlay would otherwise swallow.
    ///
    /// Pollable because a long-press holds and a drag moves across
    /// frames; a click completes in its first Poll.
    /// </summary>
    public sealed class UapGamePointerTool : IUapPollableTool, IUapAbandonableTool
    {
        public const string ToolName = "uap_game_pointer";

        private const string PointerDown = "pointerDown";
        private const string PointerUp = "pointerUp";
        private const string PointerClick = "pointerClick";
        private const string InitializePotentialDrag = "initializePotentialDrag";
        private const string BeginDrag = "beginDrag";
        private const string Drag = "drag";
        private const string EndDrag = "endDrag";
        private const string Drop = "drop";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Clicks, long-presses or drags a uGUI widget (Canvas button, toggle, slider, draggable)"
                    + " in the RUNNING game (Play Mode) by sending the pointer events its EventSystem would --"
                    + " so you can press Start, open a menu or move a slider yourself and then check the"
                    + " result with uap_console_logs / uap_editor_screenshot. Works with the legacy Input"
                    + " Manager and the Input System alike. Say where with target (a hierarchy path such as"
                    + " Canvas/Panel/StartButton, from uap_query_hierarchy; recommended, exact, ignores"
                    + " raycast blockers) or with x/y in pixels from the TOP-left of the Game View at its"
                    + " render resolution (uap_game_view_size), or normalized:true with 0..1 fractions."
                    + " action: click (default) / long_press (duration seconds) / drag (from_x, from_y ->"
                    + " x, y at speed px/s; drop_target optional). Requires Play Mode, not paused, and an"
                    + " EventSystem in the scene. For keyboard input use uap_game_key; 3D objects without"
                    + " uGUI handlers do not receive these events.";
            }
        }

        public string Module
        {
            get { return "editor"; }
        }

        public bool Undoable
        {
            get { return false; }
        }

        public bool ReadOnly
        {
            get { return false; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("action", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("click").Add("long_press").Add("drag"))
                            .Set("description", "'click' (the default): pointerDown, pointerUp, pointerClick."
                                + " 'long_press': pointerDown, hold 'duration' seconds, pointerUp (no click)."
                                + " 'drag': beginDrag at from_x/from_y, drag events to x/y at 'speed', endDrag"
                                + " and drop."))
                        .Set("target", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the widget to hit directly (e.g."
                                + " Canvas/Panel/StartButton). With it, x/y default to the widget's centre"
                                + " and no raycast is done."))
                        .Set("x", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Pointer x from the LEFT edge of the Game View, in render"
                                + " pixels (or 0..1 with normalized:true). For drag: the destination."))
                        .Set("y", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Pointer y from the TOP edge of the Game View, in render"
                                + " pixels (or 0..1 with normalized:true). For drag: the destination."))
                        .Set("from_x", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "drag only: where the drag starts (same units as x)."
                                + " Defaults to the target's centre when target is given."))
                        .Set("from_y", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "drag only: where the drag starts (same units as y)."))
                        .Set("normalized", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true: x/y/from_x/from_y are fractions of the view from 0"
                                + " to 1 instead of pixels (robust when your screenshot was resized)."))
                        .Set("button", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("left").Add("right").Add("middle"))
                            .Set("description", "Mouse button for click / long_press (default left)."))
                        .Set("duration", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "long_press only: seconds to hold, 0 to "
                                + UapGameInputPolicy.MaxHoldSeconds + " (default "
                                + UapGamePointerPolicy.DefaultLongPressSeconds + ")."))
                        .Set("speed", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "drag only: pixels per second (default "
                                + UapGamePointerPolicy.DefaultDragPixelsPerSecond + "; 0 = instant, one"
                                + " drag event)."))
                        .Set("drop_target", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "drag only: hierarchy path of the object to receive the"
                                + " drop event, instead of whatever is under the destination.")))
                    .Set("additionalProperties", false);
            }
        }

        /// <summary>Synchronous fallback for callers that bypass the dispatcher (DirectUapToolExecutor); production dispatch polls.</summary>
        public JsonNode Execute(JsonNode input)
        {
            object state = null;
            JsonNode result;
            while (!Poll(input, ref state, out result))
            {
                Thread.Sleep(10);
            }
            return result;
        }

        public bool Poll(JsonNode input, ref object stateObj, out JsonNode result)
        {
            var state = stateObj as PointerState;
            if (state == null)
            {
                state = Begin(input);
                stateObj = state;
                if (state.Phase == Phase.Done)
                {
                    result = state.Result;
                    return true;
                }
                result = null;
                return false;
            }
            return Continue(state, out result);
        }

        private PointerState Begin(JsonNode input)
        {
            var state = new PointerState
            {
                Action = UapGamePointerPolicy.ParseAction(input["action"].AsString(null)),
                Button = UapGamePointerPolicy.ParseButton(input["button"].AsString(null)),
                Normalized = input["normalized"].AsBool(false),
                TargetPath = (input["target"].AsString(null) ?? string.Empty).Trim(),
                DropTargetPath = (input["drop_target"].AsString(null) ?? string.Empty).Trim()
            };
            string argumentError;
            if (state.Action == UapGamePointerAction.LongPress)
            {
                state.HoldSeconds = input["duration"].AsDouble(UapGamePointerPolicy.DefaultLongPressSeconds);
                if (!UapGameInputPolicy.ValidateHold(state.HoldSeconds, "duration", out argumentError))
                {
                    throw new ArgumentException(argumentError);
                }
            }
            if (state.Action == UapGamePointerAction.Drag)
            {
                state.Speed = input["speed"].AsDouble(UapGamePointerPolicy.DefaultDragPixelsPerSecond);
                if (!UapGamePointerPolicy.ValidateSpeed(state.Speed, out argumentError))
                {
                    throw new ArgumentException(argumentError);
                }
            }

            string stateError;
            if (!UapGameInputPolicy.CheckPlayState(ReadPlayState(), false, out stateError))
            {
                throw new InvalidOperationException(stateError);
            }
            string eventSystemError;
            state.EventSystem = UapUguiEventAccess.CurrentEventSystem(out eventSystemError);
            if (state.EventSystem == null)
            {
                throw new InvalidOperationException(eventSystemError);
            }
            Vector2 viewSize = Handles.GetMainGameViewSize();
            state.ViewWidth = viewSize.x;
            state.ViewHeight = viewSize.y;

            GameObject target = null;
            if (state.TargetPath.Length > 0)
            {
                target = ResolveActive(state.TargetPath, "target");
            }

            // Where the pointer goes down: x/y (click, long_press) or from_x/from_y (drag),
            // else the target widget's centre.
            bool isDrag = state.Action == UapGamePointerAction.Drag;
            string downX = isDrag ? "from_x" : "x";
            string downY = isDrag ? "from_y" : "y";
            Vector2 downScreen;
            if (!input[downX].IsNull || !input[downY].IsNull)
            {
                downScreen = ToScreen(state, input[downX].AsDouble(0), input[downY].AsDouble(0), downX, downY);
            }
            else if (target != null)
            {
                downScreen = WidgetCentre(target);
            }
            else
            {
                throw new ArgumentException("Give either 'target' (a hierarchy path) or '" + downX + "'/'" + downY
                    + "' (pixels from the top-left of the Game View, or 0..1 with normalized:true).");
            }
            state.DownScreen = downScreen;
            if (isDrag)
            {
                if (input["x"].IsNull && input["y"].IsNull)
                {
                    throw new ArgumentException("drag needs a destination: 'x' and 'y' (from_x/from_y or target say where it starts).");
                }
                state.UpScreen = ToScreen(state, input["x"].AsDouble(0), input["y"].AsDouble(0), "x", "y");
            }

            state.PointerData = UapUguiEventAccess.CreatePointerData(state.EventSystem, downScreen, state.Button);
            if (target != null)
            {
                UapUguiEventAccess.SetDirectTarget(state.PointerData, target);
                state.Raw = target;
            }
            else
            {
                state.Raw = UapUguiEventAccess.Raycast(state.EventSystem, state.PointerData);
            }

            switch (state.Action)
            {
                case UapGamePointerAction.Click:
                    DoClick(state, target != null);
                    state.Result = Describe(state);
                    state.Phase = Phase.Done;
                    break;
                case UapGamePointerAction.LongPress:
                    BeginLongPress(state, target != null);
                    break;
                default:
                    StartDragAction(state, target != null);
                    break;
            }
            return state;
        }

        private static void DoClick(PointerState state, bool direct)
        {
            if (state.Raw == null)
            {
                state.Message = "Nothing under the pointer at " + DescribePoint(state, state.DownScreen)
                    + " (no Canvas graphic with raycasting on). No event was fired. Check the position against"
                    + " a screenshot at " + state.ViewWidth + " x " + state.ViewHeight + ", or pass target.";
                return;
            }
            state.PressTarget = UapUguiEventAccess.ExecuteHierarchy(PointerDown, state.Raw, state.PointerData);
            state.ClickTarget = UapUguiEventAccess.GetHandler(PointerClick, state.Raw);
            if (direct && state.PressTarget == null && state.ClickTarget == null)
            {
                throw new InvalidOperationException("'" + state.TargetPath + "' (and nothing above it) handles"
                    + " pointerDown or pointerClick, so a click there does nothing. Point at the Button /"
                    + " Toggle / handler object itself.");
            }
            UapUguiEventAccess.SetPress(state.PointerData, state.PressTarget ?? state.ClickTarget, state.Raw, true);
            UapUguiEventAccess.Execute(PointerUp, state.PressTarget, state.PointerData);
            UapUguiEventAccess.Execute(PointerClick, state.ClickTarget, state.PointerData);
            state.Message = state.ClickTarget != null
                ? "Clicked " + Path(state.ClickTarget) + " (" + ButtonName(state.Button) + ")."
                : (state.PressTarget != null
                    ? "Pressed and released " + Path(state.PressTarget) + "; nothing there handles pointerClick."
                    : "Hit " + Path(state.Raw) + ", but nothing at or above it handles pointerDown or"
                      + " pointerClick, so the click did nothing.");
        }

        private static void BeginLongPress(PointerState state, bool direct)
        {
            if (state.Raw == null)
            {
                state.Message = "Nothing under the pointer at " + DescribePoint(state, state.DownScreen)
                    + ". No event was fired.";
                state.Result = Describe(state);
                state.Phase = Phase.Done;
                return;
            }
            state.PressTarget = UapUguiEventAccess.ExecuteHierarchy(PointerDown, state.Raw, state.PointerData);
            if (state.PressTarget == null)
            {
                if (direct)
                {
                    throw new InvalidOperationException("'" + state.TargetPath + "' (and nothing above it) handles"
                        + " pointerDown, so a long-press there does nothing.");
                }
                state.Message = "Hit " + Path(state.Raw) + ", but nothing at or above it handles pointerDown,"
                    + " so the long-press did nothing.";
                state.Result = Describe(state);
                state.Phase = Phase.Done;
                return;
            }
            UapUguiEventAccess.SetPress(state.PointerData, state.PressTarget, state.Raw, false);
            state.StartedAt = EditorApplication.timeSinceStartup;
            state.Phase = Phase.Holding;
        }

        private static void StartDragAction(PointerState state, bool direct)
        {
            if (state.Raw == null)
            {
                throw new InvalidOperationException("Nothing under the pointer at " + DescribePoint(state, state.DownScreen)
                    + " to drag. Give from_x/from_y on the draggable widget, or its path as target.");
            }
            state.DragTarget = UapUguiEventAccess.GetHandler(Drag, state.Raw);
            if (state.DragTarget == null)
            {
                throw new InvalidOperationException((direct ? "'" + state.TargetPath + "'" : Path(state.Raw))
                    + " (and nothing above it) handles drag events (IDragHandler), so there is nothing to drag."
                    + " Sliders, scrollbars, ScrollRects and custom IDragHandler widgets can be dragged.");
            }
            if (state.DropTargetPath.Length > 0)
            {
                GameObject dropObject = ResolveActive(state.DropTargetPath, "drop_target");
                state.DropTarget = UapUguiEventAccess.GetHandler(Drop, dropObject);
                if (state.DropTarget == null)
                {
                    throw new InvalidOperationException("'" + state.DropTargetPath + "' (and nothing above it)"
                        + " handles drop events (IDropHandler).");
                }
            }
            state.PressTarget = UapUguiEventAccess.ExecuteHierarchy(PointerDown, state.Raw, state.PointerData);
            UapUguiEventAccess.SetPress(state.PointerData, state.PressTarget, state.Raw, false);
            UapUguiEventAccess.Execute(InitializePotentialDrag, state.DragTarget, state.PointerData);
            UapUguiEventAccess.SetDragging(state.PointerData, true, state.DragTarget);
            UapUguiEventAccess.Execute(BeginDrag, state.DragTarget, state.PointerData);
            state.LastScreen = state.DownScreen;
            state.Distance = Vector2.Distance(state.DownScreen, state.UpScreen);
            state.StartedAt = EditorApplication.timeSinceStartup;
            state.Phase = Phase.Dragging;
        }

        private bool Continue(PointerState state, out JsonNode result)
        {
            if (!EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Play Mode ended during the " + ActionName(state.Action)
                    + "; the game did not observe the whole input. Restart it with uap_play_mode and retry.");
            }
            switch (state.Phase)
            {
                case Phase.Holding:
                    if (EditorApplication.timeSinceStartup - state.StartedAt < state.HoldSeconds)
                    {
                        result = null;
                        return false;
                    }
                    state.HeldFor = EditorApplication.timeSinceStartup - state.StartedAt;
                    UapUguiEventAccess.Execute(PointerUp, state.PressTarget, state.PointerData);
                    state.Message = "Held " + Path(state.PressTarget) + " for " + Math.Round(state.HeldFor, 3)
                        + " s and released it (pointerDown, then pointerUp; no pointerClick).";
                    state.Result = Describe(state);
                    state.Phase = Phase.Done;
                    result = state.Result;
                    return true;
                case Phase.Dragging:
                    double progress = UapGamePointerPolicy.DragProgress(
                        EditorApplication.timeSinceStartup - state.StartedAt, state.Distance, state.Speed);
                    Vector2 position = Vector2.Lerp(state.DownScreen, state.UpScreen, (float)progress);
                    UapUguiEventAccess.SetPosition(state.PointerData, position, position - state.LastScreen);
                    state.LastScreen = position;
                    UapUguiEventAccess.Execute(Drag, state.DragTarget, state.PointerData);
                    state.DragEvents++;
                    if (progress < 1.0)
                    {
                        result = null;
                        return false;
                    }
                    FinishDrag(state);
                    state.Result = Describe(state);
                    state.Phase = Phase.Done;
                    result = state.Result;
                    return true;
                default:
                    result = state.Result;
                    return true;
            }
        }

        /// <summary>
        /// The dispatcher gave up on this call: close what it opened -- a
        /// long-press gets its pointerUp, a drag its pointerUp and endDrag --
        /// so no widget is left believing a pointer is still down.
        /// IUapAbandonableTool; design note 2026-09-22-uloop-phase1 section 3.
        /// </summary>
        public void OnAbandoned(object pollState)
        {
            var state = pollState as PointerState;
            if (state == null || (state.Phase != Phase.Holding && state.Phase != Phase.Dragging))
            {
                return;
            }
            Phase phase = state.Phase;
            state.Phase = Phase.Done;
            if (!EditorApplication.isPlaying || state.PointerData == null)
            {
                return;
            }
            UapUguiEventAccess.Execute(PointerUp, state.PressTarget, state.PointerData);
            if (phase == Phase.Dragging)
            {
                UapUguiEventAccess.Execute(EndDrag, state.DragTarget, state.PointerData);
                UapUguiEventAccess.SetDragging(state.PointerData, false, null);
            }
        }

        private static void FinishDrag(PointerState state)
        {
            // The order uGUI's own input module uses on release: pointerUp
            // on what was pressed, drop on what is now under the pointer,
            // then endDrag on the dragged object.
            UapUguiEventAccess.Execute(PointerUp, state.PressTarget, state.PointerData);
            GameObject dropRaw = state.DropTarget;
            if (dropRaw == null)
            {
                dropRaw = UapUguiEventAccess.Raycast(state.EventSystem, state.PointerData);
            }
            else
            {
                UapUguiEventAccess.SetDirectTarget(state.PointerData, dropRaw);
            }
            state.DropTarget = dropRaw != null ? UapUguiEventAccess.ExecuteHierarchy(Drop, dropRaw, state.PointerData) : null;
            UapUguiEventAccess.Execute(EndDrag, state.DragTarget, state.PointerData);
            UapUguiEventAccess.SetDragging(state.PointerData, false, null);
            state.Message = "Dragged " + Path(state.DragTarget) + " from " + DescribePoint(state, state.DownScreen)
                + " to " + DescribePoint(state, state.UpScreen) + " in " + state.DragEvents + " drag event(s)"
                + (state.DropTarget != null
                    ? "; dropped on " + Path(state.DropTarget) + "."
                    : "; nothing under the destination handles drop.");
        }

        private static JsonNode Describe(PointerState state)
        {
            Vector2 down = ToTopLeft(state, state.DownScreen);
            JsonNode result = JsonNode.NewObject()
                .Set("action", ActionName(state.Action))
                .Set("button", ButtonName(state.Button))
                .Set("viewWidth", Math.Round(state.ViewWidth))
                .Set("viewHeight", Math.Round(state.ViewHeight))
                .Set("x", Math.Round(down.x, 1))
                .Set("y", Math.Round(down.y, 1));
            if (state.Action == UapGamePointerAction.Drag)
            {
                Vector2 up = ToTopLeft(state, state.UpScreen);
                result.Set("toX", Math.Round(up.x, 1)).Set("toY", Math.Round(up.y, 1))
                    .Set("dragEvents", state.DragEvents);
            }
            if (state.Action == UapGamePointerAction.LongPress && state.HeldFor > 0)
            {
                result.Set("heldSeconds", Math.Round(state.HeldFor, 3));
            }
            result.Set("hit", state.Raw != null ? Path(state.Raw) : null);
            SetPath(result, "pressTarget", state.PressTarget);
            SetPath(result, "clickTarget", state.ClickTarget);
            SetPath(result, "dragTarget", state.DragTarget);
            SetPath(result, "dropTarget", state.DropTarget);
            result.Set("message", state.Message ?? string.Empty);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static void SetPath(JsonNode result, string key, GameObject target)
        {
            if (target != null)
            {
                result.Set(key, Path(target));
            }
        }

        private static GameObject ResolveActive(string path, string parameterName)
        {
            string error;
            GameObject target = UapAddressing.ResolveHierarchyPath(null, path, out error);
            if (target == null)
            {
                throw new ArgumentException("'" + parameterName + "': " + error);
            }
            if (!target.activeInHierarchy)
            {
                throw new InvalidOperationException("'" + parameterName + "' " + path + " is inactive, so no"
                    + " pointer could reach it.");
            }
            return target;
        }

        /// <summary>The widget's centre in screen space (bottom-left origin), through the Canvas' camera when it has one; its Transform position when it has no RectTransform.</summary>
        private static Vector2 WidgetCentre(GameObject target)
        {
            var rect = target.transform as RectTransform;
            Camera camera = CanvasCamera(target.transform);
            Vector3 world = rect != null ? rect.TransformPoint(rect.rect.center) : target.transform.position;
            return RectTransformUtility.WorldToScreenPoint(camera, world);
        }

        /// <summary>The camera a Screen Space - Camera or World Space canvas renders through (null for Overlay, which is what WorldToScreenPoint wants for it).</summary>
        private static Camera CanvasCamera(Transform start)
        {
            Component canvas = UapCanvasProbe.FindGoverningCanvas(start);
            if (canvas == null)
            {
                return Camera.main;
            }
            string renderMode = UapCanvasProbe.ReadRenderMode(canvas);
            if (renderMode == "ScreenSpaceOverlay")
            {
                return null;
            }
            PropertyInfo worldCamera = canvas.GetType().GetProperty("worldCamera", BindingFlags.Public | BindingFlags.Instance);
            Camera camera = worldCamera != null ? worldCamera.GetValue(canvas, null) as Camera : null;
            return camera != null ? camera : Camera.main;
        }

        private static Vector2 ToScreen(PointerState state, double x, double y, string nameX, string nameY)
        {
            double screenX;
            double screenY;
            string error;
            if (!UapGamePointerPolicy.TryToScreenSpace(x, y, state.ViewWidth, state.ViewHeight, state.Normalized,
                out screenX, out screenY, out error))
            {
                throw new ArgumentException("'" + nameX + "'/'" + nameY + "': " + error);
            }
            return new Vector2((float)screenX, (float)screenY);
        }

        private static Vector2 ToTopLeft(PointerState state, Vector2 screen)
        {
            return new Vector2(screen.x, (float)state.ViewHeight - screen.y);
        }

        private static string DescribePoint(PointerState state, Vector2 screen)
        {
            Vector2 topLeft = ToTopLeft(state, screen);
            return "(" + Math.Round(topLeft.x, 1) + ", " + Math.Round(topLeft.y, 1) + " from the top-left)";
        }

        private static string Path(GameObject target)
        {
            return target != null ? UapAddressing.DescribeHierarchyPath(target.transform) : string.Empty;
        }

        private static string ActionName(UapGamePointerAction action)
        {
            switch (action)
            {
                case UapGamePointerAction.Click: return "click";
                case UapGamePointerAction.LongPress: return "long_press";
                default: return "drag";
            }
        }

        private static string ButtonName(UapGamePointerButton button)
        {
            return button.ToString().ToLowerInvariant();
        }

        private static UapPlayModeSnapshot ReadPlayState()
        {
            return new UapPlayModeSnapshot
            {
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                WillChangePlayMode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsCompiling = EditorApplication.isCompiling
            };
        }

        private enum Phase
        {
            Holding,
            Dragging,
            Done
        }

        private sealed class PointerState
        {
            public UapGamePointerAction Action;
            public UapGamePointerButton Button;
            public bool Normalized;
            public string TargetPath;
            public string DropTargetPath;
            public double HoldSeconds;
            public double Speed;
            public object EventSystem;
            public object PointerData;
            public double ViewWidth;
            public double ViewHeight;
            public Vector2 DownScreen;
            public Vector2 UpScreen;
            public Vector2 LastScreen;
            public double Distance;
            public double StartedAt;
            public double HeldFor;
            public int DragEvents;
            public GameObject Raw;
            public GameObject PressTarget;
            public GameObject ClickTarget;
            public GameObject DragTarget;
            public GameObject DropTarget;
            public string Message;
            public Phase Phase;
            public JsonNode Result;
        }
    }
}

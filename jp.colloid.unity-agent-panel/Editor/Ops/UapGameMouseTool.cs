using System;
using System.Reflection;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Injects mouse input into the RUNNING game through the Input System's
    /// Mouse.current (design note docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.2, uLoop's simulate-mouse-input): click /
    /// long-press a button, jump the cursor to an absolute point, inject a
    /// raw move-delta (for FPS-style look code that reads Mouse.current.
    /// delta rather than its position), or scroll the wheel. Built the same
    /// way as uap_game_key -- Pollable because a press must stay down for a
    /// whole input update before it releases, reflection into the optional
    /// package with the same fields verified against the real package
    /// source (com.unity.inputsystem 1.7.0) -- and reuses uap_game_pointer's
    /// button/coordinate policy (UapGamePointerPolicy) rather than
    /// re-deriving it.
    ///
    /// This is deliberately the SECOND way to move a mouse in this panel:
    /// uap_game_pointer dispatches straight to uGUI's EventSystem handlers
    /// (pointerDown/Click/Drag) and works with the legacy Input Manager
    /// too, which is right for Canvas buttons, toggles and sliders. This
    /// tool instead drives the actual Mouse.current device, which is right
    /// for game code that reads it directly (FPS look, custom raycast-based
    /// picking) and for hitting 3D/2D scene geometry the way a real mouse
    /// would -- something a uGUI event never touches.
    ///
    /// dry_run:true is the one part of this tool that needs neither the
    /// Input System nor Play Mode: it casts a ray from Camera.main through
    /// the given point (3D Physics.Raycast, then a reflected
    /// Physics2D.GetRayIntersection) and reports what is there, so an agent
    /// can sanity-check a coordinate before spending a real click on it.
    /// Physics2D is reached by reflection rather than a direct reference
    /// because, unlike 3D Physics (already referenced by this package --
    /// see Editor/Ops/Markers/SceneMeshRaycaster.cs, which uses RaycastHit
    /// from the same module), nothing else in Core touches it, and
    /// com.unity.modules.physics2d can be stripped from a project that
    /// never uses 2D physics; a hard reference would break the build there.
    /// </summary>
    public sealed class UapGameMouseTool : IUapPollableTool, IUapAbandonableTool
    {
        public const string ToolName = "uap_game_mouse";

        private static ushort HeldButtonsMask;
        private static bool _playModeHookInstalled;
        private static bool _physics2DResolved;
        private static MethodInfo _getRayIntersectionMethod;

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Injects mouse input into the RUNNING game (Play Mode) through the Input System's"
                    + " Mouse.current: click / long_press a button, move the cursor to an absolute point, inject"
                    + " a raw move_delta (for FPS-style look code reading Mouse.current.delta rather than its"
                    + " position), or scroll the wheel. Prefer uap_game_pointer for uGUI widgets (Canvas"
                    + " buttons/toggles/sliders) -- it dispatches straight to their EventSystem handlers and"
                    + " also works with the legacy Input Manager. Use this tool when the game code reads"
                    + " Mouse.current directly, or to hit 3D/2D scene geometry the way a real mouse would."
                    + " Also has dry_run:true (needs no Input System, works in Edit Mode): casts a ray from"
                    + " Camera.main through a point and reports the 3D or 2D collider there, without touching"
                    + " any device -- a way to check a coordinate before spending a real click on it. Every"
                    + " other action requires the com.unity.inputsystem package and Play Mode, not paused;"
                    + " held buttons release when Play Mode ends.";
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
                            .Set("enum", JsonNode.NewArray().Add("click").Add("long_press").Add("move").Add("move_delta").Add("scroll"))
                            .Set("description", "'click' (default): press the button, hold at least one input"
                                + " update, release. 'long_press': same, held for 'duration' seconds. 'move':"
                                + " jump the device cursor to an absolute point (x, y). 'move_delta': inject a"
                                + " raw delta (dx, dy) WITHOUT moving the cursor position -- for look/aim code"
                                + " that reads Mouse.current.delta. 'scroll': inject a scroll-wheel delta (dx, dy)."))
                        .Set("button", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("left").Add("right").Add("middle"))
                            .Set("description", "Mouse button for click / long_press (default left)."))
                        .Set("x", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "click / long_press: optional -- move the cursor there first"
                                + " (omit to click wherever it already is). move / dry_run: required. Pixels"
                                + " from the LEFT edge of the Game View (or 0..1 with normalized:true), origin"
                                + " top-left like a screenshot -- same convention as uap_game_pointer."))
                        .Set("y", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Same units as x, measured from the TOP edge."))
                        .Set("normalized", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true: x/y are 0..1 fractions of the Game View instead of pixels."))
                        .Set("dx", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "move_delta / scroll: required. Positive dx moves/scrolls right."))
                        .Set("dy", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "move_delta: required, positive dy is DOWN the screen (same"
                                + " convention as y). scroll: required, positive dy scrolls up/away, matching"
                                + " Mouse.scroll.y."))
                        .Set("duration", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "long_press: seconds to hold, 0 to " + UapGameInputPolicy.MaxHoldSeconds
                                + " (default " + UapGamePointerPolicy.DefaultLongPressSeconds + "). move_delta:"
                                + " when greater than 0, spreads dx/dy across that many seconds of real Editor"
                                + " ticks (SmoothDelta) instead of one instant delta event (default 0 = instant)."))
                        .Set("dry_run", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true: no Input System, no Play Mode required. Casts a ray from"
                                + " Camera.main through (x, y) -- 3D Physics.Raycast first, then a 2D"
                                + " Physics2D.GetRayIntersection -- and reports what it would hit. Needs x and"
                                + " y; ignores action/button/dx/dy/duration."))
                        .Set("layer_mask", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "dry_run only: layer mask for the raycast (default: every layer)."))
                        .Set("max_distance", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "dry_run only: longest ray distance in world units (default: unbounded).")))
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
            var state = stateObj as MouseCallState;
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

        // -----------------------------------------------------------------
        // Real input (Play Mode, Input System)
        // -----------------------------------------------------------------

        private MouseCallState Begin(JsonNode input)
        {
            if (input["dry_run"].AsBool(false))
            {
                return DryRun(input);
            }

            var state = new MouseCallState
            {
                Action = UapGameMousePolicy.ParseAction(input["action"].AsString(null)),
                Button = UapGamePointerPolicy.ParseButton(input["button"].AsString(null)),
                Normalized = input["normalized"].AsBool(false)
            };

            string argumentError;
            if (state.Action == UapGameMouseAction.LongPress)
            {
                state.HoldSeconds = input["duration"].AsDouble(UapGamePointerPolicy.DefaultLongPressSeconds);
                if (!UapGameInputPolicy.ValidateHold(state.HoldSeconds, "duration", out argumentError))
                {
                    throw new ArgumentException(argumentError);
                }
            }
            else if (state.Action == UapGameMouseAction.MoveDelta)
            {
                state.HoldSeconds = input["duration"].AsDouble(0.0);
                if (!UapGameInputPolicy.ValidateHold(state.HoldSeconds, "duration", out argumentError))
                {
                    throw new ArgumentException(argumentError);
                }
            }
            // Validate the coordinates this action needs BEFORE touching Editor
            // state, so an argument mistake is reported the same way whether or
            // not the game happens to be running (mirrors uap_game_pointer /
            // uap_game_key's Begin ordering).
            if (state.Action == UapGameMouseAction.Move)
            {
                RequireXy(input, "action:move");
            }
            double dx = 0, dy = 0;
            if (state.Action == UapGameMouseAction.MoveDelta || state.Action == UapGameMouseAction.Scroll)
            {
                RequireDxDy(input, out dx, out dy);
                state.Dx = dx;
                state.Dy = dy;
            }

            string stateError;
            if (!UapGameInputPolicy.CheckPlayState(ReadPlayState(), false, out stateError))
            {
                throw new InvalidOperationException(stateError);
            }
            string mouseError;
            state.Mouse = UapInputSystemMouseAccess.CurrentMouse(out mouseError);
            if (state.Mouse == null)
            {
                throw new InvalidOperationException(mouseError);
            }
            EnsurePlayModeHook();

            Vector2 viewSize = Handles.GetMainGameViewSize();
            state.ViewWidth = viewSize.x;
            state.ViewHeight = viewSize.y;
            state.Position = UapInputSystemMouseAccess.ReadPosition(state.Mouse);

            switch (state.Action)
            {
                case UapGameMouseAction.Click:
                case UapGameMouseAction.LongPress:
                    if (!input["x"].IsNull || !input["y"].IsNull)
                    {
                        state.Position = ToScreen(state, input["x"].AsDouble(0), input["y"].AsDouble(0), "x", "y");
                    }
                    BeginButtonHold(state);
                    break;
                case UapGameMouseAction.Move:
                    state.Position = ToScreen(state, input["x"].AsDouble(0), input["y"].AsDouble(0), "x", "y");
                    QueueFullState(state, Vector2.zero, Vector2.zero);
                    state.Phase = Phase.AwaitReadback;
                    break;
                case UapGameMouseAction.MoveDelta:
                    BeginMoveDelta(state);
                    break;
                default: // Scroll
                    QueueFullState(state, Vector2.zero, new Vector2((float)dx, (float)dy));
                    state.Phase = Phase.AwaitReadback;
                    break;
            }
            return state;
        }

        private static void RequireXy(JsonNode input, string reason)
        {
            if (input["x"].IsNull || input["y"].IsNull)
            {
                throw new ArgumentException("'x' and 'y' are required for " + reason + " (pixels from the"
                    + " top-left of the Game View, or 0..1 with normalized:true).");
            }
        }

        private static void RequireDxDy(JsonNode input, out double dx, out double dy)
        {
            if (input["dx"].IsNull || input["dy"].IsNull)
            {
                throw new ArgumentException("'dx' and 'dy' are required for this action.");
            }
            dx = input["dx"].AsDouble(0);
            dy = input["dy"].AsDouble(0);
            string deltaError;
            if (!UapGameMousePolicy.ValidateDelta(dx, dy, "dx", "dy", out deltaError))
            {
                throw new ArgumentException(deltaError);
            }
        }

        private static void BeginMoveDelta(MouseCallState state)
        {
            double deviceDx, deviceDy;
            UapGameMousePolicy.ToDeviceDelta(state.Dx, state.Dy, out deviceDx, out deviceDy);
            state.TotalDelta = new Vector2((float)deviceDx, (float)deviceDy);
            if (state.HoldSeconds <= 0.0)
            {
                QueueFullState(state, state.TotalDelta, Vector2.zero);
                state.Phase = Phase.AwaitReadback;
            }
            else
            {
                state.StartedAt = EditorApplication.timeSinceStartup;
                state.Phase = Phase.SmoothingDelta;
            }
        }

        private static void BeginButtonHold(MouseCallState state)
        {
            HeldButtonsMask |= (ushort)(1 << UapInputSystemMouseAccess.ButtonBit(state.Button));
            QueueFullState(state, Vector2.zero, Vector2.zero);
            state.StartedAt = EditorApplication.timeSinceStartup;
            state.Phase = Phase.Holding;
        }

        private bool Continue(MouseCallState state, out JsonNode result)
        {
            if (!EditorApplication.isPlaying)
            {
                // The device this call was driving is gone; the held set is
                // cleared by the play-mode hook. Say so rather than report
                // an injection no game observed (same rationale as
                // UapGameKeyTool.Continue).
                HeldButtonsMask = 0;
                throw new InvalidOperationException("Play Mode ended while the mouse " + ActionName(state.Action)
                    + " was in progress; the game did not observe the whole input. Restart it with"
                    + " uap_play_mode and retry.");
            }
            state.Polls++;
            switch (state.Phase)
            {
                case Phase.Holding:
                    if (state.Polls < UapGameInputPolicy.MinHoldPolls
                        || EditorApplication.timeSinceStartup - state.StartedAt < state.HoldSeconds)
                    {
                        result = null;
                        return false;
                    }
                    state.HeldFor = EditorApplication.timeSinceStartup - state.StartedAt;
                    HeldButtonsMask &= (ushort)~(1 << UapInputSystemMouseAccess.ButtonBit(state.Button));
                    QueueFullState(state, Vector2.zero, Vector2.zero);
                    state.Phase = Phase.AwaitReadback;
                    result = null;
                    return false;
                case Phase.SmoothingDelta:
                    {
                        double elapsed = EditorApplication.timeSinceStartup - state.StartedAt;
                        double progress = UapGameMousePolicy.DurationProgress(elapsed, state.HoldSeconds);
                        double increment = progress - state.DeltaSoFar;
                        state.DeltaSoFar = progress;
                        Vector2 tickDelta = state.TotalDelta * (float)increment;
                        QueueFullState(state, tickDelta, Vector2.zero);
                        if (progress < 1.0)
                        {
                            result = null;
                            return false;
                        }
                        state.Phase = Phase.AwaitReadback;
                        result = null;
                        return false;
                    }
                case Phase.AwaitReadback:
                    // One tick after the last queued state: the input update
                    // has run, so the device answers for what the game now sees.
                    state.Result = Describe(state);
                    state.Phase = Phase.Done;
                    result = state.Result;
                    return true;
                default:
                    result = state.Result;
                    return true;
            }
        }

        private static void QueueFullState(MouseCallState state, Vector2 delta, Vector2 scroll)
        {
            object mouseState = UapInputSystemMouseAccess.BuildState(state.Position, delta, scroll, HeldButtonsMask);
            UapInputSystemMouseAccess.QueueState(state.Mouse, mouseState);
            UapInputSystemMouseAccess.FlushIfManual();
        }

        private static Vector2 ToScreen(MouseCallState state, double x, double y, string nameX, string nameY)
        {
            double screenX, screenY;
            string error;
            if (!UapGamePointerPolicy.TryToScreenSpace(x, y, state.ViewWidth, state.ViewHeight, state.Normalized,
                out screenX, out screenY, out error))
            {
                throw new ArgumentException("'" + nameX + "'/'" + nameY + "': " + error);
            }
            return new Vector2((float)screenX, (float)screenY);
        }

        private static JsonNode Describe(MouseCallState state)
        {
            Vector2 topLeft = ToTopLeft(state, state.Position);
            JsonNode result = JsonNode.NewObject()
                .Set("action", ActionName(state.Action))
                .Set("x", Math.Round(topLeft.x, 1))
                .Set("y", Math.Round(topLeft.y, 1));
            string message;
            switch (state.Action)
            {
                case UapGameMouseAction.Click:
                case UapGameMouseAction.LongPress:
                    {
                        bool devicePressed = UapInputSystemMouseAccess.IsPressed(state.Mouse, state.Button);
                        result.Set("button", ButtonName(state.Button)).Set("devicePressed", devicePressed);
                        if (state.Action == UapGameMouseAction.LongPress)
                        {
                            result.Set("heldSeconds", Math.Round(state.HeldFor, 3));
                            message = "Held the " + ButtonName(state.Button) + " button at " + DescribePoint(topLeft)
                                + " for " + Math.Round(state.HeldFor, 3) + " s and released it.";
                        }
                        else
                        {
                            message = "Clicked the " + ButtonName(state.Button) + " button at " + DescribePoint(topLeft) + ".";
                        }
                        if (devicePressed)
                        {
                            message += " The device still reports it pressed -- something else (a physical"
                                + " mouse, another injector) is holding it.";
                        }
                        break;
                    }
                case UapGameMouseAction.Move:
                    message = "Moved the mouse to " + DescribePoint(topLeft) + ".";
                    break;
                case UapGameMouseAction.MoveDelta:
                    result.Set("dx", Math.Round(state.Dx, 1)).Set("dy", Math.Round(state.Dy, 1));
                    message = "Injected a mouse delta of (" + Math.Round(state.Dx, 1) + ", " + Math.Round(state.Dy, 1) + ")"
                        + (state.HoldSeconds > 0 ? " smoothed over " + state.HoldSeconds + " s" : " in one input update")
                        + "; the cursor position was not moved.";
                    break;
                default: // Scroll
                    result.Set("dx", Math.Round(state.Dx, 1)).Set("dy", Math.Round(state.Dy, 1));
                    message = "Scrolled the wheel by (" + Math.Round(state.Dx, 1) + ", " + Math.Round(state.Dy, 1) + ").";
                    break;
            }
            result.Set("message", message);
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static Vector2 ToTopLeft(MouseCallState state, Vector2 screen)
        {
            return new Vector2(screen.x, (float)state.ViewHeight - screen.y);
        }

        private static string DescribePoint(Vector2 topLeft)
        {
            return "(" + Math.Round(topLeft.x, 1) + ", " + Math.Round(topLeft.y, 1) + " from the top-left)";
        }

        private static string ActionName(UapGameMouseAction action)
        {
            switch (action)
            {
                case UapGameMouseAction.Click: return "click";
                case UapGameMouseAction.LongPress: return "long_press";
                case UapGameMouseAction.Move: return "move";
                case UapGameMouseAction.MoveDelta: return "move_delta";
                default: return "scroll";
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

        /// <summary>
        /// Clears the held-buttons mask when Play Mode ends, installed on
        /// first use, never from [InitializeOnLoadMethod] (a project that
        /// never calls this tool pays nothing for it -- same rule as
        /// UapGameKeyTool.EnsurePlayModeHook).
        /// </summary>
        private static void EnsurePlayModeHook()
        {
            if (_playModeHookInstalled)
            {
                return;
            }
            _playModeHookInstalled = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                HeldButtonsMask = 0;
            }
        }

        /// <summary>Test seam: the buttons this tool believes it is holding, by name.</summary>
        internal static string[] HeldButtonNamesForTests()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (UapGamePointerButton button in new[] { UapGamePointerButton.Left, UapGamePointerButton.Right, UapGamePointerButton.Middle })
            {
                // Bit position mirrors MouseButton's own ordinal (Left=0,
                // Right=1, Middle=2 -- verified against the real package,
                // see UapInputSystemMouseAccess), independent of the Input
                // System package being installed, so this seam does not
                // need one to be usable.
                if ((HeldButtonsMask & (1 << (int)button)) != 0)
                {
                    names.Add(button.ToString());
                }
            }
            return names.ToArray();
        }

        /// <summary>Test seam: forget every held button without touching a device.</summary>
        internal static void ResetHeldForTests()
        {
            HeldButtonsMask = 0;
        }

        // -----------------------------------------------------------------
        // dry_run: a raycast that needs neither Play Mode nor the Input
        // System.
        // -----------------------------------------------------------------

        private static MouseCallState DryRun(JsonNode input)
        {
            RequireXy(input, "dry_run");
            double x = input["x"].AsDouble(0);
            double y = input["y"].AsDouble(0);
            bool normalized = input["normalized"].AsBool(false);
            Vector2 viewSize = Handles.GetMainGameViewSize();
            double screenX, screenY;
            string toScreenError;
            if (!UapGamePointerPolicy.TryToScreenSpace(x, y, viewSize.x, viewSize.y, normalized, out screenX, out screenY, out toScreenError))
            {
                throw new ArgumentException("'x'/'y': " + toScreenError);
            }
            double maxDistance = input["max_distance"].AsDouble(double.PositiveInfinity);
            string maxDistanceError;
            if (!UapGameMousePolicy.ValidateMaxDistance(maxDistance, out maxDistanceError))
            {
                throw new ArgumentException(maxDistanceError);
            }
            int layerMask = input["layer_mask"].IsNull ? ~0 : input["layer_mask"].AsInt(~0);

            Camera camera = Camera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("No active Main Camera (Camera.main is null); tag a camera"
                    + " 'MainCamera' so dry_run has something to raycast through.");
            }
            Ray ray = camera.ScreenPointToRay(new Vector3((float)screenX, (float)screenY, 0f));

            JsonNode result = JsonNode.NewObject()
                .Set("dryRun", true)
                .Set("x", Math.Round(x, 1))
                .Set("y", Math.Round(y, 1));

            RaycastHit hit3D;
            if (Physics.Raycast(ray, out hit3D, (float)maxDistance, layerMask))
            {
                result.Set("hit", true)
                    .Set("kind", "3d")
                    .Set("path", hit3D.transform != null ? UapAddressing.DescribeHierarchyPath(hit3D.transform) : null)
                    .Set("point", Vec3(hit3D.point))
                    .Set("normal", Vec3(hit3D.normal))
                    .Set("distance", Math.Round(hit3D.distance, 4))
                    .Set("message", "3D hit.");
                return Done(result);
            }

            Transform hit2DTransform;
            Vector2 hit2DPoint, hit2DNormal;
            float hit2DDistance;
            if (TryPhysics2DRaycast(ray, (float)maxDistance, layerMask, out hit2DTransform, out hit2DPoint, out hit2DNormal, out hit2DDistance))
            {
                result.Set("hit", true)
                    .Set("kind", "2d")
                    .Set("path", hit2DTransform != null ? UapAddressing.DescribeHierarchyPath(hit2DTransform) : null)
                    .Set("point", JsonNode.NewObject().Set("x", Math.Round(hit2DPoint.x, 4)).Set("y", Math.Round(hit2DPoint.y, 4)))
                    .Set("normal", JsonNode.NewObject().Set("x", Math.Round(hit2DNormal.x, 4)).Set("y", Math.Round(hit2DNormal.y, 4)))
                    .Set("distance", Math.Round(hit2DDistance, 4))
                    .Set("message", "2D hit.");
                return Done(result);
            }

            result.Set("hit", false)
                .Set("message", "Nothing along the ray from Camera.main through that point (checked 3D colliders, then 2D).");
            return Done(result);
        }

        private static MouseCallState Done(JsonNode resultObject)
        {
            return new MouseCallState { Phase = Phase.Done, Result = UapToolResults.Text(JsonWriter.Write(resultObject)) };
        }

        private static JsonNode Vec3(Vector3 v)
        {
            return JsonNode.NewObject().Set("x", Math.Round(v.x, 4)).Set("y", Math.Round(v.y, 4)).Set("z", Math.Round(v.z, 4));
        }

        /// <summary>
        /// Physics2D.GetRayIntersection by reflection (see the class
        /// remarks for why this one call, alone in this file, is not a
        /// direct reference). False with everything zeroed when the module
        /// is absent, the method could not be resolved, or the ray hit
        /// nothing -- callers do not need to tell those apart, since either
        /// way there is no 2D hit to report.
        /// </summary>
        private static bool TryPhysics2DRaycast(Ray ray, float maxDistance, int layerMask,
            out Transform hitTransform, out Vector2 hitPoint, out Vector2 hitNormal, out float hitDistance)
        {
            hitTransform = null;
            hitPoint = Vector2.zero;
            hitNormal = Vector2.zero;
            hitDistance = 0f;
            if (!ResolvePhysics2D())
            {
                return false;
            }
            object hit;
            try
            {
                hit = _getRayIntersectionMethod.Invoke(null, new object[] { ray, maxDistance, layerMask });
            }
            catch (Exception)
            {
                return false;
            }
            // RaycastHit2D is a value type -- Invoke always returns a boxed
            // (non-null) instance here, even on a miss. The one field that
            // distinguishes a miss is 'collider', which is null when
            // nothing was hit.
            object colliderObject = GetPropertyValue(hit, "collider");
            if (!(colliderObject is Component))
            {
                // RaycastHit2D's default value (a miss): collider is null.
                return false;
            }
            // RaycastHit2D.transform (like RaycastHit.transform on the 3D
            // side, used above) prefers the hit collider's attached
            // Rigidbody2D's transform over the collider's own -- relevant
            // when the collider sits on a child of the body. Reading the
            // struct's own property keeps this path's "what object did I
            // hit" answer consistent with the 3D one instead of always
            // reporting the collider's GameObject.
            object transformValue = GetPropertyValue(hit, "transform");
            hitTransform = transformValue as Transform;
            object pointValue = GetPropertyValue(hit, "point");
            object normalValue = GetPropertyValue(hit, "normal");
            object distanceValue = GetPropertyValue(hit, "distance");
            hitPoint = pointValue is Vector2 ? (Vector2)pointValue : Vector2.zero;
            hitNormal = normalValue is Vector2 ? (Vector2)normalValue : Vector2.zero;
            hitDistance = distanceValue is float ? (float)distanceValue : 0f;
            return true;
        }

        private static bool ResolvePhysics2D()
        {
            if (_physics2DResolved)
            {
                return _getRayIntersectionMethod != null;
            }
            _physics2DResolved = true;
            Type physics2DType = UapUguiEventAccess.FindType("UnityEngine.Physics2D");
            if (physics2DType == null)
            {
                return false;
            }
            MethodInfo[] methods = physics2DType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "GetRayIntersection")
                {
                    continue;
                }
                ParameterInfo[] parameters = methods[i].GetParameters();
                if (parameters.Length == 3 && parameters[0].ParameterType == typeof(Ray)
                    && parameters[1].ParameterType == typeof(float) && parameters[2].ParameterType == typeof(int))
                {
                    _getRayIntersectionMethod = methods[i];
                    break;
                }
            }
            return _getRayIntersectionMethod != null;
        }

        /// <summary>
        /// The dispatcher gave up on this call (its HTTP wait ran out, e.g.
        /// the 8 s throttled wait during a long_press): release the button
        /// THIS call pressed, so the game does not see it held until Play
        /// Mode ends (IUapAbandonableTool; design note 2026-09-22-uloop-
        /// phase1 section 3).
        /// </summary>
        public void OnAbandoned(object pollState)
        {
            var state = pollState as MouseCallState;
            if (state == null || state.Phase != Phase.Holding)
            {
                return;
            }
            HeldButtonsMask &= (ushort)~(1 << UapInputSystemMouseAccess.ButtonBit(state.Button));
            state.Phase = Phase.Done;
            if (EditorApplication.isPlaying && state.Mouse != null)
            {
                QueueFullState(state, Vector2.zero, Vector2.zero);
            }
        }

        private static object GetPropertyValue(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property != null ? property.GetValue(target, null) : null;
        }

        private enum Phase
        {
            Holding,
            SmoothingDelta,
            AwaitReadback,
            Done
        }

        private sealed class MouseCallState
        {
            public UapGameMouseAction Action;
            public UapGamePointerButton Button;
            public bool Normalized;
            public object Mouse;
            public double ViewWidth;
            public double ViewHeight;
            public Vector2 Position;
            public double HoldSeconds;
            public double StartedAt;
            public double HeldFor;
            public int Polls;
            public Vector2 TotalDelta;
            public double DeltaSoFar;
            public double Dx;
            public double Dy;
            public Phase Phase;
            public JsonNode Result;
        }
    }
}

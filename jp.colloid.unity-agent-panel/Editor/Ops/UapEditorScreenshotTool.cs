using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Captures the Scene view or Game view to a PNG under the project's
    /// Temp/ folder and returns the absolute path (design section 1.2/3c:
    /// "editor_screenshot" -- the visual-feedback half of the "dump -> act
    /// -> screenshot" self-correction loop the design calls the tool
    /// family's core differentiator). Read-only (never mutates the scene or
    /// project) -- Undoable is still explicitly false for permission-card
    /// honesty rather than implying "nothing to undo" is the same as
    /// "explicitly verified undoable".
    ///
    /// Pragmatic v1 implementation: a stable public EditorWindow API for
    /// reading back raw window pixels does not exist, so this renders the
    /// view's RELEVANT CAMERA (the open SceneView's own camera, or
    /// Camera.main / the first active Camera for the "game" target) to an
    /// offscreen RenderTexture -- the same technique standard Unity
    /// screenshot utilities use. Whether the requested view is open AT ALL
    /// is still checked structurally FIRST (SceneView.lastActiveSceneView /
    /// an actually-open GameView window instance), so batch-mode and other
    /// headless callers get an honest structured error instead of either an
    /// exception from a null window or a silent camera-only render nobody
    /// asked to see.
    /// </summary>
    public sealed class UapEditorScreenshotTool : IUapTool
    {
        public string Name
        {
            get { return "uap_editor_screenshot"; }
        }

        public string Description
        {
            get
            {
                return "Captures a screenshot of the Scene view or Game view to a PNG file and returns"
                    + " its absolute path plus the pixel position of every Scene-view marker (pass"
                    + " return_image:true to also get the picture inline) -- use this"
                    + " to visually verify the result of other tool calls. capture:\"camera\" (default)"
                    + " renders the view's camera offscreen (no gizmos, Handles or marker labels; lighting"
                    + " can differ from the Scene view); capture:\"window\" (Scene view only) reads the"
                    + " view exactly as displayed, labels and gizmos included, and needs the view visible"
                    + " on screen. Fails with a structured error when the requested view is not open."
                    + " annotate_ui:true also lists the clickable/draggable uGUI elements (feed 'path'"
                    + " straight into uap_game_pointer's target); ui_only:true skips the image entirely"
                    + " and just returns that list."
                    + " To render a specific camera pass camera (hierarchy path) with optional width/height:"
                    + " it is drawn offscreen and needs no open view. To position the Scene view first pass"
                    + " sceneView {pivot, rotation, size, orthographic, lookAt}.";
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
            get { return true; }
        }

        public JsonNode InputSchema
        {
            get
            {
                return JsonNode.NewObject()
                    .Set("type", "object")
                    .Set("properties", JsonNode.NewObject()
                        .Set("view", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "\"scene\" (the open Scene view's own camera) or \"game\""
                                + " (Camera.main / the first active camera, requires an open Game view)."
                                + " Default \"game\"."))
                        .Set("return_image", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true also returns the picture itself as an image content block"
                                + " (downscaled to a 1568 px long edge) so you can look at it without reading the file."
                                + " Default false."))
                        .Set("capture", JsonNode.NewObject().Set("type", "string")
                            .Set("enum", JsonNode.NewArray().Add("camera").Add("window"))
                            .Set("description", "\"camera\" (default): offscreen render of the view's camera."
                                + " \"window\": the Scene view's pixels as displayed (labels, gizmos, grid);"
                                + " view must be \"scene\" and visible on screen."))
                        .Set("annotate_ui", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true also lists the uGUI elements an agent can act on"
                                + " (components derived from Selectable -- Button, Toggle, Slider, Dropdown,"
                                + " InputField, Scrollbar, TMP equivalents -- plus anything implementing"
                                + " IPointerClickHandler/IDragHandler/IDropHandler under an active Canvas):"
                                + " label, hierarchy path (feed it straight into uap_game_pointer's target),"
                                + " component type, interactable, visible text, centre in Game View render"
                                + " pixels/normalized 0..1 (uap_game_pointer's x/y convention) and rect."
                                + " Works in Edit Mode and Play Mode. Only view:\"game\" draws outlines and"
                                + " letter labels onto the image itself (Scene view pixels are not in the"
                                + " same coordinate space); the JSON list is returned either way and is the"
                                + " primary output. Capped at maxElements (default " + UapUiAnnotationPolicy.DefaultElementCap
                                + "), with a truncated flag. Default false."))
                        .Set("ui_only", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "true skips capturing/writing the image entirely and just"
                                + " returns the uGUI element list (implies annotate_ui:true); works even with"
                                + " no Scene/Game view open, since it never needs one. Default false."))
                        .Set("maxElements", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Cap on how many uGUI elements annotate_ui/ui_only report"
                                + " (default " + UapUiAnnotationPolicy.DefaultElementCap + ")."))
                        .Set("camera", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of a GameObject with a Camera to render offscreen"
                                + " (no open view needed; forces camera capture; 'view' and 'sceneView' are ignored/not allowed)."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path for 'camera' and 'sceneView.lookAt'. Omit to use the active scene (or the open prefab stage)."))
                        .Set("width", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Image width in pixels when 'camera' is given (default 1280, clamped to 32..4096)."))
                        .Set("height", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Image height in pixels when 'camera' is given (default 720, clamped to 32..4096)."))
                        .Set("sceneView", JsonNode.NewObject().Set("type", "object")
                            .Set("description", "Moves the Scene view before capturing (implies view \"scene\"). Needs an open Scene view.")
                            .Set("properties", JsonNode.NewObject()
                                .Set("pivot", Vec3Schema("Point the view looks at (world space)."))
                                .Set("rotation", Vec3Schema("View rotation as Euler angles in degrees."))
                                .Set("size", JsonNode.NewObject().Set("type", "number")
                                    .Set("description", "Scene view size (zoom); omit to keep the current one (or auto-fit with lookAt)."))
                                .Set("orthographic", JsonNode.NewObject().Set("type", "boolean")
                                    .Set("description", "true for an orthographic view, false for perspective."))
                                .Set("lookAt", JsonNode.NewObject().Set("type", "string")
                                    .Set("description", "Hierarchy path of a GameObject to frame (pivot/size default to its bounds).")))
                            .Set("additionalProperties", false)))
                    .Set("additionalProperties", false);
            }
        }

        private static JsonNode Vec3Schema(string description)
        {
            return JsonNode.NewObject()
                .Set("type", "object")
                .Set("description", description)
                .Set("properties", JsonNode.NewObject()
                    .Set("x", JsonNode.NewObject().Set("type", "number"))
                    .Set("y", JsonNode.NewObject().Set("type", "number"))
                    .Set("z", JsonNode.NewObject().Set("type", "number")));
        }

        public JsonNode Execute(JsonNode input)
        {
            string cameraPath = input["camera"].AsString(null);
            JsonNode sceneViewSpec = input["sceneView"];
            bool hasSceneViewSpec = sceneViewSpec.IsObject;
            if (!string.IsNullOrEmpty(cameraPath))
            {
                if (hasSceneViewSpec)
                {
                    throw new ArgumentException("'camera' renders offscreen and cannot be combined with 'sceneView'.");
                }
                return ExecuteCameraCapture(input, cameraPath);
            }
            string viewArg = input["view"].AsString(null);
            if (hasSceneViewSpec)
            {
                if (string.IsNullOrEmpty(viewArg))
                {
                    viewArg = "scene";
                }
                else if (!string.Equals(viewArg.Trim(), "scene", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("'sceneView' needs view:\"scene\" (or omit 'view').");
                }
            }
            UapScreenshotView view;
            string error;
            if (!UapEditorScreenshotPaths.TryParseView(viewArg, out view, out error))
            {
                throw new ArgumentException(error);
            }
            UapScreenshotCapture capture;
            if (!UapEditorScreenshotPaths.TryParseCapture(input["capture"].AsString(null), out capture, out error))
            {
                throw new ArgumentException(error);
            }
            if (capture == UapScreenshotCapture.Window && view != UapScreenshotView.Scene)
            {
                throw new ArgumentException("capture:\"window\" reads the Scene view's pixels and needs view:\"scene\";"
                    + " the Game view has no window capture -- use capture:\"camera\" for it.");
            }

            // annotate_ui / ui_only (design note docs/design-notes/2026-09-
            // 22-uloop-phase1.md section 2.1): ui_only implies annotate_ui
            // and, since it never touches a view/camera, can answer even
            // with nothing open (batch mode included).
            bool uiOnly = input["ui_only"].AsBool(false);
            bool annotateUi = uiOnly || input["annotate_ui"].AsBool(false);
            int maxElements = input["maxElements"].AsInt(UapUiAnnotationPolicy.DefaultElementCap);
            if (maxElements <= 0)
            {
                maxElements = UapUiAnnotationPolicy.DefaultElementCap;
            }
            if (uiOnly)
            {
                JsonNode uiOnlyResult = BuildUiAnnotationResult(maxElements, out _, out _);
                return UapToolResults.Text(JsonWriter.Write(uiOnlyResult));
            }

            Camera camera;
            SceneView sceneView;
            int width;
            int height;
            string viewDescription;
            if (!UapScreenCapture.TryResolveView(view, out camera, out sceneView, out width, out height, out viewDescription, out error))
            {
                throw new InvalidOperationException(error);
            }

            if (hasSceneViewSpec)
            {
                if (!UapScreenCapture.TryApplySceneViewSettings(sceneView, sceneViewSpec,
                        input["scene"].AsString(null), out error))
                {
                    throw new InvalidOperationException(error);
                }
                // Re-read: the view's camera and size may have changed.
                camera = sceneView.camera;
                width = UapScreenCapture.ClampRequestedDimension(Mathf.RoundToInt(sceneView.position.width), true);
                height = UapScreenCapture.ClampRequestedDimension(Mathf.RoundToInt(sceneView.position.height), false);
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = UapEditorScreenshotPaths.BuildOutputPath(DateTime.UtcNow, view, capture, projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string note;
            JsonNode uiResult = null;
            if (capture == UapScreenshotCapture.Window)
            {
                if (!UapScreenCapture.TryCaptureSceneViewWindow(sceneView, path, out width, out height, out error))
                {
                    throw new InvalidOperationException(error);
                }
                viewDescription += " as displayed (window capture)";
                note = null;
                if (annotateUi)
                {
                    // Element coordinates are always in the Game View's
                    // render-pixel convention (matching uap_game_pointer),
                    // which is NOT the Scene view's own pixel space -- the
                    // list is still returned (it targets the running game,
                    // not this picture), but nothing is drawn onto it.
                    uiResult = BuildUiAnnotationResult(maxElements, out _, out _);
                    uiResult.Set("drawnOnImage", false)
                        .Set("note", "Only view:\"game\" draws outlines/labels onto the image -- the Scene"
                            + " view's pixels are not in the same coordinate space as the listed x/y.");
                }
            }
            else if (annotateUi && view == UapScreenshotView.Game)
            {
                Texture2D texture = UapScreenCapture.CaptureCameraToTexture(camera, width, height);
                try
                {
                    List<UapUiAnnotationMark> marks;
                    Vector2 renderSize;
                    uiResult = BuildUiAnnotationResult(maxElements, out marks, out renderSize);
                    List<UapUiAnnotationMark> scaledMarks = UapUiAnnotationPolicy.ScaleMarks(
                        marks, renderSize.x, renderSize.y, width, height);
                    UapScreenCapture.DrawUiAnnotationMarks(texture, scaledMarks);
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                uiResult.Set("drawnOnImage", true)
                    .Set("note", "Screen Space - Overlay canvas elements are listed but will not be visible in"
                        + " this image (a camera render never includes the overlay compositor); their"
                        + " coordinates are still correct for uap_game_pointer.");
                note = "Note: this is an offscreen render of the view's camera -- lighting can differ from the"
                    + " Scene view and gizmos/marker labels are not included; use capture:\"window\" for the"
                    + " Scene view exactly as displayed.";
            }
            else
            {
                UapScreenCapture.CaptureCameraToPng(camera, width, height, path);
                note = "Note: this is an offscreen render of the view's camera -- lighting can differ from the"
                    + " Scene view and gizmos/marker labels are not included; use capture:\"window\" for the"
                    + " Scene view exactly as displayed.";
                if (annotateUi)
                {
                    uiResult = BuildUiAnnotationResult(maxElements, out _, out _);
                    uiResult.Set("drawnOnImage", false)
                        .Set("note", "Only view:\"game\" draws outlines/labels onto the image.");
                }
            }

            var sb = new StringBuilder(160);
            sb.Append("Captured ").Append(viewDescription).Append(" to ").Append(path).Append(" (")
              .Append(width).Append('x').Append(height).Append(").");
            string markers = UapEditorScreenshotPaths.FormatMarkersInView(
                UapScreenCapture.ProjectMarkers(camera), width, height);
            if (markers != null)
            {
                sb.Append('\n').Append(markers);
            }
            if (note != null)
            {
                sb.Append('\n').Append(note);
            }
            return FinishResult(sb.ToString(), path, input["return_image"].AsBool(false), uiResult);
        }

        private static JsonNode ExecuteCameraCapture(JsonNode input, string cameraPath)
        {
            Camera camera;
            string error;
            if (!UapScreenCapture.TryResolveCameraByPath(cameraPath, input["scene"].AsString(null), out camera, out error))
            {
                throw new InvalidOperationException(error);
            }
            int width = UapScreenCapture.ClampRequestedDimension(input["width"].AsInt(0), true);
            int height = UapScreenCapture.ClampRequestedDimension(input["height"].AsInt(0), false);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = UapEditorScreenshotPaths.BuildCameraOutputPath(DateTime.UtcNow, projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            UapScreenCapture.CaptureCameraToPng(camera, width, height, path);
            var sb = new StringBuilder(160);
            sb.Append("Captured camera '").Append(UapAddressing.DescribeHierarchyPath(camera.transform))
              .Append("' offscreen to ").Append(path).Append(" (").Append(width).Append('x').Append(height).Append(").");
            string markers = UapEditorScreenshotPaths.FormatMarkersInView(
                UapScreenCapture.ProjectMarkers(camera), width, height);
            if (markers != null)
            {
                sb.Append('\n').Append(markers);
            }
            sb.Append("\nNote: offscreen render of the camera -- no gizmos or marker labels.");
            return FinishResult(sb.ToString(), path, input["return_image"].AsBool(false));
        }

        private static JsonNode FinishResult(string text, string path, bool returnImage, JsonNode uiResult = null)
        {
            JsonNode content = UapToolResults.Text(text);
            if (uiResult != null)
            {
                content.Add(JsonNode.NewObject().Set("type", "text").Set("text", JsonWriter.Write(uiResult)));
            }
            if (returnImage)
            {
                // Same pipeline as a composer attachment: long-edge cap,
                // PNG or JPEG, hard size cap -- so the block the model gets
                // is never bigger than what a user could send it.
                Colloid.AgentPanel.Model.ImageAttachment encoded;
                string encodeError;
                if (Colloid.AgentPanel.Integration.ImageAttachmentEncoder.TryImportBytes(
                        File.ReadAllBytes(path), Path.GetFileName(path), out encoded, out encodeError))
                {
                    UapToolResults.AddImage(content, File.ReadAllBytes(encoded.path), encoded.mediaType);
                }
                else
                {
                    content.Add(JsonNode.NewObject().Set("type", "text")
                        .Set("text", "Warning: the image could not be embedded (" + encodeError + "); read the file instead."));
                }
            }
            return content;
        }

        /// <summary>
        /// Collects, sorts, caps and JSON-shapes the uGUI element list
        /// (annotate_ui / ui_only). <paramref name="gameViewSize"/> is
        /// handed back so the caller can rescale <paramref name="marks"/>
        /// from this canonical render-pixel space into an actually-
        /// captured image's own pixel dimensions before drawing.
        /// </summary>
        private static JsonNode BuildUiAnnotationResult(int maxElements, out List<UapUiAnnotationMark> marks, out Vector2 gameViewSize)
        {
            string warning;
            List<UapUiElementCandidate> candidates = UapUguiElementCatalog.Collect(out warning);
            gameViewSize = Handles.GetMainGameViewSize();
            bool truncated;
            JsonNode elements = UapUiAnnotationPolicy.BuildResult(
                candidates, gameViewSize.x, gameViewSize.y, maxElements, out truncated, out marks);
            JsonNode result = JsonNode.NewObject()
                .Set("viewWidth", Math.Round(gameViewSize.x))
                .Set("viewHeight", Math.Round(gameViewSize.y))
                .Set("totalFound", candidates.Count)
                .Set("truncated", truncated)
                .Set("elements", elements);
            if (warning != null)
            {
                result.Set("warning", warning);
            }
            return result;
        }
    }
}

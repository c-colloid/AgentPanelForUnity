using System;
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
                    + " on screen. Fails with a structured error when the requested view is not open.";
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
                                + " view must be \"scene\" and visible on screen.")))
                    .Set("additionalProperties", false);
            }
        }

        public JsonNode Execute(JsonNode input)
        {
            string viewArg = input["view"].AsString(null);
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

            Camera camera;
            SceneView sceneView;
            int width;
            int height;
            string viewDescription;
            if (!UapScreenCapture.TryResolveView(view, out camera, out sceneView, out width, out height, out viewDescription, out error))
            {
                throw new InvalidOperationException(error);
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = UapEditorScreenshotPaths.BuildOutputPath(DateTime.UtcNow, view, capture, projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string note;
            if (capture == UapScreenshotCapture.Window)
            {
                if (!UapScreenCapture.TryCaptureSceneViewWindow(sceneView, path, out width, out height, out error))
                {
                    throw new InvalidOperationException(error);
                }
                viewDescription += " as displayed (window capture)";
                note = null;
            }
            else
            {
                UapScreenCapture.CaptureCameraToPng(camera, width, height, path);
                note = "Note: this is an offscreen render of the view's camera -- lighting can differ from the"
                    + " Scene view and gizmos/marker labels are not included; use capture:\"window\" for the"
                    + " Scene view exactly as displayed.";
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
            JsonNode content = UapToolResults.Text(sb.ToString());
            if (input["return_image"].AsBool(false))
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

    }
}

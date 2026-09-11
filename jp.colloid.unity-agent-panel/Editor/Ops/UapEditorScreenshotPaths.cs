using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Which editor view uap_editor_screenshot targets.</summary>
    public enum UapScreenshotView
    {
        Scene,
        Game
    }

    /// <summary>
    /// How uap_editor_screenshot captures (design note 2026-09-07 decision
    /// M2/M2'): Camera renders the view's camera offscreen (no gizmos,
    /// Handles or labels; lighting can differ from the on-screen view);
    /// Window reads the Scene view's pixels as displayed.
    /// </summary>
    public enum UapScreenshotCapture
    {
        Camera,
        Window
    }

    /// <summary>One marker's place in a capture, for the "Markers in view" listing (pure data).</summary>
    public struct UapMarkerScreenPoint
    {
        public int Id;
        public string Label;
        /// <summary>False when the marker follows a target that no longer resolves.</summary>
        public bool Resolved;
        /// <summary>Camera.WorldToViewportPoint result (x/y in 0..1 when on screen, z &gt; 0 when in front).</summary>
        public Vector3 Viewport;
    }

    /// <summary>
    /// Pure (no UnityEditor/UnityEngine window or camera APIs) seams for
    /// uap_editor_screenshot: which view a "view" tool argument names, and
    /// where the resulting PNG goes. Split out from UapEditorScreenshotTool
    /// so unit tests can cover both WITHOUT a live SceneView/GameView --
    /// exactly the "unit tests cover the error path and the pure filename/
    /// target-selection seam only" scoping from the 5b kickoff (section A2),
    /// since a real capture can only ever be exercised interactively (batch
    /// mode / the EditMode test runner never has a SceneView or GameView
    /// open).
    /// </summary>
    public static class UapEditorScreenshotPaths
    {
        public const string DefaultViewArgument = "game";
        public const string DefaultCaptureArgument = "camera";

        /// <summary>
        /// Parses a "capture" tool argument: "camera" (default for
        /// null/empty) or "window", case-insensitive. False with a
        /// descriptive error for anything else.
        /// </summary>
        public static bool TryParseCapture(string raw, out UapScreenshotCapture capture, out string error)
        {
            error = null;
            string normalized = string.IsNullOrEmpty(raw) ? DefaultCaptureArgument : raw.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "camera":
                    capture = UapScreenshotCapture.Camera;
                    return true;
                case "window":
                    capture = UapScreenshotCapture.Window;
                    return true;
                default:
                    capture = default(UapScreenshotCapture);
                    error = "'capture' must be \"camera\" or \"window\" (was: \"" + raw + "\").";
                    return false;
            }
        }

        /// <summary>
        /// The "Markers in view" block appended to every capture result
        /// (design note 2026-09-07 section 1.3.3): one line per marker with
        /// its image pixel coordinates (origin top-left, x right, y down)
        /// when it is inside the captured frame, "off-screen" when it is
        /// outside or behind the camera, "not drawn" when its target no
        /// longer resolves. Null when there are no markers at all, so the
        /// caller appends nothing.
        /// </summary>
        public static string FormatMarkersInView(IList<UapMarkerScreenPoint> points, int width, int height)
        {
            if (points == null || points.Count == 0)
            {
                return null;
            }
            var sb = new StringBuilder(64 + points.Count * 48);
            sb.Append("Markers in view (image ").Append(width).Append('x').Append(height).Append("):");
            for (int i = 0; i < points.Count; i++)
            {
                UapMarkerScreenPoint point = points[i];
                sb.Append("\n  #").Append(point.Id);
                if (!string.IsNullOrEmpty(point.Label))
                {
                    sb.Append(" \"").Append(point.Label).Append('"');
                }
                if (!point.Resolved)
                {
                    sb.Append(" not drawn (target no longer exists)");
                    continue;
                }
                Vector3 v = point.Viewport;
                bool onScreen = v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
                if (!onScreen)
                {
                    sb.Append(" off-screen");
                    continue;
                }
                int px = Mathf.Clamp(Mathf.RoundToInt(v.x * width), 0, Math.Max(0, width - 1));
                int py = Mathf.Clamp(Mathf.RoundToInt((1f - v.y) * height), 0, Math.Max(0, height - 1));
                sb.Append(" at pixel (").Append(px).Append(", ").Append(py).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a "view" tool argument: "scene" or "game" (case-
        /// insensitive, surrounding whitespace ignored); null/empty
        /// defaults to "game". Returns false with a descriptive
        /// <paramref name="error"/> for anything else.
        /// </summary>
        public static bool TryParseView(string raw, out UapScreenshotView view, out string error)
        {
            error = null;
            string normalized = string.IsNullOrEmpty(raw) ? DefaultViewArgument : raw.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "scene":
                    view = UapScreenshotView.Scene;
                    return true;
                case "game":
                    view = UapScreenshotView.Game;
                    return true;
                default:
                    view = default(UapScreenshotView);
                    error = "'view' must be \"scene\" or \"game\" (was: \"" + raw + "\").";
                    return false;
            }
        }

        /// <summary>
        /// Builds the absolute output path for a captured PNG: a project-
        /// root-relative "Temp/UapOpsScreenshots/" folder (Unity's own
        /// per-project scratch directory -- never version-controlled, never
        /// asset-imported) plus a view-and-timestamp-qualified file name so
        /// rapid/concurrent calls never collide. Uses forward slashes
        /// regardless of platform, matching every other UapOps path in the
        /// tool result text (see UapAssetCreateTool et al.).
        /// </summary>
        public static string BuildOutputPath(DateTime utcTimestamp, UapScreenshotView view, string projectRoot)
        {
            return BuildOutputPath(utcTimestamp, view, UapScreenshotCapture.Camera, projectRoot);
        }

        /// <summary>
        /// As <see cref="BuildOutputPath(DateTime, UapScreenshotView, string)"/>,
        /// with the capture mode in the file name ("uap_scene_window_...")
        /// so a window capture never overwrites or gets confused with a
        /// camera render taken the same millisecond.
        /// </summary>
        public static string BuildOutputPath(DateTime utcTimestamp, UapScreenshotView view,
            UapScreenshotCapture capture, string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException("'projectRoot' must not be empty.", "projectRoot");
            }
            string viewTag = view == UapScreenshotView.Scene ? "scene" : "game";
            string captureTag = capture == UapScreenshotCapture.Window ? "window_" : string.Empty;
            string fileName = "uap_" + viewTag + "_" + captureTag
                + utcTimestamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png";
            string folder = Path.Combine(projectRoot, "Temp", "UapOpsScreenshots");
            return Path.Combine(folder, fileName).Replace('\\', '/');
        }
    }
}

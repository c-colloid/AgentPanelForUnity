using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEditor.Media;
using UnityEditorInternal;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The background half of uap_record_video (docs/design-notes/2026-09-
    /// 23-uloop-phase2.md section 3.1, uLoop's record-video): a static,
    /// EditorApplication.update-driven encoding session, entirely outside
    /// UapMainThreadDispatcher's per-call lifecycle -- action:start sets
    /// this up and returns immediately; the actual frame-by-frame capture
    /// happens on later, ordinary editor ticks, the same way uap_compile's
    /// CompilationPipeline subscription runs outside the call that armed it.
    ///
    /// Encoding uses UnityEditor.Media.MediaEncoder (Editor-bundled, no
    /// package dependency, unlike uap_game_mouse/uap_game_key's reflection
    /// into optional packages). Two things about it were confirmed against
    /// UnityCsReference (2022.3 and 6000.0 branches, Editor/Mono/Media/
    /// Bindings/MediaEncoder.bindings.cs -- byte-identical between the two
    /// branches bar one internal attribute argument) rather than assumed:
    ///
    ///   1. VideoTrackAttributes.codec is an INTERNAL field -- public code
    ///      (this class included) cannot set it, so every MediaEncoder this
    ///      class creates uses VideoCodec.Auto (the type's implicit default,
    ///      0). The public MediaEncoder(string, VideoTrackAttributes)
    ///      constructor is the one this class uses (no audio track).
    ///   2. AddFrame(Texture2D) always passes MediaTime.Invalid, meaning
    ///      "the next frame in sequence at the declared frameRate" -- there
    ///      is no way to stamp an explicit timestamp through that overload,
    ///      which is why this class paces itself against wall-clock time by
    ///      calling AddFrame the same texture multiple times to catch up
    ///      (see <see cref="OnUpdate"/>) rather than relying on an explicit
    ///      MediaTime per frame.
    ///
    /// What could NOT be confirmed against source (native-only, closed):
    /// whether VideoCodec.Auto truly resolves purely from the output file's
    /// extension (.mp4 -> H264, .webm -> VP8) with no other signal, and
    /// that Linux specifically lacks an H.264 encoder. The extension choice
    /// is standard, widely repeated Unity guidance (Unity Manual "Video
    /// encoding compatibility reference" / support-forum guidance: the
    /// Linux Editor has no H.264 encoder and webm/VP8 is the supported
    /// substitute) rather than something this session could verify live --
    /// see the design note's "unverified" list.
    /// </summary>
    public static class UapVideoRecorder
    {
        private const string SessionStateKey = "Colloid.AgentPanel.Ops.UapRecordVideoResult";

        private sealed class ActiveSession
        {
            public string RecordingId;
            public string Path;
            public UapScreenshotView ViewMode;
            public UapScreenshotCapture CaptureMode;
            public int Fps;
            public int Width;
            public int Height;
            /// <summary>The view's actual point size at Start, before any max_width downscale. Equal to Width/Height when no downscale was requested.</summary>
            public int SourceWidth;
            public int SourceHeight;
            public double MaxSeconds;
            public bool StopOnPlayExit;
            public double StartedAtEditorTime;
            public long StartedAtUtcTicks;
            public int FramesWritten;
            public int DroppedFrames;
            public bool HasGoodFrame;
            public MediaEncoder Encoder;
            public Texture2D FrameTexture;
            /// <summary>
            /// Only allocated for capture:"window" when SourceWidth/Height
            /// differ from Width/Height (a max_width downscale is in
            /// effect): ReadScreenPixel has no scale parameter of its own
            /// (it reads exactly the rectangle it is asked for), so a
            /// window-mode capture must read the FULL source rectangle into
            /// WindowSourceTexture and then Graphics.Blit it down into
            /// WindowScaleTarget (an encode-size RenderTexture) before that
            /// gets read back into FrameTexture -- otherwise the encode
            /// would just be a cropped, zoomed-in corner of the window
            /// instead of the whole view scaled down. Null (and unused)
            /// whenever no downscale applies, including every camera-mode
            /// capture, since Camera.Render already renders at the target
            /// RenderTexture's resolution.
            /// </summary>
            public Texture2D WindowSourceTexture;
            public RenderTexture WindowScaleTarget;
        }

        private static ActiveSession _active;
        private static bool _hooksInstalled;

        /// <summary>True while THIS domain holds a live encoding session. The one thing action:start's "already recording" refusal is allowed to trust -- see <see cref="Status"/>'s own comment for why a persisted "recording" record alone must never gate a new start.</summary>
        public static bool IsActive
        {
            get { return _active != null; }
        }

        // -- Public entry points (called from UapRecordVideoTool.Execute) --

        public static JsonNode Start(JsonNode input)
        {
            string alreadyActiveError;
            if (!UapRecordVideoPolicy.DecideStart(IsActive, out alreadyActiveError))
            {
                throw new InvalidOperationException(alreadyActiveError);
            }

            UapScreenshotView view;
            string viewError;
            if (!UapEditorScreenshotPaths.TryParseView(input["view"].AsString(null), out view, out viewError))
            {
                throw new ArgumentException(viewError);
            }
            UapScreenshotCapture capture;
            string captureError;
            if (!UapEditorScreenshotPaths.TryParseCapture(input["capture"].AsString(null), out capture, out captureError))
            {
                throw new ArgumentException(captureError);
            }

            int fps = input["fps"].IsNull ? UapRecordVideoPolicy.DefaultFps : input["fps"].AsInt(UapRecordVideoPolicy.DefaultFps);
            string fpsError;
            if (!UapRecordVideoPolicy.ValidateFps(fps, out fpsError))
            {
                throw new ArgumentException(fpsError);
            }

            double maxSeconds = input["max_seconds"].IsNull
                ? UapRecordVideoPolicy.DefaultMaxSeconds
                : input["max_seconds"].AsDouble(UapRecordVideoPolicy.DefaultMaxSeconds);
            string maxSecondsError;
            if (!UapRecordVideoPolicy.ValidateMaxSeconds(maxSeconds, out maxSecondsError))
            {
                throw new ArgumentException(maxSecondsError);
            }

            int? maxWidth = null;
            if (!input["max_width"].IsNull)
            {
                int requestedMaxWidth = input["max_width"].AsInt(0);
                string maxWidthError;
                if (!UapRecordVideoPolicy.ValidateMaxWidth(requestedMaxWidth, out maxWidthError))
                {
                    throw new ArgumentException(maxWidthError);
                }
                maxWidth = requestedMaxWidth;
            }

            bool stopOnPlayExit = input["stop_on_play_exit"].AsBool(true);

            // The encoder's frame size is fixed for its whole lifetime
            // (MediaEncoder has no "resize" concept), so the source is
            // measured ONCE, up front; every later tick captures at exactly
            // this size regardless of what the view does afterwards.
            int sourceWidth;
            int sourceHeight;
            string sizeError;
            if (!TryResolveSourceSize(view, capture, out sourceWidth, out sourceHeight, out sizeError))
            {
                throw new InvalidOperationException(sizeError);
            }
            int width;
            int height;
            UapRecordVideoPolicy.ResolveEncodeSize(sourceWidth, sourceHeight, maxWidth, out width, out height);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string extension = UapRecordVideoPolicy.ChooseExtension(CurrentHostOs());
            DateTime nowUtc = DateTime.UtcNow;
            string outputPath = UapRecordVideoPolicy.BuildOutputPath(projectRoot, nowUtc, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var videoAttrs = new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps),
                width = (uint)width,
                height = (uint)height,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.Medium,
            };

            MediaEncoder encoder;
            try
            {
                encoder = new MediaEncoder(outputPath, videoAttrs);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not start encoding to '" + outputPath + "': " + ex.Message);
            }

            var session = new ActiveSession
            {
                RecordingId = Guid.NewGuid().ToString("N"),
                Path = outputPath,
                ViewMode = view,
                CaptureMode = capture,
                Fps = fps,
                Width = width,
                Height = height,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                MaxSeconds = maxSeconds,
                StopOnPlayExit = stopOnPlayExit,
                StartedAtEditorTime = EditorApplication.timeSinceStartup,
                StartedAtUtcTicks = nowUtc.Ticks,
                Encoder = encoder,
                FrameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false),
            };
            if (capture == UapScreenshotCapture.Window && (sourceWidth != width || sourceHeight != height))
            {
                // See ActiveSession.WindowSourceTexture's own comment: only
                // window-mode capture needs this extra scale step, and only
                // when max_width actually changed the encode size.
                session.WindowSourceTexture = new Texture2D(sourceWidth, sourceHeight, TextureFormat.RGBA32, false);
                session.WindowScaleTarget = new RenderTexture(width, height, 0);
            }
            _active = session;

            // Capture and write frame 0 immediately: a status call right
            // after Start (before any Editor tick has run) should already
            // report a recording that has BEGUN, not one that merely will.
            // A capture failure here is not fatal (OnUpdate keeps retrying
            // every tick -- the view may simply not be ready yet), but an
            // AddFrame failure means the encoder itself cannot accept this
            // size/format and would only fail again on its very next tick,
            // so that aborts the whole start instead of leaving a
            // "recording" doomed to immediately self-terminate.
            string firstCaptureError;
            if (TryCaptureFrame(session, out firstCaptureError))
            {
                // AddFrame is a native call that returns false on a failed
                // encode (mismatched size/format, or the encoder rejecting
                // the frame for some other reason) WITHOUT throwing -- only
                // catching exceptions here would let that failure through
                // silently as if the first frame had been written.
                bool added;
                Exception addException = null;
                try
                {
                    added = session.Encoder.AddFrame(session.FrameTexture);
                }
                catch (Exception ex)
                {
                    added = false;
                    addException = ex;
                }
                if (!added)
                {
                    _active = null;
                    DisposeWindowScaleHelpers(session);
                    try { session.Encoder.Dispose(); } catch (Exception) { }
                    try { UnityEngine.Object.DestroyImmediate(session.FrameTexture); } catch (Exception) { }
                    string reason = addException != null ? addException.Message : "the encoder rejected the frame.";
                    throw new InvalidOperationException("Could not encode the first frame: " + reason);
                }
                session.FramesWritten = 1;
                session.HasGoodFrame = true;
            }

            EnsureHooksInstalled();

            var persisted = BuildResult(session, UapRecordVideoState.Recording, string.Empty, 0L);
            persisted.Message = firstCaptureError == null
                ? "Recording started."
                : "Recording started (first frame not captured yet: " + firstCaptureError
                    + "; retrying every Editor tick).";
            if (!EditorApplication.isPlaying)
            {
                persisted.Warning = "Started in Edit Mode: entering Play Mode afterward reloads the domain by"
                    + " default, which ends this recording (stoppedBy:\"assembly-reload\") -- to record"
                    + " gameplay, start Play Mode first (uap_play_mode action:start), then call"
                    + " uap_record_video action:start.";
            }
            SavePersisted(persisted);
            return UapRecordVideoPolicy.ToJson(persisted);
        }

        public static JsonNode Stop()
        {
            if (!IsActive)
            {
                throw new InvalidOperationException("No recording is in progress; call uap_record_video action:start first.");
            }
            StopInternal(UapRecordVideoPolicy.StoppedByStop, null);
            return UapRecordVideoPolicy.ToJson(LoadPersisted());
        }

        public static JsonNode Status()
        {
            if (IsActive)
            {
                ActiveSession session = _active;
                double elapsed = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
                UapRecordVideoResult live = BuildResult(session, UapRecordVideoState.Recording, string.Empty,
                    elapsed < 0 ? 0 : elapsed);
                live.FileSizeBytes = TryGetFileSize(session.Path);
                live.Message = "Recording in progress.";
                return UapRecordVideoPolicy.ToJson(live);
            }

            UapRecordVideoResult persisted = LoadPersisted();
            if (persisted.State == UapRecordVideoState.Recording)
            {
                // A "Recording" record with nothing live in THIS domain
                // means a reload/restart happened without OnBeforeAssembly
                // Reload getting to finalize it (an editor crash, or a
                // reload this class was never subscribed for) -- say so
                // rather than claim a recording is still running.
                persisted.Message = "No recording is active in this Editor session; the last known state was"
                    + " 'recording' from before a reload/restart this tool did not observe finishing cleanly.";
            }
            else if (persisted.State == UapRecordVideoState.Idle)
            {
                persisted.Message = "No recording has been started through uap_record_video yet.";
            }
            else if (string.IsNullOrEmpty(persisted.Message))
            {
                persisted.Message = UapRecordVideoPolicy.DescribeStoppedBy(persisted.StoppedBy);
            }
            return UapRecordVideoPolicy.ToJson(persisted);
        }

        // -- Editor-event-driven pacing loop --------------------------------

        private static void OnUpdate()
        {
            ActiveSession session = _active;
            if (session == null)
            {
                UnhookAll();
                return;
            }
            double elapsed = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            if (elapsed >= session.MaxSeconds)
            {
                StopInternal(UapRecordVideoPolicy.StoppedByDuration, null);
                return;
            }
            int targetCount = UapRecordVideoPolicy.TargetFrameCount(elapsed, session.Fps);
            int framesNeeded = targetCount - session.FramesWritten;
            if (framesNeeded <= 0)
            {
                // Ahead of pace: the Editor is ticking faster than fps needs -- skip this tick's frame.
                return;
            }
            string captureError;
            bool fresh = TryCaptureFrame(session, out captureError);
            if (fresh)
            {
                session.HasGoodFrame = true;
            }
            if (!session.HasGoodFrame)
            {
                // Never captured a single good frame yet (e.g. the view was
                // never open) -- nothing to write; try again next tick.
                return;
            }
            try
            {
                for (int i = 0; i < framesNeeded; i++)
                {
                    // AddFrame returns false (rather than throwing) on a
                    // failed encode -- see the matching comment in Start --
                    // so that must be treated exactly like a caught
                    // exception instead of being allowed to pass silently.
                    if (!session.Encoder.AddFrame(session.FrameTexture))
                    {
                        StopInternal(UapRecordVideoPolicy.StoppedByError,
                            "Encoding a frame failed: the encoder rejected the frame.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                StopInternal(UapRecordVideoPolicy.StoppedByError, "Encoding a frame failed: " + ex.Message);
                return;
            }
            session.FramesWritten += framesNeeded;
            session.DroppedFrames += fresh ? framesNeeded - 1 : framesNeeded;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }
            ActiveSession session = _active;
            if (session != null && session.StopOnPlayExit)
            {
                StopInternal(UapRecordVideoPolicy.StoppedByPlayModeExit, null);
            }
        }

        /// <summary>
        /// Last chance to finalize the encoder before the domain tears
        /// down: MediaEncoder holds a native handle that cannot survive a
        /// reload, so an in-progress recording that does not get Dispose()d
        /// here is not just interrupted but left with an unfinalized (very
        /// likely unplayable) output file. Reached by ANY reload while
        /// recording -- a script compile via uap_compile, an external
        /// editor edit, or entering/leaving Play Mode with domain reload
        /// enabled and stop_on_play_exit:false (or a reload that races
        /// ahead of the playModeStateChanged handler above).
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            if (_active == null)
            {
                return;
            }
            StopInternal(UapRecordVideoPolicy.StoppedByAssemblyReload,
                "The Editor is reloading the domain (a script compile, or entering/leaving Play Mode with"
                + " domain reload enabled); the recording was finalized up to the last captured frame.");
        }

        private static void StopInternal(string stoppedBy, string message)
        {
            ActiveSession session = _active;
            if (session == null)
            {
                return;
            }
            _active = null;
            UnhookAll();

            try
            {
                session.Encoder.Dispose();
            }
            catch (Exception)
            {
                // Best-effort finalize -- a failing Dispose must not stop the rest of this cleanup from running.
            }

            double secondsRecorded = EditorApplication.timeSinceStartup - session.StartedAtEditorTime;
            UapRecordVideoResult result = BuildResult(session, UapRecordVideoState.Stopped, stoppedBy,
                secondsRecorded < 0 ? 0 : secondsRecorded);
            result.FinishedAtUtcTicks = DateTime.UtcNow.Ticks;
            result.FileSizeBytes = TryGetFileSize(session.Path);
            result.Message = message ?? UapRecordVideoPolicy.DescribeStoppedBy(stoppedBy);
            SavePersisted(result);

            if (session.FrameTexture != null)
            {
                try { UnityEngine.Object.DestroyImmediate(session.FrameTexture); }
                catch (Exception) { }
            }
            DisposeWindowScaleHelpers(session);

            try { PruneOldRecordings(Path.GetDirectoryName(session.Path)); }
            catch (Exception) { }
        }

        // -- Hooks (lazy on first Start, unsubscribed once the recording ends) --

        private static void EnsureHooksInstalled()
        {
            if (_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = true;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void UnhookAll()
        {
            if (!_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = false;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        // -- Frame capture ---------------------------------------------------

        private static bool TryCaptureFrame(ActiveSession session, out string error)
        {
            return session.CaptureMode == UapScreenshotCapture.Window
                ? TryCaptureWindowFrame(session, out error)
                : TryCaptureCameraFrame(session, out error);
        }

        private static bool TryCaptureCameraFrame(ActiveSession session, out string error)
        {
            Camera camera;
            if (!UapScreenCapture.TryResolveView(session.ViewMode, out camera, out _,
                    out _, out _, out _, out error))
            {
                return false;
            }
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var rt = new RenderTexture(session.Width, session.Height, 24);
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                session.FrameTexture.ReadPixels(new Rect(0, 0, session.Width, session.Height), 0, 0);
                session.FrameTexture.Apply();
                return true;
            }
            catch (Exception ex)
            {
                error = "Rendering the camera frame failed: " + ex.Message;
                return false;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Reads the view's actual displayed pixels (design note section
        /// 3.1, design decisions): for view:"scene" this reuses
        /// UapScreenCapture's own tracked geometry (its camera area,
        /// measured live from inside SceneView.duringSceneGui -- the exact
        /// same source uap_editor_screenshot's capture:"window" uses). For
        /// view:"game" there is no equivalent per-frame GUI hook to crop
        /// GameView's toolbar out precisely (GameView is sealed/internal,
        /// unlike SceneView it exposes no duringSceneGui-style callback), so
        /// this reads the WHOLE window from EditorWindow.position -- which
        /// UnityCsReference documents as "the position of the window in
        /// screen space" -- accepting that the top of the image may include
        /// a few rows of Unity's own Play-mode toolbar rather than only
        /// game content. Either way the read is at the session's fixed
        /// SOURCE size (the window's size measured once at Start, NOT the
        /// possibly-downscaled encode Width/Height -- see
        /// ActiveSession.WindowSourceTexture's own comment for why), so a
        /// window that grew after Start just has its extra margin ignored;
        /// one that SHRANK returns too few pixels, which is treated as an
        /// ordinary capture failure for that tick (the last good frame is
        /// repeated -- see OnUpdate).
        /// </summary>
        private static bool TryCaptureWindowFrame(ActiveSession session, out string error)
        {
            error = null;
            Vector2 origin;
            if (session.ViewMode == UapScreenshotView.Scene)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                UapScreenCapture.SceneViewGeometry geometry;
                if (sceneView == null || !UapScreenCapture.TryGetGeometry(sceneView, out geometry))
                {
                    error = "The Scene view has not been drawn on screen yet (batch mode, minimized, or hidden"
                        + " behind another tab) -- open it, or use capture:\"camera\" instead.";
                    return false;
                }
                origin = geometry.ScreenOrigin;
            }
            else
            {
                EditorWindow gameView = UapScreenCapture.FindOpenGameView();
                if (gameView == null)
                {
                    error = "No Game view is open -- open one, or use capture:\"camera\" instead.";
                    return false;
                }
                origin = gameView.position.position;
            }

            Color[] pixels;
            try
            {
                // Always read at the SOURCE size, never at the (possibly
                // downscaled) encode Width/Height -- ReadScreenPixel reads
                // exactly the rectangle it is given, with no scaling of its
                // own, so reading a smaller rectangle would just crop to
                // the window's top-left corner instead of shrinking it.
                pixels = InternalEditorUtility.ReadScreenPixel(origin, session.SourceWidth, session.SourceHeight);
            }
            catch (Exception ex)
            {
                error = "Reading the view's pixels failed: " + ex.Message;
                return false;
            }
            if (pixels == null || pixels.Length < session.SourceWidth * session.SourceHeight)
            {
                error = "Reading the view's pixels returned no image (is the window visible on screen, and at"
                    + " least " + session.SourceWidth + "x" + session.SourceHeight + "?).";
                return false;
            }
            try
            {
                if (session.WindowSourceTexture == null)
                {
                    // No downscale in effect: source size already equals the encode size.
                    session.FrameTexture.SetPixels(pixels);
                    session.FrameTexture.Apply();
                }
                else
                {
                    // max_width is downscaling this recording -- scale the
                    // full source rectangle down into the encode-size
                    // FrameTexture with a GPU blit (bilinear), the same way
                    // a camera-mode capture is implicitly resized by
                    // rendering straight into an encode-size RenderTexture.
                    session.WindowSourceTexture.SetPixels(pixels);
                    session.WindowSourceTexture.Apply();
                    RenderTexture previousActive = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(session.WindowSourceTexture, session.WindowScaleTarget);
                        RenderTexture.active = session.WindowScaleTarget;
                        session.FrameTexture.ReadPixels(new Rect(0, 0, session.Width, session.Height), 0, 0);
                        session.FrameTexture.Apply();
                    }
                    finally
                    {
                        RenderTexture.active = previousActive;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Copying the captured pixels into the encode frame failed: " + ex.Message;
                return false;
            }
        }

        private static bool TryResolveSourceSize(UapScreenshotView view, UapScreenshotCapture capture,
            out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;
            if (capture == UapScreenshotCapture.Camera)
            {
                return UapScreenCapture.TryResolveView(view, out _, out _, out width, out height,
                    out _, out error);
            }
            if (view == UapScreenshotView.Scene)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                UapScreenCapture.SceneViewGeometry geometry;
                if (sceneView == null || !UapScreenCapture.TryGetGeometry(sceneView, out geometry))
                {
                    error = "The Scene view has not been drawn on screen yet (batch mode, minimized, or hidden"
                        + " behind another tab) -- open it, or use capture:\"camera\" instead.";
                    return false;
                }
                width = geometry.PointWidth;
                height = geometry.PointHeight;
                return true;
            }
            EditorWindow gameView = UapScreenCapture.FindOpenGameView();
            if (gameView == null)
            {
                error = "No Game view is open (e.g. running in batch mode) -- open one, or use"
                    + " capture:\"camera\" instead.";
                return false;
            }
            width = Mathf.RoundToInt(gameView.position.width);
            height = Mathf.RoundToInt(gameView.position.height);
            return true;
        }

        // -- Persistence (SessionState; survives the domain reload a recording itself may end in) --

        public static UapRecordVideoResult LoadPersisted()
        {
            string json = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return UapRecordVideoResult.Idle();
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json, out node, out error))
            {
                return UapRecordVideoResult.Idle();
            }
            return UapRecordVideoPolicy.FromJson(node);
        }

        private static void SavePersisted(UapRecordVideoResult result)
        {
            SessionState.SetString(SessionStateKey, JsonWriter.Write(UapRecordVideoPolicy.ToJson(result)));
        }

        private static UapRecordVideoResult BuildResult(ActiveSession session, UapRecordVideoState state,
            string stoppedBy, double secondsRecorded)
        {
            return new UapRecordVideoResult
            {
                State = state,
                RecordingId = session.RecordingId,
                Path = session.Path,
                View = ViewName(session.ViewMode),
                Capture = CaptureName(session.CaptureMode),
                Fps = session.Fps,
                Width = session.Width,
                Height = session.Height,
                MaxSeconds = session.MaxSeconds,
                StopOnPlayExit = session.StopOnPlayExit,
                StartedAtUtcTicks = session.StartedAtUtcTicks,
                FinishedAtUtcTicks = 0L,
                FramesWritten = session.FramesWritten,
                DroppedFrames = session.DroppedFrames,
                SecondsRecorded = secondsRecorded,
                FileSizeBytes = 0L,
                StoppedBy = stoppedBy ?? string.Empty,
                Message = string.Empty,
                Warning = string.Empty,
            };
        }

        private static string ViewName(UapScreenshotView view)
        {
            return view == UapScreenshotView.Scene ? "scene" : "game";
        }

        private static string CaptureName(UapScreenshotCapture capture)
        {
            return capture == UapScreenshotCapture.Window ? "window" : "camera";
        }

        private static long TryGetFileSize(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        private static UapVideoHostOs CurrentHostOs()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return UapVideoHostOs.Windows;
                case RuntimePlatform.OSXEditor: return UapVideoHostOs.Mac;
                case RuntimePlatform.LinuxEditor: return UapVideoHostOs.Linux;
                default: return UapVideoHostOs.Other;
            }
        }

        private static void PruneOldRecordings(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }
            string[] files = Directory.GetFiles(folder, "uap_video_*.*");
            Array.Sort(files, StringComparer.Ordinal);
            List<string> toDelete = UapRecordVideoPolicy.SelectFilesToPrune(files, UapRecordVideoPolicy.DefaultKeepCount, null);
            for (int i = 0; i < toDelete.Count; i++)
            {
                try { File.Delete(toDelete[i]); }
                catch (Exception) { }
            }
        }

        // -- Test seam (InternalsVisibleTo Colloid.AgentPanel.Editor.Tests) --

        /// <summary>Forgets any live session (finalizing its encoder best-effort, without pruning) and the persisted record. A real recording must never be left running into the next test.</summary>
        internal static void ResetForTests()
        {
            if (_active != null)
            {
                try { _active.Encoder.Dispose(); }
                catch (Exception) { }
                if (_active.FrameTexture != null)
                {
                    try { UnityEngine.Object.DestroyImmediate(_active.FrameTexture); }
                    catch (Exception) { }
                }
                DisposeWindowScaleHelpers(_active);
            }
            _active = null;
            UnhookAll();
            SessionState.SetString(SessionStateKey, string.Empty);
        }

        /// <summary>Releases the optional window-capture downscale helpers (see ActiveSession.WindowSourceTexture's own comment) -- a no-op when a recording never needed them.</summary>
        private static void DisposeWindowScaleHelpers(ActiveSession session)
        {
            if (session.WindowSourceTexture != null)
            {
                try { UnityEngine.Object.DestroyImmediate(session.WindowSourceTexture); }
                catch (Exception) { }
                session.WindowSourceTexture = null;
            }
            if (session.WindowScaleTarget != null)
            {
                try { session.WindowScaleTarget.Release(); }
                catch (Exception) { }
                try { UnityEngine.Object.DestroyImmediate(session.WindowScaleTarget); }
                catch (Exception) { }
                session.WindowScaleTarget = null;
            }
        }
    }
}

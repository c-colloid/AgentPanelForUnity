using System;
using System.Collections.Generic;
using System.Globalization;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What uap_record_video was asked to do. Default is Start -- like uap_compile's action:request default, this tool exists to DO something, and stop/status only ever act on a recording start already created (docs/design-notes/2026-09-23-uloop-phase2.md section 3.1).</summary>
    public enum UapRecordVideoAction
    {
        Start = 0,
        Stop = 1,
        Status = 2,
    }

    /// <summary>
    /// Lifecycle of the one recording an Editor session can have at a time.
    /// Idle: nothing has ever been started (or the record was cleared).
    /// Recording: in progress -- <see cref="UapVideoRecorder.IsActive"/>
    /// says whether THIS domain still holds it live; a "Recording" record
    /// with that false means a reload/restart happened without a clean
    /// finalize being observed. Stopped: terminal; StoppedBy on the result
    /// says why (see the StoppedBy* constants below).
    /// </summary>
    public enum UapRecordVideoState
    {
        Idle = 0,
        Recording = 1,
        Stopped = 2,
    }

    /// <summary>
    /// Host OS as it affects which container/codec UnityEditor.Media.
    /// MediaEncoder can actually produce (kept Unity-free -- no
    /// UnityEngine.Application -- so ci/SmokeTests can cover
    /// <see cref="UapRecordVideoPolicy.ChooseExtension"/> without it).
    /// Windows/Mac: the Editor has an H.264 encoder, so a ".mp4" path
    /// resolves via VideoCodec.Auto to H264. Linux: the Editor's
    /// MediaEncoder has no H.264 encoder (confirmed via Unity's own manual/
    /// support guidance -- see UapVideoRecorder's class comment for the
    /// exact source), so the same Auto codec against a ".webm" path
    /// resolves to VP8 instead. Other is treated like Linux (the
    /// conservative choice when the host cannot be identified).
    /// </summary>
    public enum UapVideoHostOs
    {
        Windows,
        Mac,
        Linux,
        Other,
    }

    /// <summary>
    /// The full state uap_record_video persists (SessionState, via
    /// UapVideoRecorder) and reports from action:status/start/stop. Plain
    /// data -- <see cref="UapRecordVideoPolicy.ToJson"/>/
    /// <see cref="UapRecordVideoPolicy.FromJson"/> are its only
    /// (de)serializers, the same split UapCompileResult/UapCompilePolicy use.
    /// </summary>
    public sealed class UapRecordVideoResult
    {
        public UapRecordVideoState State;
        public string RecordingId;
        public string Path;
        /// <summary>"scene" | "game" -- mirrors uap_editor_screenshot's "view" wire values.</summary>
        public string View;
        /// <summary>"camera" | "window" -- mirrors uap_editor_screenshot's "capture" wire values.</summary>
        public string Capture;
        public int Fps;
        public int Width;
        public int Height;
        public double MaxSeconds;
        public bool StopOnPlayExit;
        /// <summary>UTC DateTime.Ticks when action:start armed this record. 0 = unset (Idle).</summary>
        public long StartedAtUtcTicks;
        /// <summary>UTC DateTime.Ticks when the recording stopped. 0 while Recording.</summary>
        public long FinishedAtUtcTicks;
        public int FramesWritten;
        /// <summary>Frames written that repeated the last successfully captured image rather than a fresh one (a wall-clock catch-up duplicate, or a capture that failed for that tick) -- see UapVideoRecorder's pacing loop.</summary>
        public int DroppedFrames;
        public double SecondsRecorded;
        public long FileSizeBytes;
        /// <summary>Empty while Recording; one of the StoppedBy* constants once Stopped.</summary>
        public string StoppedBy;
        public string Message;
        /// <summary>Non-fatal caution for the reply (e.g. "started in Edit Mode"); empty when there is none.</summary>
        public string Warning;

        /// <summary>A fresh, empty record: nothing started yet.</summary>
        public static UapRecordVideoResult Idle()
        {
            return new UapRecordVideoResult
            {
                State = UapRecordVideoState.Idle,
                RecordingId = string.Empty,
                Path = string.Empty,
                View = string.Empty,
                Capture = string.Empty,
                StoppedBy = string.Empty,
                Message = string.Empty,
                Warning = string.Empty,
            };
        }
    }

    /// <summary>
    /// Every rule uap_record_video decides by, with no UnityEditor/
    /// UnityEngine in sight (docs/design-notes/2026-09-23-uloop-phase2.md
    /// section 3.1) -- argument ranges, even-dimension rounding, output
    /// naming/pruning, the wall-clock pacing target and the persisted JSON
    /// shape. Split out for the same reason UapCompilePolicy/
    /// UapGameInputPolicy are: worth getting right, and cheap to run in the
    /// license-free smoke gate (ci/SmokeTests) as well as under a Unity
    /// Editor. UapVideoRecorder (Unity-aware: MediaEncoder, EditorApplication,
    /// the actual camera/window pixel capture) is the only thing that is not
    /// here.
    /// </summary>
    public static class UapRecordVideoPolicy
    {
        public const double DefaultMaxSeconds = 60.0;
        public const double MaxMaxSeconds = 300.0;
        public const int DefaultFps = 30;
        public const int MinFps = 1;
        public const int MaxFps = 60;
        public const int MinDimension = 2;
        public const int MaxDimensionCap = 4096;
        public const int MinMaxWidth = 16;

        /// <summary>
        /// How many past recordings' output files are kept under the video
        /// output folder; the rest are pruned right after a recording stops
        /// (uLoop's own record-video keeps a bounded set for the same
        /// reason -- an agent that records often must not slowly fill the
        /// disk with files far larger than uap_editor_screenshot's PNGs).
        /// </summary>
        public const int DefaultKeepCount = 10;

        public const string StoppedByStop = "stop";
        public const string StoppedByDuration = "duration";
        public const string StoppedByPlayModeExit = "play-mode-exit";
        public const string StoppedByAssemblyReload = "assembly-reload";
        public const string StoppedByError = "error";

        /// <summary>Pure: an absent action is "start"; an unrecognized one is refused BY NAME.</summary>
        public static UapRecordVideoAction ParseAction(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return UapRecordVideoAction.Start;
            }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "start", StringComparison.OrdinalIgnoreCase)) { return UapRecordVideoAction.Start; }
            if (string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase)) { return UapRecordVideoAction.Stop; }
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase)) { return UapRecordVideoAction.Status; }
            throw new ArgumentException("Unknown action '" + value + "'. Valid actions: start, stop, status.");
        }

        /// <summary>Pure: the whole refusal rule for action:start. Only ONE recording may run at a time; <paramref name="alreadyActive"/> must come from UapVideoRecorder.IsActive (in-memory, this domain) rather than a persisted state string -- see that class's own comment for why a stale persisted "recording" record must never block a new one.</summary>
        public static bool DecideStart(bool alreadyActive, out string error)
        {
            if (alreadyActive)
            {
                error = "A recording is already in progress. Call uap_record_video action:stop to end it, or"
                    + " action:status to check on it, before starting another -- only one recording runs at a"
                    + " time.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool ValidateFps(int fps, out string error)
        {
            if (fps < MinFps || fps > MaxFps)
            {
                error = "'fps' must be from " + MinFps.ToString(CultureInfo.InvariantCulture) + " to "
                    + MaxFps.ToString(CultureInfo.InvariantCulture) + " (got " + fps.ToString(CultureInfo.InvariantCulture) + ").";
                return false;
            }
            error = null;
            return true;
        }

        public static bool ValidateMaxSeconds(double seconds, out string error)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            {
                error = "'max_seconds' must be a positive number of seconds, up to "
                    + MaxMaxSeconds.ToString(CultureInfo.InvariantCulture) + " (got " + seconds + ").";
                return false;
            }
            if (seconds > MaxMaxSeconds)
            {
                error = "'max_seconds' must be at most " + MaxMaxSeconds.ToString(CultureInfo.InvariantCulture)
                    + " (got " + seconds + "). Stop and start a new recording for a longer capture.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool ValidateMaxWidth(int maxWidth, out string error)
        {
            if (maxWidth < MinMaxWidth || maxWidth > MaxDimensionCap)
            {
                error = "'max_width' must be from " + MinMaxWidth.ToString(CultureInfo.InvariantCulture) + " to "
                    + MaxDimensionCap.ToString(CultureInfo.InvariantCulture) + " (got "
                    + maxWidth.ToString(CultureInfo.InvariantCulture) + ").";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>Pure: floors to the nearest even number, never below MinDimension -- both H.264 and VP8 require an even width/height.</summary>
        public static int RoundDownToEven(int value)
        {
            if (value < MinDimension)
            {
                return MinDimension;
            }
            return value % 2 == 0 ? value : value - 1;
        }

        /// <summary>
        /// Pure: the actual encode width/height for a captured source of
        /// <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>,
        /// optionally downscaled so the width is at most
        /// <paramref name="maxWidth"/> (aspect preserved, never upscaled),
        /// then rounded down to even on both axes.
        /// </summary>
        public static void ResolveEncodeSize(int sourceWidth, int sourceHeight, int? maxWidth, out int width, out int height)
        {
            int w = sourceWidth < 1 ? 1 : sourceWidth;
            int h = sourceHeight < 1 ? 1 : sourceHeight;
            if (maxWidth.HasValue && maxWidth.Value > 0 && maxWidth.Value < w)
            {
                double scale = maxWidth.Value / (double)w;
                w = maxWidth.Value;
                h = (int)Math.Round(h * scale, MidpointRounding.AwayFromZero);
                if (h < 1)
                {
                    h = 1;
                }
            }
            width = RoundDownToEven(w);
            height = RoundDownToEven(h);
        }

        /// <summary>Pure: "mp4" where the Editor's MediaEncoder can produce H.264 (Windows/Mac), "webm" where it cannot (Linux and unknown hosts).</summary>
        public static string ChooseExtension(UapVideoHostOs os)
        {
            return os == UapVideoHostOs.Windows || os == UapVideoHostOs.Mac ? "mp4" : "webm";
        }

        /// <summary>Pure: the output folder under the project root -- a sibling of uap_editor_screenshot's Temp/UapOpsScreenshots (same "never version-controlled, never asset-imported" Temp/ location), in its own folder since videos are pruned on their own schedule instead of accumulating forever.</summary>
        public static string BuildOutputFolder(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException("'projectRoot' must not be empty.", "projectRoot");
            }
            return (projectRoot.TrimEnd('/', '\\') + "/Temp/UapOpsVideos").Replace('\\', '/');
        }

        /// <summary>Pure: a timestamp-qualified file name so rapid recordings never collide, mirroring UapEditorScreenshotPaths.BuildOutputPath's naming.</summary>
        public static string BuildOutputFileName(DateTime utcTimestamp, string extension)
        {
            return "uap_video_" + utcTimestamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "." + extension;
        }

        public static string BuildOutputPath(string projectRoot, DateTime utcTimestamp, string extension)
        {
            return BuildOutputFolder(projectRoot) + "/" + BuildOutputFileName(utcTimestamp, extension);
        }

        /// <summary>
        /// Pure: given every existing recording's output path in the video
        /// folder, sorted OLDEST FIRST (the timestamp embedded in the file
        /// name makes plain ordinal sort correct), returns the ones beyond
        /// the newest <paramref name="keep"/> for the caller to delete.
        /// <paramref name="justWritten"/> is never returned even if it would
        /// otherwise be evicted, so a recording that just finished can never
        /// delete itself when keep is 0 or negative.
        /// </summary>
        public static List<string> SelectFilesToPrune(IList<string> existingSortedOldestFirst, int keep, string justWritten)
        {
            var toDelete = new List<string>();
            if (existingSortedOldestFirst == null)
            {
                return toDelete;
            }
            if (keep < 0)
            {
                keep = 0;
            }
            int overflow = existingSortedOldestFirst.Count - keep;
            for (int i = 0; i < overflow; i++)
            {
                string path = existingSortedOldestFirst[i];
                if (!string.Equals(path, justWritten, StringComparison.Ordinal))
                {
                    toDelete.Add(path);
                }
            }
            return toDelete;
        }

        /// <summary>
        /// Pure: how many total frames should have been written by
        /// <paramref name="elapsedSeconds"/> into a recording at
        /// <paramref name="fps"/>, counting frame 0 at t=0 -- the
        /// wall-clock pacing target UapVideoRecorder's update tick chases
        /// ("skip or repeat frames so the video's duration matches real
        /// time", like uLoop's own record-video).
        /// </summary>
        public static int TargetFrameCount(double elapsedSeconds, int fps)
        {
            if (elapsedSeconds < 0 || fps < 1)
            {
                return 1;
            }
            return (int)Math.Floor(elapsedSeconds * fps) + 1;
        }

        /// <summary>Pure: the wire name for a state -- idle/recording/stopped.</summary>
        public static string StateName(UapRecordVideoState state)
        {
            switch (state)
            {
                case UapRecordVideoState.Idle: return "idle";
                case UapRecordVideoState.Recording: return "recording";
                case UapRecordVideoState.Stopped: return "stopped";
                default: return "idle";
            }
        }

        /// <summary>The inverse of <see cref="StateName"/>. Unrecognized/garbled text maps to Idle rather than throwing -- persisted state must never crash a read.</summary>
        public static bool TryParseStateName(string value, out UapRecordVideoState state)
        {
            if (string.Equals(value, "idle", StringComparison.OrdinalIgnoreCase)) { state = UapRecordVideoState.Idle; return true; }
            if (string.Equals(value, "recording", StringComparison.OrdinalIgnoreCase)) { state = UapRecordVideoState.Recording; return true; }
            if (string.Equals(value, "stopped", StringComparison.OrdinalIgnoreCase)) { state = UapRecordVideoState.Stopped; return true; }
            state = UapRecordVideoState.Idle;
            return false;
        }

        /// <summary>Pure: one human sentence for a StoppedBy* code (or "not stopped." for empty/unknown -- a live recording has no StoppedBy yet).</summary>
        public static string DescribeStoppedBy(string stoppedBy)
        {
            if (string.Equals(stoppedBy, StoppedByStop, StringComparison.Ordinal))
            {
                return "Stopped by request (action:stop).";
            }
            if (string.Equals(stoppedBy, StoppedByDuration, StringComparison.Ordinal))
            {
                return "Stopped automatically: reached its max_seconds cap.";
            }
            if (string.Equals(stoppedBy, StoppedByPlayModeExit, StringComparison.Ordinal))
            {
                return "Stopped automatically: Play Mode ended (stop_on_play_exit).";
            }
            if (string.Equals(stoppedBy, StoppedByAssemblyReload, StringComparison.Ordinal))
            {
                return "Stopped by a domain reload (a script compile, or entering/leaving Play Mode with domain"
                    + " reload enabled) -- the recording was finalized up to the last captured frame; nothing"
                    + " after the reload was captured.";
            }
            if (string.Equals(stoppedBy, StoppedByError, StringComparison.Ordinal))
            {
                return "Stopped because of an error.";
            }
            return "Not stopped.";
        }

        /// <summary>The persisted-result JSON shape UapVideoRecorder writes to SessionState AND the shape action:start/stop/status all report.</summary>
        public static JsonNode ToJson(UapRecordVideoResult result)
        {
            if (result == null)
            {
                result = UapRecordVideoResult.Idle();
            }
            return JsonNode.NewObject()
                .Set("state", StateName(result.State))
                .Set("recordingId", result.RecordingId ?? string.Empty)
                .Set("path", result.Path ?? string.Empty)
                .Set("view", result.View ?? string.Empty)
                .Set("capture", result.Capture ?? string.Empty)
                .Set("fps", result.Fps)
                .Set("width", result.Width)
                .Set("height", result.Height)
                .Set("maxSeconds", result.MaxSeconds)
                .Set("stopOnPlayExit", result.StopOnPlayExit)
                .Set("startedAtUtcTicks", result.StartedAtUtcTicks)
                .Set("finishedAtUtcTicks", result.FinishedAtUtcTicks)
                .Set("framesWritten", result.FramesWritten)
                .Set("droppedFrames", result.DroppedFrames)
                .Set("secondsRecorded", result.SecondsRecorded)
                .Set("fileSizeBytes", result.FileSizeBytes)
                .Set("stoppedBy", result.StoppedBy ?? string.Empty)
                .Set("message", result.Message ?? string.Empty)
                .Set("warning", result.Warning ?? string.Empty);
        }

        /// <summary>The inverse of <see cref="ToJson"/>. Missing/garbled fields fall back to Idle-equivalent defaults field by field (never throws).</summary>
        public static UapRecordVideoResult FromJson(JsonNode node)
        {
            UapRecordVideoResult result = UapRecordVideoResult.Idle();
            if (node == null || !node.IsObject)
            {
                return result;
            }
            UapRecordVideoState state;
            TryParseStateName(node["state"].AsString(string.Empty), out state);
            result.State = state;
            result.RecordingId = node["recordingId"].AsString(string.Empty);
            result.Path = node["path"].AsString(string.Empty);
            result.View = node["view"].AsString(string.Empty);
            result.Capture = node["capture"].AsString(string.Empty);
            result.Fps = node["fps"].AsInt(0);
            result.Width = node["width"].AsInt(0);
            result.Height = node["height"].AsInt(0);
            result.MaxSeconds = node["maxSeconds"].AsDouble(0.0);
            result.StopOnPlayExit = node["stopOnPlayExit"].AsBool(true);
            result.StartedAtUtcTicks = node["startedAtUtcTicks"].AsLong(0L);
            result.FinishedAtUtcTicks = node["finishedAtUtcTicks"].AsLong(0L);
            result.FramesWritten = node["framesWritten"].AsInt(0);
            result.DroppedFrames = node["droppedFrames"].AsInt(0);
            result.SecondsRecorded = node["secondsRecorded"].AsDouble(0.0);
            result.FileSizeBytes = node["fileSizeBytes"].AsLong(0L);
            result.StoppedBy = node["stoppedBy"].AsString(string.Empty);
            result.Message = node["message"].AsString(string.Empty);
            result.Warning = node["warning"].AsString(string.Empty);
            return result;
        }
    }
}

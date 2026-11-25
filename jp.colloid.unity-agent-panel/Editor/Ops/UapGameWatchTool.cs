using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Colloid.AgentPanel.Core.Json;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reads one or more fields/properties of a component on a GameObject
    /// repeatedly over a short window and returns the time series in ONE
    /// call -- "read PlayerHealth.current on Player/Health every 0.2 s for
    /// 5 s" -- instead of an agent hand-polling uap_object_inspect and
    /// eyeballing the diff itself (design note docs/design-notes/2026-09-23-
    /// uloop-phase2.md, uLoop's Harmony-based `watch` without Harmony --
    /// roadmap section 3.2, docs/design-notes/2026-09-22-uloop-full-native-
    /// roadmap.md). uLoop's watch rides pause-point (Harmony) to freeze
    /// execution and dump a stack frame's locals; this tool has no such
    /// hook and never will in this phase, so it reads the SAME kind of
    /// thing uap_object_inspect does -- a named field/property on a
    /// resolved component -- just on a timer instead of once. That covers
    /// the common case ("did the health value actually drop") without the
    /// half of uLoop's watch that needs a specific source line.
    ///
    /// Deliberately reflection, not SerializedProperty: uap_object_inspect
    /// walks a SerializedObject, which only sees SERIALIZED data -- a
    /// runtime-only property like Rigidbody.velocity, or a public property
    /// backed by a private field, has no SerializedProperty at all. Reading
    /// "PlayerHealth.current" or "velocity.y" needs a real reflection get,
    /// which is exactly what UapGameWatchPolicy.ResolveMemberPath does
    /// (Unity-free, so it is smoke-tested against a plain C# object; this
    /// class supplies the live Component and, for display only, the same
    /// per-type formatting uap_object_inspect uses for a Vector/Color/Rect
    /// value).
    ///
    /// Pollable so a 5-second watch does not hold the HTTP worker (or, via
    /// the dispatcher, the Unity main thread's PROCESSING of this one work
    /// item) for 5 seconds straight -- each Poll is one Editor tick, most of
    /// which do nothing but check whether the next sample is due yet, the
    /// same idiom uap_game_key/uap_game_mouse use for their holds.
    /// IUapAbandonableTool is NOT implemented: unlike a held key or mouse
    /// button, an abandoned watch holds nothing external that needs
    /// releasing -- the dispatcher simply drops the poll state, and nothing
    /// in the running game was ever touched.
    /// </summary>
    public sealed class UapGameWatchTool : IUapPollableTool
    {
        public const string ToolName = "uap_game_watch";

        public string Name
        {
            get { return ToolName; }
        }

        public string Description
        {
            get
            {
                return "Reads one or more fields/properties of a component on a GameObject repeatedly over a"
                    + " short window and returns the time series -- e.g. read PlayerHealth.current on"
                    + " Player/Health every 0.2 s for 5 s in ONE call, instead of polling uap_object_inspect by"
                    + " hand. target/scene/component: same addressing as uap_object_inspect. members: one or"
                    + " more field/property names (dotted paths like 'velocity.y' read into a struct member)."
                    + " interval: seconds between samples (default 0.1). duration: seconds to watch, at most "
                    + UapGameWatchPolicy.MaxDurationSeconds + " per call (default " + UapGameWatchPolicy.DefaultDurationSeconds
                    + ") -- call again to keep watching longer. changed_only (default true): only record a"
                    + " sample when some member's value changed, plus the first and last sample regardless."
                    + " until: an optional stop condition on the FIRST member -- \"changed\", or an operator"
                    + " (==, !=, <, <=, >, >=) followed by a number/true/false/text. max_samples caps how many"
                    + " samples are kept (default " + UapGameWatchPolicy.DefaultMaxSamples + "); stoppedBy in the"
                    + " result says why the watch ended: duration, until, target-destroyed, play-mode-exit or"
                    + " max-samples. Works in Edit Mode too, though most values only change there through your"
                    + " own script edits or Inspector changes. Read-only.";
            }
        }

        public string Module
        {
            get { return "core"; }
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
                        .Set("target", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Hierarchy path of the GameObject to watch (same addressing as uap_object_inspect's 'path')."))
                        .Set("scene", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Scene name or path. Omit to use the active scene (or the open prefab stage)."))
                        .Set("component", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Component type to read from (short or fully-qualified name)."))
                        .Set("componentIndex", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Which component when several of the same type exist (default 0), same PER-TYPE ordinal as uap_object_inspect's."))
                        .Set("members", JsonNode.NewObject().Set("type", "array")
                            .Set("items", JsonNode.NewObject().Set("type", "string"))
                            .Set("description", "One or more field/property names to read each sample, e.g."
                                + " [\"current\"] or [\"velocity.y\", \"isGrounded\"]. Dotted paths walk into a"
                                + " struct/nested member (cheap field/property reads only)."))
                        .Set("interval", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Seconds between samples (default " + UapGameWatchPolicy.DefaultIntervalSeconds
                                + "). Cannot sample faster than one Editor tick regardless of how small this is; 0 means \"every tick\"."))
                        .Set("duration", JsonNode.NewObject().Set("type", "number")
                            .Set("description", "Seconds to watch, greater than 0 and at most " + UapGameWatchPolicy.MaxDurationSeconds
                                + " per call (default " + UapGameWatchPolicy.DefaultDurationSeconds + "). Call again for a longer watch."))
                        .Set("changed_only", JsonNode.NewObject().Set("type", "boolean")
                            .Set("description", "Default true: only record a sample when some member's value"
                                + " changed since the last recorded sample, plus the very first and very last"
                                + " sample regardless. false: record every sample taken."))
                        .Set("until", JsonNode.NewObject().Set("type", "string")
                            .Set("description", "Optional early-stop condition on the FIRST member's value:"
                                + " \"changed\" (differs from its value at the start of this call), or one of"
                                + " ==, !=, <, <=, >, >= followed by a number, true/false, or text (e.g. \"== 0\","
                                + " \"< 10\", \">= 3.5\", \"!= true\"). A Vector/Color/similar struct value"
                                + " compares by MAGNITUDE unless a dotted member already picked one component."))
                        .Set("max_samples", JsonNode.NewObject().Set("type", "integer")
                            .Set("description", "Most samples to keep (default " + UapGameWatchPolicy.DefaultMaxSamples
                                + ", max " + UapGameWatchPolicy.MaxSamplesCeiling + "). Reaching it stops the watch early with stoppedBy:\"max-samples\".")))
                    .Set("required", JsonNode.NewArray().Add("target").Add("component").Add("members"))
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
            var state = stateObj as WatchState;
            if (state == null)
            {
                state = Begin(input);
                stateObj = state;
                if (state.Done)
                {
                    result = state.Result;
                    return true;
                }
                result = null;
                return false;
            }
            return Continue(state, out result);
        }

        private WatchState Begin(JsonNode input)
        {
            string target = input["target"].AsString(null);
            if (string.IsNullOrEmpty(target))
            {
                throw new ArgumentException("'target' is required (hierarchy path of the GameObject to watch).");
            }
            string componentTypeName = input["component"].AsString(null);
            if (string.IsNullOrEmpty(componentTypeName))
            {
                throw new ArgumentException("'component' is required (component type name on 'target').");
            }
            string[] rawMembers = input["members"].AsStringArray();
            string membersError;
            string[] members = UapGameWatchPolicy.NormalizeMembers(rawMembers, out membersError);
            if (members == null)
            {
                throw new ArgumentException(membersError);
            }

            double interval = input["interval"].AsDouble(UapGameWatchPolicy.DefaultIntervalSeconds);
            string intervalError;
            if (!UapGameWatchPolicy.ValidateInterval(interval, out intervalError))
            {
                throw new ArgumentException(intervalError);
            }

            double duration = input["duration"].AsDouble(UapGameWatchPolicy.DefaultDurationSeconds);
            string durationError;
            if (!UapGameWatchPolicy.ValidateDuration(duration, out durationError))
            {
                throw new ArgumentException(durationError);
            }

            int maxSamples = input["max_samples"].AsInt(UapGameWatchPolicy.DefaultMaxSamples);
            string maxSamplesError;
            if (!UapGameWatchPolicy.ValidateMaxSamples(maxSamples, out maxSamplesError))
            {
                throw new ArgumentException(maxSamplesError);
            }

            bool changedOnly = input["changed_only"].AsBool(true);

            bool hasCondition = false;
            UapGameWatchCondition condition = default(UapGameWatchCondition);
            string untilText = input["until"].AsString(null);
            if (!string.IsNullOrEmpty(untilText))
            {
                string conditionError;
                hasCondition = UapGameWatchPolicy.TryParseCondition(untilText, out condition, out conditionError);
                if (!hasCondition)
                {
                    throw new ArgumentException(conditionError);
                }
            }

            string sceneQuery = input["scene"].AsString(null);
            string addressError;
            GameObject go = UapAddressing.ResolveHierarchyPath(sceneQuery, target, out addressError);
            if (go == null)
            {
                throw new InvalidOperationException(addressError);
            }
            Type type = UapComponentTypeResolver.ResolveComponentType(componentTypeName, out addressError);
            if (type == null)
            {
                throw new InvalidOperationException(addressError);
            }
            int componentIndex = input["componentIndex"].AsInt(0);
            Component[] comps = go.GetComponents(type);
            if (componentIndex < 0 || componentIndex >= comps.Length)
            {
                throw new InvalidOperationException("No component of type '" + componentTypeName
                    + "' at index " + componentIndex + " on '" + target + "' (found " + comps.Length + ").");
            }

            var state = new WatchState
            {
                Target = go,
                TargetComponent = comps[componentIndex],
                TargetPath = target,
                ComponentTypeName = comps[componentIndex].GetType().FullName,
                Members = members,
                Interval = interval,
                Duration = duration,
                ChangedOnly = changedOnly,
                MaxSamples = maxSamples,
                HasCondition = hasCondition,
                Condition = condition,
                UntilText = untilText,
                WasPlayingAtStart = EditorApplication.isPlaying,
                StartRealtime = Time.realtimeSinceStartupAsDouble
            };
            state.NextSampleAt = state.StartRealtime;

            // Take the first sample synchronously so a duration of, say, 2 s
            // does not spend one of its ticks doing nothing but arg
            // validation, and so an 'until' already true at t=0 (e.g. "== 0"
            // on a value that starts at 0) is caught immediately.
            TakeSample(state, 0.0);
            if (!state.Done)
            {
                state.NextSampleAt = state.StartRealtime + state.Interval;
            }
            return state;
        }

        private bool Continue(WatchState state, out JsonNode result)
        {
            if (state.WasPlayingAtStart && !EditorApplication.isPlaying)
            {
                FinalizeStop(state, UapGameWatchStopReason.PlayModeExit);
                result = state.Result;
                return true;
            }
            if (state.Target == null || state.TargetComponent == null)
            {
                FinalizeStop(state, UapGameWatchStopReason.TargetDestroyed);
                result = state.Result;
                return true;
            }

            double elapsedNow = Time.realtimeSinceStartupAsDouble - state.StartRealtime;
            bool durationElapsedNow = elapsedNow >= state.Duration;
            bool dueBySchedule = Time.realtimeSinceStartupAsDouble >= state.NextSampleAt;
            if (!dueBySchedule && !durationElapsedNow)
            {
                result = null;
                return false;
            }

            TakeSample(state, elapsedNow);
            if (!state.Done)
            {
                state.NextSampleAt = state.Interval > 0
                    ? state.NextSampleAt + state.Interval
                    : Time.realtimeSinceStartupAsDouble;
            }
            result = state.Done ? state.Result : null;
            return state.Done;
        }

        /// <summary>
        /// Reads every member once, decides whether to keep the sample and
        /// whether the watch is now done, and -- when done -- builds the
        /// final result. <paramref name="elapsed"/> is passed in rather
        /// than re-read so the "is duration up" decision and the sample's
        /// own timestamp always agree.
        /// </summary>
        private void TakeSample(WatchState state, double elapsed)
        {
            var rawValues = new object[state.Members.Length];
            var values = new string[state.Members.Length];
            for (int i = 0; i < state.Members.Length; i++)
            {
                string memberError;
                object raw = UapGameWatchPolicy.ResolveMemberPath(state.TargetComponent, state.Members[i], out memberError);
                if (memberError != null)
                {
                    throw new InvalidOperationException("Could not read '" + state.Members[i] + "' on '"
                        + state.ComponentTypeName + "': " + memberError);
                }
                rawValues[i] = raw;
                values[i] = DescribeRuntimeValue(raw);
            }

            bool isFirst = state.Samples.Count == 0;
            if (isFirst)
            {
                state.BaselineValues = values;
            }
            bool durationElapsed = elapsed >= state.Duration;
            bool changedFromBaseline = !isFirst && UapGameWatchPolicy.AnyValueDifferent(state.BaselineValues, values);
            bool changedFromLastEmitted = UapGameWatchPolicy.AnyValueDifferent(state.LastEmittedValues, values);

            bool untilMet = false;
            if (state.HasCondition)
            {
                UapGameWatchValue classified = UapGameWatchPolicy.ClassifyValue(rawValues[0]);
                string conditionError;
                untilMet = UapGameWatchPolicy.EvaluateCondition(state.Condition, classified, changedFromBaseline, out conditionError);
                if (conditionError != null)
                {
                    throw new ArgumentException(conditionError);
                }
            }

            bool tentativeFinal = untilMet || durationElapsed;
            bool willEmit = UapGameWatchPolicy.ShouldEmitSample(state.ChangedOnly, isFirst, tentativeFinal, changedFromLastEmitted);
            bool maxSamplesReached = false;
            if (willEmit)
            {
                maxSamplesReached = state.Samples.Count + 1 >= state.MaxSamples;
            }

            UapGameWatchStopReason? stopReason = UapGameWatchPolicy.DecideStop(
                targetDestroyed: false, playModeExited: false, untilMet: untilMet,
                durationElapsed: durationElapsed, maxSamplesReached: maxSamplesReached);

            if (willEmit)
            {
                state.Samples.Add(new Sample
                {
                    Elapsed = elapsed,
                    Frame = Time.frameCount,
                    SimTime = EditorApplication.isPlaying ? (double?)Time.time : null,
                    Values = values
                });
                state.LastEmittedValues = values;
            }

            if (stopReason.HasValue)
            {
                state.Done = true;
                state.StoppedBy = stopReason.Value;
                state.Truncated = stopReason.Value == UapGameWatchStopReason.MaxSamples;
                state.Result = BuildResult(state);
            }
        }

        /// <summary>
        /// Ends the watch WITHOUT attempting one more read (the target is
        /// gone, or Play Mode -- and with it the likely reason the target
        /// existed -- just ended): keeps whatever samples were already
        /// recorded and reports why it stopped.
        /// </summary>
        private void FinalizeStop(WatchState state, UapGameWatchStopReason reason)
        {
            state.Done = true;
            state.StoppedBy = reason;
            state.Truncated = false;
            state.Result = BuildResult(state);
        }

        private static JsonNode BuildResult(WatchState state)
        {
            JsonNode samplesJson = JsonNode.NewArray();
            for (int i = 0; i < state.Samples.Count; i++)
            {
                Sample s = state.Samples[i];
                JsonNode valuesNode = JsonNode.NewObject();
                for (int m = 0; m < state.Members.Length; m++)
                {
                    valuesNode.Set(state.Members[m], s.Values[m]);
                }
                JsonNode sampleNode = JsonNode.NewObject()
                    .Set("elapsed", Math.Round(s.Elapsed, 4))
                    .Set("frame", s.Frame)
                    .Set("values", valuesNode);
                if (s.SimTime.HasValue)
                {
                    sampleNode.Set("time", Math.Round(s.SimTime.Value, 4));
                }
                samplesJson.Add(sampleNode);
            }

            UapGameWatchStopReason stoppedBy = state.StoppedBy ?? UapGameWatchStopReason.Duration;
            JsonNode result = JsonNode.NewObject()
                .Set("target", state.TargetPath)
                .Set("component", state.ComponentTypeName)
                .Set("interval", state.Interval)
                .Set("duration", state.Duration)
                .Set("changedOnly", state.ChangedOnly)
                .Set("stoppedBy", UapGameWatchPolicy.StopReasonWireName(stoppedBy))
                .Set("truncated", state.Truncated)
                .Set("sampleCount", state.Samples.Count)
                .Set("samples", samplesJson);
            if (state.HasCondition)
            {
                result.Set("until", state.UntilText);
            }
            if (!EditorApplication.isPlaying)
            {
                result.Set("note", "Edit Mode: most fields only change through your own script edits or manual"
                    + " Inspector changes, so a watch here often reports just the first sample.");
            }
            result.Set("message", BuildMessage(state, stoppedBy));
            return UapToolResults.Text(JsonWriter.Write(result));
        }

        private static string BuildMessage(WatchState state, UapGameWatchStopReason stoppedBy)
        {
            double lastElapsed = state.Samples.Count > 0 ? state.Samples[state.Samples.Count - 1].Elapsed : 0.0;
            string message = "Watched " + state.ComponentTypeName + "." + string.Join(", ", state.Members)
                + " on '" + state.TargetPath + "' for " + lastElapsed.ToString("0.##", CultureInfo.InvariantCulture)
                + " s: " + state.Samples.Count + " sample(s) recorded, stopped by "
                + UapGameWatchPolicy.StopReasonWireName(stoppedBy) + ".";
            if (state.Truncated)
            {
                message += " Hit max_samples (" + state.MaxSamples + ") before duration/until finished -- call again to keep watching.";
            }
            return message;
        }

        /// <summary>
        /// Formats a raw reflected value for the JSON result. Matches
        /// uap_object_inspect's own formatting (UapObjectInspectTool.
        /// FormatFloat/FormatVector2/FormatVector3/FormatVector4/FormatColor/
        /// FormatRect, made internal for this reuse) for every type that
        /// tool can also show; a few runtime-only cases it never sees --
        /// double, and a live UnityEngine.Object reference, which
        /// object_inspect's DescribeObjectReference formats as an ASSET
        /// path that a live scene reference does not have -- get their own,
        /// simpler formatting here instead.
        /// </summary>
        private static string DescribeRuntimeValue(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }
            if (value is string stringValue)
            {
                return stringValue;
            }
            if (value is float floatValue)
            {
                return UapObjectInspectTool.FormatFloat(floatValue);
            }
            if (value is double doubleValue)
            {
                return doubleValue.ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is Vector2 v2)
            {
                return UapObjectInspectTool.FormatVector2(v2);
            }
            if (value is Vector3 v3)
            {
                return UapObjectInspectTool.FormatVector3(v3);
            }
            if (value is Vector4 v4)
            {
                return UapObjectInspectTool.FormatVector4(v4);
            }
            if (value is Quaternion q)
            {
                // Same as uap_object_inspect's Quaternion case: the raw
                // x/y/z/w is not something an agent reasons about, the
                // Inspector-familiar Euler angles are.
                return UapObjectInspectTool.FormatVector3(q.eulerAngles);
            }
            if (value is Color color)
            {
                return UapObjectInspectTool.FormatColor(color);
            }
            if (value is Rect rect)
            {
                return UapObjectInspectTool.FormatRect(rect);
            }
            if (value is Enum)
            {
                return value.ToString();
            }
            if (value is UnityEngine.Object unityObject)
            {
                return unityObject == null ? "null" : unityObject.name + " (" + value.GetType().Name + ")";
            }
            if (IsIntegralNumber(value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        private static bool IsIntegralNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong || value is decimal;
        }

        private sealed class Sample
        {
            public double Elapsed;
            public int Frame;
            public double? SimTime;
            public string[] Values;
        }

        private sealed class WatchState
        {
            public GameObject Target;
            public Component TargetComponent;
            public string TargetPath;
            public string ComponentTypeName;
            public string[] Members;
            public double Interval;
            public double Duration;
            public bool ChangedOnly;
            public int MaxSamples;
            public bool HasCondition;
            public UapGameWatchCondition Condition;
            public string UntilText;
            public bool WasPlayingAtStart;
            public double StartRealtime;
            public double NextSampleAt;
            public readonly List<Sample> Samples = new List<Sample>();
            public string[] BaselineValues;
            public string[] LastEmittedValues;
            public bool Truncated;
            public bool Done;
            public UapGameWatchStopReason? StoppedBy;
            public JsonNode Result;
        }
    }
}

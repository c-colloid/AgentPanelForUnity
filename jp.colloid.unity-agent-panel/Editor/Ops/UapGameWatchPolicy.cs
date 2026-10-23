using System;
using System.Globalization;
using System.Reflection;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Why a uap_game_watch call stopped sampling. Wire name via
    /// <see cref="UapGameWatchPolicy.StopReasonWireName"/>.
    /// </summary>
    public enum UapGameWatchStopReason
    {
        Duration,
        Until,
        TargetDestroyed,
        PlayModeExit,
        MaxSamples
    }

    /// <summary>What an 'until' condition compares against.</summary>
    public enum UapGameWatchConditionOp
    {
        /// <summary>No operator/value -- true the first tick the watched value differs from its very first reading.</summary>
        Changed,
        Equal,
        NotEqual,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual
    }

    /// <summary>A parsed 'until' argument (design note docs/design-notes/2026-09-23-uloop-phase2.md, uap_game_watch).</summary>
    public struct UapGameWatchCondition
    {
        public UapGameWatchConditionOp Op;
        /// <summary>Raw right-hand-side text (e.g. "0", "3.5", "true"); null for <see cref="UapGameWatchConditionOp.Changed"/>.</summary>
        public string ValueText;
    }

    /// <summary>What kind of comparison a sampled value supports.</summary>
    public enum UapGameWatchValueKind
    {
        Number,
        Bool,
        /// <summary>String or enum -- both compare as text, == / != only.</summary>
        Text
    }

    /// <summary>
    /// A watched value normalized enough to feed <see cref="UapGameWatchPolicy.EvaluateCondition"/>.
    /// For <see cref="UapGameWatchValueKind.Number"/> on a struct made only of
    /// numeric fields/properties (Vector2/3/4, Quaternion, Color, or any
    /// project struct shaped the same way) <see cref="Number"/> is its
    /// magnitude, NOT a signed component -- read a specific axis with a
    /// dotted member path (e.g. "velocity.y") to compare that axis instead.
    /// </summary>
    public struct UapGameWatchValue
    {
        public UapGameWatchValueKind Kind;
        public double Number;
        public bool Bool;
        public string Text;
    }

    /// <summary>
    /// The Unity-free half of uap_game_watch (design note docs/design-notes/
    /// 2026-09-23-uloop-phase2.md, uLoop's Harmony-based `watch` without
    /// Harmony -- section 3.2 of the roadmap, docs/design-notes/2026-09-22-
    /// uloop-full-native-roadmap.md). Everything that can be decided without
    /// touching a live GameObject lives here: argument rules, the dotted
    /// member-path reflection walk (generic System.Reflection only -- no
    /// UnityEngine type is named, so it runs unmodified in the license-free
    /// smoke gate against a plain C# test object), the 'until' condition
    /// parser/comparator, and the sample-series compaction (changed_only,
    /// first/last-always-kept, the max_samples cutoff, and which stop reason
    /// wins when several apply at once). UapGameWatchTool supplies the one
    /// thing this class cannot: which CLR type counts as "vector-shaped" for
    /// magnitude, and how to format Vector2/3/4/Quaternion/Color/Rect for
    /// display the same way uap_object_inspect does -- both need the actual
    /// UnityEngine types in hand.
    /// </summary>
    public static class UapGameWatchPolicy
    {
        public const double DefaultIntervalSeconds = 0.1;

        public const double DefaultDurationSeconds = 5.0;

        /// <summary>
        /// Hardest cap on 'duration'. UapMainThreadDispatcher gives a call
        /// 15 s normally and only 8 s while Play Mode is running with the
        /// Editor unfocused (ThrottledTimeoutMillis) -- an abandoned
        /// pollable is never ticked again (SEC-7), so a watch that ran
        /// right up to that bound would return nothing. 6 s leaves headroom
        /// for the polls that carry the request there and back and for the
        /// JSON build at the end; a longer watch is simply another call.
        /// </summary>
        public const double MaxDurationSeconds = 6.0;

        public const int DefaultMaxSamples = 500;

        /// <summary>Upper bound on 'max_samples' -- a safety valve, not a tuned figure.</summary>
        public const int MaxSamplesCeiling = 2000;

        /// <summary>Upper bound on how many members one call may list.</summary>
        public const int MaxMembers = 20;

        /// <summary>Deepest a dotted member path ("a.b.c...") may go.</summary>
        public const int MaxMemberPathSegments = 8;

        /// <summary>Absolute tolerance for a numeric == / != comparison in <see cref="EvaluateCondition"/> (values are floats/doubles read off a live game, not exact decimals).</summary>
        public const double NumberEqualityEpsilon = 1e-6;

        /// <summary>Pure: true when 'interval' is a usable sampling period (zero means "every tick"), else false with a message.</summary>
        public static bool ValidateInterval(double seconds, out string error)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                error = "'interval' must be zero (sample every tick) or a positive number of seconds (got " + seconds + ").";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>Pure: true when 'duration' is usable (0, MaxDurationSeconds], else false with a message naming the two-call alternative.</summary>
        public static bool ValidateDuration(double seconds, out string error)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
            {
                error = "'duration' must be a positive number of seconds, at most " + MaxDurationSeconds
                    + " (got " + seconds + ").";
                return false;
            }
            if (seconds > MaxDurationSeconds)
            {
                error = "'duration' must be at most " + MaxDurationSeconds + " seconds per call (got " + seconds
                    + "), so this call returns before the dispatcher's wait budget runs out. Call uap_game_watch"
                    + " again (optionally with 'until') to keep watching longer.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>Pure: true when 'max_samples' is usable (1..MaxSamplesCeiling), else false with a message.</summary>
        public static bool ValidateMaxSamples(int maxSamples, out string error)
        {
            if (maxSamples <= 0 || maxSamples > MaxSamplesCeiling)
            {
                error = "'max_samples' must be from 1 to " + MaxSamplesCeiling + " (got " + maxSamples + ").";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Pure: trims each member name, rejects an empty list, a name that
        /// is empty/whitespace, a duplicate (the output keys one JSON object
        /// per sample by member name -- a duplicate would silently overwrite
        /// itself), and a list past <see cref="MaxMembers"/>. Returns null
        /// with a descriptive <paramref name="error"/> on any of those.
        /// </summary>
        public static string[] NormalizeMembers(string[] members, out string error)
        {
            if (members == null || members.Length == 0)
            {
                error = "'members' must list at least one field/property name (dotted paths like 'velocity.y' are allowed).";
                return null;
            }
            if (members.Length > MaxMembers)
            {
                error = "'members' supports at most " + MaxMembers + " entries (got " + members.Length + ").";
                return null;
            }
            var result = new string[members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                string m = members[i] == null ? null : members[i].Trim();
                if (string.IsNullOrEmpty(m))
                {
                    error = "'members[" + i + "]' must not be empty.";
                    return null;
                }
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(result[j], m, StringComparison.Ordinal))
                    {
                        error = "'members' lists '" + m + "' more than once.";
                        return null;
                    }
                }
                result[i] = m;
            }
            error = null;
            return result;
        }

        /// <summary>
        /// Pure reflection member-path walker: no UnityEngine type is named
        /// here, so it works on any object graph (a live Component included
        /// -- the caller passes one in as plain <see cref="object"/>).
        /// Walks each dotted segment as a field first, then a (readable,
        /// non-indexer) property, searching public AND non-public instance
        /// members -- object_inspect's SerializedProperty walk already
        /// surfaces a private [SerializeField] field the same way, and a
        /// runtime-only value (Rigidbody.velocity) has no SerializedProperty
        /// at all, which is the whole reason this tool exists. Never
        /// throws: a missing member, a null encountered mid-path, or a
        /// getter that itself throws all come back as a null result with
        /// <paramref name="error"/> set, which the caller turns into the
        /// tool's own exception.
        /// </summary>
        public static object ResolveMemberPath(object root, string dottedPath, out string error)
        {
            if (root == null)
            {
                error = "the target object is null.";
                return null;
            }
            if (string.IsNullOrEmpty(dottedPath))
            {
                error = "member path must not be empty.";
                return null;
            }
            string[] segments = dottedPath.Split('.');
            if (segments.Length > MaxMemberPathSegments)
            {
                error = "'" + dottedPath + "' has more than " + MaxMemberPathSegments + " '.'-separated segments.";
                return null;
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object current = root;
            string traversed = string.Empty;
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i].Trim();
                if (seg.Length == 0)
                {
                    error = "'" + dottedPath + "' has an empty '.'-separated segment.";
                    return null;
                }
                if (current == null)
                {
                    error = "'" + traversed + "' is null; cannot read '." + seg + "' on it.";
                    return null;
                }
                Type type = current.GetType();
                FieldInfo field = type.GetField(seg, Flags);
                try
                {
                    if (field != null)
                    {
                        current = field.GetValue(current);
                    }
                    else
                    {
                        PropertyInfo prop = type.GetProperty(seg, Flags);
                        if (prop == null || !prop.CanRead || prop.GetIndexParameters().Length != 0)
                        {
                            error = "No readable field or property named '" + seg + "' on type '" + type.FullName + "'"
                                + (traversed.Length > 0 ? " (resolving '" + dottedPath + "')" : "") + ".";
                            return null;
                        }
                        current = prop.GetValue(current, null);
                    }
                }
                catch (Exception ex)
                {
                    Exception real = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    error = "reading '" + seg + "' on '" + type.FullName + "' threw " + real.GetType().Name + ": " + real.Message;
                    return null;
                }
                traversed = traversed.Length == 0 ? seg : traversed + "." + seg;
            }
            error = null;
            return current;
        }

        /// <summary>Pure: true when <paramref name="t"/> is one of the built-in numeric primitive types (not bool, not char).</summary>
        public static bool IsNumericPrimitive(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
                || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }

        /// <summary>
        /// Classifies a raw reflected value for comparison purposes (display
        /// formatting is a separate concern -- see UapGameWatchTool). null
        /// and unrecognized reference types fall back to their ToString()
        /// as <see cref="UapGameWatchValueKind.Text"/> so an 'until' on them
        /// still gets a sensible "not comparable that way" error rather
        /// than a crash. A value-type that is not itself numeric/bool/enum
        /// is treated as "vector-shaped" -- see
        /// <see cref="TryVectorMagnitude"/> -- when ALL of its public
        /// instance fields (or, failing that, all of its public instance
        /// properties) are numeric primitives; this is deliberately not
        /// limited to UnityEngine.Vector2/3/4 by name, so a project's own
        /// small numeric struct gets the same treatment.
        /// </summary>
        public static UapGameWatchValue ClassifyValue(object value)
        {
            if (value == null)
            {
                return new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = "null" };
            }
            if (value is bool b)
            {
                return new UapGameWatchValue { Kind = UapGameWatchValueKind.Bool, Bool = b };
            }
            if (value is string s)
            {
                return new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = s };
            }
            if (value is Enum)
            {
                return new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = value.ToString() };
            }
            Type type = value.GetType();
            if (IsNumericPrimitive(type))
            {
                return new UapGameWatchValue
                {
                    Kind = UapGameWatchValueKind.Number,
                    Number = Convert.ToDouble(value, CultureInfo.InvariantCulture)
                };
            }
            double magnitude;
            if (type.IsValueType && TryVectorMagnitude(value, type, out magnitude))
            {
                return new UapGameWatchValue { Kind = UapGameWatchValueKind.Number, Number = magnitude };
            }
            return new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = value.ToString() };
        }

        /// <summary>
        /// True when <paramref name="type"/> has 1-8 public instance fields
        /// and every one is a numeric primitive (Vector2/3/4, Quaternion,
        /// Color and Vector2Int/3Int are all shaped this way); when it is
        /// not (no qualifying fields, e.g. Rect's are private, or a
        /// non-numeric field among them), the same check is retried against
        /// its public instance PROPERTIES instead. Either way,
        /// <paramref name="magnitude"/> is then
        /// their Euclidean length (sqrt of the sum of squares) -- e.g. a
        /// Vector3's magnitude, or a Color's (an odd but harmless number:
        /// nothing stops an agent asking to watch a Color, and refusing it
        /// outright would be worse than a defined-if-unusual comparison).
        /// A struct's SIGN is lost this way, which is why comparing a
        /// specific axis needs a dotted path ("velocity.y") instead --
        /// that resolves to a plain float, not a struct, and skips this
        /// path entirely.
        /// </summary>
        public static bool TryVectorMagnitude(object value, Type type, out double magnitude)
        {
            magnitude = 0.0;
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length > 0 && fields.Length <= 8)
            {
                double sumOfSquares = 0.0;
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!IsNumericPrimitive(fields[i].FieldType))
                    {
                        fields = Array.Empty<FieldInfo>();
                        break;
                    }
                    double component = Convert.ToDouble(fields[i].GetValue(value), CultureInfo.InvariantCulture);
                    sumOfSquares += component * component;
                }
                if (fields.Length > 0)
                {
                    magnitude = Math.Sqrt(sumOfSquares);
                    return true;
                }
            }
            PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (props.Length > 0 && props.Length <= 8)
            {
                double sumOfSquares = 0.0;
                for (int i = 0; i < props.Length; i++)
                {
                    if (!props[i].CanRead || props[i].GetIndexParameters().Length != 0
                        || !IsNumericPrimitive(props[i].PropertyType))
                    {
                        return false;
                    }
                    double component = Convert.ToDouble(props[i].GetValue(value, null), CultureInfo.InvariantCulture);
                    sumOfSquares += component * component;
                }
                magnitude = Math.Sqrt(sumOfSquares);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pure: parses an 'until' argument. "changed" (case-insensitive) is
        /// the no-value form; otherwise the text must start with one of
        /// ==, !=, &lt;=, &gt;=, &lt;, &gt; (longest first, so "&gt;=" is never
        /// split into "&gt;" + "=") followed by a value. Returns false with
        /// <paramref name="error"/> set on anything else, INCLUDING an
        /// empty/null <paramref name="text"/> -- the caller only calls this
        /// when 'until' was actually supplied.
        /// </summary>
        public static bool TryParseCondition(string text, out UapGameWatchCondition condition, out string error)
        {
            condition = default(UapGameWatchCondition);
            if (string.IsNullOrEmpty(text))
            {
                error = "'until' must not be empty.";
                return false;
            }
            string trimmed = text.Trim();
            if (string.Equals(trimmed, "changed", StringComparison.OrdinalIgnoreCase))
            {
                condition = new UapGameWatchCondition { Op = UapGameWatchConditionOp.Changed, ValueText = null };
                error = null;
                return true;
            }
            string[] ops = { ">=", "<=", "==", "!=", ">", "<" };
            UapGameWatchConditionOp[] kinds =
            {
                UapGameWatchConditionOp.GreaterOrEqual, UapGameWatchConditionOp.LessOrEqual,
                UapGameWatchConditionOp.Equal, UapGameWatchConditionOp.NotEqual,
                UapGameWatchConditionOp.GreaterThan, UapGameWatchConditionOp.LessThan
            };
            for (int i = 0; i < ops.Length; i++)
            {
                if (trimmed.StartsWith(ops[i], StringComparison.Ordinal))
                {
                    string value = trimmed.Substring(ops[i].Length).Trim();
                    if (value.Length == 0)
                    {
                        error = "'until' condition \"" + text + "\" needs a value after '" + ops[i] + "'.";
                        return false;
                    }
                    condition = new UapGameWatchCondition { Op = kinds[i], ValueText = value };
                    error = null;
                    return true;
                }
            }
            error = "'until' must be \"changed\" or start with one of ==, !=, <, <=, >, >= followed by a value (got \""
                + text + "\").";
            return false;
        }

        /// <summary>Pure: accepts "true"/"false"/"1"/"0" (case-insensitive), else false.</summary>
        public static bool TryParseConditionBool(string text, out bool value)
        {
            string t = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (t == "true" || t == "1")
            {
                value = true;
                return true;
            }
            if (t == "false" || t == "0")
            {
                value = false;
                return true;
            }
            value = false;
            return false;
        }

        /// <summary>
        /// Pure: evaluates a parsed 'until' condition against the CURRENT
        /// sample's first-member value. <paramref name="changedFromBaseline"/>
        /// is only consulted for <see cref="UapGameWatchConditionOp.Changed"/>
        /// and must already mean "differs from the very first reading", NOT
        /// "differs from the last EMITTED sample" (changed_only's
        /// compaction is a different, unrelated notion of "changed" -- see
        /// <see cref="AnyValueDifferent"/>/<see cref="ShouldEmitSample"/> --
        /// conflating the two would make "until: changed" fire on sample 1
        /// whenever changed_only also happens to be true). A type mismatch
        /// (e.g. "&lt; 10" against a text value) is reported through
        /// <paramref name="error"/>, not by silently returning false, so
        /// the caller can fail the whole call instead of watching forever.
        /// </summary>
        public static bool EvaluateCondition(UapGameWatchCondition condition, UapGameWatchValue current,
            bool changedFromBaseline, out string error)
        {
            error = null;
            if (condition.Op == UapGameWatchConditionOp.Changed)
            {
                return changedFromBaseline;
            }
            switch (current.Kind)
            {
                case UapGameWatchValueKind.Number:
                    double threshold;
                    if (!double.TryParse(condition.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold))
                    {
                        error = "'until' value '" + condition.ValueText + "' is not a number, but the watched value is numeric.";
                        return false;
                    }
                    switch (condition.Op)
                    {
                        case UapGameWatchConditionOp.Equal:
                            return Math.Abs(current.Number - threshold) <= NumberEqualityEpsilon;
                        case UapGameWatchConditionOp.NotEqual:
                            return Math.Abs(current.Number - threshold) > NumberEqualityEpsilon;
                        case UapGameWatchConditionOp.LessThan:
                            return current.Number < threshold;
                        case UapGameWatchConditionOp.LessOrEqual:
                            return current.Number <= threshold;
                        case UapGameWatchConditionOp.GreaterThan:
                            return current.Number > threshold;
                        default:
                            return current.Number >= threshold;
                    }
                case UapGameWatchValueKind.Bool:
                    bool thresholdBool;
                    if (!TryParseConditionBool(condition.ValueText, out thresholdBool))
                    {
                        error = "'until' value '" + condition.ValueText + "' is not true/false, but the watched value is boolean.";
                        return false;
                    }
                    if (condition.Op == UapGameWatchConditionOp.Equal)
                    {
                        return current.Bool == thresholdBool;
                    }
                    if (condition.Op == UapGameWatchConditionOp.NotEqual)
                    {
                        return current.Bool != thresholdBool;
                    }
                    error = "'until' only supports == or != for a boolean value (got an ordering operator).";
                    return false;
                default:
                    if (condition.Op == UapGameWatchConditionOp.Equal)
                    {
                        return string.Equals(current.Text, condition.ValueText, StringComparison.Ordinal);
                    }
                    if (condition.Op == UapGameWatchConditionOp.NotEqual)
                    {
                        return !string.Equals(current.Text, condition.ValueText, StringComparison.Ordinal);
                    }
                    error = "'until' only supports == or != for a text/enum value (got an ordering operator).";
                    return false;
            }
        }

        /// <summary>Pure: true when the two same-length display-string arrays differ anywhere (a length mismatch or a null array also counts as "different").</summary>
        public static bool AnyValueDifferent(string[] previous, string[] current)
        {
            if (previous == null || current == null || previous.Length != current.Length)
            {
                return true;
            }
            for (int i = 0; i < previous.Length; i++)
            {
                if (!string.Equals(previous[i], current[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: the sample-series compaction rule. changed_only:false
        /// keeps every reading; changed_only:true keeps only the FIRST
        /// reading, the LAST one (whatever stopped the watch), and any
        /// reading where <paramref name="anyChangedSinceLastEmitted"/> is
        /// true -- so a flat run of identical readings collapses to just
        /// its two endpoints instead of one row per tick.
        /// </summary>
        public static bool ShouldEmitSample(bool changedOnly, bool isFirst, bool isFinal, bool anyChangedSinceLastEmitted)
        {
            return !changedOnly || isFirst || isFinal || anyChangedSinceLastEmitted;
        }

        /// <summary>
        /// Pure: which stop reason wins when several apply to the same
        /// tick. Destruction/Play-Mode-exit always win (nothing further can
        /// be read); an explicit 'until' match beats the generic timers;
        /// max_samples is reported ONLY when it is what actually cut the
        /// watch short (durationElapsed/untilMet already true the same tick
        /// means the cap merely coincided with a natural end, not that
        /// anything was truncated).
        /// </summary>
        public static UapGameWatchStopReason? DecideStop(bool targetDestroyed, bool playModeExited, bool untilMet,
            bool durationElapsed, bool maxSamplesReached)
        {
            if (targetDestroyed)
            {
                return UapGameWatchStopReason.TargetDestroyed;
            }
            if (playModeExited)
            {
                return UapGameWatchStopReason.PlayModeExit;
            }
            if (untilMet)
            {
                return UapGameWatchStopReason.Until;
            }
            if (durationElapsed)
            {
                return UapGameWatchStopReason.Duration;
            }
            if (maxSamplesReached)
            {
                return UapGameWatchStopReason.MaxSamples;
            }
            return null;
        }

        /// <summary>The wire ("stoppedBy") spelling of <paramref name="reason"/>.</summary>
        public static string StopReasonWireName(UapGameWatchStopReason reason)
        {
            switch (reason)
            {
                case UapGameWatchStopReason.Until:
                    return "until";
                case UapGameWatchStopReason.TargetDestroyed:
                    return "target-destroyed";
                case UapGameWatchStopReason.PlayModeExit:
                    return "play-mode-exit";
                case UapGameWatchStopReason.MaxSamples:
                    return "max-samples";
                default:
                    return "duration";
            }
        }
    }
}

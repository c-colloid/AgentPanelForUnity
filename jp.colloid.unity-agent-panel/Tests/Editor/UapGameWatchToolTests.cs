using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// uap_game_watch (design note docs/design-notes/2026-09-23-uloop-
    /// phase2.md, roadmap section 3.2 of docs/design-notes/2026-09-22-uloop-
    /// full-native-roadmap.md).
    ///
    /// What is NOT tested here, on purpose: Play Mode's Time.time sample
    /// field (needs Play Mode, which batch mode EditMode tests never enter)
    /// and a genuinely long watch (this suite drives every case to
    /// completion in well under a second via interval:0/max_samples/until,
    /// never by sleeping). The pure argument/condition/reflection rules are
    /// also pinned in ci/SmokeTests via UapGameWatchPolicy directly.
    /// </summary>
    [TestFixture]
    public class UapGameWatchToolTests
    {
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("UapGameWatchTestObject");
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                UnityEngine.Object.DestroyImmediate(_go);
            }
        }

        // ------------------------------------------------------------
        // Argument rules (Tool.Execute -- Begin() runs synchronously on
        // the first Poll, so a bad argument throws before any waiting).
        // ------------------------------------------------------------

        [Test]
        public void Execute_MissingTarget_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition")));
            });
            StringAssert.Contains("target", ex.Message);
        }

        [Test]
        public void Execute_MissingComponent_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("members", JsonNode.NewArray().Add("localPosition")));
            });
            StringAssert.Contains("component", ex.Message);
        }

        [Test]
        public void Execute_MissingMembers_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform"));
            });
            StringAssert.Contains("members", ex.Message);
        }

        [Test]
        public void Execute_DuplicateMembers_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition").Add("localPosition")));
            });
            StringAssert.Contains("more than once", ex.Message);
        }

        [Test]
        public void Execute_DurationTooLarge_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("duration", UapGameWatchPolicy.MaxDurationSeconds + 1));
            });
            StringAssert.Contains("duration", ex.Message);
        }

        [Test]
        public void Execute_ZeroOrNegativeDuration_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("duration", 0));
            });
        }

        [Test]
        public void Execute_NegativeInterval_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("interval", -1));
            });
            StringAssert.Contains("interval", ex.Message);
        }

        [Test]
        public void Execute_MaxSamplesOutOfRange_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("max_samples", 0));
            });
            Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("max_samples", UapGameWatchPolicy.MaxSamplesCeiling + 1));
            });
        }

        [Test]
        public void Execute_MalformedUntil_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition"))
                    .Set("until", "banana 3"));
            });
            StringAssert.Contains("until", ex.Message);
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "NoSuchObjectAtAll12345")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("localPosition")));
            });
        }

        [Test]
        public void Execute_UnknownComponentType_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "NotARealComponentType12345")
                    .Set("members", JsonNode.NewArray().Add("localPosition")));
            });
        }

        [Test]
        public void Execute_ComponentIndexOutOfRange_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("componentIndex", 5)
                    .Set("members", JsonNode.NewArray().Add("localPosition")));
            });
        }

        [Test]
        public void Execute_UnknownMember_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(delegate
            {
                new UapGameWatchTool().Execute(JsonNode.NewObject()
                    .Set("target", "UapGameWatchTestObject")
                    .Set("component", "Transform")
                    .Set("members", JsonNode.NewArray().Add("noSuchMemberAtAll")));
            });
            StringAssert.Contains("noSuchMemberAtAll", ex.Message);
        }

        // ------------------------------------------------------------
        // A real watch, driving Poll() directly (as UapScriptsCommitTool's
        // tests do), so the test controls exactly when each sample lands.
        // ------------------------------------------------------------

        [Test]
        public void Poll_ReadsTransformAcrossPolls_ThenStopsAtMaxSamples()
        {
            var tool = new UapGameWatchTool();
            object state = null;
            JsonNode input = JsonNode.NewObject()
                .Set("target", "UapGameWatchTestObject")
                .Set("component", "Transform")
                .Set("members", JsonNode.NewArray().Add("localPosition"))
                .Set("interval", 0)
                .Set("duration", 2)
                .Set("changed_only", false)
                .Set("max_samples", 2);

            JsonNode result;
            bool done = tool.Poll(input, ref state, out result);
            Assert.IsFalse(done, "max_samples is 2; the very first sample alone must not finish the watch");

            _go.transform.localPosition = new Vector3(1f, 2f, 3f);
            done = tool.Poll(input, ref state, out result);
            Assert.IsTrue(done, "the second sample reaches max_samples and must finish the watch");

            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.AreEqual("max-samples", parsed["stoppedBy"].AsString());
            Assert.IsTrue(parsed["truncated"].AsBool());
            Assert.AreEqual(2, parsed["sampleCount"].AsInt(-1));
            JsonNode samples = parsed["samples"];
            Assert.AreEqual(2, samples.Count);
            Assert.AreEqual(UapObjectInspectTool.FormatVector3(Vector3.zero), samples[0]["values"]["localPosition"].AsString());
            Assert.AreEqual(UapObjectInspectTool.FormatVector3(new Vector3(1f, 2f, 3f)), samples[1]["values"]["localPosition"].AsString());
        }

        [Test]
        public void Poll_ChangedOnly_SkipsUnchangedSamples_OnlyRealChangesCountTowardMaxSamples()
        {
            var tool = new UapGameWatchTool();
            object state = null;
            JsonNode input = JsonNode.NewObject()
                .Set("target", "UapGameWatchTestObject")
                .Set("component", "Transform")
                .Set("members", JsonNode.NewArray().Add("localPosition"))
                .Set("interval", 0)
                .Set("duration", 2)
                .Set("changed_only", true)
                .Set("max_samples", 3);

            JsonNode result;
            // Sample 1 (the baseline) is always kept.
            Assert.IsFalse(tool.Poll(input, ref state, out result));
            // Nothing changed: with changed_only:true this reading must be
            // skipped -- and therefore NOT count toward max_samples.
            Assert.IsFalse(tool.Poll(input, ref state, out result));

            _go.transform.localPosition = new Vector3(5f, 0f, 0f);
            Assert.IsFalse(tool.Poll(input, ref state, out result), "2 of 3 max_samples kept so far");

            // Unchanged again: still skipped, still not counted.
            Assert.IsFalse(tool.Poll(input, ref state, out result));

            _go.transform.localPosition = new Vector3(9f, 0f, 0f);
            Assert.IsTrue(tool.Poll(input, ref state, out result), "this is the THIRD real change, which reaches max_samples");

            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.AreEqual("max-samples", parsed["stoppedBy"].AsString());
            Assert.AreEqual(3, parsed["sampleCount"].AsInt(-1),
                "only the baseline plus the two REAL changes were kept; the two unchanged reads in between collapsed to nothing");
        }

        [Test]
        public void Poll_UntilChanged_DoesNotFireOnTheFirstSample_OnlyOnceTheValueMoves()
        {
            var tool = new UapGameWatchTool();
            object state = null;
            JsonNode input = JsonNode.NewObject()
                .Set("target", "UapGameWatchTestObject")
                .Set("component", "Transform")
                .Set("members", JsonNode.NewArray().Add("localPosition"))
                .Set("interval", 0)
                .Set("duration", 2)
                .Set("until", "changed");

            JsonNode result;
            bool done = tool.Poll(input, ref state, out result);
            Assert.IsFalse(done, "'until: changed' must not be satisfied by the baseline reading itself");

            _go.transform.localPosition = new Vector3(0f, 9f, 0f);
            done = tool.Poll(input, ref state, out result);
            Assert.IsTrue(done);

            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.AreEqual("until", parsed["stoppedBy"].AsString());
            Assert.AreEqual(2, parsed["sampleCount"].AsInt(-1));
        }

        [Test]
        public void Poll_TargetDestroyedMidWatch_StopsWithTargetDestroyed()
        {
            var tool = new UapGameWatchTool();
            object state = null;
            JsonNode input = JsonNode.NewObject()
                .Set("target", "UapGameWatchTestObject")
                .Set("component", "Transform")
                .Set("members", JsonNode.NewArray().Add("localPosition"))
                .Set("interval", 0.1)
                .Set("duration", 2);

            JsonNode result;
            Assert.IsFalse(tool.Poll(input, ref state, out result));

            UnityEngine.Object.DestroyImmediate(_go);
            _go = null; // TearDown must not double-destroy.
            bool done = tool.Poll(input, ref state, out result);
            Assert.IsTrue(done);

            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.AreEqual("target-destroyed", parsed["stoppedBy"].AsString());
            Assert.IsFalse(parsed["truncated"].AsBool());
        }

        [Test]
        public void Poll_EditModeResult_CarriesAnEditModeNote()
        {
            var tool = new UapGameWatchTool();
            object state = null;
            JsonNode input = JsonNode.NewObject()
                .Set("target", "UapGameWatchTestObject")
                .Set("component", "Transform")
                .Set("members", JsonNode.NewArray().Add("localPosition"))
                .Set("interval", 0)
                .Set("duration", 2)
                .Set("max_samples", 1);

            JsonNode result;
            bool done = tool.Poll(input, ref state, out result);
            Assert.IsTrue(done, "max_samples:1 must finish right on the first (synchronous) sample");
            JsonNode parsed = JsonParser.Parse(result[0]["text"].AsString());
            Assert.IsTrue(parsed.HasKey("note"));
            StringAssert.Contains("Edit Mode", parsed["note"].AsString());
        }

        [Test]
        public void ReadOnly_And_NotAbandonable()
        {
            var tool = new UapGameWatchTool();
            Assert.IsTrue(tool.ReadOnly, "resolving a path/component and reflecting a field/property value mutates nothing");
            Assert.IsFalse(tool is IUapAbandonableTool, "no held device/state to release when a call is abandoned");
            Assert.AreEqual("core", tool.Module, "same module as uap_object_inspect");
        }

        // ------------------------------------------------------------
        // UapGameWatchPolicy -- pure rules (also compiled verbatim into
        // ci/SmokeTests; these NUnit cases exercise the same methods under
        // the real Editor test runner).
        // ------------------------------------------------------------

        [Test]
        public void ValidateInterval_AllowsZeroAndPositive_RefusesNegativeOrNonFinite()
        {
            string error;
            Assert.IsTrue(UapGameWatchPolicy.ValidateInterval(0, out error), error);
            Assert.IsTrue(UapGameWatchPolicy.ValidateInterval(0.2, out error), error);
            Assert.IsFalse(UapGameWatchPolicy.ValidateInterval(-0.1, out error));
            Assert.IsFalse(UapGameWatchPolicy.ValidateInterval(double.NaN, out error));
            Assert.IsFalse(UapGameWatchPolicy.ValidateInterval(double.PositiveInfinity, out error));
        }

        [Test]
        public void ValidateDuration_RefusesZeroNegativeOrAboveCap()
        {
            string error;
            Assert.IsTrue(UapGameWatchPolicy.ValidateDuration(UapGameWatchPolicy.MaxDurationSeconds, out error), error);
            Assert.IsFalse(UapGameWatchPolicy.ValidateDuration(0, out error));
            Assert.IsFalse(UapGameWatchPolicy.ValidateDuration(-1, out error));
            Assert.IsFalse(UapGameWatchPolicy.ValidateDuration(UapGameWatchPolicy.MaxDurationSeconds + 0.01, out error));
            StringAssert.Contains("duration", error);
        }

        [Test]
        public void ValidateMaxSamples_RangeChecksInclusive()
        {
            string error;
            Assert.IsTrue(UapGameWatchPolicy.ValidateMaxSamples(1, out error), error);
            Assert.IsTrue(UapGameWatchPolicy.ValidateMaxSamples(UapGameWatchPolicy.MaxSamplesCeiling, out error), error);
            Assert.IsFalse(UapGameWatchPolicy.ValidateMaxSamples(0, out error));
            Assert.IsFalse(UapGameWatchPolicy.ValidateMaxSamples(UapGameWatchPolicy.MaxSamplesCeiling + 1, out error));
        }

        [Test]
        public void NormalizeMembers_TrimsRejectsEmptyAndDuplicates()
        {
            string error;
            string[] ok = UapGameWatchPolicy.NormalizeMembers(new[] { " current ", "velocity.y" }, out error);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { "current", "velocity.y" }, ok);

            Assert.IsNull(UapGameWatchPolicy.NormalizeMembers(null, out error));
            Assert.IsNull(UapGameWatchPolicy.NormalizeMembers(Array.Empty<string>(), out error));
            Assert.IsNull(UapGameWatchPolicy.NormalizeMembers(new[] { "a", " " }, out error));
            Assert.IsNull(UapGameWatchPolicy.NormalizeMembers(new[] { "a", "a" }, out error));
            StringAssert.Contains("more than once", error);
        }

        [Test]
        public void TryParseCondition_AllOperators_LongestPrefixFirst()
        {
            UapGameWatchCondition condition;
            string error;
            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("changed", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.Changed, condition.Op);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition(">= 3.5", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.GreaterOrEqual, condition.Op);
            Assert.AreEqual("3.5", condition.ValueText);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("<=10", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.LessOrEqual, condition.Op);
            Assert.AreEqual("10", condition.ValueText);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("== 0", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.Equal, condition.Op);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("!= true", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.NotEqual, condition.Op);
            Assert.AreEqual("true", condition.ValueText);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("< 10", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.LessThan, condition.Op);

            Assert.IsTrue(UapGameWatchPolicy.TryParseCondition("> 10", out condition, out error));
            Assert.AreEqual(UapGameWatchConditionOp.GreaterThan, condition.Op);

            Assert.IsFalse(UapGameWatchPolicy.TryParseCondition(">=", out condition, out error));
            StringAssert.Contains("needs a value", error);

            Assert.IsFalse(UapGameWatchPolicy.TryParseCondition("banana", out condition, out error));
            StringAssert.Contains("until", error);

            Assert.IsFalse(UapGameWatchPolicy.TryParseCondition("", out condition, out error));
            Assert.IsFalse(UapGameWatchPolicy.TryParseCondition(null, out condition, out error));
        }

        [Test]
        public void EvaluateCondition_Number_Bool_Text()
        {
            string error;
            UapGameWatchCondition lt10;
            UapGameWatchPolicy.TryParseCondition("< 10", out lt10, out error);
            Assert.IsTrue(UapGameWatchPolicy.EvaluateCondition(lt10,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Number, Number = 5 }, false, out error));
            Assert.IsFalse(UapGameWatchPolicy.EvaluateCondition(lt10,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Number, Number = 15 }, false, out error));

            UapGameWatchCondition eqZero;
            UapGameWatchPolicy.TryParseCondition("== 0", out eqZero, out error);
            Assert.IsTrue(UapGameWatchPolicy.EvaluateCondition(eqZero,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Number, Number = 0 }, false, out error));

            UapGameWatchCondition neqTrue;
            UapGameWatchPolicy.TryParseCondition("!= true", out neqTrue, out error);
            Assert.IsFalse(UapGameWatchPolicy.EvaluateCondition(neqTrue,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Bool, Bool = true }, false, out error));
            Assert.IsTrue(UapGameWatchPolicy.EvaluateCondition(neqTrue,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Bool, Bool = false }, false, out error));

            UapGameWatchCondition lessOnBool;
            UapGameWatchPolicy.TryParseCondition("< 1", out lessOnBool, out error);
            UapGameWatchPolicy.EvaluateCondition(lessOnBool,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Bool, Bool = true }, false, out error);
            Assert.IsNotNull(error, "an ordering operator against a boolean value must be reported, not silently false");

            UapGameWatchCondition eqIdle;
            UapGameWatchPolicy.TryParseCondition("== Idle", out eqIdle, out error);
            Assert.IsTrue(UapGameWatchPolicy.EvaluateCondition(eqIdle,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = "Idle" }, false, out error));
            Assert.IsFalse(UapGameWatchPolicy.EvaluateCondition(eqIdle,
                new UapGameWatchValue { Kind = UapGameWatchValueKind.Text, Text = "Running" }, false, out error));

            UapGameWatchCondition changed;
            UapGameWatchPolicy.TryParseCondition("changed", out changed, out error);
            Assert.IsTrue(UapGameWatchPolicy.EvaluateCondition(changed, default(UapGameWatchValue), true, out error));
            Assert.IsFalse(UapGameWatchPolicy.EvaluateCondition(changed, default(UapGameWatchValue), false, out error));
        }

        [Test]
        public void AnyValueDifferent_And_ShouldEmitSample()
        {
            Assert.IsTrue(UapGameWatchPolicy.AnyValueDifferent(null, new[] { "a" }));
            Assert.IsFalse(UapGameWatchPolicy.AnyValueDifferent(new[] { "a", "b" }, new[] { "a", "b" }));
            Assert.IsTrue(UapGameWatchPolicy.AnyValueDifferent(new[] { "a", "b" }, new[] { "a", "c" }));
            Assert.IsTrue(UapGameWatchPolicy.AnyValueDifferent(new[] { "a" }, new[] { "a", "b" }));

            Assert.IsTrue(UapGameWatchPolicy.ShouldEmitSample(changedOnly: false, isFirst: false, isFinal: false, anyChangedSinceLastEmitted: false));
            Assert.IsTrue(UapGameWatchPolicy.ShouldEmitSample(changedOnly: true, isFirst: true, isFinal: false, anyChangedSinceLastEmitted: false));
            Assert.IsTrue(UapGameWatchPolicy.ShouldEmitSample(changedOnly: true, isFirst: false, isFinal: true, anyChangedSinceLastEmitted: false));
            Assert.IsTrue(UapGameWatchPolicy.ShouldEmitSample(changedOnly: true, isFirst: false, isFinal: false, anyChangedSinceLastEmitted: true));
            Assert.IsFalse(UapGameWatchPolicy.ShouldEmitSample(changedOnly: true, isFirst: false, isFinal: false, anyChangedSinceLastEmitted: false));
        }

        [Test]
        public void DecideStop_PriorityOrder()
        {
            Assert.AreEqual(UapGameWatchStopReason.TargetDestroyed,
                UapGameWatchPolicy.DecideStop(true, true, true, true, true));
            Assert.AreEqual(UapGameWatchStopReason.PlayModeExit,
                UapGameWatchPolicy.DecideStop(false, true, true, true, true));
            Assert.AreEqual(UapGameWatchStopReason.Until,
                UapGameWatchPolicy.DecideStop(false, false, true, true, true));
            Assert.AreEqual(UapGameWatchStopReason.Duration,
                UapGameWatchPolicy.DecideStop(false, false, false, true, true));
            Assert.AreEqual(UapGameWatchStopReason.MaxSamples,
                UapGameWatchPolicy.DecideStop(false, false, false, false, true));
            Assert.IsNull(UapGameWatchPolicy.DecideStop(false, false, false, false, false));
        }

        [Test]
        public void ResolveMemberPath_Field_Property_Nested_MissingMember_NullMidPath()
        {
            var poco = new PocoTarget { PublicField = 7, Nested = new PocoNested { Inner = 42 } };
            string error;

            Assert.AreEqual(7, UapGameWatchPolicy.ResolveMemberPath(poco, "PublicField", out error));
            Assert.IsNull(error);

            Assert.AreEqual("hi", UapGameWatchPolicy.ResolveMemberPath(poco, "PublicProperty", out error));
            Assert.IsNull(error);

            Assert.AreEqual(-3, UapGameWatchPolicy.ResolveMemberPath(poco, "PrivateField", out error));
            Assert.IsNull(error, "a private field must still be readable, the way object_inspect reads a private [SerializeField]");

            Assert.AreEqual(42, UapGameWatchPolicy.ResolveMemberPath(poco, "Nested.Inner", out error));
            Assert.IsNull(error);

            Assert.IsNull(UapGameWatchPolicy.ResolveMemberPath(poco, "NoSuchMember", out error));
            StringAssert.Contains("NoSuchMember", error);

            poco.Nested = null;
            Assert.IsNull(UapGameWatchPolicy.ResolveMemberPath(poco, "Nested.Inner", out error));
            StringAssert.Contains("null", error);
        }

        [Test]
        public void ClassifyValue_ScalarsAndVectorShapedStructs()
        {
            Assert.AreEqual(UapGameWatchValueKind.Number, UapGameWatchPolicy.ClassifyValue(5).Kind);
            Assert.AreEqual(5.0, UapGameWatchPolicy.ClassifyValue(5).Number);
            UapGameWatchValue negFloat = UapGameWatchPolicy.ClassifyValue(-2.5f);
            Assert.AreEqual(UapGameWatchValueKind.Number, negFloat.Kind);
            Assert.AreEqual(-2.5, negFloat.Number, 1e-9, "a plain scalar keeps its sign -- only a struct's magnitude discards it");
            Assert.AreEqual(UapGameWatchValueKind.Bool, UapGameWatchPolicy.ClassifyValue(true).Kind);
            Assert.AreEqual(UapGameWatchValueKind.Text, UapGameWatchPolicy.ClassifyValue("hi").Kind);
            Assert.AreEqual(UapGameWatchValueKind.Text, UapGameWatchPolicy.ClassifyValue(DayOfWeek.Monday).Kind);
            Assert.AreEqual("Monday", UapGameWatchPolicy.ClassifyValue(DayOfWeek.Monday).Text);
            Assert.AreEqual(UapGameWatchValueKind.Text, UapGameWatchPolicy.ClassifyValue(null).Kind);

            var vectorLike = new PocoVector3 { x = 3, y = 4, z = 0 };
            UapGameWatchValue classified = UapGameWatchPolicy.ClassifyValue(vectorLike);
            Assert.AreEqual(UapGameWatchValueKind.Number, classified.Kind);
            Assert.AreEqual(5.0, classified.Number, 1e-9, "3-4-5 triangle magnitude");

            var mixed = new PocoMixed { Number = 3, Label = "x" };
            Assert.AreEqual(UapGameWatchValueKind.Text, UapGameWatchPolicy.ClassifyValue(mixed).Kind,
                "a struct with a non-numeric field is not vector-shaped");

            var propsOnly = new PocoPropsOnlyVector2(1, 1);
            UapGameWatchValue classifiedProps = UapGameWatchPolicy.ClassifyValue(propsOnly);
            Assert.AreEqual(UapGameWatchValueKind.Number, classifiedProps.Kind,
                "falls back to public properties when there are no qualifying public fields");
            Assert.AreEqual(Math.Sqrt(2), classifiedProps.Number, 1e-9);
        }

        private sealed class PocoNested
        {
            public int Inner;
        }

        private sealed class PocoTarget
        {
            public int PublicField;
#pragma warning disable 0414
            private int PrivateField = -3;
#pragma warning restore 0414
            public string PublicProperty
            {
                get { return "hi"; }
            }
            public PocoNested Nested;
        }

        private struct PocoVector3
        {
            public double x, y, z;
        }

        private struct PocoMixed
        {
            public double Number;
            public string Label;
        }

        private struct PocoPropsOnlyVector2
        {
            public PocoPropsOnlyVector2(double x, double y)
            {
                X = x;
                Y = y;
            }
            public double X { get; }
            public double Y { get; }
        }
    }
}

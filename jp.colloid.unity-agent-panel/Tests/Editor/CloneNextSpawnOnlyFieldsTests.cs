using System;
using System.Collections.Generic;
using System.Reflection;
using Colloid.AgentPanel.Integration;
using Colloid.AgentPanel.Model;
using NUnit.Framework;
using PanelSettings = Colloid.AgentPanel.Model.PanelSettings;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Guards the one invariant that ties AgentHub.CloneNextSpawnOnlyFields
    /// to SettingsChangeDetector.RequiresReconnect: <b>the clone must
    /// preserve every field the detector compares.</b>
    ///
    /// <para><b>Why this fixture exists (2026-09-22, reported from a real
    /// editor).</b> v0.59.0-beta.1 added `uloopAgentUseEnabled` to
    /// RequiresReconnect but not to the clone. The clone is a hand-written
    /// field-by-field copy, so the snapshot taken at spawn silently kept
    /// the field's DEFAULT (true) while the user's live settings said
    /// false. RequiresReconnect then compared true vs false on every
    /// evaluation, forever: the "changes apply after the next reconnect"
    /// pill never cleared, no matter how many times the user reconnected,
    /// and every later settings edit scheduled another needless reconnect.
    /// One missing line, invisible to every test that existed -- because
    /// every test named its fields by hand, exactly like the code it was
    /// checking.</para>
    ///
    /// <para>So the guard below names nothing. It walks PanelSettings'
    /// fields by reflection, and for each one it can mutate: change it,
    /// clone, and assert the detector sees no difference. A field the
    /// detector ignores is free to be dropped by the clone (that is what
    /// "next-spawn-only" means, and model / agentModelOverrides are
    /// deliberately in that group); a field it compares and the clone drops
    /// fails here, naming itself.</para>
    /// </summary>
    [TestFixture]
    public class CloneNextSpawnOnlyFieldsTests
    {
        /// <summary>
        /// The reported regression, pinned on its own so the symptom has a
        /// test that says what it was -- the reflection sweep below would
        /// also catch it, but not by name.
        /// </summary>
        [Test]
        public void UloopAgentUseEnabled_SurvivesTheClone_SoThePendingPillCanClear()
        {
            var settings = new PanelSettings { uloopAgentUseEnabled = false };
            PanelSettings snapshot = AgentHub.CloneNextSpawnOnlyFields(settings);

            Assert.IsFalse(snapshot.uloopAgentUseEnabled,
                "the spawn snapshot kept the field's default instead of what was spawned");
            Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(snapshot, settings),
                "a reconnect that just happened must leave nothing pending");
        }

        /// <summary>
        /// The general form: no field the detector compares may be lost by
        /// the clone. Mutating one field at a time keeps the failure
        /// message pointed at the culprit.
        /// </summary>
        [Test]
        public void EveryFieldTheDetectorComparesSurvivesTheClone()
        {
            var skipped = new List<string>();
            foreach (FieldInfo field in typeof(PanelSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var settings = new PanelSettings();
                if (!TryMutate(field, settings))
                {
                    skipped.Add(field.Name);
                    continue;
                }
                PanelSettings snapshot = AgentHub.CloneNextSpawnOnlyFields(settings);
                Assert.IsFalse(SettingsChangeDetector.RequiresReconnect(snapshot, settings),
                    "CloneNextSpawnOnlyFields drops '" + field.Name + "', which RequiresReconnect compares:"
                    + " the spawn snapshot keeps that field's default, so the panel reports a pending"
                    + " reconnect forever. Add it to the clone.");
            }

            // Not an assertion about which fields are skipped -- only a
            // guard that the sweep did not quietly degenerate into testing
            // nothing (a type this mutator stops understanding would
            // otherwise vanish from coverage without a sound).
            Assert.Less(skipped.Count, 8,
                "too many field types are unmutatable for this sweep to mean anything; skipped: "
                + string.Join(", ", skipped.ToArray()));
        }

        /// <summary>
        /// Moves <paramref name="field"/> off its default value on
        /// <paramref name="settings"/>. False when this mutator does not
        /// understand the field's type, which only costs coverage of that
        /// one field.
        /// </summary>
        private static bool TryMutate(FieldInfo field, PanelSettings settings)
        {
            Type type = field.FieldType;
            if (type == typeof(bool))
            {
                field.SetValue(settings, !(bool)field.GetValue(settings));
                return true;
            }
            if (type == typeof(string))
            {
                field.SetValue(settings, ((string)field.GetValue(settings) ?? string.Empty) + "-clone-guard");
                return true;
            }
            if (type == typeof(int))
            {
                field.SetValue(settings, (int)field.GetValue(settings) + 1);
                return true;
            }
            if (type == typeof(List<string>))
            {
                var list = (List<string>)field.GetValue(settings) ?? new List<string>();
                list = new List<string>(list);
                list.Add("clone-guard-probe");
                field.SetValue(settings, list);
                return true;
            }
            if (type.IsEnum)
            {
                Array values = Enum.GetValues(type);
                object current = field.GetValue(settings);
                foreach (object candidate in values)
                {
                    if (!Equals(candidate, current))
                    {
                        field.SetValue(settings, candidate);
                        return true;
                    }
                }
                // A single-valued enum cannot differ, so it cannot regress.
                return false;
            }
            return false;
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Source-scan guards over SettingsView's change handlers, in the same
    /// spirit as GlyphAuditTests: some invariants live in what a hand-written
    /// handler REMEMBERS to call, and no runtime assertion can see that.
    ///
    /// <para><b>Why (2026-09-22).</b> The thirteen UapOps module toggles are
    /// near-identical copies of each other. Twelve ended with
    /// <c>AgentHub.RequestAutoApplyReconnect()</c>; "web", added last in
    /// v0.57.0, did not. Because it still called RefreshReconnectHint, the
    /// panel SHOWED "changes apply after the next reconnect" and then never
    /// applied it -- the pill sat there until the user reconnected by hand.
    /// The module list is reconnect-relevant (the steering text is baked
    /// into the spawn payload), so this was a real lost change, not a
    /// cosmetic one, and it survived a release because every test named its
    /// handlers by hand just like the code did.</para>
    ///
    /// <para><b>What changed in the fix.</b> Rather than keep checking that
    /// ~100 handlers each remember the same two calls, SettingsView grew one
    /// chokepoint -- <c>CommitSettingsChange()</c> -- that every handler ends
    /// at, plus <c>ToggleUapOpsModule(id, on)</c> that all thirteen module
    /// toggles now share. These scans therefore guard the chokepoint's
    /// EXCLUSIVITY: as long as save / hint / auto-apply happen in exactly one
    /// place, a handler cannot forget any of them.</para>
    ///
    /// <para>The scans name no individual handler: they find them by shape,
    /// so a fourteenth module written from the same template is covered the
    /// day it is added.</para>
    /// </summary>
    public class SettingsHandlerAuditTests
    {
        private const string SettingsViewPath =
            "Packages/jp.colloid.unity-agent-panel/Editor/UI/SettingsView.cs";

        private static string ReadSettingsView()
        {
            string path = Path.GetFullPath(SettingsViewPath);
            Assert.IsTrue(File.Exists(path), "SettingsView.cs not found at " + path);
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The source with every whole-line comment removed. The doc
        /// comments in this file discuss the very call names the scans
        /// count, so counting them raw would report prose as code.
        /// </summary>
        private static string CodeOnly(string source)
        {
            string[] lines = source.Split('\n');
            var kept = new List<string>();
            foreach (string line in lines)
            {
                if (!line.TrimStart().StartsWith("//"))
                {
                    kept.Add(line);
                }
            }
            return string.Join("\n", kept.ToArray());
        }

        /// <summary>
        /// Each handler's body, keyed by name, for every method matching
        /// <paramref name="namePattern"/>. Bodies are cut at the method's
        /// closing brace -- the file indents methods by eight spaces, so a
        /// line that is exactly "        }" ends one. Comments are stripped
        /// so a handler cannot satisfy a scan by merely mentioning a call.
        /// </summary>
        private static Dictionary<string, string> HandlerBodies(string source, string namePattern)
        {
            var bodies = new Dictionary<string, string>();
            var signature = new Regex(@"private (?:static )?void (" + namePattern + @")\s*\(");
            foreach (Match match in signature.Matches(source))
            {
                int end = source.IndexOf("\n        }", match.Index);
                Assert.Greater(end, match.Index, "could not find the end of " + match.Groups[1].Value);
                bodies[match.Groups[1].Value] = CodeOnly(source.Substring(match.Index, end - match.Index));
            }
            return bodies;
        }

        /// <summary>
        /// The thirteen module toggles differ only in a module id (and, for
        /// "markers", one extra side effect), so they share one helper. A
        /// fourteenth copied from the template inherits the save, the hint
        /// and the auto-apply for free -- which is the whole point.
        /// </summary>
        [Test]
        public void EveryUapOpsModuleToggle_RoutesThroughTheSharedHelper()
        {
            Dictionary<string, string> handlers =
                HandlerBodies(ReadSettingsView(), @"OnUapOps\w*ModuleToggleChanged");

            Assert.GreaterOrEqual(handlers.Count, 13,
                "the scan found fewer module toggles than this package ships; the naming convention"
                + " it matches on has probably changed, which would make this guard silently vacuous");

            foreach (KeyValuePair<string, string> handler in handlers)
            {
                StringAssert.Contains("ToggleUapOpsModule(", handler.Value,
                    handler.Key + " edits uapOpsModules by hand instead of calling ToggleUapOpsModule."
                    + " That list is reconnect-relevant (the steering text is baked into the spawn"
                    + " payload), so a hand-rolled copy that forgets the commit shows a pending change"
                    + " nothing applies -- exactly the defect the 'web' toggle shipped with in v0.57.0.");
            }
        }

        /// <summary>
        /// The invariant the chokepoint exists to hold: write the field,
        /// then hand off. A handler that writes a setting and stops has
        /// saved nothing to disk, refreshed no pill and scheduled no
        /// reconnect.
        /// </summary>
        [Test]
        public void EveryHandlerThatWritesASetting_EndsAtTheChokepoint()
        {
            Dictionary<string, string> handlers = HandlerBodies(ReadSettingsView(), @"On\w+");
            var writesASetting = new Regex(@"PanelStateStore\.instance\.Settings\.\w+\s*=[^=]");
            var offenders = new List<string>();
            int writers = 0;
            foreach (KeyValuePair<string, string> handler in handlers)
            {
                if (!writesASetting.IsMatch(handler.Value))
                {
                    continue;
                }
                writers++;
                if (!handler.Value.Contains("CommitSettingsChange()"))
                {
                    offenders.Add(handler.Key);
                }
            }
            Assert.GreaterOrEqual(writers, 20,
                "the scan found far fewer setting-writing handlers than this view has; its shape"
                + " match has probably drifted, which would make this guard silently vacuous");
            CollectionAssert.IsEmpty(offenders,
                "these handlers write a PanelSettings field and never call CommitSettingsChange(),"
                + " so the edit is never saved and no reconnect is ever scheduled for it: "
                + string.Join(", ", offenders.ToArray()));
        }

        /// <summary>
        /// Exclusivity. One call site each means the question "is this
        /// change reconnect-relevant?" is asked in one place
        /// (SettingsChangeDetector, via AgentHub) instead of being
        /// re-derived by hand in every handler -- which is how "web" got it
        /// wrong. A second call site is not a bug on its own, but it is the
        /// shape the old defect grew in, so it has to be a deliberate edit
        /// to this guard rather than an accident.
        /// </summary>
        [Test]
        public void OnlyTheChokepointSavesAndSchedulesTheReconnect()
        {
            string code = CodeOnly(ReadSettingsView());
            Assert.AreEqual(1, CountOf(code, "AgentHub.RequestAutoApplyReconnect("),
                "settings auto-apply must be requested from CommitSettingsChange() alone");
            Assert.AreEqual(1, CountOf(code, "PanelStateStore.instance.SaveNow()"),
                "this view must save through CommitSettingsChange() alone");
        }

        /// <summary>
        /// The same invariant stated the other way round, which is how the
        /// original escaped: showing the pending pill is a PROMISE that
        /// something will apply the change. A handler that makes it without
        /// going through the chokepoint leaves the user waiting on nothing.
        /// OnReconnectClicked is the one honest exception -- it performs the
        /// reconnect itself rather than scheduling one.
        /// </summary>
        [Test]
        public void NoHandler_ShowsThePendingPill_WithoutSchedulingTheWork()
        {
            Dictionary<string, string> handlers = HandlerBodies(ReadSettingsView(), @"On\w+");
            var offenders = new List<string>();
            foreach (KeyValuePair<string, string> handler in handlers)
            {
                if (handler.Key == "OnReconnectClicked")
                {
                    continue;
                }
                if (handler.Value.Contains("RefreshReconnectHint(")
                    && !handler.Value.Contains("CommitSettingsChange()"))
                {
                    offenders.Add(handler.Key);
                }
            }
            CollectionAssert.IsEmpty(offenders,
                "these handlers show the reconnect-pending pill but never schedule the reconnect that"
                + " clears it: " + string.Join(", ", offenders.ToArray()));
        }

        private static int CountOf(string source, string needle)
        {
            int count = 0;
            int at = source.IndexOf(needle);
            while (at >= 0)
            {
                count++;
                at = source.IndexOf(needle, at + needle.Length);
            }
            return count;
        }
    }
}

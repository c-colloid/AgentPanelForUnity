using Colloid.AgentPanel.Ops;
using Colloid.AgentPanel.Ops.Markers;
using Colloid.AgentPanel.UI;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The one overlay visibility switch (design note docs/design-notes/
    /// 2026-10-01-scene-overlay-visibility.md): hiding never touches the
    /// marker store, the state survives a cold read (EditorPrefs), and
    /// Changed fires once per real change so the toolbar toggle and the
    /// context-bar chip stay in step without a repaint loop.
    /// </summary>
    [TestFixture]
    public class SceneMarkerVisibilityTests
    {
        private bool _visibleBefore;

        [SetUp]
        public void SetUp()
        {
            _visibleBefore = SceneMarkerVisibility.Visible;
            SceneMarkerStore.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SceneMarkerVisibility.Visible = _visibleBefore;
            SceneMarkerStore.ResetForTests();
        }

        [Test]
        public void Hide_KeepsEveryMarkerInTheStore()
        {
            SceneMarkerStore.Add(new SceneMarker { Label = "a", Origin = SceneMarkerOrigin.Agent });
            SceneMarkerStore.Add(new SceneMarker { Label = "p", Origin = SceneMarkerOrigin.User });
            int before = SceneMarkerStore.Count;

            SceneMarkerVisibility.Visible = false;

            Assert.IsFalse(SceneMarkerVisibility.Visible);
            Assert.AreEqual(before, SceneMarkerStore.Count, "hiding is not clearing");
            SceneMarkerVisibility.Visible = true;
            Assert.AreEqual(before, SceneMarkerStore.Count);
        }

        [Test]
        public void Changed_FiresOncePerRealChange_AndNotForTheSameValue()
        {
            int fired = 0;
            System.Action handler = delegate { fired++; };
            SceneMarkerVisibility.Changed += handler;
            try
            {
                SceneMarkerVisibility.Visible = true;    // already visible after SetUp? only counts if it changed
                int afterFirst = fired;
                SceneMarkerVisibility.Visible = false;
                Assert.AreEqual(afterFirst + 1, fired);
                SceneMarkerVisibility.Visible = false;
                Assert.AreEqual(afterFirst + 1, fired, "same value again is a no-op");
                SceneMarkerVisibility.Toggle();
                Assert.AreEqual(afterFirst + 2, fired);
                Assert.IsTrue(SceneMarkerVisibility.Visible);
            }
            finally
            {
                SceneMarkerVisibility.Changed -= handler;
            }
        }

        [Test]
        public void Hidden_IsPersistedAsAPreference()
        {
            SceneMarkerVisibility.Visible = false;
            Assert.IsTrue(EditorPrefs.GetBool("Colloid.AgentPanel.SceneOverlaysHidden", false),
                "the toggle must survive a restart like the Gizmos button, so it is an EditorPrefs value");
            SceneMarkerVisibility.Visible = true;
            Assert.IsFalse(EditorPrefs.GetBool("Colloid.AgentPanel.SceneOverlaysHidden", true));
        }

        /// <summary>
        /// The toolbar toggles moved from EditorGUIUtility.IconContent (logs
        /// an error for a missing name) to the silent IconLoader.Find; this
        /// pins that every name the four toggles use still resolves on this
        /// editor, so none silently degrades to its text label.
        /// </summary>
        [Test]
        public void AgentToolsToolbar_IconNamesResolveSilently()
        {
            string[] names =
            {
                "d_ToolHandlePivot", "d_Mesh Icon", "d_TerrainInspector.TerrainToolRaise",
                "d_scenevis_visible_hover", "d_scenevis_hidden_hover"
            };
            foreach (string name in names)
            {
                Assert.IsNotNull(IconLoader.Find(name), name + " did not resolve through IconLoader.Find");
            }
        }

        [Test]
        public void MarkerTools_TellTheAgentWhenTheOverlaysAreHidden()
        {
            SceneMarkerVisibility.Visible = false;
            var add = new UapMarkerAddTool();
            string added = add.Execute(Colloid.AgentPanel.Core.Json.JsonParser.Parse(
                "{\"position\":[0,0,0],\"label\":\"x\"}"))[0]["text"].AsString(string.Empty);
            StringAssert.Contains("hidden", added);

            var list = new UapMarkerListTool();
            string listed = list.Execute(Colloid.AgentPanel.Core.Json.JsonNode.NewObject())[0]["text"].AsString(string.Empty);
            StringAssert.Contains("hidden", listed);

            SceneMarkerVisibility.Visible = true;
            listed = list.Execute(Colloid.AgentPanel.Core.Json.JsonNode.NewObject())[0]["text"].AsString(string.Empty);
            StringAssert.DoesNotContain("hidden", listed);
        }
    }
}

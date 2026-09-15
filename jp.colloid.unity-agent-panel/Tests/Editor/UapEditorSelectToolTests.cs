using System;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Real Selection writes through uap_editor_select (design note
    /// docs/design-notes/2026-09-14-selection-set-tool.md). The behaviour
    /// that matters to callers is narrow but easy to get wrong: a single
    /// path must land in Selection.activeGameObject (that is the field
    /// NDMF's and Modular Avatar's "Manual bake avatar" read), a multi
    /// selection must NOT collapse to one object, and a bad path must
    /// leave the previous selection untouched rather than half-applying.
    /// </summary>
    [TestFixture]
    public class UapEditorSelectToolTests
    {
        private GameObject _a;
        private GameObject _b;
        private UapEditorSelectTool _tool;
        private UnityEngine.Object[] _previousSelection;

        [SetUp]
        public void SetUp()
        {
            _previousSelection = Selection.objects;
            _a = new GameObject("UapEditorSelectTestA");
            _b = new GameObject("UapEditorSelectTestB");
            _tool = new UapEditorSelectTool();
        }

        [TearDown]
        public void TearDown()
        {
            Selection.objects = _previousSelection ?? new UnityEngine.Object[0];
            if (_a != null) { UnityEngine.Object.DestroyImmediate(_a); }
            if (_b != null) { UnityEngine.Object.DestroyImmediate(_b); }
        }

        [Test]
        public void Execute_SinglePath_BecomesTheActiveGameObject()
        {
            // The whole point of the tool: a selection-driven menu item
            // reads Selection.activeGameObject, not just Selection.objects.
            _tool.Execute(JsonNode.NewObject().Set("path", "UapEditorSelectTestA"));

            Assert.AreSame(_a, Selection.activeGameObject);
            Assert.AreEqual(1, Selection.objects.Length);
        }

        [Test]
        public void Execute_SeveralPaths_SelectsThemAll()
        {
            JsonNode paths = JsonNode.NewArray().Add("UapEditorSelectTestA").Add("UapEditorSelectTestB");

            _tool.Execute(JsonNode.NewObject().Set("paths", paths));

            Assert.AreEqual(2, Selection.objects.Length,
                "a multi-object selection must not collapse to a single object");
            CollectionAssert.Contains(Selection.objects, _a);
            CollectionAssert.Contains(Selection.objects, _b);
            // Which of the two Unity makes active is its own choice, so that
            // is deliberately not pinned here -- callers that need a specific
            // active object pass 'path', which this fixture covers above.
        }

        [Test]
        public void Execute_Clear_EmptiesTheSelection()
        {
            Selection.objects = new UnityEngine.Object[] { _a };

            _tool.Execute(JsonNode.NewObject().Set("clear", true));

            Assert.AreEqual(0, Selection.objects.Length);
            Assert.IsNull(Selection.activeObject);
        }

        [Test]
        public void Execute_OneBadPathAmongSeveral_LeavesTheSelectionUntouched()
        {
            // Resolve-then-assign, not assign-as-you-go: a half-applied
            // selection is worse than none, because the very next call is
            // usually a bake that acts on whatever is selected.
            Selection.objects = new UnityEngine.Object[] { _a };
            JsonNode paths = JsonNode.NewArray().Add("UapEditorSelectTestB").Add("NoSuchObjectAtAll12345");

            Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("paths", paths));
            });

            Assert.AreEqual(1, Selection.objects.Length);
            Assert.AreSame(_a, Selection.activeGameObject);
        }

        [Test]
        public void Execute_NoArguments_Throws()
        {
            Assert.Throws<ArgumentException>(delegate { _tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void Execute_TwoTargetKindsAtOnce_Throws()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                _tool.Execute(JsonNode.NewObject()
                    .Set("path", "UapEditorSelectTestA")
                    .Set("clear", true));
            });
        }

        [Test]
        public void Execute_UnknownPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("path", "NoSuchObjectAtAll12345"));
            });
        }

        [Test]
        public void Execute_UnknownAssetPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                _tool.Execute(JsonNode.NewObject().Set("assetPath", "Assets/NoSuchAsset12345.prefab"));
            });
        }

        [Test]
        public void Execute_ReportsWhatEndedUpSelected()
        {
            JsonNode content = _tool.Execute(JsonNode.NewObject().Set("path", "UapEditorSelectTestA"));

            string text = content[0]["text"].AsString(string.Empty);
            JsonNode result = JsonParser.Parse(text);
            Assert.AreEqual(1, result["selectionCount"].AsInt(0));
            Assert.AreEqual("UapEditorSelectTestA", result["active"].AsString(null));
        }
    }
}

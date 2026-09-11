using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// P2 of the 2026-09-07 design note: how a drop is split between
    /// context chips and image attachments -- OS file drops (paths only),
    /// Texture assets (object + project path), mixed drops, and the
    /// droppable check that used to ignore path-only drags.
    /// </summary>
    [TestFixture]
    public class DropPayloadClassifierTests
    {
        private const string Root = "/proj";
        private readonly List<Object> _spawned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _spawned)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }
            _spawned.Clear();
        }

        private Texture2D Tex()
        {
            var t = new Texture2D(2, 2);
            _spawned.Add(t);
            return t;
        }

        private GameObject Go(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        [Test]
        public void OsDrop_PathsOnly_ImageFilesBecomeAttachments_OthersIgnored()
        {
            DropPayload payload = DropPayloadClassifier.Classify(null,
                new[] { "/home/me/shot.PNG", "/home/me/notes.txt", "C:\\pics\\a.jpg" }, o => null, Root);

            CollectionAssert.AreEqual(new[] { "/home/me/shot.PNG", "C:/pics/a.jpg" }, payload.ImagePaths);
            Assert.AreEqual(0, payload.Objects.Count);
        }

        [Test]
        public void TextureAsset_BecomesAnImagePath_NotAChip()
        {
            Texture2D tex = Tex();
            DropPayload payload = DropPayloadClassifier.Classify(new Object[] { tex }, new[] { "Assets/Art/icon.png" },
                o => o == tex ? "Assets/Art/icon.png" : null, Root);

            CollectionAssert.AreEqual(new[] { "/proj/Assets/Art/icon.png" }, payload.ImagePaths);
            Assert.AreEqual(0, payload.Objects.Count, "the texture is an image, not a text chip");
        }

        [Test]
        public void TextureWithoutImageFile_StaysAnObject()
        {
            Texture2D tex = Tex();   // e.g. a render texture / .asset-backed texture
            DropPayload payload = DropPayloadClassifier.Classify(new Object[] { tex }, new[] { "Assets/rt.renderTexture" },
                o => "Assets/rt.renderTexture", Root);
            Assert.AreEqual(1, payload.Objects.Count);
            Assert.AreEqual(0, payload.ImagePaths.Count);
        }

        [Test]
        public void MixedDrop_ObjectsStayObjects_PathsBackedByObjectsAreNotDuplicated()
        {
            GameObject go = Go("UapDropTest_Prefab");
            Texture2D tex = Tex();
            DropPayload payload = DropPayloadClassifier.Classify(new Object[] { go, tex },
                new[] { "Assets/Prefabs/Thing.prefab", "Assets/Art/icon.png" },
                o => o == go ? "Assets/Prefabs/Thing.prefab" : "Assets/Art/icon.png", Root);

            Assert.AreEqual(1, payload.Objects.Count);
            Assert.AreSame(go, payload.Objects[0]);
            CollectionAssert.AreEqual(new[] { "/proj/Assets/Art/icon.png" }, payload.ImagePaths);
        }

        [Test]
        public void DuplicatePaths_Collapse()
        {
            DropPayload payload = DropPayloadClassifier.Classify(null, new[] { "/a/x.png", "/a/x.png" }, o => null, Root);
            Assert.AreEqual(1, payload.ImagePaths.Count);
        }

        [Test]
        public void HasDroppable_ObjectsOrImagePaths_NotTextFiles()
        {
            Assert.IsFalse(DropPayloadClassifier.HasDroppable(null, null));
            Assert.IsFalse(DropPayloadClassifier.HasDroppable(new Object[0], new[] { "/a/readme.md" }));
            Assert.IsTrue(DropPayloadClassifier.HasDroppable(null, new[] { "/a/readme.md", "/a/b.jpeg" }));
            Assert.IsTrue(DropPayloadClassifier.HasDroppable(new Object[] { Go("UapDropTest_Go") }, null));
        }

        [Test]
        public void ToAbsolute_ProjectRelativeAndRooted()
        {
            Assert.AreEqual("/proj/Assets/a.png", DropPayloadClassifier.ToAbsolute("Assets/a.png", Root));
            Assert.AreEqual("/tmp/a.png", DropPayloadClassifier.ToAbsolute("/tmp/a.png", Root));
            Assert.AreEqual("Assets/a.png", DropPayloadClassifier.ToAbsolute("Assets/a.png", null));
        }
    }
}

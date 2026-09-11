using System.Collections.Generic;
using Colloid.AgentPanel.Ops.Markers;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// P5 of the 2026-09-07 design note: the user pin's pure pieces -- the
    /// marker a pin becomes, the click-point resolution against a real
    /// collider (Physics.Raycast works in EditMode), the nearest-object
    /// listing, the chip payload, and the Armed toggle event.
    ///
    /// Regression note (2026-09-08, full-suite order dependence):
    /// PinsInStore_GetReusableNumbers asserts the English pin label and
    /// failed in the full AITemp run with the Japanese label. Fixtures that build
    /// a PermissionWindow / AgentPanelWindow via CreateGUI apply the
    /// sandbox's persisted language setting (Auto, which resolves to
    /// Japanese on this OS) through L10n.ApplyFromSettings and used to
    /// leave it applied. Those fixtures now restore L10n in TearDown, and
    /// this fixture pins English itself because its expectations are
    /// English strings, not "whatever the machine's OS language is".
    /// </summary>
    [TestFixture]
    public class SceneMarkerPinTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private GameObject Spawn(string name, Vector3 position, PrimitiveType type = PrimitiveType.Cube)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.position = position;
            _spawned.Add(go);
            return go;
        }

        [SetUp]
        public void SetUp()
        {
            UI.L10n.OverrideForTests(Model.PanelLanguage.English);
            SceneMarkerStore.ResetForTests();
            SceneMarkerPin.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UI.L10n.OverrideForTests(null);
            SceneMarkerPin.ResetForTests();
            SceneMarkerStore.ResetForTests();
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _spawned.Clear();
        }

        [Test]
        public void CreatePinMarker_IsAMagentaUserPoint()
        {
            SceneMarker pin = SceneMarkerPin.CreatePinMarker(new Vector3(1, 2, 3));
            Assert.AreEqual(SceneMarkerOrigin.User, pin.Origin);
            Assert.AreEqual(SceneMarkerKind.Point, pin.Kind);
            Assert.AreEqual(new Vector3(1, 2, 3), pin.Position);
            Assert.AreEqual(SceneMarkerPin.PinSize, pin.Size);
            Assert.IsFalse(pin.HasTarget);
            Assert.Greater(pin.Color.r, 0.9f);
            Assert.Greater(pin.Color.b, 0.9f);
        }

        [Test]
        public void ResolveClickPoint_HitsTheColliderUnderTheRay_AndNamesIt()
        {
            Spawn("UapPinTest_Floor", new Vector3(0f, -1f, 0f), PrimitiveType.Plane);
            Physics.SyncTransforms();
            Vector3 position;
            string hit;

            SceneMarkerPin.ResolveClickPoint(new Ray(new Vector3(0.5f, 5f, 0.5f), Vector3.down), null, out position, out hit);

            Assert.AreEqual(-1f, position.y, 0.01f);
            Assert.AreEqual(0.5f, position.x, 0.01f);
            Assert.AreEqual("UapPinTest_Floor", hit);
        }

        [Test]
        public void MeshRaycaster_IsAvailable_OnThisEditor()
        {
            Assert.IsTrue(SceneMeshRaycaster.Available, "HandleUtility.IntersectRayMesh not found by reflection");
        }

        [Test]
        public void ResolveClickPoint_HitsAColliderlessMesh_WhereItIsDrawn()
        {
            // A cube with its collider removed: Physics.Raycast cannot see
            // it, the mesh raycast must (the user's "only the floor" bug).
            GameObject cube = Spawn("UapPinTest_NoCollider", new Vector3(0f, 2f, 0f));
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            Spawn("UapPinTest_Floor", new Vector3(0f, -1f, 0f), PrimitiveType.Plane);
            Physics.SyncTransforms();
            Vector3 position;
            Vector3 normal;
            string hit;

            SceneMarkerPin.ResolveClickPoint(new Ray(new Vector3(0.1f, 10f, 0.1f), Vector3.down), null,
                new[] { cube.GetComponent<Renderer>() }, out position, out normal, out hit);

            Assert.AreEqual(2.5f, position.y, 0.01f, "expected the cube's top face, not the floor");
            Assert.AreEqual(1f, normal.y, 0.01f);
            Assert.AreEqual("UapPinTest_NoCollider", hit);
        }

        [Test]
        public void ResolveClickPoint_PrefersTheNearerOfColliderAndMesh()
        {
            GameObject near = Spawn("UapPinTest_NearMesh", new Vector3(0f, 3f, 0f));
            Object.DestroyImmediate(near.GetComponent<Collider>());
            Spawn("UapPinTest_FarCollider", new Vector3(0f, 0f, 0f));
            Physics.SyncTransforms();
            Vector3 position;
            Vector3 normal;
            string hit;

            SceneMarkerPin.ResolveClickPoint(new Ray(new Vector3(0f, 10f, 0f), Vector3.down), null,
                new[] { near.GetComponent<Renderer>() }, out position, out normal, out hit);
            Assert.AreEqual("UapPinTest_NearMesh", hit);

            // Ray from below: the collider cube is nearer than the mesh cube.
            SceneMarkerPin.ResolveClickPoint(new Ray(new Vector3(0f, -10f, 0f), Vector3.up), null,
                new[] { near.GetComponent<Renderer>() }, out position, out normal, out hit);
            Assert.AreEqual("UapPinTest_FarCollider", hit);
            Assert.AreEqual(-0.5f, position.y, 0.01f);
        }

        [Test]
        public void ResolveClickPoint_SkipsDisabledAndHiddenRenderers()
        {
            GameObject cube = Spawn("UapPinTest_Disabled", new Vector3(0f, 2f, 0f));
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.GetComponent<Renderer>().enabled = false;
            Vector3 position;
            Vector3 normal;
            string hit;

            SceneMarkerPin.ResolveClickPoint(new Ray(new Vector3(0f, 10f, 0f), Vector3.down), null,
                new[] { cube.GetComponent<Renderer>() }, out position, out normal, out hit);

            Assert.AreNotEqual("UapPinTest_Disabled", hit);
        }

        [Test]
        public void ResolveClickPoint_NothingUnderTheRay_FallsBackAlongTheRay()
        {
            Vector3 position;
            string hit;
            var ray = new Ray(new Vector3(100f, 100f, 100f), Vector3.up);

            SceneMarkerPin.ResolveClickPoint(ray, null, out position, out hit);

            Assert.AreEqual(ray.origin + Vector3.up * SceneMarkerPin.FallbackDistance, position);
            StringAssert.Contains(SceneMarkerPin.FallbackDistance.ToString(), hit);
        }

        [Test]
        public void DescribeNearest_SortsByDistance_AndCaps()
        {
            var far = Spawn("UapPinTest_Far", new Vector3(10f, 0f, 0f)).GetComponent<Renderer>();
            var near = Spawn("UapPinTest_Near", new Vector3(1f, 0f, 0f)).GetComponent<Renderer>();
            var mid = Spawn("UapPinTest_Mid", new Vector3(4f, 0f, 0f)).GetComponent<Renderer>();

            string text = SceneMarkerPin.DescribeNearest(Vector3.zero, new[] { far, near, mid }, 2);

            Assert.AreEqual("UapPinTest_Near (1.0m), UapPinTest_Mid (4.0m)", text);
            Assert.AreEqual(string.Empty, SceneMarkerPin.DescribeNearest(Vector3.zero, null, 3));
        }

        [Test]
        public void FormatPayload_HasEveryLine_InOrder()
        {
            string text = SceneMarkerPin.FormatPayload(7, new Vector3(1.234f, 0f, -2f), "Env/Ground",
                "Tree (1.2m)", new Vector3(9f, 7f, -9f), new Vector3(1f, 0f, 1f));

            StringAssert.StartsWith("Scene marker P7", text);
            StringAssert.Contains("world position: (1.23, 0.00, -2.00)", text);
            StringAssert.Contains("hit object: Env/Ground", text);
            StringAssert.Contains("nearest objects: Tree (1.2m)", text);
            StringAssert.Contains("scene view camera: position (9.00, 7.00, -9.00), pivot (1.00, 0.00, 1.00)", text);
            Assert.Less(text.IndexOf("world position"), text.IndexOf("hit object"));
        }

        [Test]
        public void FormatPayload_OmitsNearestWhenEmpty()
        {
            string text = SceneMarkerPin.FormatPayload(1, Vector3.zero, "x", string.Empty, Vector3.zero, Vector3.zero);
            StringAssert.DoesNotContain("nearest objects", text);
        }

        [Test]
        public void Armed_RaisesArmedChanged_OnlyOnTransitions()
        {
            int raised = 0;
            System.Action handler = () => raised++;
            SceneMarkerPin.ArmedChanged += handler;
            try
            {
                SceneMarkerPin.Armed = true;
                SceneMarkerPin.Armed = true;
                SceneMarkerPin.Armed = false;
            }
            finally
            {
                SceneMarkerPin.ArmedChanged -= handler;
            }
            Assert.AreEqual(2, raised);
        }

        [Test]
        public void PinsInStore_GetReusableNumbers_IndependentOfIds()
        {
            SceneMarkerStore.Add(new SceneMarker { Label = "agent first" });          // id 1
            SceneMarker p1 = SceneMarkerStore.Add(SceneMarkerPin.CreatePinMarker(Vector3.zero)); // id 2
            SceneMarker p2 = SceneMarkerStore.Add(SceneMarkerPin.CreatePinMarker(Vector3.one));  // id 3
            Assert.AreEqual(1, p1.PinNumber);
            Assert.AreEqual(2, p2.PinNumber);
            Assert.AreEqual("P1  pin", p1.DisplayLabel);

            SceneMarkerStore.Remove(p1.Id);
            SceneMarker p3 = SceneMarkerStore.Add(SceneMarkerPin.CreatePinMarker(Vector3.up));
            Assert.AreEqual(1, p3.PinNumber, "the freed number is reused");
            Assert.AreNotEqual(p1.Id, p3.Id, "ids are never reused");
        }

        [Test]
        public void PinWorldSize_ScalesWithDistance()
        {
            var go = new GameObject("UapPinTest_Camera");
            _spawned.Add(go);
            Camera cam = go.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            float near = SceneMarkerRenderer.PinWorldSize(cam, cam.transform.position + cam.transform.forward * 2f);
            float far = SceneMarkerRenderer.PinWorldSize(cam, cam.transform.position + cam.transform.forward * 20f);
            Assert.Greater(far, near * 9f);
            Assert.Less(near, 0.1f, "a pin two metres away is a few centimetres, not a 0.3 m ball");
        }

        [Test]
        public void PinInStore_IsListedAsUserPin_AndSurvivesAgentClear()
        {
            SceneMarker pin = SceneMarkerStore.Add(SceneMarkerPin.CreatePinMarker(Vector3.one));
            SceneMarkerStore.Add(new SceneMarker { Label = "agent" });

            Assert.AreEqual(1, SceneMarkerStore.Clear(false));
            Assert.IsNotNull(SceneMarkerStore.Find(pin.Id), "a default clear keeps the user's pin");
            Assert.AreEqual(1, SceneMarkerStore.CountByOrigin(SceneMarkerOrigin.User));
        }
    }
}

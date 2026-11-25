using System;
using System.Collections.Generic;
using System.Reflection;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Exercises the reflection layers' "package PRESENT" branch (design
    /// note docs/design-notes/2026-09-22-uloop-phase1.md section 2.6).
    /// ci/HostProjectCoreOnly now carries com.unity.ugui and
    /// com.unity.inputsystem so this branch runs somewhere in CI;
    /// ci/HostProject deliberately keeps both absent, which is what
    /// UapGameKeyToolTests.InputSystemAccess_LoadsOrNamesThePackage,
    /// UapGamePointerToolTests.UguiAccess_LoadsOrNamesThePackage and
    /// UapUguiElementCatalogTests.Collect_LoadsOrNamesThePackage exercise
    /// (the "package absent" branch). This file self-skips with
    /// Assert.Ignore wherever a package it needs is not in the running
    /// project, so it never fails a suite that legitimately lacks one --
    /// a developer's Core-only sandbox without the Input System, for
    /// instance, or (until this task) either CI host project.
    /// </summary>
    [TestFixture]
    public class UapOptionalPackagePresenceTests
    {
        private const string Prefix = "UapOptionalPackagePresenceTest";

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in UnityObjectCompat.FindAll<GameObject>())
            {
                if (go != null && go.name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void ParseKey_Space_ResolvesWhenTheInputSystemIsPresent()
        {
            string loadError;
            if (!UapInputSystemKeyboardAccess.TryLoad(out loadError))
            {
                Assert.Ignore("com.unity.inputsystem is not in this project: " + loadError);
                return;
            }
            string parseError;
            object key = UapInputSystemKeyboardAccess.ParseKey("Space", out parseError);
            Assert.IsNotNull(key, "ParseKey(\"Space\") returned null: " + parseError);
        }

        [Test]
        public void ParseKey_EveryKnownKeyName_ResolvesAgainstTheInstalledInputSystem()
        {
            // UapGameKeyPolicy.KnownKeys is a hand-written mirror of
            // UnityEngine.InputSystem.Key (it has to be: the policy class
            // stays Unity-free so the license-free smoke gate can compile
            // it, and the real enum only exists once the package is
            // present). This is the one place that checks the mirror
            // against the real enum; a name Unity renamed or removed would
            // otherwise surface only as "unknown key" at call time, not as
            // a failing test here.
            string loadError;
            if (!UapInputSystemKeyboardAccess.TryLoad(out loadError))
            {
                Assert.Ignore("com.unity.inputsystem is not in this project: " + loadError);
                return;
            }
            var missing = new List<string>();
            foreach (string name in UapGameKeyPolicy.KnownKeys)
            {
                string parseError;
                object key = UapInputSystemKeyboardAccess.ParseKey(name, out parseError);
                if (key == null)
                {
                    missing.Add(name);
                }
            }
            Assert.IsEmpty(missing, "UapGameKeyPolicy.KnownKeys names missing from the installed Input System's"
                + " Key enum -- update the hand-written list in UapGameKeyPolicy.cs: " + string.Join(", ", missing.ToArray()));
        }

        [Test]
        public void UguiHandlerResolution_WorksOnAFreshEventSystemAndButton()
        {
            string loadError;
            if (!UapUguiEventAccess.TryLoad(out loadError))
            {
                Assert.Ignore("com.unity.ugui is not in this project: " + loadError);
                return;
            }

            Type eventSystemType = UapUguiEventAccess.FindType("UnityEngine.EventSystems.EventSystem");
            Type buttonType = UapUguiEventAccess.FindType("UnityEngine.UI.Button");
            Assert.IsNotNull(eventSystemType, "uGUI loaded but UnityEngine.EventSystems.EventSystem could not be found.");
            Assert.IsNotNull(buttonType, "uGUI loaded but UnityEngine.UI.Button could not be found.");

            var eventSystemGo = new GameObject(Prefix + "EventSystem");
            object eventSystem = eventSystemGo.AddComponent(eventSystemType);
            Assert.IsNotNull(eventSystem);

            // EventSystem.current is deliberately NOT set: nothing below reads
            // it, and in batch mode an EventSystem that has not gone through
            // OnEnable is "unknown" to the setter, which logs an error that
            // fails the test (CI, Core-only host, 2026-09-22).

            var buttonGo = new GameObject(Prefix + "Button");
            object button = buttonGo.AddComponent(buttonType);
            Assert.IsNotNull(button);

            object pointerData = UapUguiEventAccess.CreatePointerData(eventSystem, Vector2.zero, UapGamePointerButton.Left);
            Assert.IsNotNull(pointerData, "CreatePointerData returned null for a real EventSystem.");

            GameObject handler = UapUguiEventAccess.GetHandler("pointerClick", buttonGo);
            Assert.AreSame(buttonGo, handler,
                "ExecuteEvents.GetEventHandler<IPointerClickHandler> should find the Button on its own GameObject.");

            // A plain GameObject with no handler component must not match --
            // GetHandler should walk past it. Parenting it under buttonGo
            // and asking from the child also exercises the "walk up to the
            // parent" branch that a direct-hit lookup does not.
            var childGo = new GameObject(Prefix + "Child");
            childGo.transform.SetParent(buttonGo.transform, false);
            GameObject bubbled = UapUguiEventAccess.GetHandler("pointerClick", childGo);
            Assert.AreSame(buttonGo, bubbled, "GetHandler should bubble up from a child with no handler of its own to the Button.");
        }

        [Test]
        public void AbsentBranchMessages_StillNameThePackage()
        {
            // Regression guard for the "package absent" branch: even on a
            // host project that HAS both packages (ci/HostProjectCoreOnly,
            // since this task), the constant strings the tools show when a
            // package is missing must still name it -- ci/HostProject
            // (package absent) reads the exact same source.
            StringAssert.Contains("com.unity.ugui", UapUguiEventAccess.MissingPackageMessage);
            StringAssert.Contains("com.unity.inputsystem", UapInputSystemKeyboardAccess.MissingPackageMessage);
        }
    }
}

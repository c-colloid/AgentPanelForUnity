using System.Collections.Generic;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The window-list decision behind the "a native modal dialog is open"
    /// timeout note (design note 2026-09-17-modal-menu-and-base64-scan
    /// section 1.3). The window lists are the ones enumerated live on
    /// 2022.3.22f1 while "File/Save" sat on an untitled scene, and after
    /// the dialog was cancelled.
    /// </summary>
    [TestFixture]
    public class UapNativeModalProbeTests
    {
        private static UapNativeModalProbe.WindowInfo Window(string className, string title, bool enabled)
        {
            return new UapNativeModalProbe.WindowInfo { ClassName = className, Title = title, Enabled = enabled };
        }

        [Test]
        public void FindBlockingDialog_DialogOverDisabledUnityWindows_ReturnsItsTitle()
        {
            var windows = new List<UapNativeModalProbe.WindowInfo>
            {
                Window(UapNativeModalProbe.DialogWindowClass, "Save Scene", true),
                Window(UapNativeModalProbe.UnityContainerWindowClass, "Build Settings", false),
                Window(UapNativeModalProbe.UnityContainerWindowClass, "AITemp - Untitled - Unity 2022.3.22f1", false),
            };

            Assert.AreEqual("Save Scene", UapNativeModalProbe.FindBlockingDialog(windows));
        }

        [Test]
        public void FindBlockingDialog_NoDialog_IsNull()
        {
            var windows = new List<UapNativeModalProbe.WindowInfo>
            {
                Window(UapNativeModalProbe.UnityContainerWindowClass, "AITemp - Untitled", true),
            };

            Assert.IsNull(UapNativeModalProbe.FindBlockingDialog(windows));
        }

        [Test]
        public void FindBlockingDialog_DialogClassedWindowThatDisablesNothing_IsNotModal()
        {
            var windows = new List<UapNativeModalProbe.WindowInfo>
            {
                Window(UapNativeModalProbe.DialogWindowClass, "Some tool window", true),
                Window(UapNativeModalProbe.UnityContainerWindowClass, "AITemp - Untitled", true),
            };

            Assert.IsNull(UapNativeModalProbe.FindBlockingDialog(windows));
        }

        [Test]
        public void FindBlockingDialog_NullOrNoUnityWindow_IsNull()
        {
            Assert.IsNull(UapNativeModalProbe.FindBlockingDialog(null));
            Assert.IsNull(UapNativeModalProbe.FindBlockingDialog(new List<UapNativeModalProbe.WindowInfo>
            {
                Window(UapNativeModalProbe.DialogWindowClass, "orphan", true),
            }));
        }

        [Test]
        public void FindBlockingDialog_UntitledDialog_IsEmptyNotNull()
        {
            var windows = new List<UapNativeModalProbe.WindowInfo>
            {
                Window(UapNativeModalProbe.DialogWindowClass, null, true),
                Window(UapNativeModalProbe.UnityContainerWindowClass, "AITemp", false),
            };

            Assert.AreEqual(string.Empty, UapNativeModalProbe.FindBlockingDialog(windows));
        }

        [Test]
        public void DescribeBlockingDialog_NamesTheDialog_AndSaysOnlyAPersonCanCloseIt()
        {
            string text = UapNativeModalProbe.DescribeBlockingDialog("Save Scene");

            StringAssert.Contains("(\"Save Scene\")", text);
            StringAssert.Contains("PERSON", text);
            StringAssert.Contains("uap_ping", text);
            StringAssert.DoesNotContain("(\"", UapNativeModalProbe.DescribeBlockingDialog(string.Empty));
        }

        [Test]
        public void Describe_WithNoDialogOpen_IsNull_AndNeverThrows()
        {
            // The Editor running this test has no modal dialog up -- it
            // could not be running it otherwise.
            Assert.IsNull(UapNativeModalProbe.Describe());
        }

        [Test]
        public void Dispatcher_TimeoutText_CarriesTheDialogNote()
        {
            var dispatcher = new UapMainThreadDispatcher
            {
                DefaultTimeoutMillis = 50,
                ThrottledTimeoutMillis = 50,
                Jobs = new UapJobLedger(),
                StallHintProvider = () => UapNativeModalProbe.DescribeBlockingDialog("Save Scene"),
            };

            var ex = Assert.Throws<System.TimeoutException>(
                () => dispatcher.Execute(new UapPingTool(), JsonNode.NewObject()));

            StringAssert.Contains("was never started", ex.Message);
            StringAssert.Contains("native modal dialog (\"Save Scene\")", ex.Message);
        }
    }
}

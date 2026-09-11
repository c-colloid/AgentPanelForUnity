using System.Collections.Generic;
using Colloid.AgentPanel.Integration;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Clipboard image paste (design note 2026-09-07 section 4.15): the
    /// pure half -- the paste chord, the text-wins rule, the per-OS helper
    /// script and command, and parsing the helper's output. The helper
    /// processes themselves are OS-specific and verified by hand / the
    /// Linux GUI run; nothing here starts a process.
    /// </summary>
    [TestFixture]
    public class ClipboardImageReaderTests
    {
        [Test]
        public void IsPasteChord_OnlyPlainCtrlOrCmdV()
        {
            Assert.IsTrue(ClipboardImageReader.IsPasteChord(KeyCode.V, true, false, false));
            Assert.IsFalse(ClipboardImageReader.IsPasteChord(KeyCode.V, false, false, false));
            Assert.IsFalse(ClipboardImageReader.IsPasteChord(KeyCode.V, true, true, false), "Shift+Ctrl+V is not ours");
            Assert.IsFalse(ClipboardImageReader.IsPasteChord(KeyCode.V, true, false, true));
            Assert.IsFalse(ClipboardImageReader.IsPasteChord(KeyCode.C, true, false, false));
        }

        [Test]
        public void ShouldTryImage_OnlyWhenClipboardHasNoText()
        {
            Assert.IsTrue(ClipboardImageReader.ShouldTryImage(null));
            Assert.IsTrue(ClipboardImageReader.ShouldTryImage(string.Empty));
            Assert.IsFalse(ClipboardImageReader.ShouldTryImage("hello"));
        }

        [TestCase(ClipboardHost.Windows)]
        [TestCase(ClipboardHost.MacOS)]
        [TestCase(ClipboardHost.Linux)]
        public void HelperScript_ReadsTheOutputEnv_AndPrintsEveryMarker(ClipboardHost host)
        {
            string script = ClipboardImageReader.BuildHelperScript(host);

            Assert.IsNotNull(script);
            StringAssert.Contains(ClipboardImageReader.OutputPathEnv, script);
            StringAssert.Contains(ClipboardImageReader.ImageMarker, script);
            StringAssert.Contains(ClipboardImageReader.FilesMarker, script);
            StringAssert.Contains(ClipboardImageReader.EmptyMarker, script);
            Assert.IsNotNull(ClipboardImageReader.HelperScriptExtension(host));
        }

        [Test]
        public void HelperScript_Windows_UsesStaClipboardAndPng()
        {
            string script = ClipboardImageReader.BuildHelperScript(ClipboardHost.Windows);
            string fileName;
            string arguments;

            StringAssert.Contains("System.Windows.Forms.Clipboard]::GetImage()", script);
            StringAssert.Contains("ImageFormat]::Png", script);
            StringAssert.Contains("GetFileDropList", script);
            Assert.IsTrue(ClipboardImageReader.TryBuildCommand(ClipboardHost.Windows, @"C:\tmp\h.ps1", out fileName, out arguments));
            Assert.AreEqual("powershell.exe", fileName);
            StringAssert.Contains("-STA", arguments);
            StringAssert.Contains("-NonInteractive", arguments);
            StringAssert.Contains("\"C:\\tmp\\h.ps1\"", arguments);
        }

        [Test]
        public void HelperScript_MacOS_ReadsPngOrTiffThenFileUrls_ViaJxa()
        {
            string script = ClipboardImageReader.BuildHelperScript(ClipboardHost.MacOS);
            string fileName;
            string arguments;

            StringAssert.Contains("dataForType('public.png')", script);
            StringAssert.Contains("dataForType('public.tiff')", script);
            StringAssert.Contains("stringForType('public.file-url')", script);
            StringAssert.Contains("objectForKey('" + ClipboardImageReader.OutputPathEnv + "')", script);
            Assert.IsTrue(ClipboardImageReader.TryBuildCommand(ClipboardHost.MacOS, "/tmp/h.js", out fileName, out arguments));
            Assert.AreEqual("/usr/bin/osascript", fileName);
            Assert.AreEqual("-l JavaScript \"/tmp/h.js\"", arguments);
        }

        [Test]
        public void HelperScript_Linux_TriesXclipThenWlPaste()
        {
            string script = ClipboardImageReader.BuildHelperScript(ClipboardHost.Linux);
            string fileName;
            string arguments;

            StringAssert.Contains("xclip -selection clipboard -t image/png -o", script);
            StringAssert.Contains("wl-paste -t image/png", script);
            StringAssert.Contains("text/uri-list", script);
            Assert.IsTrue(ClipboardImageReader.TryBuildCommand(ClipboardHost.Linux, "/tmp/h.sh", out fileName, out arguments));
            Assert.AreEqual("/bin/sh", fileName);
        }

        [Test]
        public void Unsupported_HasNoScriptOrCommand()
        {
            string fileName;
            string arguments;
            Assert.IsNull(ClipboardImageReader.BuildHelperScript(ClipboardHost.Unsupported));
            Assert.IsFalse(ClipboardImageReader.TryBuildCommand(ClipboardHost.Unsupported, "x", out fileName, out arguments));
            Assert.AreEqual("unsupported platform", ClipboardImageReader.Read(ClipboardHost.Unsupported).Error);
        }

        [Test]
        public void Parse_ImageMarker_NeedsTheFile()
        {
            ClipboardImageResult ok = ClipboardImageReader.ParseHelperOutput("UAP_IMAGE\r\n", "/tmp/out.png", p => p == "/tmp/out.png");
            ClipboardImageResult missing = ClipboardImageReader.ParseHelperOutput("UAP_IMAGE\n", "/tmp/out.png", p => false);

            Assert.IsTrue(ok.HasImage);
            Assert.AreEqual("/tmp/out.png", ok.ImagePath);
            Assert.IsNull(ok.Error);
            Assert.IsFalse(missing.HasImage);
            StringAssert.Contains("no file", missing.Error);
        }

        [Test]
        public void Parse_FilesMarker_ListsPaths_UnescapingFileUris()
        {
            ClipboardImageResult r = ClipboardImageReader.ParseHelperOutput(
                "UAP_FILES\nC:\\Pictures\\shot 1.png\n\nfile:///home/me/a%20b.jpg\nfile://localhost/tmp/c.png\nfile:///C:/x/y.png\n",
                "/tmp/out.png", p => false);

            Assert.IsFalse(r.HasImage);
            Assert.IsNull(r.Error);
            CollectionAssert.AreEqual(new List<string>
            {
                "C:\\Pictures\\shot 1.png", "/home/me/a b.jpg", "/tmp/c.png", "C:/x/y.png"
            }, r.FilePaths);
        }

        [Test]
        public void Parse_EmptyMarker_IsEmpty_AndGarbageIsAnError()
        {
            ClipboardImageResult empty = ClipboardImageReader.ParseHelperOutput("UAP_EMPTY", "/tmp/out.png", p => false);
            ClipboardImageResult garbage = ClipboardImageReader.ParseHelperOutput("powershell : not recognized", "/tmp/out.png", p => false);
            ClipboardImageResult none = ClipboardImageReader.ParseHelperOutput("   ", "/tmp/out.png", p => false);

            Assert.IsTrue(empty.IsEmpty);
            Assert.IsFalse(garbage.IsEmpty);
            StringAssert.Contains("not recognized", garbage.Error);
            Assert.AreEqual("no output", none.Error);
        }

        [Test]
        public void FilterImageFiles_KeepsOnlyPngJpeg()
        {
            List<string> kept = ClipboardImageReader.FilterImageFiles(new[] { "/a/x.PNG", "/a/y.txt", "/a/z.jpeg", "/a/w.gif" });

            CollectionAssert.AreEqual(new[] { "/a/x.PNG", "/a/z.jpeg" }, kept);
            Assert.AreEqual(0, ClipboardImageReader.FilterImageFiles(null).Count);
        }
    }
}

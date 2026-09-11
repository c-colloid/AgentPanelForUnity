using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>Which OS clipboard helper to run.</summary>
    public enum ClipboardHost
    {
        Windows,
        MacOS,
        Linux,
        Unsupported
    }

    /// <summary>What the clipboard held: an image (written to <see cref="ImagePath"/>), copied files, or nothing.</summary>
    public sealed class ClipboardImageResult
    {
        /// <summary>PNG written from the clipboard bitmap; null when there was none.</summary>
        public string ImagePath;
        /// <summary>Files copied to the clipboard (a file-manager copy); empty when none.</summary>
        public readonly List<string> FilePaths = new List<string>();
        /// <summary>Set when the helper could not run or failed; the other fields are then empty.</summary>
        public string Error;

        public bool HasImage
        {
            get { return !string.IsNullOrEmpty(ImagePath); }
        }

        public bool IsEmpty
        {
            get { return !HasImage && FilePaths.Count == 0 && string.IsNullOrEmpty(Error); }
        }
    }

    /// <summary>
    /// Reads an image off the OS clipboard for the composer's Ctrl/Cmd+V
    /// and "+ > Paste image from clipboard" (design note 2026-09-07
    /// section 4.15). Unity's own clipboard API is text-only
    /// (GUIUtility.systemCopyBuffer), so the bitmap is fetched by a short
    /// helper process per OS -- PowerShell's System.Windows.Forms.Clipboard
    /// on Windows, osascript (JXA) reading NSPasteboard on macOS, xclip or
    /// wl-paste on Linux -- that writes a PNG to a temp path and prints one
    /// of three markers on stdout: <see cref="ImageMarker"/>,
    /// <see cref="FilesMarker"/> (then one path per line: a copied file
    /// from Explorer/Finder), or <see cref="EmptyMarker"/>. The composer
    /// then imports the PNG like any dropped file. Everything except
    /// <see cref="Read"/> is pure and covered by EditMode tests; the
    /// helper scripts themselves are exercised on the developer's OS.
    /// </summary>
    public static class ClipboardImageReader
    {
        public const string ImageMarker = "UAP_IMAGE";
        public const string FilesMarker = "UAP_FILES";
        public const string EmptyMarker = "UAP_EMPTY";
        /// <summary>Environment variable the helper reads the output PNG path from (no quoting issues).</summary>
        public const string OutputPathEnv = "UAP_CLIP_OUT";
        /// <summary>Helper wall-clock budget; PowerShell start-up is the slow case (~1 s cold).</summary>
        public const int TimeoutMs = 10000;

        /// <summary>The composer swallows Ctrl/Cmd+V only for this chord (no Shift/Alt variants).</summary>
        public static bool IsPasteChord(KeyCode key, bool ctrlOrCmd, bool shift, bool alt)
        {
            return key == KeyCode.V && ctrlOrCmd && !shift && !alt;
        }

        /// <summary>
        /// Text wins: when the clipboard carries text the TextField's own
        /// paste runs and the image helper is never started (a spreadsheet
        /// cell copy pastes as text, as everywhere else).
        /// </summary>
        public static bool ShouldTryImage(string clipboardText)
        {
            return string.IsNullOrEmpty(clipboardText);
        }

        public static ClipboardHost CurrentHost()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return ClipboardHost.Windows;
                case RuntimePlatform.OSXEditor: return ClipboardHost.MacOS;
                case RuntimePlatform.LinuxEditor: return ClipboardHost.Linux;
            }
            return ClipboardHost.Unsupported;
        }

        /// <summary>The helper script for <paramref name="host"/> (null when unsupported). Reads <see cref="OutputPathEnv"/>.</summary>
        public static string BuildHelperScript(ClipboardHost host)
        {
            switch (host)
            {
                case ClipboardHost.Windows:
                    return "Add-Type -AssemblyName System.Windows.Forms\n"
                        + "Add-Type -AssemblyName System.Drawing\n"
                        + "$o = $env:" + OutputPathEnv + "\n"
                        + "$i = [System.Windows.Forms.Clipboard]::GetImage()\n"
                        + "if ($i -ne $null) {\n"
                        + "  $i.Save($o, [System.Drawing.Imaging.ImageFormat]::Png)\n"
                        + "  Write-Output '" + ImageMarker + "'\n"
                        + "} elseif ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {\n"
                        + "  Write-Output '" + FilesMarker + "'\n"
                        + "  [System.Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { Write-Output $_ }\n"
                        + "} else {\n"
                        + "  Write-Output '" + EmptyMarker + "'\n"
                        + "}\n";
                case ClipboardHost.MacOS:
                    // JXA (osascript -l JavaScript): NSPasteboard gives PNG
                    // directly, converts TIFF (most app copies) to PNG, and
                    // lists copied files; pure ASCII unlike the AppleScript
                    // raw-class syntax.
                    return "ObjC.import('AppKit');\n"
                        + "ObjC.import('Foundation');\n"
                        + "function run() {\n"
                        + "  var env = $.NSProcessInfo.processInfo.environment;\n"
                        + "  var out = ObjC.unwrap(env.objectForKey('" + OutputPathEnv + "'));\n"
                        + "  var pb = $.NSPasteboard.generalPasteboard;\n"
                        + "  var data = pb.dataForType('public.png');\n"
                        + "  if (data.isNil()) {\n"
                        + "    var tiff = pb.dataForType('public.tiff');\n"
                        + "    if (!tiff.isNil()) {\n"
                        + "      var rep = $.NSBitmapImageRep.imageRepWithData(tiff);\n"
                        + "      if (!rep.isNil()) { data = rep.representationUsingTypeProperties(4, $()); }\n"
                        + "    }\n"
                        + "  }\n"
                        + "  if (!data.isNil()) { data.writeToFileAtomically(out, true); return '" + ImageMarker + "'; }\n"
                        + "  var lines = [];\n"
                        + "  var items = pb.pasteboardItems;\n"
                        + "  for (var i = 0; i < items.count; i++) {\n"
                        + "    var s = items.objectAtIndex(i).stringForType('public.file-url');\n"
                        + "    if (!s.isNil()) { lines.push(ObjC.unwrap($.NSURL.URLWithString(s).path)); }\n"
                        + "  }\n"
                        + "  if (lines.length > 0) { return '" + FilesMarker + "\\n' + lines.join('\\n'); }\n"
                        + "  return '" + EmptyMarker + "';\n"
                        + "}\n";
                case ClipboardHost.Linux:
                    return "#!/bin/sh\n"
                        + "out=\"$" + OutputPathEnv + "\"\n"
                        + "if command -v xclip >/dev/null 2>&1; then\n"
                        + "  t=$(xclip -selection clipboard -t TARGETS -o 2>/dev/null)\n"
                        + "  if echo \"$t\" | grep -q '^image/png$'; then\n"
                        + "    xclip -selection clipboard -t image/png -o > \"$out\" 2>/dev/null && echo " + ImageMarker + " && exit 0\n"
                        + "  fi\n"
                        + "  if echo \"$t\" | grep -q '^text/uri-list$'; then\n"
                        + "    echo " + FilesMarker + "; xclip -selection clipboard -t text/uri-list -o 2>/dev/null; exit 0\n"
                        + "  fi\n"
                        + "elif command -v wl-paste >/dev/null 2>&1; then\n"
                        + "  if wl-paste -l 2>/dev/null | grep -q '^image/png$'; then\n"
                        + "    wl-paste -t image/png > \"$out\" 2>/dev/null && echo " + ImageMarker + " && exit 0\n"
                        + "  fi\n"
                        + "  if wl-paste -l 2>/dev/null | grep -q '^text/uri-list$'; then\n"
                        + "    echo " + FilesMarker + "; wl-paste -t text/uri-list 2>/dev/null; exit 0\n"
                        + "  fi\n"
                        + "fi\n"
                        + "echo " + EmptyMarker + "\n";
            }
            return null;
        }

        /// <summary>File extension the helper script is saved with.</summary>
        public static string HelperScriptExtension(ClipboardHost host)
        {
            switch (host)
            {
                case ClipboardHost.Windows: return ".ps1";
                case ClipboardHost.MacOS: return ".js";
                case ClipboardHost.Linux: return ".sh";
            }
            return null;
        }

        /// <summary>The process to run for <paramref name="host"/> given the saved script. False when unsupported.</summary>
        public static bool TryBuildCommand(ClipboardHost host, string scriptPath, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;
            switch (host)
            {
                case ClipboardHost.Windows:
                    fileName = "powershell.exe";
                    arguments = "-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                    return true;
                case ClipboardHost.MacOS:
                    fileName = "/usr/bin/osascript";
                    arguments = "-l JavaScript \"" + scriptPath + "\"";
                    return true;
                case ClipboardHost.Linux:
                    fileName = "/bin/sh";
                    arguments = "\"" + scriptPath + "\"";
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Turns the helper's stdout into a result. The image marker only
        /// counts when the PNG actually exists and is non-empty (per
        /// <paramref name="fileExists"/>); file lines are trimmed, blank
        /// lines dropped, and "file://" URIs (Linux uri-list) unescaped.
        /// Unknown output is reported as an error with the raw text.
        /// </summary>
        public static ClipboardImageResult ParseHelperOutput(string stdout, string outputPath, Func<string, bool> fileExists)
        {
            var result = new ClipboardImageResult();
            string text = (stdout ?? string.Empty).Replace("\r", string.Empty).Trim();
            if (text.Length == 0)
            {
                result.Error = "no output";
                return result;
            }
            string[] lines = text.Split('\n');
            string marker = lines[0].Trim();
            if (marker == ImageMarker)
            {
                if (fileExists != null && fileExists(outputPath))
                {
                    result.ImagePath = outputPath;
                }
                else
                {
                    result.Error = "helper reported an image but wrote no file";
                }
                return result;
            }
            if (marker == FilesMarker)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        line = Uri.UnescapeDataString(line.Substring("file://".Length));
                        // file://localhost/path and file:///C:/path forms.
                        if (line.StartsWith("localhost/"))
                        {
                            line = line.Substring("localhost".Length);
                        }
                        if (line.Length > 2 && line[0] == '/' && char.IsLetter(line[1]) && line[2] == ':')
                        {
                            line = line.Substring(1);
                        }
                    }
                    result.FilePaths.Add(line);
                }
                return result;
            }
            if (marker == EmptyMarker)
            {
                return result;
            }
            result.Error = text.Length > 200 ? text.Substring(0, 200) : text;
            return result;
        }

        /// <summary>Keeps only files the attachment policy accepts (PNG/JPEG), in order.</summary>
        public static List<string> FilterImageFiles(IList<string> paths)
        {
            var kept = new List<string>();
            if (paths == null)
            {
                return kept;
            }
            for (int i = 0; i < paths.Count; i++)
            {
                if (ImageAttachmentPolicy.IsSupportedFile(paths[i]))
                {
                    kept.Add(paths[i]);
                }
            }
            return kept;
        }

        private static string _scriptPath;
        private static ClipboardHost _scriptHost = ClipboardHost.Unsupported;

        /// <summary>
        /// Runs the helper for the current OS and returns what it found.
        /// Synchronous (a paste is a user gesture; the budget is
        /// <see cref="TimeoutMs"/>). The PNG lands in the temp directory;
        /// the caller imports it into the attachment store and may delete
        /// it. Never throws.
        /// </summary>
        public static ClipboardImageResult Read()
        {
            return Read(CurrentHost());
        }

        public static ClipboardImageResult Read(ClipboardHost host)
        {
            var result = new ClipboardImageResult();
            string script = BuildHelperScript(host);
            string fileName;
            string arguments;
            if (script == null)
            {
                result.Error = "unsupported platform";
                return result;
            }
            string outputPath = Path.Combine(Path.GetTempPath(),
                "uap-clipboard-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png");
            try
            {
                if (_scriptPath == null || _scriptHost != host || !File.Exists(_scriptPath))
                {
                    _scriptPath = Path.Combine(Path.GetTempPath(), "uap-clipboard-helper" + HelperScriptExtension(host));
                    File.WriteAllText(_scriptPath, script, new UTF8Encoding(false));
                    _scriptHost = host;
                }
                TryBuildCommand(host, _scriptPath, out fileName, out arguments);
                var info = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                info.EnvironmentVariables[OutputPathEnv] = outputPath;
                string stdout;
                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        result.Error = "could not start " + fileName;
                        return result;
                    }
                    stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(TimeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        result.Error = "timed out";
                        return result;
                    }
                    if (string.IsNullOrEmpty(stdout) && !string.IsNullOrEmpty(stderr))
                    {
                        stdout = stderr;
                    }
                }
                return ParseHelperOutput(stdout, outputPath, p => File.Exists(p) && new FileInfo(p).Length > 0);
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                return result;
            }
        }
    }
}

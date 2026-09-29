using Colloid.AgentPanel.Integration;
using NUnit.Framework;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>The per-platform terminal command lines (design note 2026-09-27-mcp-servers-in-panel.md section 4). Pure.</summary>
    public class TerminalLauncherTests
    {
        [Test]
        public void BuildCliCommand_QuotesPathsAndPassesStrictConfig()
        {
            Assert.AreEqual("\"C:\\Users\\me\\claude.cmd\" --mcp-config \"C:\\p\\uap-mcp-config.json\" --strict-mcp-config",
                TerminalLauncher.BuildCliCommand(RuntimePlatform.WindowsEditor, "C:\\Users\\me\\claude.cmd", "C:\\p\\uap-mcp-config.json"));
            Assert.AreEqual("\"/usr/local/bin/claude\"",
                TerminalLauncher.BuildCliCommand(RuntimePlatform.OSXEditor, "/usr/local/bin/claude", null),
                "no config file means the CLI's own configuration is used");
        }

        [Test]
        public void Build_Windows_UsesStartAndKeepsTheWindow()
        {
            TerminalLauncher.Launch launch = TerminalLauncher.Build(RuntimePlatform.WindowsEditor, "claude", "c.json");
            Assert.AreEqual("cmd.exe", launch.FileName);
            StringAssert.StartsWith("/c start \"Claude Code\" cmd.exe /k ", launch.Arguments);
            StringAssert.Contains("--strict-mcp-config", launch.Arguments);
        }

        [Test]
        public void Build_Mac_UsesTerminalAppViaOsascript()
        {
            TerminalLauncher.Launch launch = TerminalLauncher.Build(RuntimePlatform.OSXEditor, "/bin/claude", "/p/c.json");
            Assert.AreEqual("/usr/bin/osascript", launch.FileName);
            StringAssert.Contains("tell application \\\"Terminal\\\" to do script", launch.Arguments);
            StringAssert.Contains("to activate", launch.Arguments);
        }

        [Test]
        public void Build_Linux_KeepsAShellOpenAfterTheCli()
        {
            TerminalLauncher.Launch launch = TerminalLauncher.Build(RuntimePlatform.LinuxEditor, "/bin/claude", null);
            Assert.AreEqual("x-terminal-emulator", launch.FileName);
            StringAssert.Contains("exec bash", launch.Arguments);
        }
    }
}

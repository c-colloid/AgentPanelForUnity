namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// Tree-kill abstraction (ARCHITECTURE.md D1). claude.exe spawns child
    /// processes (bash, ripgrep, MCP servers), so a plain Process.Kill()
    /// leaves orphans; Windows uses taskkill /T /F, Unix implementations
    /// should signal the process group. Also the seam for a future Win32
    /// Job Object upgrade (risk table #14).
    /// </summary>
    public interface IProcessKiller
    {
        /// <summary>
        /// Force-kills the process with the given PID and its whole child
        /// tree. Must never throw; returns true when the kill command was
        /// issued successfully.
        /// </summary>
        bool KillTree(int pid);
    }
}

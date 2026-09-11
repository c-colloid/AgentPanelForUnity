namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Opt-in marker for a tool that runs on the HTTP worker thread that
    /// received the request, WITHOUT ever being marshaled to the Unity
    /// main thread (design note 2026-09-09-jobs-and-destructive-confirm
    /// section 1.3; UnityMCP's MainThread=false). The whole point is to
    /// answer while the main thread is blocked -- uap_job_status asking
    /// about the very job that is blocking it -- so an implementation MUST
    /// NOT touch any UnityEngine/UnityEditor API, static Editor state, or
    /// anything else that is only safe on the main thread: pure C# over
    /// data the main thread publishes thread-safely (the job ledger) is the
    /// entire allowed surface. UapMainThreadDispatcher checks for this
    /// interface first and calls Execute inline; DirectUapToolExecutor is
    /// unaffected (it already runs everything on the calling thread).
    /// </summary>
    public interface IUapOffThreadTool : IUapTool
    {
    }
}

using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>One compiler diagnostic, Unity-independent (mirrors UnityEditor.Compilation.CompilerMessage) so the state machine below has zero UnityEditor dependency.</summary>
    public struct ScriptCompileMessage
    {
        public string File;
        public int Line;
        public int Column;
        public string Message;
        public bool IsError;

        public override string ToString()
        {
            return (IsError ? "error" : "warning") + ": " + File + "(" + Line + "," + Column + "): " + Message;
        }
    }

    /// <summary>Phase of a single uap_scripts_commit invocation.</summary>
    public enum ScriptCommitPhase
    {
        Idle,
        NothingStaged,
        Compiling,
        Succeeded,
        Failed
    }

    /// <summary>
    /// Pure state machine behind uap_scripts_commit (design section 7.4/8.1
    /// P1): decides, given only compiler OUTCOMES (never the actual
    /// UnityEditor.Compilation types), whether a commit succeeds and files
    /// should move into Assets/, or fails and the stage is left untouched.
    /// No UnityEditor dependency at all -- the real tool feeds it
    /// UnityEditor.Compilation.CompilerMessage data converted to
    /// <see cref="ScriptCompileMessage"/>, but every transition here is
    /// unit tested with plain fixtures.
    /// </summary>
    public sealed class ScriptCommitPipeline
    {
        private readonly List<ScriptCompileMessage> _errors = new List<ScriptCompileMessage>();

        public ScriptCommitPhase Phase { get; private set; }

        /// <summary>Only entries with IsError==true (warnings never fail a commit).</summary>
        public IReadOnlyList<ScriptCompileMessage> Errors
        {
            get { return _errors; }
        }

        public ScriptCommitPipeline()
        {
            Phase = ScriptCommitPhase.Idle;
        }

        /// <summary>Nothing was staged -- a terminal phase distinct from Failed (no compiler ever ran).</summary>
        public void MarkNothingStaged()
        {
            RequireIdle();
            Phase = ScriptCommitPhase.NothingStaged;
        }

        /// <summary>Compilation has been kicked off and is in flight (one or more editor ticks may pass before CompleteCompilation).</summary>
        public void MarkCompiling()
        {
            RequireIdle();
            Phase = ScriptCommitPhase.Compiling;
        }

        /// <summary>
        /// Feeds the finished compiler output. Only messages with
        /// IsError==true fail the commit; any number of warnings still
        /// yields Succeeded. Throws if called outside the Compiling phase
        /// (including a second call -- a pipeline instance is single-use).
        /// </summary>
        public void CompleteCompilation(IEnumerable<ScriptCompileMessage> messages)
        {
            if (Phase != ScriptCommitPhase.Compiling)
            {
                throw new InvalidOperationException(
                    "CompleteCompilation called outside the Compiling phase (current: " + Phase + ").");
            }
            _errors.Clear();
            if (messages != null)
            {
                foreach (ScriptCompileMessage m in messages)
                {
                    if (m.IsError)
                    {
                        _errors.Add(m);
                    }
                }
            }
            Phase = _errors.Count > 0 ? ScriptCommitPhase.Failed : ScriptCommitPhase.Succeeded;
        }

        /// <summary>True only in the Succeeded phase -- the sole signal the real tool uses to decide whether to move staged files into Assets/.</summary>
        public bool ShouldMoveFilesToAssets
        {
            get { return Phase == ScriptCommitPhase.Succeeded; }
        }

        private void RequireIdle()
        {
            if (Phase != ScriptCommitPhase.Idle)
            {
                throw new InvalidOperationException("Pipeline already started (current: " + Phase + ").");
            }
        }
    }
}

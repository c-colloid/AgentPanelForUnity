namespace Colloid.AgentPanel.Core.Process
{
    /// <summary>
    /// A transport that can let go of the CLI without ending it (the CLI
    /// relay, design note docs/design-notes/2026-11-25-cli-relay.md): the
    /// process lives in a relay that outlasts Unity's domain reloads, and
    /// this side only closes its link to that relay.
    /// </summary>
    public interface IDetachableCliTransport
    {
        /// <summary>
        /// Closes the link and leaves the CLI running. After this the
        /// transport raises no Exited and delivers no more lines. Main
        /// thread; never throws.
        /// </summary>
        void Detach();

        /// <summary>
        /// The client has finished processing one more turn result: the
        /// relay may forget everything up to it (it is in the saved
        /// transcript now and must never be replayed). Main thread.
        /// </summary>
        void NoteResultProcessed();
    }
}

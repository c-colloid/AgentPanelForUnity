namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Pure reentrant-safe counter mirroring the semantics of
    /// AssetDatabase.DisallowAutoRefresh/AllowAutoRefresh's own internal
    /// counter (R11 section 1: "counter-based, reentrant-safe"). A caller
    /// wraps the native calls so that ONLY the 0-&gt;1 transition triggers
    /// DisallowAutoRefresh and ONLY the 1-&gt;0 transition triggers
    /// AllowAutoRefresh -- nested Increment/Decrement calls beyond the
    /// first never touch the native API again, so the native counter always
    /// carries exactly one matched pair regardless of nesting depth on this
    /// side. No UnityEditor dependency: fully unit tested in isolation.
    /// </summary>
    public sealed class RefreshSuppressionCounter
    {
        private int _count;

        public int Count
        {
            get { return _count; }
        }

        public bool IsSuppressed
        {
            get { return _count > 0; }
        }

        /// <summary>Increments. Returns true exactly on the 0-&gt;1 transition (caller should call DisallowAutoRefresh only then).</summary>
        public bool Increment()
        {
            _count++;
            return _count == 1;
        }

        /// <summary>
        /// Decrements (never below zero). Returns true exactly on the
        /// 1-&gt;0 transition (caller should call AllowAutoRefresh only
        /// then). A decrement while already at zero is a no-op that
        /// returns false -- it can never trigger a spurious Allow call.
        /// </summary>
        public bool Decrement()
        {
            if (_count <= 0)
            {
                _count = 0;
                return false;
            }
            _count--;
            return _count == 0;
        }

        /// <summary>
        /// Resets to zero WITHOUT reporting a transition -- for the
        /// domain-reload safety net (design section 8.2 "always balanced
        /// including exceptions/domain reload"): the caller is expected to
        /// release the single outstanding native disallow itself, based on
        /// <see cref="IsSuppressed"/> read BEFORE calling this, then call
        /// this to clear its own bookkeeping.
        /// </summary>
        public void ForceReset()
        {
            _count = 0;
        }
    }
}

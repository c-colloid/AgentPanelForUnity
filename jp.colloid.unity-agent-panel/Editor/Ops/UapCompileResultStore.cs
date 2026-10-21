using Colloid.AgentPanel.Core.Json;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// SessionState-backed persistence for uap_compile's result (docs/
    /// design-notes/2026-09-22-uloop-phase1.md section 2.3): a successful
    /// compile reloads the domain, which wipes every plain static field in
    /// this codebase (see Colloid.AgentPanel.Model.SessionStateBridge's own
    /// doc comment for the same reasoning applied to the panel's other
    /// reload-spanning state) -- so the request id, per-message diagnostics
    /// and counters action:status answers with have to survive that reload
    /// from something other than a static.
    ///
    /// Deliberately its OWN SessionState key rather than a new field on
    /// SessionStateBridge: that class is shared, heavily documented state
    /// several other streams touch concurrently, and uap_compile's result is
    /// a single, self-contained JSON document with exactly one reader (this
    /// tool) -- growing SessionStateBridge for it would only add merge risk
    /// for no benefit. One string key holds the whole
    /// <see cref="UapCompileResult"/> as JSON via
    /// <see cref="UapCompilePolicy.ToJson"/>/<see cref="UapCompilePolicy.FromJson"/>
    /// (both Unity-free and covered by ci/SmokeTests), so the only thing
    /// this class itself does is read/write one SessionState string --
    /// exactly the same idiom SessionStateBridge's own string-backed
    /// properties use, just not living in that file.
    /// </summary>
    public static class UapCompileResultStore
    {
        private const string Key = "Colloid.AgentPanel.Ops.UapCompileResult";

        /// <summary>The persisted result, or a fresh Idle one when nothing has been saved yet (or the saved JSON is unparseable/foreign -- FromJson degrades field by field rather than throwing).</summary>
        public static UapCompileResult Load()
        {
            string json = SessionState.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return UapCompileResult.Idle();
            }
            JsonNode node;
            string error;
            if (!JsonParser.TryParse(json, out node, out error))
            {
                return UapCompileResult.Idle();
            }
            return UapCompilePolicy.FromJson(node);
        }

        /// <summary>Persists <paramref name="result"/> as the current/last-known compile result. Called synchronously from UapCompileTool's compilationFinished handler -- see that class's own comment for why this cannot wait for an update tick.</summary>
        public static void Save(UapCompileResult result)
        {
            SessionState.SetString(Key, JsonWriter.Write(UapCompilePolicy.ToJson(result)));
        }

        /// <summary>Test seam: forgets the persisted result (back to Idle), the same way a fresh editor session with nothing saved yet would read.</summary>
        internal static void ResetForTests()
        {
            SessionState.SetString(Key, string.Empty);
        }
    }
}

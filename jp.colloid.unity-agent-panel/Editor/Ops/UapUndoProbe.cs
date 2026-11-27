using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Best-effort look into UnityEditor.Undo's PRIVATE record list
    /// (internal static Undo.GetRecords(List&lt;string&gt;, out int),
    /// verified against UnityCsReference's Editor/Mono/Undo/Undo.bindings.cs):
    /// there is no public API that says whether anything was recorded,
    /// only ones that report the current group INDEX, which does not move
    /// because a record was added. Reflection into an internal engine
    /// method can break on a future Unity version without warning, so
    /// every caller treats null ("unknown") as its own case instead of
    /// guessing.
    ///
    /// Shared by uap_execute_code (did this snippet record Undo?) and
    /// request checkpoints (did this turn leave anything in the Undo
    /// history? -- design note 2026-11-23-request-checkpoints.md section 10).
    /// </summary>
    internal static class UapUndoProbe
    {
        private static MethodInfo _getRecordsMethod;
        private static bool _resolved;

        /// <summary>The names on Unity's Undo stack, or null when the internal method could not be reached.</summary>
        internal static List<string> TryReadRecords()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _getRecordsMethod = typeof(Undo).GetMethod("GetRecords",
                        BindingFlags.NonPublic | BindingFlags.Static, null,
                        new[] { typeof(List<string>), typeof(int).MakeByRefType() }, null);
                }
                if (_getRecordsMethod == null)
                {
                    return null;
                }
                var list = new List<string>();
                object[] args = { list, 0 };
                _getRecordsMethod.Invoke(null, args);
                return list;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>How many entries the Undo stack holds, or -1 when unknown.</summary>
        internal static int TryCount()
        {
            List<string> records = TryReadRecords();
            return records == null ? -1 : records.Count;
        }
    }
}

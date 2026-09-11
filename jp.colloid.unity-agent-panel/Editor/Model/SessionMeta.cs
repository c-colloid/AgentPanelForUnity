using System;
using System.Collections.Generic;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// Panel-owned metadata for ONE session, keyed by the CLI session id.
    /// Everything here is the user's own organisation of their history
    /// (pin / archive / rename / grouping) plus the scene we record on
    /// their behalf -- none of it exists in the CLI's transcript, so it
    /// lives in our own sidecar (<c>SessionMetaFile</c>) rather than being
    /// derived on load. See
    /// docs/design-notes/2026-08-03-history-usage-restore-and-browsing.md
    /// section 2.2.
    /// </summary>
    [Serializable]
    public sealed class SessionMeta
    {
        /// <summary>Sorts into a leading "pinned" group in every grouping mode.</summary>
        public bool pinned;

        /// <summary>Hidden from the list unless the archived filter is on.</summary>
        public bool archived;

        /// <summary>
        /// User-supplied title that wins over the CLI's ai-title. Empty
        /// means "no override" -- never a synonym for "untitled", so
        /// clearing the rename box restores the ai-title rather than
        /// blanking the row.
        /// </summary>
        public string titleOverride = string.Empty;

        /// <summary>Id of the custom group this session belongs to; empty = ungrouped.</summary>
        public string customGroupId = string.Empty;

        /// <summary>
        /// Scene asset path (e.g. "Assets/Scenes/Main.unity") last recorded
        /// as active while this session was running, or empty for sessions
        /// that predate the recording. Measured 2026-08-03: transcripts
        /// carry NO scene information whatsoever, so this can only ever be
        /// populated going forward -- older sessions group under "not
        /// recorded" and that is a data limitation, not a bug.
        /// </summary>
        public string recordedScenePath = string.Empty;

        /// <summary>True when nothing here differs from a fresh instance
        /// (lets the store drop empty entries instead of growing forever).</summary>
        public bool IsEmpty()
        {
            return !pinned
                && !archived
                && string.IsNullOrEmpty(titleOverride)
                && string.IsNullOrEmpty(customGroupId)
                && string.IsNullOrEmpty(recordedScenePath);
        }

        public SessionMeta Clone()
        {
            return new SessionMeta
            {
                pinned = pinned,
                archived = archived,
                titleOverride = titleOverride ?? string.Empty,
                customGroupId = customGroupId ?? string.Empty,
                recordedScenePath = recordedScenePath ?? string.Empty
            };
        }
    }

    /// <summary>One user-defined history group ("custom group").</summary>
    [Serializable]
    public sealed class SessionGroup
    {
        /// <summary>Stable id referenced by <see cref="SessionMeta.customGroupId"/>.</summary>
        public string id = string.Empty;
        /// <summary>Display name (raw user text -- never Unity-serialized, see SessionMetaFile).</summary>
        public string name = string.Empty;
        /// <summary>Ascending display order among groups.</summary>
        public int order;

        public SessionGroup Clone()
        {
            return new SessionGroup { id = id ?? string.Empty, name = name ?? string.Empty, order = order };
        }
    }

    /// <summary>
    /// In-memory model of the whole sidecar: per-session metadata plus the
    /// custom-group definitions. Pure data + lookup helpers; all I/O lives
    /// in <c>SessionMetaFile</c> so this stays trivially unit-testable.
    /// </summary>
    [Serializable]
    public sealed class SessionMetaStore
    {
        public readonly Dictionary<string, SessionMeta> bySessionId =
            new Dictionary<string, SessionMeta>(StringComparer.Ordinal);

        public readonly List<SessionGroup> groups = new List<SessionGroup>();

        /// <summary>
        /// Metadata for a session, or a FRESH empty instance when nothing
        /// was ever set. Never returns null and never creates an entry --
        /// use <see cref="Edit"/> to mutate. MODEL-11: this used to hand
        /// out one shared static Empty and rely on every caller treating it
        /// as read-only by convention; one caller writing
        /// `store.Get(id).pinned = true` on a missing id would have
        /// poisoned the shared instance for every later miss. A tiny
        /// allocation on the miss path buys out that whole failure class.
        /// </summary>
        public SessionMeta Get(string sessionId)
        {
            SessionMeta meta;
            if (!string.IsNullOrEmpty(sessionId)
                && bySessionId.TryGetValue(sessionId, out meta) && meta != null)
            {
                return meta;
            }
            return new SessionMeta();
        }

        /// <summary>
        /// Metadata for a session, creating the entry if needed so the
        /// caller can mutate it. Returns null only for an empty id.
        /// </summary>
        public SessionMeta Edit(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return null;
            }
            SessionMeta meta;
            if (!bySessionId.TryGetValue(sessionId, out meta) || meta == null)
            {
                meta = new SessionMeta();
                bySessionId[sessionId] = meta;
            }
            return meta;
        }

        /// <summary>Drops entries that carry no information (called before save).</summary>
        public void Compact()
        {
            var dead = new List<string>();
            foreach (KeyValuePair<string, SessionMeta> pair in bySessionId)
            {
                if (pair.Value == null || pair.Value.IsEmpty())
                {
                    dead.Add(pair.Key);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                bySessionId.Remove(dead[i]);
            }
        }

        /// <summary>Group by id, or null when absent (including for an empty id).</summary>
        public SessionGroup FindGroup(string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return null;
            }
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] != null
                    && string.Equals(groups[i].id, groupId, StringComparison.Ordinal))
                {
                    return groups[i];
                }
            }
            return null;
        }

    }
}

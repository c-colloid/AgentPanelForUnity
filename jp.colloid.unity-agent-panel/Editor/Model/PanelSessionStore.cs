using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Core.Protocol;

namespace Colloid.AgentPanel.Model
{
    /// <summary>
    /// The panel's OWN per-session transcript store, one JSON file per
    /// session under <c>UserSettings/AgentPanel/Sessions/</c> (design note
    /// docs/design-notes/2026-09-13-acp-feature-parity.md section 1).
    ///
    /// Why it exists: the History browser was built on Claude Code's own
    /// transcript directory (<c>~/.claude/projects/&lt;slug&gt;/*.jsonl</c>,
    /// <see cref="SessionIndex"/>). ACP agents keep no equivalent the panel
    /// can read -- ACP has no session listing the panel can rely on, and
    /// each CLI stores its history in its own private format -- so
    /// conversations with Gemini CLI / Codex / Grok Build never showed up
    /// in History. The panel already holds everything History needs to
    /// render and restore a transcript (the <see cref="ChatSession"/>
    /// display cache), so it keeps a copy of every ACP session here, in
    /// the <see cref="SessionCacheFile"/> format, keyed by session id.
    ///
    /// Only ACP sessions are written (AgentHub.PersistPanelSession):
    /// Claude Code's sessions stay on the CLI's own jsonl, so no session is
    /// ever listed twice.
    ///
    /// Pure C# + System.IO + Core/Json (EditMode-testable against a temp
    /// directory, same as SessionCacheFile). Never throws from any public
    /// member; failures log one line.
    /// </summary>
    public sealed class PanelSessionStore
    {
        /// <summary>Directory relative to the project root.</summary>
        public const string DefaultRelativeDirectory = "UserSettings/AgentPanel/Sessions";

        private const string FileExtension = ".json";
        private const int PreviewMaxChars = 80;

        private readonly string _directory;
        private readonly Action<string> _log;

        public PanelSessionStore(string directory, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("directory is required", "directory");
            }
            _directory = directory;
            _log = log;
        }

        /// <summary>Absolute directory the store reads and writes.</summary>
        public string Directory
        {
            get { return _directory; }
        }

        /// <summary>The store's default directory under the project root.</summary>
        public static string DefaultDirectory(string projectRoot)
        {
            return Path.Combine(projectRoot ?? ".", "UserSettings", "AgentPanel", "Sessions");
        }

        /// <summary>Store at the default location under the project root.</summary>
        public static PanelSessionStore CreateDefault(string projectRoot, Action<string> log = null)
        {
            return new PanelSessionStore(DefaultDirectory(projectRoot), log);
        }

        /// <summary>
        /// The file a session id maps to. Session ids are agent-issued
        /// strings; anything outside [A-Za-z0-9._-] is replaced by '_' so
        /// an id can never escape the directory or hit a reserved name.
        /// Empty id yields null.
        /// </summary>
        public string PathFor(string sessionId)
        {
            string name = SafeFileStem(sessionId);
            return name == null ? null : Path.Combine(_directory, name + FileExtension);
        }

        /// <summary>Pure: the file stem for a session id, or null when the id is empty.</summary>
        public static string SafeFileStem(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return null;
            }
            var sb = new StringBuilder(sessionId.Length);
            for (int i = 0; i < sessionId.Length; i++)
            {
                char c = sessionId[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.';
                sb.Append(ok ? c : '_');
            }
            string stem = sb.ToString().Trim('.');
            return stem.Length == 0 ? null : stem;
        }

        /// <summary>
        /// Writes (or overwrites) the session's file. A session without an
        /// id or without messages is not worth a row and is skipped. Never
        /// throws.
        /// </summary>
        public bool Save(ChatSession session, IReadOnlyDictionary<string, ModelUsage> modelUsage)
        {
            if (session == null || session.messages == null || session.messages.Count == 0)
            {
                return false;
            }
            string path = PathFor(session.sessionId);
            if (path == null)
            {
                return false;
            }
            new SessionCacheFile(path, _log).Save(session, modelUsage);
            return true;
        }

        /// <summary>
        /// Loads one session (null when missing or unreadable). Delegates
        /// to <see cref="SessionCacheFile.Load(out Dictionary{string, ModelUsage})"/>,
        /// so a corrupt file is discarded exactly like the main cache.
        /// </summary>
        public ChatSession Load(string sessionId, out Dictionary<string, ModelUsage> modelUsage)
        {
            modelUsage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            string path = PathFor(sessionId);
            if (path == null)
            {
                return null;
            }
            return new SessionCacheFile(path, _log).Load(out modelUsage);
        }

        /// <summary>
        /// Moves a session's file to a new id: an ACP agent that cannot
        /// <c>session/load</c> hands the SAME conversation a new id on every
        /// reconnect, and without this the list would fill with one row per
        /// reconnect, each a prefix of the next. No-op (false) when the
        /// source is missing; an existing target is replaced.
        /// </summary>
        public bool Rename(string oldSessionId, string newSessionId)
        {
            string from = PathFor(oldSessionId);
            string to = PathFor(newSessionId);
            if (from == null || to == null || string.Equals(from, to, StringComparison.Ordinal))
            {
                return false;
            }
            try
            {
                if (!File.Exists(from))
                {
                    return false;
                }
                if (File.Exists(to))
                {
                    File.Delete(to);
                }
                File.Move(from, to);
                return true;
            }
            catch (Exception ex)
            {
                Log("Could not rename the stored session '" + oldSessionId + "' to '"
                    + newSessionId + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Every stored session, newest (by last-write time) first. Tolerant
        /// of a missing directory (empty) and of files that fail to stat
        /// (skipped). Does not parse the files -- the entries read their
        /// content lazily, like <see cref="SessionIndexEntry"/>.
        /// </summary>
        public List<PanelSessionEntry> Enumerate()
        {
            var found = new List<PanelSessionEntry>();
            string[] files;
            try
            {
                if (!System.IO.Directory.Exists(_directory))
                {
                    return found;
                }
                files = System.IO.Directory.GetFiles(_directory, "*" + FileExtension);
            }
            catch (Exception)
            {
                return found;
            }
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    var info = new FileInfo(files[i]);
                    string stem = Path.GetFileNameWithoutExtension(files[i]);
                    if (string.IsNullOrEmpty(stem))
                    {
                        continue;
                    }
                    found.Add(new PanelSessionEntry(stem, files[i], info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception)
                {
                    // Unreadable file: skip, never throw.
                }
            }
            found.Sort(delegate (PanelSessionEntry a, PanelSessionEntry b)
            {
                return b.LastModifiedUtc.CompareTo(a.LastModifiedUtc);
            });
            return found;
        }

        /// <summary>
        /// Reads the row-level facts out of one stored file WITHOUT
        /// deleting it on failure (a History scan must never destroy a
        /// file the user has not asked to delete). Every field defaults to
        /// empty / -1 when the file cannot be read or parsed.
        /// </summary>
        public static PanelSessionSummary ReadSummary(string filePath, Action<string> log = null)
        {
            var summary = new PanelSessionSummary();
            try
            {
                string json = AtomicFile.ReadAllText(filePath, log);
                if (json == null)
                {
                    return summary;
                }
                JsonNode root = JsonParser.Parse(json);
                if (!root.IsObject)
                {
                    return summary;
                }
                summary.SessionId = root["sessionId"].AsString(string.Empty);
                summary.AgentBackend = root["agentBackend"].AsInt(-1);
                summary.Title = Truncate(FirstLine(root["title"].AsString(string.Empty)));
                summary.Preview = Truncate(FirstLine(FirstUserText(root["messages"])));
                JsonNode usage = root["lastModelUsage"];
                if (usage.IsObject)
                {
                    foreach (string model in usage.Keys)
                    {
                        if (!string.IsNullOrEmpty(model))
                        {
                            summary.ModelName = model;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Could not read the stored session '" + filePath + "': " + ex.Message);
                }
            }
            return summary;
        }

        private static string FirstUserText(JsonNode messages)
        {
            if (!messages.IsArray)
            {
                return string.Empty;
            }
            for (int i = 0; i < messages.Count; i++)
            {
                JsonNode message = messages[i];
                if (!message.IsObject
                    || !string.Equals(message["role"].AsString(string.Empty), ChatMessage.RoleUser, StringComparison.Ordinal))
                {
                    continue;
                }
                JsonNode blocks = message["blocks"];
                if (!blocks.IsArray)
                {
                    continue;
                }
                for (int b = 0; b < blocks.Count; b++)
                {
                    JsonNode block = blocks[b];
                    if (block.IsObject
                        && string.Equals(block["kind"].AsString(string.Empty), ChatBlockKind.Text.ToString(), StringComparison.Ordinal))
                    {
                        string text = block["text"].AsString(string.Empty);
                        if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0)
                        {
                            return text;
                        }
                    }
                }
            }
            return string.Empty;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            int newline = text.IndexOfAny(new[] { '\r', '\n' });
            return (newline >= 0 ? text.Substring(0, newline) : text).Trim();
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= PreviewMaxChars)
            {
                return text ?? string.Empty;
            }
            return text.Substring(0, PreviewMaxChars - 3) + "...";
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }

    /// <summary>Row-level facts of one stored session (see <see cref="PanelSessionStore.ReadSummary"/>).</summary>
    public sealed class PanelSessionSummary
    {
        public string SessionId = string.Empty;
        /// <summary>The AgentBackend (as int) that owns the session, or -1 when the file does not say.</summary>
        public int AgentBackend = -1;
        public string Title = string.Empty;
        public string Preview = string.Empty;
        public string ModelName = string.Empty;
    }

    /// <summary>
    /// One file found by <see cref="PanelSessionStore.Enumerate"/>. The
    /// stat fields are eager; the content-derived ones read the file once,
    /// on first access, and cache the result -- the same shape as
    /// <see cref="SessionIndexEntry"/> so History can hydrate only the rows
    /// it actually shows.
    /// </summary>
    public sealed class PanelSessionEntry
    {
        private readonly string _filePath;
        private PanelSessionSummary _summary;

        internal PanelSessionEntry(string sessionId, string filePath, DateTime lastModifiedUtc, long sizeBytes)
        {
            SessionId = sessionId ?? string.Empty;
            _filePath = filePath;
            LastModifiedUtc = lastModifiedUtc;
            SizeBytes = sizeBytes;
        }

        /// <summary>The file stem -- the (sanitized) session id.</summary>
        public string SessionId { get; private set; }

        public string FilePath
        {
            get { return _filePath; }
        }

        public DateTime LastModifiedUtc { get; private set; }
        public long SizeBytes { get; private set; }

        /// <summary>The owning backend as int (-1 unknown). Lazy.</summary>
        public int AgentBackend
        {
            get { return Summary.AgentBackend; }
        }

        /// <summary>The session title (first user line at the time), truncated. Lazy.</summary>
        public string Title
        {
            get { return Summary.Title; }
        }

        /// <summary>First user text, truncated to ~80 chars. Lazy.</summary>
        public string FirstUserTextPreview
        {
            get { return Summary.Preview; }
        }

        /// <summary>The model of the last recorded turn, or empty. Lazy.</summary>
        public string ModelName
        {
            get { return Summary.ModelName; }
        }

        private PanelSessionSummary Summary
        {
            get
            {
                if (_summary == null)
                {
                    _summary = PanelSessionStore.ReadSummary(_filePath);
                }
                return _summary;
            }
        }
    }
}

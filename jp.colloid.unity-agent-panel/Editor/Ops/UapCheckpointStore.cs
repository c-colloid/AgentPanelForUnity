using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colloid.AgentPanel.Core.FileIo;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// One file (or created folder) a request touched, with what is needed
    /// to put it back (design note docs/design-notes/2026-11-23-request-
    /// checkpoints.md section 3). First touch wins: the entry describes the
    /// state BEFORE the request, so a file the request wrote five times has
    /// one entry.
    /// </summary>
    public sealed class UapCheckpointEntry
    {
        /// <summary>Project-relative path with forward slashes.</summary>
        public string Path;

        /// <summary>True when the file existed before the request (its bytes are in <see cref="Blob"/> unless <see cref="Skipped"/> says otherwise). False = the request created it, so a revert removes it.</summary>
        public bool Existed;

        /// <summary>A folder the request created (Existed is always false). Removed on revert only when it is empty by then.</summary>
        public bool IsDirectory;

        /// <summary>File name under the checkpoint's "files" folder holding the pre-request bytes. Null when the entry has none.</summary>
        public string Blob;

        /// <summary>Byte length of the pre-request file.</summary>
        public long Size;

        /// <summary>Null when the bytes were stored. Otherwise why they were not ("too-large", "budget", "read-failed"): the file is named in the revert report as one that could not be put back.</summary>
        public string Skipped;

        /// <summary>Length of the file when the request's last turn ended; -1 = it did not exist then. Compared at revert time to tell the user which files changed afterwards.</summary>
        public long AfterSize = -1;

        /// <summary>LastWriteTimeUtc ticks matching <see cref="AfterSize"/>.</summary>
        public long AfterTicks;

        /// <summary>False until the first turn end stamped the entry (nothing to compare against before that).</summary>
        public bool AfterStamped;
    }

    /// <summary>
    /// Everything recorded for one user request: the files it touched, the
    /// Unity Undo groups of its turns, and the tools whose effects nothing
    /// here can see. A request that auto-continued across a domain reload
    /// is still ONE checkpoint (<see cref="Turns"/> counts its turns).
    /// </summary>
    public sealed class UapCheckpoint
    {
        public const string StateOpen = "open";
        public const string StateReverted = "reverted";

        /// <summary>Sortable id: creation order is ordinal string order.</summary>
        public string Id;
        public string CreatedUtc;
        public string Title = string.Empty;
        public string SessionId = string.Empty;

        /// <summary>The editor launch that recorded the Undo groups below. Unity's Undo history does not outlive the editor process, so a different value at revert time means the scene part cannot be undone.</summary>
        public long EditorSessionId;
        public string State = StateOpen;
        public int Turns;
        public readonly List<UapCheckpointEntry> Entries = new List<UapCheckpointEntry>();

        /// <summary>Undo group index each turn of this request opened (UapTurnScope).</summary>
        public readonly List<int> UndoGroups = new List<int>();

        /// <summary>How many Undo-covered UapOps tool calls the request made.</summary>
        public int SceneOps;

        /// <summary>
        /// A turn of the request left records in Unity's Undo history,
        /// whoever made them: a UapOps tool, a menu item, or a
        /// uap_execute_code snippet that called Undo itself. Observed from
        /// the Undo history rather than inferred from tool names.
        /// </summary>
        public bool UndoRecorded;

        /// <summary>Tool name -> call count for tools whose changes are not tracked (shell commands, arbitrary editor code, menu items).</summary>
        public readonly Dictionary<string, int> Untracked = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Project-relative paths of the scenes that had unsaved changes when the request started.</summary>
        public readonly List<string> DirtyScenes = new List<string>();

        /// <summary>Writes the request made outside the project folder (not recorded).</summary>
        public int OutsideProject;

        /// <summary>Sum of the stored pre-request bytes.</summary>
        public long StoredBytes;

        internal readonly Dictionary<string, int> Index;

        public UapCheckpoint(bool caseInsensitive)
        {
            Index = new Dictionary<string, int>(caseInsensitive
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        /// <summary>True when the request made edits Unity's Undo holds (the part of a revert that goes through Undo).</summary>
        public bool HasSceneEdits
        {
            get { return SceneOps > 0 || UndoRecorded; }
        }

        /// <summary>True when a revert would change anything: a recorded file, or scene work Undo covers.</summary>
        public bool HasRevertibleContent
        {
            get { return Entries.Count > 0 || HasSceneEdits; }
        }

        public bool IsReverted
        {
            get { return State == StateReverted; }
        }

        public UapCheckpointEntry Find(string relativePath)
        {
            int index;
            if (relativePath != null && Index.TryGetValue(relativePath, out index))
            {
                return Entries[index];
            }
            return null;
        }
    }

    /// <summary>
    /// The project's files at one moment: size and write time per file,
    /// and the folders. Two of these around the untracked part of a
    /// request (a shell command, arbitrary editor code) say what that part
    /// created, changed and deleted -- see UapCheckpointStore.Scan.
    /// </summary>
    public sealed class UapProjectScan
    {
        /// <summary>Project-relative path -> { length, LastWriteTimeUtc ticks }.</summary>
        public readonly Dictionary<string, long[]> Files;
        public readonly HashSet<string> Directories;

        /// <summary>The scan stopped at its file limit; it is not a complete picture and must not be diffed.</summary>
        public bool Truncated;

        public UapProjectScan(bool caseInsensitive)
        {
            StringComparer comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Files = new Dictionary<string, long[]>(comparer);
            Directories = new HashSet<string>(comparer);
        }
    }

    /// <summary>What a revert did, for the transcript note and the model.</summary>
    public sealed class UapCheckpointRevertReport
    {
        /// <summary>Files put back to their pre-request bytes.</summary>
        public readonly List<string> Restored = new List<string>();

        /// <summary>Files (and their .meta) the request created, now removed.</summary>
        public readonly List<string> Removed = new List<string>();

        /// <summary>Files left as they are because the caller asked (an open scene with its own unsaved changes).</summary>
        public readonly List<string> Kept = new List<string>();

        /// <summary>Files whose pre-request bytes were never stored (too large, over budget, unreadable).</summary>
        public readonly List<string> NotStored = new List<string>();

        /// <summary>"path: reason" for every file the revert could not write or remove.</summary>
        public readonly List<string> Failed = new List<string>();
    }

    /// <summary>
    /// The on-disk journal behind "revert this request" (design note
    /// docs/design-notes/2026-11-23-request-checkpoints.md). Unity-free on
    /// purpose: everything here is plain file IO plus JSON, so the smoke
    /// tier (ci/SmokeTests) runs the real capture / revert / prune code
    /// without an editor. UapCheckpointRecorder is the Unity shell that
    /// decides WHEN to call it.
    ///
    /// Layout under the store root (Library/AgentPanel/Checkpoints):
    ///   &lt;id&gt;/manifest.json     the UapCheckpoint
    ///   &lt;id&gt;/files/&lt;n&gt;.bin    pre-request bytes, one per stored entry
    ///   &lt;id&gt;/redo/&lt;n&gt;.bin     what a revert overwrote or removed (kept so
    ///                          a revert is itself recoverable by hand)
    ///
    /// Being on disk is the point: a domain reload, an editor restart or a
    /// crash loses none of it, which Unity's own Undo cannot offer for
    /// files at all.
    /// </summary>
    public sealed class UapCheckpointStore
    {
        /// <summary>A single file larger than this is not stored (recorded as "too-large").</summary>
        public const long DefaultMaxFileBytes = 128L * 1024 * 1024;

        /// <summary>Stored bytes one request may hold; later files are recorded as "budget".</summary>
        public const long DefaultMaxCheckpointBytes = 512L * 1024 * 1024;

        /// <summary>Checkpoints kept; older ones are deleted when a new one is created.</summary>
        public const int DefaultMaxCheckpoints = 20;

        /// <summary>Stored bytes the whole store may hold before the oldest checkpoints go.</summary>
        public const long DefaultMaxStoreBytes = 2048L * 1024 * 1024;

        public const string SkippedTooLarge = "too-large";
        public const string SkippedBudget = "budget";
        public const string SkippedReadFailed = "read-failed";

        /// <summary>
        /// The file was changed or deleted by something that gave no
        /// chance to copy it first (seen only by comparing two scans), so
        /// its earlier content is not known.
        /// </summary>
        public const string SkippedUntracked = "untracked";

        /// <summary>Files a scan reads before it gives up (<see cref="UapProjectScan.Truncated"/>).</summary>
        public const int DefaultMaxScanFiles = 400000;

        /// <summary>
        /// The folders a scan walks. The rest of the project folder is
        /// caches, IDE files Unity regenerates, and the agent's own
        /// working files -- none of it something a revert should judge.
        /// </summary>
        private static readonly string[] ScanRoots = { "Assets", "Packages", "ProjectSettings" };

        private const string ManifestName = "manifest.json";
        private const string FilesDir = "files";
        private const string RedoDir = "redo";
        private const string MetaSuffix = ".meta";

        /// <summary>
        /// Project folders never recorded: caches Unity rebuilds, and this
        /// store itself. A revert must not rewrite them.
        /// </summary>
        private static readonly string[] ExcludedPrefixes =
        {
            "Library/", "Temp/", "Logs/", "obj/", ".git/"
        };

        private readonly string _storeRoot;
        private readonly string _projectRoot;
        private readonly bool _caseInsensitive;
        private readonly StringComparison _pathComparison;
        private long _lastIdTicks;

        public long MaxFileBytes = DefaultMaxFileBytes;
        public long MaxCheckpointBytes = DefaultMaxCheckpointBytes;
        public int MaxCheckpoints = DefaultMaxCheckpoints;
        public long MaxStoreBytes = DefaultMaxStoreBytes;

        /// <summary>Optional diagnostics sink (the Unity shell passes Debug.Log).</summary>
        public Action<string> Log;

        public UapCheckpointStore(string storeRoot, string projectRoot, bool caseInsensitive)
        {
            if (string.IsNullOrEmpty(storeRoot))
            {
                throw new ArgumentException("storeRoot must be set.", "storeRoot");
            }
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException("projectRoot must be set.", "projectRoot");
            }
            _storeRoot = System.IO.Path.GetFullPath(storeRoot);
            _projectRoot = TrimSeparators(System.IO.Path.GetFullPath(projectRoot));
            _caseInsensitive = caseInsensitive;
            _pathComparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        public string StoreRoot
        {
            get { return _storeRoot; }
        }

        /// <summary>The project folder relative paths are resolved against.</summary>
        public string ProjectRoot
        {
            get { return _projectRoot; }
        }

        // -- Paths ---------------------------------------------------------------

        /// <summary>
        /// Maps an absolute or project-relative path to the project-relative
        /// forward-slash form entries use. Null when the path is empty,
        /// outside the project, the project root itself, or under a folder
        /// this store never records.
        /// </summary>
        public string ToRelative(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            string full;
            try
            {
                full = System.IO.Path.IsPathRooted(path)
                    ? System.IO.Path.GetFullPath(path)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectRoot, path));
            }
            catch (Exception)
            {
                return null;
            }
            full = TrimSeparators(full);
            if (full.Length <= _projectRoot.Length + 1
                || !full.StartsWith(_projectRoot, _pathComparison))
            {
                return null;
            }
            char boundary = full[_projectRoot.Length];
            if (boundary != '/' && boundary != '\\')
            {
                return null;
            }
            string relative = full.Substring(_projectRoot.Length + 1).Replace('\\', '/');
            for (int i = 0; i < ExcludedPrefixes.Length; i++)
            {
                if (relative.StartsWith(ExcludedPrefixes[i], _pathComparison))
                {
                    return null;
                }
            }
            return relative;
        }

        /// <summary>True when <paramref name="path"/> resolves to somewhere outside the project folder (as opposed to an excluded folder inside it).</summary>
        public bool IsOutsideProject(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                string full = TrimSeparators(System.IO.Path.IsPathRooted(path)
                    ? System.IO.Path.GetFullPath(path)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(_projectRoot, path)));
                if (full.Length <= _projectRoot.Length)
                {
                    return true;
                }
                if (!full.StartsWith(_projectRoot, _pathComparison))
                {
                    return true;
                }
                char boundary = full[_projectRoot.Length];
                return boundary != '/' && boundary != '\\';
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string ToAbsolute(string relative)
        {
            return System.IO.Path.Combine(_projectRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        private static string TrimSeparators(string path)
        {
            return path.TrimEnd('/', '\\');
        }

        private string CheckpointDir(string id)
        {
            return System.IO.Path.Combine(_storeRoot, id);
        }

        // -- Create / save / load --------------------------------------------------

        /// <summary>
        /// Opens a new checkpoint and writes its (empty) manifest, then
        /// prunes the store down to its limits. Returns null when the
        /// store folder cannot be written -- recording is best effort and
        /// must never stop the request it is recording.
        /// </summary>
        public UapCheckpoint Create(string title, string sessionId, long editorSessionId)
        {
            long ticks = DateTime.UtcNow.Ticks;
            if (ticks <= _lastIdTicks)
            {
                ticks = _lastIdTicks + 1;
            }
            _lastIdTicks = ticks;
            var checkpoint = new UapCheckpoint(_caseInsensitive)
            {
                Id = ticks.ToString("D19", CultureInfo.InvariantCulture) + "-"
                    + Guid.NewGuid().ToString("N").Substring(0, 6),
                CreatedUtc = new DateTime(ticks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
                Title = title ?? string.Empty,
                SessionId = sessionId ?? string.Empty,
                EditorSessionId = editorSessionId
            };
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(CheckpointDir(checkpoint.Id), FilesDir));
            }
            catch (Exception ex)
            {
                Report("Could not create the checkpoint folder: " + ex.Message);
                return null;
            }
            if (!Save(checkpoint))
            {
                return null;
            }
            Prune(checkpoint.Id);
            return checkpoint;
        }

        public bool Save(UapCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                return false;
            }
            string path = System.IO.Path.Combine(CheckpointDir(checkpoint.Id), ManifestName);
            return AtomicFile.WriteAllText(path, JsonWriter.Write(ToJson(checkpoint)), Log);
        }

        public UapCheckpoint Load(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOf('/') >= 0 || id.IndexOf('\\') >= 0 || id.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            string path = System.IO.Path.Combine(CheckpointDir(id), ManifestName);
            if (!File.Exists(path))
            {
                return null;
            }
            string text = AtomicFile.ReadAllText(path, Log);
            JsonNode node;
            string error;
            if (string.IsNullOrEmpty(text) || !JsonParser.TryParse(text, out node, out error) || !node.IsObject)
            {
                return null;
            }
            UapCheckpoint checkpoint = FromJson(node);
            // The folder name is the identity; a manifest copied by hand
            // into another folder must not answer to its old id.
            checkpoint.Id = id;
            return checkpoint;
        }

        /// <summary>Every readable checkpoint, oldest first.</summary>
        public List<UapCheckpoint> LoadAll()
        {
            var result = new List<UapCheckpoint>();
            string[] directories;
            try
            {
                if (!Directory.Exists(_storeRoot))
                {
                    return result;
                }
                directories = Directory.GetDirectories(_storeRoot);
            }
            catch (Exception)
            {
                return result;
            }
            var ids = new List<string>(directories.Length);
            for (int i = 0; i < directories.Length; i++)
            {
                ids.Add(System.IO.Path.GetFileName(directories[i]));
            }
            ids.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                UapCheckpoint checkpoint = Load(ids[i]);
                if (checkpoint != null)
                {
                    result.Add(checkpoint);
                }
            }
            return result;
        }

        public void Delete(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOf('/') >= 0 || id.IndexOf('\\') >= 0 || id.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return;
            }
            try
            {
                string directory = CheckpointDir(id);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (Exception ex)
            {
                Report("Could not delete checkpoint " + id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes the oldest checkpoints until the store is within
        /// <see cref="MaxCheckpoints"/> and <see cref="MaxStoreBytes"/>.
        /// <paramref name="keepId"/> (the one being recorded) is never deleted.
        /// </summary>
        public void Prune(string keepId)
        {
            List<UapCheckpoint> all = LoadAll();
            long total = 0;
            for (int i = 0; i < all.Count; i++)
            {
                total += all[i].StoredBytes;
            }
            int index = 0;
            while (index < all.Count && (all.Count - index > MaxCheckpoints || total > MaxStoreBytes))
            {
                UapCheckpoint oldest = all[index];
                index++;
                if (oldest.Id == keepId)
                {
                    continue;
                }
                total -= oldest.StoredBytes;
                Delete(oldest.Id);
            }
        }

        // -- Recording -------------------------------------------------------------

        /// <summary>
        /// Records the state of <paramref name="path"/> BEFORE the request
        /// changes it: an existing file's bytes are copied into the
        /// checkpoint, a missing one is recorded as "created by the
        /// request" together with every folder above it that does not
        /// exist yet. A path already recorded is left alone (first touch
        /// wins). Returns true when an entry was added. The manifest is NOT
        /// saved here -- the caller saves once per batch.
        /// </summary>
        public bool Capture(UapCheckpoint checkpoint, string path)
        {
            if (checkpoint == null)
            {
                return false;
            }
            string relative = ToRelative(path);
            if (relative == null)
            {
                if (IsOutsideProject(path))
                {
                    checkpoint.OutsideProject++;
                }
                return false;
            }
            if (checkpoint.Index.ContainsKey(relative))
            {
                return false;
            }
            string absolute = ToAbsolute(relative);
            if (Directory.Exists(absolute))
            {
                // A folder that already exists has nothing to put back;
                // its files are recorded one by one as they are touched.
                return false;
            }
            if (!File.Exists(absolute))
            {
                MarkMissingParentsCreated(checkpoint, relative);
                AddEntry(checkpoint, new UapCheckpointEntry { Path = relative, Existed = false });
                return true;
            }
            var entry = new UapCheckpointEntry { Path = relative, Existed = true };
            try
            {
                long size = new FileInfo(absolute).Length;
                entry.Size = size;
                if (size > MaxFileBytes)
                {
                    entry.Skipped = SkippedTooLarge;
                }
                else if (checkpoint.StoredBytes + size > MaxCheckpointBytes)
                {
                    entry.Skipped = SkippedBudget;
                }
                else
                {
                    string blob = checkpoint.Entries.Count.ToString(CultureInfo.InvariantCulture) + ".bin";
                    string filesDir = System.IO.Path.Combine(CheckpointDir(checkpoint.Id), FilesDir);
                    Directory.CreateDirectory(filesDir);
                    File.Copy(absolute, System.IO.Path.Combine(filesDir, blob), true);
                    entry.Blob = blob;
                    checkpoint.StoredBytes += size;
                }
            }
            catch (Exception ex)
            {
                entry.Blob = null;
                entry.Skipped = SkippedReadFailed;
                Report("Could not store '" + relative + "': " + ex.Message);
            }
            AddEntry(checkpoint, entry);
            return true;
        }

        /// <summary>
        /// Records <paramref name="path"/> as created by the request even
        /// though it exists by now (Unity reports a new asset through the
        /// .meta it has just written, after the file itself is on disk).
        /// No-op for a path that already has an entry.
        /// </summary>
        public bool MarkCreated(UapCheckpoint checkpoint, string path, bool isDirectory)
        {
            if (checkpoint == null)
            {
                return false;
            }
            string relative = ToRelative(path);
            if (relative == null || checkpoint.Index.ContainsKey(relative))
            {
                return false;
            }
            AddEntry(checkpoint, new UapCheckpointEntry { Path = relative, Existed = false, IsDirectory = isDirectory });
            return true;
        }

        /// <summary>
        /// As <see cref="Capture"/>, but only for a file that exists: a
        /// path guessed from a shell command is worth copying when there
        /// is something to copy, and is never trusted enough to record
        /// "the request created this" (the scan diff decides that from
        /// what actually appeared).
        /// </summary>
        public bool CaptureExisting(UapCheckpoint checkpoint, string path)
        {
            string relative = ToRelative(path);
            if (relative == null || !File.Exists(ToAbsolute(relative)))
            {
                return false;
            }
            return Capture(checkpoint, path);
        }

        // -- Scan and diff ---------------------------------------------------------

        /// <summary>
        /// Lists every file (size, write time) and folder under Assets,
        /// Packages and ProjectSettings. Folders whose name starts with a
        /// dot and links to other places (reparse points) are not entered.
        /// Metadata only -- no file is opened.
        /// </summary>
        public UapProjectScan Scan(int maxFiles = DefaultMaxScanFiles)
        {
            var scan = new UapProjectScan(_caseInsensitive);
            var pending = new Stack<string>();
            for (int i = ScanRoots.Length - 1; i >= 0; i--)
            {
                if (Directory.Exists(ToAbsolute(ScanRoots[i])))
                {
                    scan.Directories.Add(ScanRoots[i]);
                    pending.Push(ScanRoots[i]);
                }
            }
            while (pending.Count > 0)
            {
                string relativeDir = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(ToAbsolute(relativeDir)).EnumerateFileSystemInfos();
                }
                catch (Exception)
                {
                    continue;
                }
                try
                {
                    foreach (FileSystemInfo child in children)
                    {
                        string relative = relativeDir + "/" + child.Name;
                        var file = child as FileInfo;
                        if (file != null)
                        {
                            if (scan.Files.Count >= maxFiles)
                            {
                                scan.Truncated = true;
                                return scan;
                            }
                            scan.Files[relative] = new[] { file.Length, file.LastWriteTimeUtc.Ticks };
                            continue;
                        }
                        if (child.Name.Length > 0 && child.Name[0] == '.')
                        {
                            continue;
                        }
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        scan.Directories.Add(relative);
                        pending.Push(relative);
                    }
                }
                catch (Exception)
                {
                    // A folder that vanished or became unreadable mid-walk:
                    // what was listed so far stands.
                }
            }
            return scan;
        }

        /// <summary>
        /// Adds to the checkpoint what changed between two scans and has
        /// no entry yet: a file or folder that appeared is recorded as
        /// created by the request (a revert removes it); a file that was
        /// changed or deleted is recorded with no stored content
        /// (<see cref="SkippedUntracked"/>), so a revert names it instead
        /// of leaving it unmentioned. Returns the number of entries added.
        /// A truncated scan on either side adds nothing.
        /// </summary>
        public int ApplyScanDiff(UapCheckpoint checkpoint, UapProjectScan before, UapProjectScan after)
        {
            if (checkpoint == null || before == null || after == null || before.Truncated || after.Truncated)
            {
                return 0;
            }
            int added = 0;
            var newDirectories = new List<string>();
            foreach (string directory in after.Directories)
            {
                if (!before.Directories.Contains(directory) && !checkpoint.Index.ContainsKey(directory))
                {
                    newDirectories.Add(directory);
                }
            }
            // Shallowest first, so entries keep the order creations happen in.
            newDirectories.Sort(delegate (string a, string b) { return a.Length.CompareTo(b.Length); });
            for (int i = 0; i < newDirectories.Count; i++)
            {
                AddEntry(checkpoint, new UapCheckpointEntry { Path = newDirectories[i], Existed = false, IsDirectory = true });
                added++;
            }
            foreach (KeyValuePair<string, long[]> pair in after.Files)
            {
                if (!before.Files.ContainsKey(pair.Key) && !checkpoint.Index.ContainsKey(pair.Key))
                {
                    AddEntry(checkpoint, new UapCheckpointEntry { Path = pair.Key, Existed = false });
                    added++;
                }
            }
            foreach (KeyValuePair<string, long[]> pair in before.Files)
            {
                if (checkpoint.Index.ContainsKey(pair.Key))
                {
                    continue;
                }
                long[] now;
                bool unchanged = after.Files.TryGetValue(pair.Key, out now)
                    && now[0] == pair.Value[0] && now[1] == pair.Value[1];
                if (!unchanged)
                {
                    AddEntry(checkpoint, new UapCheckpointEntry
                    {
                        Path = pair.Key,
                        Existed = true,
                        Size = pair.Value[0],
                        Skipped = SkippedUntracked
                    });
                    added++;
                }
            }
            return added;
        }

        private void MarkMissingParentsCreated(UapCheckpoint checkpoint, string relative)
        {
            var missing = new List<string>();
            int slash = relative.LastIndexOf('/');
            while (slash > 0)
            {
                string parent = relative.Substring(0, slash);
                if (checkpoint.Index.ContainsKey(parent) || Directory.Exists(ToAbsolute(parent)))
                {
                    break;
                }
                missing.Add(parent);
                slash = parent.LastIndexOf('/');
            }
            for (int i = missing.Count - 1; i >= 0; i--)
            {
                AddEntry(checkpoint, new UapCheckpointEntry { Path = missing[i], Existed = false, IsDirectory = true });
            }
        }

        private static void AddEntry(UapCheckpoint checkpoint, UapCheckpointEntry entry)
        {
            checkpoint.Index[entry.Path] = checkpoint.Entries.Count;
            checkpoint.Entries.Add(entry);
        }

        /// <summary>
        /// Stamps every file entry with its current size and write time
        /// (called when a turn of the request ends). A revert compares
        /// against these to count the files something else changed since.
        /// </summary>
        public void StampAfter(UapCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                return;
            }
            for (int i = 0; i < checkpoint.Entries.Count; i++)
            {
                UapCheckpointEntry entry = checkpoint.Entries[i];
                if (entry.IsDirectory)
                {
                    continue;
                }
                long size;
                long ticks;
                ReadStamp(ToAbsolute(entry.Path), out size, out ticks);
                entry.AfterSize = size;
                entry.AfterTicks = ticks;
                entry.AfterStamped = true;
            }
        }

        /// <summary>Project-relative paths of the entries whose file is no longer what the request left behind.</summary>
        public List<string> ChangedSince(UapCheckpoint checkpoint)
        {
            var changed = new List<string>();
            if (checkpoint == null)
            {
                return changed;
            }
            for (int i = 0; i < checkpoint.Entries.Count; i++)
            {
                UapCheckpointEntry entry = checkpoint.Entries[i];
                if (entry.IsDirectory || !entry.AfterStamped)
                {
                    continue;
                }
                long size;
                long ticks;
                ReadStamp(ToAbsolute(entry.Path), out size, out ticks);
                if (size != entry.AfterSize || (size >= 0 && ticks != entry.AfterTicks))
                {
                    changed.Add(entry.Path);
                }
            }
            return changed;
        }

        private static void ReadStamp(string absolute, out long size, out long ticks)
        {
            size = -1;
            ticks = 0;
            try
            {
                var info = new FileInfo(absolute);
                if (info.Exists)
                {
                    size = info.Length;
                    ticks = info.LastWriteTimeUtc.Ticks;
                }
            }
            catch (Exception)
            {
                size = -1;
                ticks = 0;
            }
        }

        // -- Revert ----------------------------------------------------------------

        /// <summary>
        /// Puts every recorded file back: pre-request bytes are written
        /// over the current file, files the request created are removed
        /// together with their .meta, and folders the request created are
        /// removed when nothing is left in them. Whatever a revert
        /// overwrites or removes is first copied into the checkpoint's
        /// "redo" folder. <paramref name="keep"/> (may be null) names
        /// project-relative paths to leave untouched. Marks the checkpoint
        /// reverted and saves it. Never throws: a file that cannot be
        /// written is listed in the report and the rest still run.
        /// </summary>
        public UapCheckpointRevertReport Revert(UapCheckpoint checkpoint, Func<string, bool> keep)
        {
            var report = new UapCheckpointRevertReport();
            if (checkpoint == null)
            {
                return report;
            }
            string checkpointDir = CheckpointDir(checkpoint.Id);
            string redoDir = System.IO.Path.Combine(checkpointDir, RedoDir);
            int redoCount = 0;
            var createdDirectories = new List<string>();

            // Newest first, so the order of removals mirrors the order of
            // creations (a file before the folder that was made for it).
            for (int i = checkpoint.Entries.Count - 1; i >= 0; i--)
            {
                UapCheckpointEntry entry = checkpoint.Entries[i];
                if (entry.IsDirectory)
                {
                    createdDirectories.Add(entry.Path);
                    continue;
                }
                if (keep != null && keep(entry.Path))
                {
                    report.Kept.Add(entry.Path);
                    continue;
                }
                string absolute = ToAbsolute(entry.Path);
                if (!entry.Existed && Directory.Exists(absolute))
                {
                    // Recorded before it existed (Unity announces a new
                    // folder through its .meta first), and it turned out
                    // to be a folder.
                    createdDirectories.Add(entry.Path);
                    continue;
                }
                try
                {
                    if (entry.Existed)
                    {
                        if (entry.Blob == null)
                        {
                            report.NotStored.Add(entry.Path);
                            continue;
                        }
                        string blobPath = System.IO.Path.Combine(System.IO.Path.Combine(checkpointDir, FilesDir), entry.Blob);
                        if (!File.Exists(blobPath))
                        {
                            report.Failed.Add(entry.Path + ": the stored copy is missing");
                            continue;
                        }
                        BackUpForRedo(absolute, redoDir, ref redoCount);
                        string directory = System.IO.Path.GetDirectoryName(absolute);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        ClearReadOnly(absolute);
                        File.Copy(blobPath, absolute, true);
                        // A fresh write time so Unity (and an IDE holding
                        // the file open) notices the content changed.
                        File.SetLastWriteTimeUtc(absolute, DateTime.UtcNow);
                        report.Restored.Add(entry.Path);
                    }
                    else
                    {
                        bool removed = RemoveFile(absolute, redoDir, ref redoCount);
                        // The .meta Unity wrote for a created asset goes
                        // with it, unless the .meta has its own entry (then
                        // that entry decides).
                        string metaRelative = entry.Path + MetaSuffix;
                        if (!checkpoint.Index.ContainsKey(metaRelative))
                        {
                            RemoveFile(ToAbsolute(metaRelative), redoDir, ref redoCount);
                        }
                        if (removed)
                        {
                            report.Removed.Add(entry.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Failed.Add(entry.Path + ": " + ex.Message);
                }
            }

            // Deepest folder first; only when empty, so anything the user
            // put there afterwards keeps both the file and the folder.
            createdDirectories.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            for (int i = 0; i < createdDirectories.Count; i++)
            {
                string relative = createdDirectories[i];
                string absolute = ToAbsolute(relative);
                try
                {
                    if (!Directory.Exists(absolute))
                    {
                        continue;
                    }
                    if (Directory.GetFileSystemEntries(absolute).Length > 0)
                    {
                        continue;
                    }
                    Directory.Delete(absolute, false);
                    if (!checkpoint.Index.ContainsKey(relative + MetaSuffix))
                    {
                        RemoveFile(absolute + MetaSuffix, redoDir, ref redoCount);
                    }
                    report.Removed.Add(relative + "/");
                }
                catch (Exception ex)
                {
                    report.Failed.Add(relative + "/: " + ex.Message);
                }
            }

            checkpoint.State = UapCheckpoint.StateReverted;
            Save(checkpoint);
            return report;
        }

        private static bool RemoveFile(string absolute, string redoDir, ref int redoCount)
        {
            if (!File.Exists(absolute))
            {
                return false;
            }
            BackUpForRedo(absolute, redoDir, ref redoCount);
            ClearReadOnly(absolute);
            File.Delete(absolute);
            return true;
        }

        private static void BackUpForRedo(string absolute, string redoDir, ref int redoCount)
        {
            if (!File.Exists(absolute))
            {
                return;
            }
            Directory.CreateDirectory(redoDir);
            string name = redoCount.ToString(CultureInfo.InvariantCulture) + ".bin";
            File.Copy(absolute, System.IO.Path.Combine(redoDir, name), true);
            // One line per backup: enough for a person to map a blob back
            // to its file when they need to undo a revert by hand.
            File.AppendAllText(System.IO.Path.Combine(redoDir, "index.txt"), name + "\t" + absolute + "\n");
            redoCount++;
        }

        private static void ClearReadOnly(string absolute)
        {
            if (!File.Exists(absolute))
            {
                return;
            }
            FileAttributes attributes = File.GetAttributes(absolute);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(absolute, attributes & ~FileAttributes.ReadOnly);
            }
        }

        // -- JSON ------------------------------------------------------------------

        internal JsonNode ToJson(UapCheckpoint checkpoint)
        {
            JsonNode entries = JsonNode.NewArray();
            for (int i = 0; i < checkpoint.Entries.Count; i++)
            {
                UapCheckpointEntry entry = checkpoint.Entries[i];
                JsonNode node = JsonNode.NewObject()
                    .Set("path", entry.Path)
                    .Set("existed", entry.Existed);
                if (entry.IsDirectory)
                {
                    node.Set("dir", true);
                }
                if (entry.Blob != null)
                {
                    node.Set("blob", entry.Blob);
                }
                if (entry.Existed)
                {
                    node.Set("size", entry.Size);
                }
                if (entry.Skipped != null)
                {
                    node.Set("skipped", entry.Skipped);
                }
                if (entry.AfterStamped)
                {
                    node.Set("afterSize", entry.AfterSize);
                    node.Set("afterTicks", entry.AfterTicks.ToString(CultureInfo.InvariantCulture));
                }
                entries.Add(node);
            }
            JsonNode groups = JsonNode.NewArray();
            for (int i = 0; i < checkpoint.UndoGroups.Count; i++)
            {
                groups.Add((long)checkpoint.UndoGroups[i]);
            }
            JsonNode untracked = JsonNode.NewObject();
            foreach (KeyValuePair<string, int> pair in checkpoint.Untracked)
            {
                untracked.Set(pair.Key, pair.Value);
            }
            JsonNode dirty = JsonNode.NewArray();
            for (int i = 0; i < checkpoint.DirtyScenes.Count; i++)
            {
                dirty.Add(checkpoint.DirtyScenes[i]);
            }
            return JsonNode.NewObject()
                .Set("version", 1)
                .Set("id", checkpoint.Id)
                .Set("createdUtc", checkpoint.CreatedUtc ?? string.Empty)
                .Set("title", checkpoint.Title ?? string.Empty)
                .Set("sessionId", checkpoint.SessionId ?? string.Empty)
                // As a string: a 64-bit id does not survive a JSON number.
                .Set("editorSessionId", checkpoint.EditorSessionId.ToString(CultureInfo.InvariantCulture))
                .Set("state", checkpoint.State ?? UapCheckpoint.StateOpen)
                .Set("turns", checkpoint.Turns)
                .Set("sceneOps", checkpoint.SceneOps)
                .Set("undoRecorded", checkpoint.UndoRecorded)
                .Set("outsideProject", checkpoint.OutsideProject)
                .Set("storedBytes", checkpoint.StoredBytes)
                .Set("undoGroups", groups)
                .Set("untracked", untracked)
                .Set("dirtyScenes", dirty)
                .Set("entries", entries);
        }

        internal UapCheckpoint FromJson(JsonNode node)
        {
            var checkpoint = new UapCheckpoint(_caseInsensitive)
            {
                Id = node["id"].AsString(string.Empty),
                CreatedUtc = node["createdUtc"].AsString(string.Empty),
                Title = node["title"].AsString(string.Empty),
                SessionId = node["sessionId"].AsString(string.Empty),
                EditorSessionId = ParseLong(node["editorSessionId"].AsString("0")),
                State = node["state"].AsString(UapCheckpoint.StateOpen),
                Turns = node["turns"].AsInt(0),
                SceneOps = node["sceneOps"].AsInt(0),
                UndoRecorded = node["undoRecorded"].AsBool(false),
                OutsideProject = node["outsideProject"].AsInt(0),
                StoredBytes = node["storedBytes"].AsLong(0L)
            };
            foreach (JsonNode group in node["undoGroups"].Items)
            {
                checkpoint.UndoGroups.Add(group.AsInt(0));
            }
            foreach (KeyValuePair<string, JsonNode> pair in node["untracked"].Properties)
            {
                checkpoint.Untracked[pair.Key] = pair.Value.AsInt(0);
            }
            foreach (JsonNode scene in node["dirtyScenes"].Items)
            {
                string path = scene.AsString(null);
                if (!string.IsNullOrEmpty(path))
                {
                    checkpoint.DirtyScenes.Add(path);
                }
            }
            foreach (JsonNode item in node["entries"].Items)
            {
                string path = item["path"].AsString(null);
                // Re-validated on load: a manifest is a file on disk, and a
                // revert writes to whatever path it names.
                if (string.IsNullOrEmpty(path) || ToRelative(path) == null || checkpoint.Index.ContainsKey(path))
                {
                    continue;
                }
                string blob = item["blob"].AsString(null);
                if (blob != null && (blob.IndexOf('/') >= 0 || blob.IndexOf('\\') >= 0 || blob.IndexOf("..", StringComparison.Ordinal) >= 0))
                {
                    blob = null;
                }
                var entry = new UapCheckpointEntry
                {
                    Path = path,
                    Existed = item["existed"].AsBool(false),
                    IsDirectory = item["dir"].AsBool(false),
                    Blob = blob,
                    Size = item["size"].AsLong(0L),
                    Skipped = item["skipped"].AsString(null)
                };
                if (item.HasKey("afterSize"))
                {
                    entry.AfterStamped = true;
                    entry.AfterSize = item["afterSize"].AsLong(-1L);
                    entry.AfterTicks = ParseLong(item["afterTicks"].AsString("0"));
                }
                AddEntry(checkpoint, entry);
            }
            return checkpoint;
        }

        private static long ParseLong(string text)
        {
            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0L;
        }

        private void Report(string message)
        {
            if (Log != null)
            {
                Log("[UapCheckpointStore] " + message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Why a revert did not run.</summary>
    public enum UapRevertRefusal
    {
        None = 0,
        /// <summary>The checkpoint is gone, already reverted, or no longer the newest one.</summary>
        NotAvailable,
        /// <summary>A turn is running (its changes are still being recorded).</summary>
        Busy,
        PlayMode,
        Compiling
    }

    /// <summary>The result of <see cref="UapCheckpointRecorder.Revert"/>.</summary>
    public sealed class UapRevertOutcome
    {
        public UapRevertRefusal Refusal;
        public UapCheckpoint Checkpoint;
        public UapCheckpointRevertReport Files = new UapCheckpointRevertReport();

        /// <summary>The request's Undo groups were reverted.</summary>
        public bool SceneUndone;

        /// <summary>The request made Undo-covered edits, but the Undo history that held them is gone (the editor was restarted since).</summary>
        public bool SceneUndoUnavailable;

        /// <summary>Open scenes re-read from the file the revert restored.</summary>
        public readonly List<string> ScenesReloaded = new List<string>();

        public bool Ok
        {
            get { return Refusal == UapRevertRefusal.None; }
        }
    }

    /// <summary>What a revert would do, for the confirmation dialog.</summary>
    public sealed class UapRevertPreview
    {
        public UapCheckpoint Checkpoint;
        public int FilesToRestore;
        public int FilesToRemove;
        public int FilesNotStored;

        /// <summary>Files a shell command or editor code changed or deleted with no copy taken first: named, not restorable.</summary>
        public int FilesChangedUntracked;
        public int ChangedSince;
        public bool SceneUndo;
        public bool SceneUndoUnavailable;
        public string Untracked = string.Empty;

        /// <summary>Project-relative paths behind <see cref="FilesToRestore"/>, in the order they were first touched.</summary>
        public readonly List<string> RestorePaths = new List<string>();

        /// <summary>Paths behind <see cref="FilesToRemove"/>.</summary>
        public readonly List<string> RemovePaths = new List<string>();

        /// <summary>Paths behind <see cref="FilesNotStored"/> and <see cref="FilesChangedUntracked"/>: what a revert will NOT put back.</summary>
        public readonly List<string> LeftAsIsPaths = new List<string>();

        /// <summary>True when pressing Revert would change anything at all: a file to restore or delete, or scene edits Undo can take back.</summary>
        public bool HasAnythingToDo
        {
            get { return FilesToRestore > 0 || FilesToRemove > 0 || SceneUndo; }
        }
    }

    /// <summary>
    /// The Unity shell of request checkpoints (design note docs/design-
    /// notes/2026-11-23-request-checkpoints.md): decides WHEN the journal
    /// (<see cref="UapCheckpointStore"/>) records, and runs a revert.
    ///
    /// One checkpoint = one user request. AgentHub opens it when a message
    /// the user wrote starts a turn (<see cref="BeginRequest"/>) and
    /// re-opens the same one for the turns the panel sends by itself after
    /// a reload (<see cref="ContinueRequest"/>), so a request that was cut
    /// by a domain reload and auto-continued is still reverted as a whole.
    /// UapTurnScope closes the recording window when the turn ends
    /// (<see cref="EndTurn"/>) -- after its end-of-turn AssetDatabase
    /// refresh, so the assets that refresh imports are still attributed.
    ///
    /// What is recorded while the window is open:
    ///   - every file the CLI's Write / Edit tools are about to change
    ///     (AgentHub forwards the pre-write hook to <see cref="CaptureFromAgent"/>);
    ///   - every asset Unity is about to save, delete, move or create
    ///     (<see cref="UapCheckpointAssetWatcher"/>), whichever tool or
    ///     menu caused it;
    ///   - files a tool writes directly (the tool calls <see cref="Capture"/>
    ///     itself, e.g. uap_scripts_commit);
    ///   - the Undo group of each turn, for the scene edits.
    ///
    /// The state that must outlive a domain reload is on disk (the
    /// journal) or in SessionState (which checkpoint the conversation's
    /// last request opened, and this editor launch's id).
    /// </summary>
    public static class UapCheckpointRecorder
    {
        private const string KeyLastId = "Colloid.AgentPanel.Checkpoint.LastId";
        private const string KeyLaunchId = "Colloid.AgentPanel.Checkpoint.LaunchId";
        private const string StoreRelativeDirectory = "Library/AgentPanel/Checkpoints";

        /// <summary>Cap on the files recorded for one folder delete / move.</summary>
        private const int MaxFilesPerFolder = 2000;

        private static readonly object Gate = new object();
        private static UapCheckpointStore _store;
        private static UapCheckpointStore _storeOverride;
        private static UapCheckpoint _current;

        /// <summary>
        /// The project's files as they were just before the first
        /// untracked work of the open turn (a shell command, arbitrary
        /// editor code). Null until such work happens -- most turns never
        /// pay for a scan. Compared with a second scan when the turn ends.
        /// </summary>
        private static UapProjectScan _baseline;

        /// <summary>Undo stack size when the open turn began; -1 = unknown or no turn.</summary>
        private static int _undoRecordsAtStart = -1;

        /// <summary>
        /// The root objects of the loaded scenes at the moment this turn's
        /// scene snapshot was taken (<see cref="EnsureSceneSnapshotLocked"/>).
        /// Null = no snapshot this turn. A root that is not in here when
        /// the turn ends was created after the snapshot.
        /// </summary>
        private static HashSet<GameObject> _rootsAtSnapshot;

        /// <summary>
        /// Scene objects a snapshot will register. Measured 2026-11-23 on
        /// Unity 2022.3.22f1: about 10 microseconds an object to register
        /// (20,000 objects: 212 ms), so this bounds the pause before a
        /// snippet or menu item at roughly half a second.
        /// </summary>
        public const int MaxSnapshotObjects = 50000;

        private const string SnapshotUndoName = "Agent turn (scene snapshot)";
        private const string CreatedRootUndoName = "Agent turn (created object)";
        private static string _topId;
        private static bool _topKnown;

        /// <summary>True while a turn's changes are being recorded.</summary>
        public static bool IsRecording
        {
            get { lock (Gate) { return _current != null; } }
        }

        private static UapCheckpointStore Store
        {
            get
            {
                if (_storeOverride != null)
                {
                    return _storeOverride;
                }
                if (_store == null)
                {
                    string projectRoot = Path.GetDirectoryName(Application.dataPath);
                    _store = new UapCheckpointStore(
                        Path.Combine(projectRoot, StoreRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)),
                        projectRoot,
                        Application.platform != RuntimePlatform.LinuxEditor);
                    _store.Log = delegate (string message) { Debug.Log("[AgentPanel] " + message); };
                }
                return _store;
            }
        }

        /// <summary>
        /// An id for this editor launch: SessionState survives domain
        /// reloads and dies with the editor process, exactly like Unity's
        /// Undo history, so "same launch id" means "the Undo groups this
        /// checkpoint recorded can still exist".
        /// </summary>
        private static long LaunchId
        {
            get
            {
                string text = SessionState.GetString(KeyLaunchId, string.Empty);
                long value;
                if (long.TryParse(text, out value) && value != 0)
                {
                    return value;
                }
                value = DateTime.UtcNow.Ticks ^ ((long)Guid.NewGuid().GetHashCode() << 20);
                if (value == 0)
                {
                    value = 1;
                }
                SessionState.SetString(KeyLaunchId, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return value;
            }
        }

        // -- Recording window -------------------------------------------------------

        /// <summary>
        /// Opens a new checkpoint for a request the user just sent and
        /// starts recording. Returns its id (stored on the user's chat
        /// message so the transcript can offer the revert), or null when
        /// the journal could not be written. The previous request's
        /// checkpoint is dropped here when it recorded nothing.
        /// </summary>
        public static string BeginRequest(string title, string sessionId)
        {
            lock (Gate)
            {
                try
                {
                    CloseCurrentLocked();
                    DropLastIfEmptyLocked();
                    UapCheckpoint checkpoint = Store.Create(title, sessionId, LaunchId);
                    if (checkpoint == null)
                    {
                        return null;
                    }
                    checkpoint.Turns = 1;
                    AddDirtyScenes(checkpoint);
                    Store.Save(checkpoint);
                    _current = checkpoint;
                    _topKnown = false;
                    SessionState.SetString(KeyLastId, checkpoint.Id);
                    return checkpoint.Id;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Starting a request checkpoint failed: " + ex.Message);
                    _current = null;
                    return null;
                }
            }
        }

        /// <summary>
        /// Re-opens the conversation's last checkpoint for a turn the panel
        /// sends by itself (auto-continue after a compile or an
        /// interruption). False -- and nothing is recorded for that turn --
        /// when there is none or it was already reverted.
        /// </summary>
        public static bool ContinueRequest()
        {
            return ContinueRequest(true);
        }

        /// <summary>
        /// As <see cref="ContinueRequest()"/>; <paramref name="countTurn"/>
        /// false re-opens the window for the SAME turn (a CLI relay
        /// re-attach after a domain reload), which is not another turn of
        /// the request.
        /// </summary>
        public static bool ContinueRequest(bool countTurn)
        {
            lock (Gate)
            {
                try
                {
                    if (_current != null)
                    {
                        return true;
                    }
                    UapCheckpoint checkpoint = Store.Load(SessionState.GetString(KeyLastId, string.Empty));
                    if (checkpoint == null || checkpoint.IsReverted)
                    {
                        return false;
                    }
                    if (countTurn)
                    {
                        checkpoint.Turns++;
                    }
                    _current = checkpoint;
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Re-opening the request checkpoint failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>Remembers the Undo group the turn that just started opened (UapTurnScope).</summary>
        public static void NoteUndoGroup(int group)
        {
            lock (Gate)
            {
                if (_current != null && !_current.UndoGroups.Contains(group))
                {
                    _current.UndoGroups.Add(group);
                }
                _undoRecordsAtStart = _current != null ? UapUndoProbe.TryCount() : -1;
            }
        }

        /// <summary>
        /// Called by UapTurnScope just BEFORE it collapses the turn's Undo
        /// group: if the Undo stack grew since the turn began, the turn
        /// made edits Undo holds -- whoever made them. This is what brings
        /// a uap_execute_code snippet that called Undo itself, or a menu
        /// item that registers its own Undo, into the revert; counting
        /// Undo-covered tool calls (<see cref="NoteSceneOp"/>) only knows
        /// about this package's own tools. Unknown (the probe failed)
        /// changes nothing.
        /// </summary>
        public static void NoteTurnUndoRecords()
        {
            lock (Gate)
            {
                if (_current == null)
                {
                    return;
                }
                if (_rootsAtSnapshot != null)
                {
                    // The snapshot itself is an Undo record of this turn.
                    _current.UndoRecorded = true;
                    RegisterRootsCreatedSinceSnapshotLocked();
                }
                if (_undoRecordsAtStart < 0)
                {
                    return;
                }
                // Closes the group being written, the way uap_execute_code
                // does before it reads the list; harmless ahead of the
                // turn scope's collapse, which merges every group of the
                // turn anyway.
                Undo.IncrementCurrentGroup();
                int now = UapUndoProbe.TryCount();
                if (now > _undoRecordsAtStart)
                {
                    _current.UndoRecorded = true;
                }
                _undoRecordsAtStart = -1;
            }
        }

        /// <summary>
        /// A shell command is about to run (pre-run hook). Copies the
        /// files its text names that exist right now -- a folder gives its
        /// files, a wildcard in the last path segment is expanded -- and
        /// takes the "before" picture of the project if this turn has none
        /// yet. The CLI is waiting for the hook's answer, so this is the
        /// last moment those files are known to be untouched.
        /// </summary>
        public static void BeforeShellCommand(string command)
        {
            lock (Gate)
            {
                if (_current == null)
                {
                    return;
                }
                try
                {
                    bool added = false;
                    List<string> targets = UapCheckpointPolicy.ShellTargets(command);
                    for (int i = 0; i < targets.Count; i++)
                    {
                        List<string> files = ExpandShellTarget(targets[i]);
                        for (int f = 0; f < files.Count; f++)
                        {
                            added |= Store.CaptureExisting(_current, files[f]);
                        }
                    }
                    if (added)
                    {
                        Store.Save(_current);
                    }
                    EnsureBaselineLocked();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Preparing the request checkpoint for a shell command failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Arbitrary editor code is about to run (uap_execute_code, a menu
        /// item). Two things, each once per turn: the "before" picture of
        /// the project, so files it writes without the asset database are
        /// found when the turn ends; and a snapshot of the loaded scenes
        /// in Unity's Undo, so what it does to scene objects WITHOUT
        /// calling Undo is still taken back by the turn's Ctrl+Z and by a
        /// revert. Returns true when the scene snapshot is in place. Main
        /// thread only (it talks to Undo and the scene).
        /// </summary>
        public static bool BeforeUntrackedEditorWork()
        {
            lock (Gate)
            {
                if (_current == null)
                {
                    return false;
                }
                try
                {
                    EnsureBaselineLocked();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Scanning the project for the request checkpoint failed: " + ex.Message);
                }
                try
                {
                    return EnsureSceneSnapshotLocked();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Taking the scene snapshot for the request checkpoint failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Registers the full hierarchy of every root object of the loaded
        /// scenes with Undo (design note 2026-11-23 section 10.4). What
        /// that brings back was measured, not assumed: a changed property,
        /// a renamed object, an added or destroyed component and an added
        /// or destroyed child all return to the snapshot. A NEW root
        /// object does not -- it is in nobody's hierarchy -- which is what
        /// <see cref="RegisterRootsCreatedSinceSnapshotLocked"/> is for.
        /// Skipped in Play Mode (those changes are discarded on exit
        /// anyway) and above <see cref="MaxSnapshotObjects"/>.
        /// </summary>
        private static bool EnsureSceneSnapshotLocked()
        {
            if (_rootsAtSnapshot != null)
            {
                return true;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return false;
            }
            var roots = new List<GameObject>();
            int objects = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }
                GameObject[] sceneRoots = scene.GetRootGameObjects();
                for (int r = 0; r < sceneRoots.Length; r++)
                {
                    if (sceneRoots[r] == null)
                    {
                        continue;
                    }
                    roots.Add(sceneRoots[r]);
                    objects = CountObjects(sceneRoots[r].transform, objects);
                    if (objects > MaxSnapshotObjects)
                    {
                        Debug.Log("[AgentPanel] Request checkpoint: the open scenes hold more than " + MaxSnapshotObjects
                            + " objects, so no scene snapshot is taken; scene changes made by editor code without Undo"
                            + " cannot be reverted this turn.");
                        return false;
                    }
                }
            }
            for (int i = 0; i < roots.Count; i++)
            {
                Undo.RegisterFullObjectHierarchyUndo(roots[i], SnapshotUndoName);
            }
            _rootsAtSnapshot = new HashSet<GameObject>(roots);
            return true;
        }

        /// <summary>Adds the objects under <paramref name="transform"/> to <paramref name="count"/>, giving up once the cap is passed.</summary>
        private static int CountObjects(Transform transform, int count)
        {
            count++;
            int children = transform.childCount;
            for (int i = 0; i < children && count <= MaxSnapshotObjects; i++)
            {
                count = CountObjects(transform.GetChild(i), count);
            }
            return count;
        }

        /// <summary>
        /// Gives every root object that appeared since the snapshot a
        /// "created" Undo record, so undoing the turn destroys it. For a
        /// root some tool already registered this is a second record for
        /// the same object; undoing it twice is harmless (covered by
        /// UapCheckpointRecorderTests).
        /// </summary>
        private static void RegisterRootsCreatedSinceSnapshotLocked()
        {
            HashSet<GameObject> before = _rootsAtSnapshot;
            _rootsAtSnapshot = null;
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        continue;
                    }
                    GameObject[] sceneRoots = scene.GetRootGameObjects();
                    for (int r = 0; r < sceneRoots.Length; r++)
                    {
                        if (sceneRoots[r] != null && !before.Contains(sceneRoots[r]))
                        {
                            Undo.RegisterCreatedObjectUndo(sceneRoots[r], CreatedRootUndoName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AgentPanel] Registering objects created during the turn failed: " + ex.Message);
            }
        }

        private static void EnsureBaselineLocked()
        {
            if (_baseline != null)
            {
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            UapProjectScan scan = Store.Scan();
            watch.Stop();
            if (scan.Truncated)
            {
                Debug.LogWarning("[AgentPanel] Request checkpoint: the project has more than "
                    + UapCheckpointStore.DefaultMaxScanFiles + " files; changes made by shell commands and editor code"
                    + " are not tracked for this turn.");
                return;
            }
            if (watch.ElapsedMilliseconds > 1500)
            {
                Debug.Log("[AgentPanel] Request checkpoint: scanning " + scan.Files.Count + " files took "
                    + watch.ElapsedMilliseconds + " ms.");
            }
            _baseline = scan;
        }

        /// <summary>
        /// The existing files one shell target stands for. Paths are taken
        /// relative to the project root, which is where the CLI runs its
        /// commands unless the command changed directory first.
        /// </summary>
        private static List<string> ExpandShellTarget(string target)
        {
            var files = new List<string>();
            try
            {
                string projectRoot = Store.ProjectRoot;
                string full = Path.IsPathRooted(target) ? target : Path.Combine(projectRoot, target);
                if (target.IndexOf('*') >= 0 || target.IndexOf('?') >= 0)
                {
                    string directory = Path.GetDirectoryName(full);
                    string pattern = Path.GetFileName(full);
                    if (string.IsNullOrEmpty(directory) || directory.IndexOf('*') >= 0 || directory.IndexOf('?') >= 0
                        || !Directory.Exists(directory))
                    {
                        return files;
                    }
                    foreach (string match in Directory.EnumerateFileSystemEntries(directory, pattern))
                    {
                        if (files.Count >= MaxFilesPerFolder)
                        {
                            break;
                        }
                        if (Directory.Exists(match))
                        {
                            files.AddRange(FilesUnder(match));
                        }
                        else
                        {
                            files.Add(match);
                        }
                    }
                    return files;
                }
                if (Directory.Exists(full))
                {
                    files.AddRange(FilesUnder(full));
                }
                else if (File.Exists(full))
                {
                    files.Add(full);
                    if (File.Exists(full + ".meta"))
                    {
                        files.Add(full + ".meta");
                    }
                }
            }
            catch (Exception)
            {
                // A candidate that is not a usable path: nothing to copy.
            }
            return files;
        }

        /// <summary>Counts one Undo-covered UapOps tool call of the request.</summary>
        public static void NoteSceneOp()
        {
            lock (Gate)
            {
                if (_current != null)
                {
                    _current.SceneOps++;
                }
            }
        }

        /// <summary>Counts one call of a tool whose changes are not tracked.</summary>
        public static void NoteUntracked(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return;
            }
            lock (Gate)
            {
                if (_current != null)
                {
                    int count;
                    _current.Untracked.TryGetValue(toolName, out count);
                    _current.Untracked[toolName] = count + 1;
                }
            }
        }

        /// <summary>Fills in the session id once the CLI has reported it (a new conversation learns its id after the first send).</summary>
        public static void NoteSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }
            lock (Gate)
            {
                if (_current != null && string.IsNullOrEmpty(_current.SessionId))
                {
                    _current.SessionId = sessionId;
                }
            }
        }

        /// <summary>
        /// Closes the recording window: stamps what the turn left behind,
        /// saves the journal. Idempotent; called by UapTurnScope on every
        /// way a turn can end, a domain reload included.
        /// </summary>
        public static void EndTurn()
        {
            lock (Gate)
            {
                try
                {
                    CloseCurrentLocked();
                }
                catch (Exception ex)
                {
                    _current = null;
                    Debug.LogWarning("[AgentPanel] Closing the request checkpoint failed: " + ex.Message);
                }
                _topKnown = false;
            }
        }

        private static void CloseCurrentLocked()
        {
            if (_current == null)
            {
                return;
            }
            UapCheckpoint checkpoint = _current;
            UapProjectScan baseline = _baseline;
            _current = null;
            _baseline = null;
            _undoRecordsAtStart = -1;
            _rootsAtSnapshot = null;
            if (baseline != null)
            {
                // The turn ran a shell command or arbitrary editor code:
                // what appeared, changed or vanished since the "before"
                // picture, and has no entry of its own, is theirs.
                try
                {
                    Store.ApplyScanDiff(checkpoint, baseline, Store.Scan());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Comparing the project for the request checkpoint failed: " + ex.Message);
                }
            }
            Store.StampAfter(checkpoint);
            Store.Save(checkpoint);
        }

        private static void DropLastIfEmptyLocked()
        {
            string lastId = SessionState.GetString(KeyLastId, string.Empty);
            if (string.IsNullOrEmpty(lastId))
            {
                return;
            }
            UapCheckpoint last = Store.Load(lastId);
            if (last != null && !last.HasRevertibleContent)
            {
                Store.Delete(lastId);
            }
        }

        private static void AddDirtyScenes(UapCheckpoint checkpoint)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty && !string.IsNullOrEmpty(scene.path))
                {
                    checkpoint.DirtyScenes.Add(scene.path.Replace('\\', '/'));
                }
            }
        }

        // -- Capture ------------------------------------------------------------------

        /// <summary>
        /// Records <paramref name="path"/> as it is right now, before the
        /// caller changes it. For tools that write a file themselves
        /// instead of going through the AssetDatabase. No-op outside a
        /// recording window; never throws.
        /// </summary>
        public static void Capture(string path)
        {
            lock (Gate)
            {
                if (_current == null)
                {
                    return;
                }
                try
                {
                    if (Store.Capture(_current, path))
                    {
                        Store.Save(_current);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Recording '" + path + "' for the request checkpoint failed: " + ex.Message);
                }
            }
        }

        /// <summary>As <see cref="Capture"/> for several paths, with one journal save.</summary>
        public static void CaptureMany(IList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return;
            }
            lock (Gate)
            {
                if (_current == null)
                {
                    return;
                }
                try
                {
                    bool added = false;
                    for (int i = 0; i < paths.Count; i++)
                    {
                        added |= Store.Capture(_current, paths[i]);
                    }
                    if (added)
                    {
                        Store.Save(_current);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Recording files for the request checkpoint failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The CLI is about to write <paramref name="path"/> (pre-write
        /// hook). Recorded into the open window; when none is open -- a
        /// write that arrives after its turn was closed, e.g. from a
        /// background subagent -- it still goes into the conversation's
        /// last checkpoint, because the hook is the only chance to see the
        /// file before it changes.
        /// </summary>
        public static void CaptureFromAgent(string path)
        {
            lock (Gate)
            {
                try
                {
                    if (_current != null)
                    {
                        if (Store.Capture(_current, path))
                        {
                            Store.Save(_current);
                        }
                        return;
                    }
                    UapCheckpoint last = Store.Load(SessionState.GetString(KeyLastId, string.Empty));
                    if (last == null || last.IsReverted)
                    {
                        return;
                    }
                    if (Store.Capture(last, path))
                    {
                        Store.Save(last);
                        _topKnown = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Recording '" + path + "' for the request checkpoint failed: " + ex.Message);
                }
            }
        }

        /// <summary>Records <paramref name="path"/> as created by the request although it already exists (see UapCheckpointStore.MarkCreated).</summary>
        internal static void MarkCreated(string path, bool isDirectory)
        {
            lock (Gate)
            {
                if (_current == null)
                {
                    return;
                }
                try
                {
                    if (Store.MarkCreated(_current, path, isDirectory))
                    {
                        Store.Save(_current);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AgentPanel] Recording '" + path + "' for the request checkpoint failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Every file under <paramref name="folder"/> (an asset folder
        /// about to be deleted or moved), capped so one huge folder cannot
        /// stall the editor; the cap is logged when it bites.
        /// </summary>
        internal static List<string> FilesUnder(string folder)
        {
            var files = new List<string>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (files.Count >= MaxFilesPerFolder)
                    {
                        Debug.LogWarning("[AgentPanel] Request checkpoint: '" + folder + "' holds more than "
                            + MaxFilesPerFolder + " files; the rest are not recorded and cannot be reverted.");
                        break;
                    }
                    files.Add(file);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AgentPanel] Request checkpoint: listing '" + folder + "' failed: " + ex.Message);
            }
            return files;
        }

        // -- Revert -------------------------------------------------------------------

        /// <summary>
        /// Id of the one checkpoint that can be reverted now: the newest
        /// that is not reverted and recorded something. Reverting newest
        /// first is the rule that keeps a revert from colliding with the
        /// changes of a later request. Null when there is none.
        /// </summary>
        public static string TopRevertibleId
        {
            get
            {
                lock (Gate)
                {
                    if (!_topKnown)
                    {
                        _topId = null;
                        try
                        {
                            List<UapCheckpoint> all = Store.LoadAll();
                            for (int i = all.Count - 1; i >= 0; i--)
                            {
                                if (!all[i].IsReverted && all[i].HasRevertibleContent)
                                {
                                    _topId = all[i].Id;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("[AgentPanel] Reading the request checkpoints failed: " + ex.Message);
                        }
                        _topKnown = true;
                    }
                    return _topId;
                }
            }
        }

        /// <summary>What reverting <paramref name="id"/> would do. Null when it cannot be reverted now.</summary>
        public static UapRevertPreview Describe(string id)
        {
            if (string.IsNullOrEmpty(id) || id != TopRevertibleId)
            {
                return null;
            }
            lock (Gate)
            {
                UapCheckpoint checkpoint = Store.Load(id);
                if (checkpoint == null)
                {
                    return null;
                }
                var preview = new UapRevertPreview { Checkpoint = checkpoint };
                var acted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < checkpoint.Entries.Count; i++)
                {
                    UapCheckpointEntry entry = checkpoint.Entries[i];
                    // Folders and the .meta of a listed asset are not "files"
                    // to the reader (UapCheckpointPolicy.IsDisplayFile).
                    if (!UapCheckpointPolicy.IsDisplayFile(checkpoint, entry.Path))
                    {
                        continue;
                    }
                    if (!entry.Existed)
                    {
                        preview.FilesToRemove++;
                        preview.RemovePaths.Add(entry.Path);
                        acted.Add(entry.Path);
                    }
                    else if (entry.Blob != null)
                    {
                        preview.FilesToRestore++;
                        preview.RestorePaths.Add(entry.Path);
                        acted.Add(entry.Path);
                    }
                    else if (entry.Skipped == UapCheckpointStore.SkippedUntracked)
                    {
                        preview.FilesChangedUntracked++;
                        preview.LeftAsIsPaths.Add(entry.Path);
                    }
                    else
                    {
                        preview.FilesNotStored++;
                        preview.LeftAsIsPaths.Add(entry.Path);
                    }
                }
                // "Edited after the request, and the revert overwrites that":
                // only files the revert actually writes or deletes.
                List<string> changed = Store.ChangedSince(checkpoint);
                for (int i = 0; i < changed.Count; i++)
                {
                    if (acted.Contains(changed[i]))
                    {
                        preview.ChangedSince++;
                    }
                }
                bool undoAlive = UndoHistoryMayHold(checkpoint);
                preview.SceneUndo = checkpoint.HasSceneEdits && undoAlive;
                preview.SceneUndoUnavailable = checkpoint.HasSceneEdits && !undoAlive;
                preview.Untracked = UapCheckpointPolicy.DescribeUntracked(checkpoint.Untracked);
                return preview;
            }
        }

        private static bool UndoHistoryMayHold(UapCheckpoint checkpoint)
        {
            if (checkpoint.UndoGroups.Count == 0 || checkpoint.EditorSessionId != LaunchId)
            {
                return false;
            }
            return Undo.GetCurrentGroup() >= MinGroup(checkpoint);
        }

        private static int MinGroup(UapCheckpoint checkpoint)
        {
            int min = int.MaxValue;
            for (int i = 0; i < checkpoint.UndoGroups.Count; i++)
            {
                min = Math.Min(min, checkpoint.UndoGroups[i]);
            }
            return min;
        }

        /// <summary>
        /// Reverts the request: its scene edits through Unity's Undo, then
        /// its files from the journal. Does NOT refresh the AssetDatabase
        /// -- the caller writes its transcript note first and then calls
        /// <see cref="RefreshAfterRevert"/>, because restoring a script
        /// starts a compile and a domain reload.
        ///
        /// A scene that is open and whose file the request saved over is
        /// handled by what it looked like when the request started: clean
        /// then -> the file is restored and the scene re-read from it;
        /// already holding unsaved changes then -> the file is left alone
        /// (restoring it would throw those changes away) and only Undo
        /// reverts it in memory.
        /// </summary>
        public static UapRevertOutcome Revert(string id)
        {
            var outcome = new UapRevertOutcome();
            if (IsRecording)
            {
                outcome.Refusal = UapRevertRefusal.Busy;
                return outcome;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                outcome.Refusal = UapRevertRefusal.PlayMode;
                return outcome;
            }
            if (EditorApplication.isCompiling)
            {
                outcome.Refusal = UapRevertRefusal.Compiling;
                return outcome;
            }
            if (string.IsNullOrEmpty(id) || id != TopRevertibleId)
            {
                outcome.Refusal = UapRevertRefusal.NotAvailable;
                return outcome;
            }
            UapCheckpoint checkpoint;
            lock (Gate)
            {
                checkpoint = Store.Load(id);
            }
            if (checkpoint == null || checkpoint.IsReverted)
            {
                outcome.Refusal = UapRevertRefusal.NotAvailable;
                return outcome;
            }
            outcome.Checkpoint = checkpoint;

            if (checkpoint.HasSceneEdits)
            {
                if (UndoHistoryMayHold(checkpoint))
                {
                    try
                    {
                        Undo.RevertAllDownToGroup(MinGroup(checkpoint));
                        outcome.SceneUndone = true;
                    }
                    catch (Exception ex)
                    {
                        outcome.SceneUndoUnavailable = true;
                        Debug.LogWarning("[AgentPanel] Undoing the request's scene edits failed: " + ex.Message);
                    }
                }
                else
                {
                    outcome.SceneUndoUnavailable = true;
                }
            }

            var openScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                {
                    openScenes.Add(scene.path.Replace('\\', '/'));
                }
            }
            var dirtyAtStart = new HashSet<string>(checkpoint.DirtyScenes, StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                outcome.Files = Store.Revert(checkpoint, delegate (string relative)
                {
                    return openScenes.Contains(relative) && dirtyAtStart.Contains(relative);
                });
                _topKnown = false;
            }

            // A single open scene whose file was just restored is re-read
            // from it, so memory and disk agree and Unity has no "modified
            // externally" question to ask. With several scenes open the
            // reload is left to that question: re-opening one of them
            // additively is not something this code can do blind.
            if (SceneManager.sceneCount == 1)
            {
                string only = SceneManager.GetSceneAt(0).path;
                only = string.IsNullOrEmpty(only) ? null : only.Replace('\\', '/');
                if (only != null && ContainsIgnoreCase(outcome.Files.Restored, only))
                {
                    try
                    {
                        EditorSceneManager.OpenScene(only, OpenSceneMode.Single);
                        outcome.ScenesReloaded.Add(only);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[AgentPanel] Re-opening '" + only + "' after the revert failed: " + ex.Message);
                    }
                }
            }
            return outcome;
        }

        /// <summary>Lets Unity pick up the files a revert rewrote (the caller's last step).</summary>
        public static void RefreshAfterRevert()
        {
            AssetDatabase.Refresh();
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // -- Test seams ------------------------------------------------------------------

        /// <summary>Test-only: points the recorder at a store over a throwaway folder (null restores the project's own) and forgets all in-memory state.</summary>
        internal static void ResetForTests(UapCheckpointStore storeOverride)
        {
            lock (Gate)
            {
                _storeOverride = storeOverride;
                _current = null;
                _baseline = null;
                _undoRecordsAtStart = -1;
                _rootsAtSnapshot = null;
                _topKnown = false;
                _topId = null;
                SessionState.EraseString(KeyLastId);
            }
        }
    }

    /// <summary>
    /// Sees every asset Unity is about to save, delete, move or create and
    /// hands it to the open recording window. This is what makes the
    /// checkpoint independent of which tool did the work: uap_asset_create,
    /// a Pro tool, a menu item or arbitrary editor code all reach the disk
    /// through these four callbacks when they use the AssetDatabase. Does
    /// nothing outside a recording window, and never vetoes or takes over
    /// the operation (it always answers "not handled, carry on").
    /// </summary>
    internal sealed class UapCheckpointAssetWatcher : UnityEditor.AssetModificationProcessor
    {
        private const string MetaSuffix = ".meta";

        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (paths != null && paths.Length > 0 && UapCheckpointRecorder.IsRecording)
            {
                UapCheckpointRecorder.CaptureMany(paths);
            }
            return paths;
        }

        private static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            if (!string.IsNullOrEmpty(assetPath) && UapCheckpointRecorder.IsRecording)
            {
                var paths = new List<string>();
                if (Directory.Exists(assetPath))
                {
                    paths.AddRange(UapCheckpointRecorder.FilesUnder(assetPath));
                }
                else
                {
                    paths.Add(assetPath);
                }
                paths.Add(assetPath + MetaSuffix);
                UapCheckpointRecorder.CaptureMany(paths);
            }
            return AssetDeleteResult.DidNotDelete;
        }

        private static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath)
        {
            if (!string.IsNullOrEmpty(sourcePath) && !string.IsNullOrEmpty(destinationPath)
                && UapCheckpointRecorder.IsRecording)
            {
                var paths = new List<string>();
                if (Directory.Exists(sourcePath))
                {
                    List<string> files = UapCheckpointRecorder.FilesUnder(sourcePath);
                    string sourceRoot = Path.GetFullPath(sourcePath);
                    string destinationRoot = Path.GetFullPath(destinationPath);
                    for (int i = 0; i < files.Count; i++)
                    {
                        string full = Path.GetFullPath(files[i]);
                        paths.Add(full);
                        if (full.Length > sourceRoot.Length)
                        {
                            // The same file at its new place: it does not
                            // exist yet, so this records "created" (and the
                            // folders above it).
                            paths.Add(destinationRoot + full.Substring(sourceRoot.Length));
                        }
                    }
                }
                else
                {
                    paths.Add(sourcePath);
                    paths.Add(destinationPath);
                }
                paths.Add(sourcePath + MetaSuffix);
                paths.Add(destinationPath + MetaSuffix);
                UapCheckpointRecorder.CaptureMany(paths);
            }
            return AssetMoveResult.DidNotMove;
        }

        private static void OnWillCreateAsset(string assetName)
        {
            if (string.IsNullOrEmpty(assetName) || !UapCheckpointRecorder.IsRecording)
            {
                return;
            }
            if (!assetName.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase))
            {
                UapCheckpointRecorder.Capture(assetName);
                return;
            }
            // Unity writes the .meta of a new asset after the asset itself
            // is on disk, so the asset "exists" here although the request
            // created it: a file with no .meta until now is a new file.
            UapCheckpointRecorder.MarkCreated(assetName, false);
            string asset = assetName.Substring(0, assetName.Length - MetaSuffix.Length);
            if (Directory.Exists(asset))
            {
                UapCheckpointRecorder.MarkCreated(asset, true);
            }
            else if (File.Exists(asset))
            {
                UapCheckpointRecorder.MarkCreated(asset, false);
            }
            else
            {
                // Announced before it is on disk: recorded as created now
                // (a path that turns out to be a folder is handled as one
                // when the request is reverted).
                UapCheckpointRecorder.Capture(asset);
            }
        }
    }
}

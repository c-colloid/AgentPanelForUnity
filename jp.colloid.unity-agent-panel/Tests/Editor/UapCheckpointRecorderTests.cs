using System;
using System.Collections.Generic;
using System.IO;
using Colloid.AgentPanel.Model;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// Request checkpoints, the Unity half (design note docs/design-notes/
    /// 2026-11-23-request-checkpoints.md): UapCheckpointRecorder's
    /// recording window, the "newest first" rule, the scene part of a
    /// revert through Unity's Undo, and the asset watcher. The journal
    /// itself (capture / revert / prune as pure file IO) is covered by the
    /// smoke tier, ci/SmokeTests.
    ///
    /// Every test points the recorder at a store under the temp folder, so
    /// nothing here touches the project's real Library/AgentPanel store.
    /// All fixture literals are strict ASCII, per this suite's convention.
    /// </summary>
    [TestFixture]
    public class UapCheckpointRecorderTests
    {
        private string _root;
        private string _project;
        private UapCheckpointStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "uap-ckpt-tests-" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "Project");
            Directory.CreateDirectory(Path.Combine(_project, "Assets"));
            _store = new UapCheckpointStore(Path.Combine(_root, "Store"), _project, true);
            UapCheckpointRecorder.ResetForTests(_store);
        }

        [TearDown]
        public void TearDown()
        {
            if (UapTurnScope.IsActive)
            {
                UapTurnScope.EndIfActive();
            }
            UapCheckpointRecorder.EndTurn();
            UapCheckpointRecorder.ResetForTests(null);
            try
            {
                Directory.Delete(_root, true);
            }
            catch (Exception)
            {
            }
        }

        private string Write(string relative, string content)
        {
            string path = Path.Combine(_project, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>One whole request: open, change one existing file and create another, close.</summary>
        private string RunRequest(string title, string existingRelative, string createdRelative)
        {
            string id = UapCheckpointRecorder.BeginRequest(title, "sess");
            Assert.IsNotNull(id, "the journal folder must be writable");
            string existing = Path.Combine(_project, existingRelative.Replace('/', Path.DirectorySeparatorChar));
            UapCheckpointRecorder.Capture(existing);
            File.WriteAllText(existing, "changed by " + title);
            string created = Path.Combine(_project, createdRelative.Replace('/', Path.DirectorySeparatorChar));
            UapCheckpointRecorder.Capture(created);
            Directory.CreateDirectory(Path.GetDirectoryName(created));
            File.WriteAllText(created, "created by " + title);
            UapCheckpointRecorder.EndTurn();
            return id;
        }

        [Test]
        public void Request_IsRecorded_AndRevertPutsEverythingBack()
        {
            string existing = Write("Assets/A.txt", "original");
            string id = RunRequest("first", "Assets/A.txt", "Assets/New/B.txt");

            Assert.IsFalse(UapCheckpointRecorder.IsRecording, "EndTurn closes the window");
            Assert.AreEqual(id, UapCheckpointRecorder.TopRevertibleId);
            UapRevertPreview preview = UapCheckpointRecorder.Describe(id);
            Assert.IsNotNull(preview);
            Assert.AreEqual(1, preview.FilesToRestore);
            Assert.AreEqual(1, preview.FilesToRemove);
            Assert.AreEqual(0, preview.ChangedSince, "nothing touched the files since the turn ended");
            Assert.IsFalse(preview.SceneUndo);

            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

            Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
            Assert.AreEqual("original", File.ReadAllText(existing));
            Assert.IsFalse(File.Exists(Path.Combine(_project, "Assets", "New", "B.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_project, "Assets", "New")),
                "a folder the request created goes with its last file");
            Assert.IsNull(UapCheckpointRecorder.TopRevertibleId, "a reverted request is not offered again");
        }

        [Test]
        public void CaptureOutsideAWindow_RecordsNothing()
        {
            string existing = Write("Assets/A.txt", "original");

            UapCheckpointRecorder.Capture(existing);

            Assert.AreEqual(0, _store.LoadAll().Count);
        }

        [Test]
        public void ContinueRequest_ReopensTheSameCheckpoint_SoAReloadSplitRequestRevertsAsOne()
        {
            string first = Write("Assets/A.txt", "A0");
            string second = Write("Assets/B.txt", "B0");
            string id = UapCheckpointRecorder.BeginRequest("split by a reload", "sess");
            UapCheckpointRecorder.Capture(first);
            File.WriteAllText(first, "A1");
            // The domain reload: the window closes, the journal stays.
            UapCheckpointRecorder.EndTurn();

            Assert.IsTrue(UapCheckpointRecorder.ContinueRequest(), "the continuation turn re-opens it");
            Assert.IsTrue(UapCheckpointRecorder.IsRecording);
            UapCheckpointRecorder.Capture(second);
            File.WriteAllText(second, "B1");
            // Touched again in the second turn: the entry from the first
            // turn (the pre-request content) must win.
            UapCheckpointRecorder.Capture(first);
            File.WriteAllText(first, "A2");
            UapCheckpointRecorder.EndTurn();

            List<UapCheckpoint> all = _store.LoadAll();
            Assert.AreEqual(1, all.Count, "one request, one checkpoint");
            Assert.AreEqual(2, all[0].Turns);
            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);
            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual("A0", File.ReadAllText(first));
            Assert.AreEqual("B0", File.ReadAllText(second));
        }

        [Test]
        public void ContinueRequest_WithNothingToContinue_RecordsNothing()
        {
            Assert.IsFalse(UapCheckpointRecorder.ContinueRequest());
            Assert.IsFalse(UapCheckpointRecorder.IsRecording);
        }

        [Test]
        public void BeginRequest_DropsThePreviousCheckpoint_WhenItRecordedNothing()
        {
            string empty = UapCheckpointRecorder.BeginRequest("just a question", "sess");
            UapCheckpointRecorder.EndTurn();
            Assert.IsNull(UapCheckpointRecorder.TopRevertibleId, "an empty request offers nothing to revert");

            Write("Assets/A.txt", "original");
            string real = RunRequest("real work", "Assets/A.txt", "Assets/B.txt");

            List<UapCheckpoint> all = _store.LoadAll();
            Assert.AreEqual(1, all.Count, "the empty checkpoint must not use up one of the kept slots");
            Assert.AreEqual(real, all[0].Id);
            Assert.AreNotEqual(empty, real);
        }

        [Test]
        public void OnlyTheNewestRequest_CanBeReverted_ThenTheOneBeforeIt()
        {
            Write("Assets/A.txt", "A0");
            Write("Assets/B.txt", "B0");
            string older = RunRequest("older", "Assets/A.txt", "Assets/OlderNew.txt");
            string newer = RunRequest("newer", "Assets/B.txt", "Assets/NewerNew.txt");

            Assert.AreEqual(newer, UapCheckpointRecorder.TopRevertibleId);
            Assert.IsNull(UapCheckpointRecorder.Describe(older));
            UapRevertOutcome refused = UapCheckpointRecorder.Revert(older);
            Assert.AreEqual(UapRevertRefusal.NotAvailable, refused.Refusal);
            Assert.AreEqual("changed by older", File.ReadAllText(Path.Combine(_project, "Assets", "A.txt")),
                "a refused revert changes nothing");

            Assert.IsTrue(UapCheckpointRecorder.Revert(newer).Ok);
            Assert.AreEqual(older, UapCheckpointRecorder.TopRevertibleId, "the one before it is next");
            Assert.IsTrue(UapCheckpointRecorder.Revert(older).Ok);
            Assert.AreEqual("A0", File.ReadAllText(Path.Combine(_project, "Assets", "A.txt")));
        }

        [Test]
        public void Revert_WhileATurnIsBeingRecorded_IsRefused()
        {
            Write("Assets/A.txt", "A0");
            string done = RunRequest("done", "Assets/A.txt", "Assets/B.txt");
            UapCheckpointRecorder.BeginRequest("running", "sess");

            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(done);

            Assert.AreEqual(UapRevertRefusal.Busy, outcome.Refusal);
        }

        [Test]
        public void CaptureFromAgent_AfterTheWindowClosed_StillLandsInTheLastCheckpoint()
        {
            string late = Write("Assets/Late.txt", "before");
            Write("Assets/A.txt", "A0");
            string id = RunRequest("with a late write", "Assets/A.txt", "Assets/B.txt");

            // A background subagent's Write arriving after the turn's result.
            UapCheckpointRecorder.CaptureFromAgent(late);
            File.WriteAllText(late, "after");

            Assert.IsTrue(UapCheckpointRecorder.Revert(id).Ok);
            Assert.AreEqual("before", File.ReadAllText(late));
        }

        [Test]
        public void UntrackedToolsAndSessionId_AreKeptWithTheRequest()
        {
            string id = UapCheckpointRecorder.BeginRequest("shell work", string.Empty);
            UapCheckpointRecorder.NoteUntracked("Bash");
            UapCheckpointRecorder.NoteUntracked("Bash");
            UapCheckpointRecorder.NoteUntracked("uap_execute_code");
            UapCheckpointRecorder.NoteSessionId("sess-late");
            UapCheckpointRecorder.EndTurn();

            UapCheckpoint loaded = _store.Load(id);
            Assert.AreEqual(2, loaded.Untracked["Bash"]);
            Assert.AreEqual("sess-late", loaded.SessionId);
            Assert.AreEqual("Bash x2, uap_execute_code", UapCheckpointPolicy.DescribeUntracked(loaded.Untracked));
            Assert.IsNull(UapCheckpointRecorder.TopRevertibleId,
                "untracked tools alone give a revert nothing to do");
        }

        [Test]
        public void SceneEdits_OfTheRequest_AreUndoneThroughUnitysUndo()
        {
            string id = UapCheckpointRecorder.BeginRequest("make an object", "sess");
            GameObject created = null;
            try
            {
                // The turn scope reports its Undo group to the open window,
                // exactly as AgentHub.SendUserMessage orders the two calls.
                UapTurnScope.BeginIfNeeded();
                created = new GameObject("UapCheckpointRecorderTests_Object");
                Undo.RegisterCreatedObjectUndo(created, "Create test object");
                UapCheckpointRecorder.NoteSceneOp();
                UapTurnScope.EndIfActive();

                Assert.IsFalse(UapCheckpointRecorder.IsRecording, "the turn scope closes the window");
                UapCheckpoint loaded = _store.Load(id);
                Assert.AreEqual(1, loaded.UndoGroups.Count);
                Assert.AreEqual(1, loaded.SceneOps);
                UapRevertPreview preview = UapCheckpointRecorder.Describe(id);
                Assert.IsTrue(preview.SceneUndo);
                Assert.IsFalse(preview.SceneUndoUnavailable);

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

                Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
                Assert.IsTrue(outcome.SceneUndone);
                Assert.IsTrue(created == null, "the object the request created must be gone after the revert");
            }
            finally
            {
                if (created != null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                }
            }
        }

        [Test]
        public void SceneEdits_RecordedByAnotherEditorLaunch_AreReportedAsNotUndoable()
        {
            string id = UapCheckpointRecorder.BeginRequest("before a restart", "sess");
            UapCheckpointRecorder.NoteUndoGroup(Undo.GetCurrentGroup());
            UapCheckpointRecorder.NoteSceneOp();
            UapCheckpointRecorder.EndTurn();
            // Same journal, read back by an editor that was restarted.
            UapCheckpoint stored = _store.Load(id);
            stored.EditorSessionId = stored.EditorSessionId + 1;
            _store.Save(stored);

            UapRevertPreview preview = UapCheckpointRecorder.Describe(id);
            Assert.IsFalse(preview.SceneUndo);
            Assert.IsTrue(preview.SceneUndoUnavailable);
            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);
            Assert.IsTrue(outcome.Ok);
            Assert.IsFalse(outcome.SceneUndone);
            Assert.IsTrue(outcome.SceneUndoUnavailable);
        }

        // -- Untracked work: shell commands and arbitrary editor code (design note section 10) --

        [Test]
        public void ShellCommand_FilesItNamesAreCopiedFirst_AndWhatItCreatesIsFoundWhenTheTurnEnds()
        {
            string overwritten = Write("Assets/Data.txt", "before the command");
            string deleted = Write("Assets/Old/Gone.txt", "to be deleted");
            string unnamed = Write("Assets/Unnamed.txt", "changed by a script the command runs");
            string id = UapCheckpointRecorder.BeginRequest("run a shell command", "sess");

            // The pre-run hook: the command's text, before it runs.
            UapCheckpointRecorder.BeforeShellCommand("echo new > Assets/Data.txt && rm -rf Assets/Old && ./build.sh");
            // ...and then the command does its work.
            File.WriteAllText(overwritten, "after the command");
            Directory.Delete(Path.Combine(_project, "Assets", "Old"), true);
            File.WriteAllText(unnamed, "changed, and longer than before");
            string made = Write("Assets/Out/result.txt", "made by build.sh");
            UapCheckpointRecorder.EndTurn();

            UapCheckpoint loaded = _store.Load(id);
            Assert.IsNotNull(loaded.Find("Assets/Data.txt").Blob, "a file the command names is copied before it runs");
            Assert.IsNotNull(loaded.Find("Assets/Old/Gone.txt").Blob, "a folder the command removes gives its files");
            Assert.IsFalse(loaded.Find("Assets/Out/result.txt").Existed, "a file that appeared is recorded as created");
            Assert.AreEqual(UapCheckpointStore.SkippedUntracked, loaded.Find("Assets/Unnamed.txt").Skipped,
                "a change nothing announced is at least named");
            UapRevertPreview preview = UapCheckpointRecorder.Describe(id);
            Assert.AreEqual(1, preview.FilesChangedUntracked);
            Assert.AreEqual(0, preview.FilesNotStored);

            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

            Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
            Assert.AreEqual("before the command", File.ReadAllText(overwritten));
            Assert.AreEqual("to be deleted", File.ReadAllText(deleted));
            Assert.IsFalse(File.Exists(made));
            Assert.IsFalse(Directory.Exists(Path.Combine(_project, "Assets", "Out")));
            CollectionAssert.Contains(outcome.Files.NotStored, "Assets/Unnamed.txt");
        }

        [Test]
        public void ShellCommand_AGuessedPathThatDoesNotExist_IsNeverRecordedAsCreated()
        {
            string id = UapCheckpointRecorder.BeginRequest("guess", "sess");

            // "cd" makes the guess wrong: nothing is written at Assets/x.txt.
            UapCheckpointRecorder.BeforeShellCommand("cd Elsewhere && echo hi > Assets/x.txt");
            UapCheckpointRecorder.EndTurn();

            Assert.IsNull(_store.Load(id).Find("Assets/x.txt"),
                "only the before/after comparison may say a file was created");
        }

        [Test]
        public void EditorCode_FilesWrittenWithoutTheAssetDatabase_AreFoundWhenTheTurnEnds()
        {
            string id = UapCheckpointRecorder.BeginRequest("run a snippet", "sess");

            UapCheckpointRecorder.BeforeUntrackedEditorWork();
            string made = Write("Assets/FromSnippet.txt", "File.WriteAllText in a snippet");
            UapCheckpointRecorder.EndTurn();

            Assert.IsFalse(_store.Load(id).Find("Assets/FromSnippet.txt").Existed);
            Assert.IsTrue(UapCheckpointRecorder.Revert(id).Ok);
            Assert.IsFalse(File.Exists(made));
        }

        [Test]
        public void ATurnWithNoUntrackedWork_IsNotScanned()
        {
            string id = UapCheckpointRecorder.BeginRequest("only tracked tools", "sess");

            // Something outside the request writes a file meanwhile.
            Write("Assets/NotOurs.txt", "the user dropped this in");
            UapCheckpointRecorder.EndTurn();

            Assert.IsNull(_store.Load(id).Find("Assets/NotOurs.txt"),
                "without a shell command or editor code there is no before/after comparison to blame it on");
        }

        [Test]
        public void UndoRecordsLeftByTheTurn_AreObserved_WithoutAnyToolSayingSo()
        {
            if (UapUndoProbe.TryCount() < 0)
            {
                Assert.Ignore("This Unity version does not expose the Undo record list the probe reads.");
            }
            string id = UapCheckpointRecorder.BeginRequest("a snippet that calls Undo itself", "sess");
            GameObject created = null;
            try
            {
                UapTurnScope.BeginIfNeeded();
                // What a uap_execute_code snippet or a menu item does: an
                // Undo record, and no NoteSceneOp from a UapOps tool.
                created = new GameObject("UapCheckpointRecorderTests_SnippetObject");
                Undo.RegisterCreatedObjectUndo(created, "Create from snippet");
                UapTurnScope.EndIfActive();

                UapCheckpoint loaded = _store.Load(id);
                Assert.AreEqual(0, loaded.SceneOps);
                Assert.IsTrue(loaded.UndoRecorded, "the turn's Undo records must be seen from the Undo history itself");
                Assert.AreEqual(id, UapCheckpointRecorder.TopRevertibleId);

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

                Assert.IsTrue(outcome.SceneUndone);
                Assert.IsTrue(created == null, "the object the snippet created must be gone after the revert");
            }
            finally
            {
                if (created != null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                }
            }
        }

        [Test]
        public void EditorCodeThatNeverCallsUndo_IsStillTakenBack_ByTheSceneSnapshot()
        {
            GameObject root = new GameObject("UapCheckpointRecorderTests_SnapshotRoot");
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform);
            var doomed = new GameObject("Doomed");
            doomed.transform.SetParent(root.transform);
            try
            {
                string id = UapCheckpointRecorder.BeginRequest("a snippet with no Undo calls", "sess");
                UapTurnScope.BeginIfNeeded();

                Assert.IsTrue(UapCheckpointRecorder.BeforeUntrackedEditorWork(), "the scene snapshot must be in place");
                // The snippet: no Undo anywhere.
                child.transform.localPosition = new Vector3(1f, 2f, 3f);
                root.name = "Renamed";
                child.AddComponent<BoxCollider>();
                UnityEngine.Object.DestroyImmediate(doomed);
                var added = new GameObject("AddedChild");
                added.transform.SetParent(root.transform);
                new GameObject("UapCheckpointRecorderTests_NewRoot");
                UapTurnScope.EndIfActive();

                Assert.IsTrue(_store.Load(id).UndoRecorded, "the snapshot is an Undo record of the turn");
                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

                Assert.IsTrue(outcome.SceneUndone);
                Assert.IsTrue(root != null);
                Assert.AreEqual("UapCheckpointRecorderTests_SnapshotRoot", root.name);
                Transform restored = root.transform.Find("Child");
                Assert.IsNotNull(restored);
                Assert.AreEqual(Vector3.zero, restored.localPosition);
                Assert.IsNull(restored.GetComponent<BoxCollider>(), "a component the snippet added is gone");
                Assert.IsNotNull(root.transform.Find("Doomed"), "an object the snippet destroyed is back");
                Assert.IsNull(root.transform.Find("AddedChild"), "a child the snippet added is gone");
                Assert.IsNull(GameObject.Find("UapCheckpointRecorderTests_NewRoot"),
                    "a new root object is in no snapshot; it is registered as created when the turn ends");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
                GameObject stray = GameObject.Find("UapCheckpointRecorderTests_NewRoot");
                if (stray != null)
                {
                    UnityEngine.Object.DestroyImmediate(stray);
                }
            }
        }

        [Test]
        public void ARootAToolAlreadyRegistered_IsUndoneOnce_WithoutErrors()
        {
            string id = UapCheckpointRecorder.BeginRequest("a tool and a snippet in one turn", "sess");
            GameObject byTool = null;
            try
            {
                UapTurnScope.BeginIfNeeded();
                UapCheckpointRecorder.BeforeUntrackedEditorWork();
                // A UapOps tool creates a root and registers it itself; the
                // end of the turn then registers it a second time.
                byTool = new GameObject("UapCheckpointRecorderTests_ByTool");
                Undo.RegisterCreatedObjectUndo(byTool, "Create by tool");
                UapCheckpointRecorder.NoteSceneOp();
                UapTurnScope.EndIfActive();

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);

                Assert.IsTrue(outcome.SceneUndone);
                Assert.IsTrue(byTool == null);
                // An error logged by the second, redundant record would fail
                // this test by itself (Unity Test Framework's log check).
            }
            finally
            {
                if (byTool != null)
                {
                    UnityEngine.Object.DestroyImmediate(byTool);
                }
            }
        }

        [Test]
        public void SceneSnapshot_IsNotTaken_OutsideARecordingWindow()
        {
            Assert.IsFalse(UapCheckpointRecorder.BeforeUntrackedEditorWork());
        }

        [Test]
        public void ATurnThatRecordedNoUndo_IsNotMarked()
        {
            string id = UapCheckpointRecorder.BeginRequest("no scene work", "sess");
            Write("Assets/A.txt", "x");
            UapTurnScope.BeginIfNeeded();
            UapCheckpointRecorder.Capture(Path.Combine(_project, "Assets", "A.txt"));
            UapTurnScope.EndIfActive();

            UapCheckpoint loaded = _store.Load(id);
            Assert.IsFalse(loaded.UndoRecorded);
            Assert.IsFalse(loaded.HasSceneEdits);
        }

        /// <summary>
        /// The asset watcher against the real AssetDatabase: a folder and a
        /// material created through it during a request are recorded
        /// without any tool telling the recorder, and a revert removes
        /// them with their .meta files.
        /// </summary>
        [Test]
        public void AssetsCreatedThroughTheAssetDatabase_AreRecorded_AndRemovedOnRevert()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            var store = new UapCheckpointStore(Path.Combine(_root, "RealProjectStore"), projectRoot, true);
            UapCheckpointRecorder.ResetForTests(store);
            string folderName = "__UapCheckpointTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string folder = "Assets/" + folderName;
            string asset = folder + "/Probe.mat";
            try
            {
                string id = UapCheckpointRecorder.BeginRequest("create assets", "sess");
                AssetDatabase.CreateFolder("Assets", folderName);
                AssetDatabase.CreateAsset(new Material(Shader.Find("Hidden/InternalErrorShader")), asset);
                AssetDatabase.SaveAssets();
                UapCheckpointRecorder.EndTurn();
                Assert.IsTrue(File.Exists(Path.Combine(projectRoot, asset)), "sanity: the asset is on disk");

                UapCheckpoint loaded = store.Load(id);
                Assert.IsNotNull(loaded.Find(asset), "the created asset must be in the journal; entries: " + Describe(loaded));
                Assert.IsFalse(loaded.Find(asset).Existed);

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);
                UapCheckpointRecorder.RefreshAfterRevert();

                Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, asset)));
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, asset + ".meta")));
                Assert.IsFalse(Directory.Exists(Path.Combine(projectRoot, folder)),
                    "the folder the request created must go too; entries: " + Describe(loaded));
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, folder + ".meta")));
            }
            finally
            {
                UapCheckpointRecorder.EndTurn();
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        /// <summary>
        /// A delete through the AssetDatabase is recorded before the file
        /// goes, .meta included, so the revert brings the asset back under
        /// the same GUID.
        /// </summary>
        [Test]
        public void AssetDeletedThroughTheAssetDatabase_ComesBackWithItsGuid()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            var store = new UapCheckpointStore(Path.Combine(_root, "RealProjectStore"), projectRoot, true);
            string asset = "Assets/__UapCheckpointTest_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mat";
            try
            {
                AssetDatabase.CreateAsset(new Material(Shader.Find("Hidden/InternalErrorShader")), asset);
                AssetDatabase.SaveAssets();
                string guid = AssetDatabase.AssetPathToGUID(asset);
                Assert.IsFalse(string.IsNullOrEmpty(guid));

                UapCheckpointRecorder.ResetForTests(store);
                string id = UapCheckpointRecorder.BeginRequest("delete an asset", "sess");
                Assert.IsTrue(AssetDatabase.DeleteAsset(asset));
                UapCheckpointRecorder.EndTurn();
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, asset)));

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);
                UapCheckpointRecorder.RefreshAfterRevert();

                Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
                Assert.IsTrue(File.Exists(Path.Combine(projectRoot, asset)));
                Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(asset), "restored with its .meta, so the GUID is the same");
            }
            finally
            {
                UapCheckpointRecorder.EndTurn();
                if (File.Exists(Path.Combine(projectRoot, asset)))
                {
                    AssetDatabase.DeleteAsset(asset);
                }
            }
        }

        private static string Describe(UapCheckpoint checkpoint)
        {
            var parts = new List<string>();
            for (int i = 0; i < checkpoint.Entries.Count; i++)
            {
                UapCheckpointEntry entry = checkpoint.Entries[i];
                parts.Add(entry.Path + (entry.IsDirectory ? "/" : string.Empty) + (entry.Existed ? "(existed)" : "(created)"));
            }
            return string.Join(", ", parts.ToArray());
        }

        // -- The pieces around the recorder -----------------------------------------

        [Test]
        public void CheckpointId_OnAUserMessage_SurvivesTheSessionCache()
        {
            string path = Path.Combine(_root, "SessionCache.json");
            var cache = new SessionCacheFile(path, null);
            var session = new ChatSession();
            var message = new ChatMessage { role = ChatMessage.RoleUser, checkpointId = "0000000000000000001-abcdef" };
            message.Add(ChatMessageBlock.MakeText("do the thing"));
            session.AddMessage(message);
            var plain = new ChatMessage { role = ChatMessage.RoleUser };
            plain.Add(ChatMessageBlock.MakeText("a question"));
            session.AddMessage(plain);

            cache.Save(session);
            ChatSession loaded = cache.Load();

            Assert.AreEqual("0000000000000000001-abcdef", loaded.messages[0].checkpointId);
            Assert.AreEqual(string.Empty, loaded.messages[1].checkpointId);
            Assert.IsFalse(File.ReadAllText(path).Contains("\"checkpointId\":\"\""),
                "a message without a checkpoint writes no key at all");
        }

        [Test]
        public void RequestCheckpoints_DefaultsOn_AndIsASpawnSetting()
        {
            var settings = new PanelSettings();
            Assert.IsTrue(settings.requestCheckpoints);
            var changed = new PanelSettings { requestCheckpoints = false };
            Assert.IsTrue(SettingsChangeDetector.RequiresReconnect(settings, changed),
                "the pre-write hook is registered in the handshake, once per spawn");
        }

        [Test]
        public void ConfirmCard_SaysWhatHappens_ThenOnlyTheCaveatsThatApply()
        {
            var preview = new UapRevertPreview
            {
                Checkpoint = new UapCheckpoint(true) { Title = "tidy the scene" },
                FilesToRestore = 3,
                FilesToRemove = 2,
                FilesNotStored = 1,
                FilesChangedUntracked = 4,
                ChangedSince = 1,
                SceneUndo = true,
                Untracked = "Bash x2"
            };

            var effects = Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertEffectLines(preview);
            var caveats = Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertCaveatLines(preview);

            Assert.AreEqual(2, effects.Count, "files, then the scene");
            StringAssert.Contains("3", effects[0]);
            StringAssert.Contains("2", effects[0]);
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.RevertConfirmSceneLine, effects[1]);
            Assert.AreEqual(5, caveats.Count);
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.RevertConfirmManualEditsLine, caveats[0],
                "undoing the scene also undoes later manual edits: the first thing to know");
            StringAssert.Contains("Bash x2", caveats[caveats.Count - 1]);
        }

        [Test]
        public void ConfirmCard_WordsTheFilesLine_ByWhatApplies_AndHasNoCaveatsWhenNoneApply()
        {
            var restoreOnly = new UapRevertPreview { FilesToRestore = 4 };
            var deleteOnly = new UapRevertPreview { FilesToRemove = 2 };

            Assert.AreEqual(string.Format(Colloid.AgentPanel.UI.L10n.S.RevertConfirmRestoreOnlyFmt, 4),
                Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertEffectLines(restoreOnly)[0]);
            Assert.AreEqual(string.Format(Colloid.AgentPanel.UI.L10n.S.RevertConfirmDeleteOnlyFmt, 2),
                Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertEffectLines(deleteOnly)[0]);
            Assert.AreEqual(0, Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertCaveatLines(restoreOnly).Count,
                "a caveat that does not apply is not shown");
            Assert.IsTrue(restoreOnly.HasAnythingToDo);
        }

        [Test]
        public void ConfirmCard_WithNothingItCanDo_SaysSo()
        {
            // Only files a command changed without a copy, and scene edits
            // from before an editor restart.
            var preview = new UapRevertPreview { FilesChangedUntracked = 2, SceneUndoUnavailable = true };

            var effects = Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertEffectLines(preview);
            var caveats = Colloid.AgentPanel.UI.MessageBlockFactory.ComposeRevertCaveatLines(preview);

            Assert.AreEqual(1, effects.Count);
            Assert.AreEqual(Colloid.AgentPanel.UI.L10n.S.RevertConfirmNothingLine, effects[0]);
            Assert.AreEqual(2, caveats.Count);
            Assert.IsFalse(preview.HasAnythingToDo, "the card's Revert button is disabled for this");
        }

        [Test]
        public void Preview_ListsThePathsBehindItsCounts()
        {
            Write("Assets/A.txt", "original");
            string id = RunRequest("first", "Assets/A.txt", "Assets/New/B.txt");

            UapRevertPreview preview = UapCheckpointRecorder.Describe(id);

            CollectionAssert.AreEqual(new[] { "Assets/A.txt" }, preview.RestorePaths);
            CollectionAssert.AreEqual(new[] { "Assets/New/B.txt" }, preview.RemovePaths);
            Assert.AreEqual(0, preview.LeftAsIsPaths.Count);
        }

        [Test]
        public void Preview_CountsWhatTheUserWouldCallFiles_NotMetasOrFolders()
        {
            string existing = Write("Assets/A.txt", "original");
            string existingMeta = Write("Assets/A.txt.meta", "guid: a");
            string loneMeta = Write("Assets/Texture.png.meta", "maxSize: 1024");
            string id = UapCheckpointRecorder.BeginRequest("with metas", "sess");
            UapCheckpointRecorder.Capture(existing);
            UapCheckpointRecorder.Capture(existingMeta);
            UapCheckpointRecorder.Capture(loneMeta);
            string created = Path.Combine(_project, "Assets", "New", "B.txt");
            UapCheckpointRecorder.Capture(created);
            UapCheckpointRecorder.Capture(created + ".meta");
            File.WriteAllText(existing, "changed");
            File.WriteAllText(existingMeta, "guid: a2");
            File.WriteAllText(loneMeta, "maxSize: 2048");
            Directory.CreateDirectory(Path.GetDirectoryName(created));
            File.WriteAllText(created, "new");
            File.WriteAllText(created + ".meta", "guid: b");
            UapCheckpointRecorder.EndTurn();
            // The user edits one of the request's files afterwards.
            File.WriteAllText(existing, "changed, then edited by hand");

            UapRevertPreview preview = UapCheckpointRecorder.Describe(id);

            CollectionAssert.AreEqual(new[] { "Assets/A.txt", "Assets/Texture.png.meta" }, preview.RestorePaths,
                "A.txt.meta is part of A.txt; a .meta changed on its own is a file of its own");
            CollectionAssert.AreEqual(new[] { "Assets/New/B.txt" }, preview.RemovePaths,
                "neither the created folder nor B.txt.meta is listed");
            Assert.AreEqual(2, preview.FilesToRestore);
            Assert.AreEqual(1, preview.FilesToRemove);
            Assert.AreEqual(1, preview.ChangedSince);

            UapRevertOutcome outcome = UapCheckpointRecorder.Revert(id);
            UapCheckpoint checkpoint = outcome.Checkpoint;
            Assert.AreEqual(preview.FilesToRestore,
                UapCheckpointPolicy.DisplayFiles(checkpoint, outcome.Files.Restored).Count,
                "the result note counts by the same rule as the card");
            Assert.AreEqual(preview.FilesToRemove,
                UapCheckpointPolicy.DisplayFiles(checkpoint, outcome.Files.Removed).Count);
            Assert.AreEqual("guid: a", File.ReadAllText(existingMeta), "folded for display, still restored");
        }
    }
}

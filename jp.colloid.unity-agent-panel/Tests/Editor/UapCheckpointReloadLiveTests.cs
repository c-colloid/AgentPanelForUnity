using System;
using System.Collections;
using System.IO;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>
    /// The claim request checkpoints exist for, against a REAL domain
    /// reload (design note docs/design-notes/2026-11-23-request-
    /// checkpoints.md section 8): a request recorded before the reload is
    /// still revertible after it -- the file from the on-disk journal, and
    /// the scene edit through the Undo group recorded before the reload.
    /// The second half is the part nobody had measured: UapTurnScope's
    /// comment assumed Unity's Undo history "does not survive a domain
    /// reload cleanly", and this test is what says whether it does.
    ///
    /// Guarded like ReloadLifecycleLiveTests (same environment variable,
    /// although no CLI is spawned here): an ordinary editor run never
    /// reloads the domain in the middle of the suite. Locals do not
    /// survive the reload, so everything the second half needs travels
    /// through SessionState. This test uses the project's real store
    /// (Library/AgentPanel/Checkpoints): a test-only store override is a
    /// static, and statics are exactly what a reload wipes.
    /// </summary>
    [Category("LiveReload")]
    public class UapCheckpointReloadLiveTests
    {
        private const string KeyId = "Colloid.AgentPanel.Tests.CheckpointReload.Id";
        private const string KeyFile = "Colloid.AgentPanel.Tests.CheckpointReload.File";
        private const string ObjectName = "UapCheckpointReloadLiveTests_Object";

        [UnityTest]
        public IEnumerator RequestRecordedBeforeAReload_IsRevertedAfterIt_FilesAndSceneEdits()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UAP_FAKE_CLI")))
            {
                Assert.Ignore("Live reload test skipped (set UAP_FAKE_CLI to run the reload tests).");
                yield break;
            }

            // ---- before the reload: one request changes a file and the scene ----
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string relative = "Assets/__UapCheckpointReload_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
            string file = Path.Combine(projectRoot, relative);
            File.WriteAllText(file, "before the request");
            AssetDatabase.Refresh();
            SessionState.SetString(KeyFile, relative);

            string id = UapCheckpointRecorder.BeginRequest("reload live test", "sess-live");
            Assert.IsNotNull(id);
            SessionState.SetString(KeyId, id);
            UapTurnScope.BeginIfNeeded();
            UapCheckpointRecorder.Capture(file);
            File.WriteAllText(file, "changed by the request");
            var created = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(created, "Create live test object");
            UapCheckpointRecorder.NoteSceneOp();
            UapTurnScope.EndIfActive();

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            // ---- after the reload (locals are gone; read SessionState) ----
            string idAfter = SessionState.GetString(KeyId, string.Empty);
            string fileAfter = Path.Combine(Path.GetDirectoryName(Application.dataPath),
                SessionState.GetString(KeyFile, string.Empty));
            try
            {
                Assert.IsNotNull(GameObject.Find(ObjectName), "sanity: the object is still in the scene after the reload");
                Assert.AreEqual(idAfter, UapCheckpointRecorder.TopRevertibleId,
                    "the journal is on disk, so the request is still the revertible one after the reload");
                UapRevertPreview preview = UapCheckpointRecorder.Describe(idAfter);
                Assert.IsNotNull(preview);
                Assert.IsTrue(preview.SceneUndo, "same editor launch: the Undo group is expected to be usable");

                UapRevertOutcome outcome = UapCheckpointRecorder.Revert(idAfter);
                UapCheckpointRecorder.RefreshAfterRevert();

                Assert.IsTrue(outcome.Ok, "refused: " + outcome.Refusal);
                Assert.AreEqual("before the request", File.ReadAllText(fileAfter));
                Assert.IsTrue(outcome.SceneUndone);
                Assert.IsNull(GameObject.Find(ObjectName),
                    "Unity's Undo history must still hold the request's group after a domain reload;"
                    + " if this fails, scene edits need their own journal instead of Undo.");
            }
            finally
            {
                GameObject leftover = GameObject.Find(ObjectName);
                if (leftover != null)
                {
                    UnityEngine.Object.DestroyImmediate(leftover);
                }
                if (File.Exists(fileAfter))
                {
                    AssetDatabase.DeleteAsset(SessionState.GetString(KeyFile, string.Empty));
                }
                SessionState.EraseString(KeyId);
                SessionState.EraseString(KeyFile);
            }
        }
    }
}

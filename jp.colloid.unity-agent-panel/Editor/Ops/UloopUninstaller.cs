using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>Machine-readable outcome of <see cref="UloopUninstaller.Apply"/> (phase one). Never a thrown exception.</summary>
    public sealed class UloopUninstallApplyResult
    {
        /// <summary>True when Client.Remove was dispatched without a synchronous throw.</summary>
        public bool Success;

        /// <summary>One of the <c>UloopUninstaller.ApplyFailure*</c> constants, or empty on success.</summary>
        public string FailureCode = string.Empty;

        /// <summary>Free-text detail for logs (an exception message, typically). Empty on success.</summary>
        public string FailureDetail = string.Empty;

        /// <summary>
        /// The in-flight request Client.Remove returned, kept alive for the
        /// UI's progress poller exactly as the install path keeps its
        /// AddRequest (see UloopInstallApplyResult.ClientAddRequest). Null
        /// on every failure path.
        /// </summary>
        public RemoveRequest ClientRemoveRequest;
    }

    /// <summary>Machine-readable outcome of <see cref="UloopUninstaller.CleanUpRegistry"/> (phase two).</summary>
    public sealed class UloopRegistryCleanupResult
    {
        /// <summary>True when the cleanup ran to completion -- INCLUDING the "there was nothing to clean" case.</summary>
        public bool Success;

        /// <summary>True only when manifest.json was actually written. False on "nothing to clean".</summary>
        public bool Wrote;

        /// <summary>What was done (or would have been). <see cref="UloopRegistryCleanup.None"/> when nothing was.</summary>
        public UloopRegistryCleanup Cleanup = UloopRegistryCleanup.None;

        /// <summary>One of the <c>UloopUninstaller.CleanupFailure*</c> constants, or empty on success.</summary>
        public string FailureCode = string.Empty;

        /// <summary>Free-text detail for logs. Empty on success.</summary>
        public string FailureDetail = string.Empty;

        /// <summary>The backup written before the manifest write; empty when nothing was written.</summary>
        public string BackupPath = string.Empty;
    }

    /// <summary>
    /// One-click uLoop REMOVAL -- the counterpart to
    /// <see cref="UloopInstaller"/> (docs/design-notes/2026-09-22-uloop-
    /// remove-from-panel.md; option B of docs/design-notes/2026-09-21-uloop-
    /// always-loaded-cost.md section 5). Removing the package is the ONLY
    /// thing that actually stops uLoop's editor server, its 16 ms tick pump
    /// and its per-session skill cost, which is why this exists at all:
    /// section 4 of that note established that nothing else the panel can
    /// switch reaches them.
    ///
    /// <para><b>Symmetry with the install path, deliberately.</b> The
    /// install wrote exactly two things -- a scopedRegistries stanza (by
    /// hand) and a dependency line (via Client.Add, never by hand, because a
    /// hand-written version constant goes stale). The removal mirrors that
    /// boundary: <b>Unity removes the dependency</b> (Client.Remove) and
    /// <b>this class removes only the scopedRegistries stanza</b>.</para>
    ///
    /// <para><b>Why removal is TWO phases, and why neither order collapses
    /// into one.</b> (Design note section 2.)</para>
    /// <list type="bullet">
    /// <item><b>Manifest first is wrong.</b> Unity watches manifest.json and
    /// re-resolves on change. A manifest that has lost the registry but
    /// still has the dependency is, for that window, a project asking for a
    /// package no registry serves -- a resolve error in the console, raised
    /// by an operation the panel told the user was safe.</item>
    /// <item><b>Client.Remove first, then writing a pre-computed manifest,
    /// is worse.</b> Client.Remove REWRITES manifest.json itself. Any text
    /// computed from the pre-removal snapshot still contains the dependency
    /// line, so writing it afterwards would resurrect the dependency that
    /// was just deleted.</item>
    /// <item><b>So: dispatch Client.Remove, wait for the removal to be
    /// OBSERVED (the detector no longer sees the dependency), then
    /// recompute the cleanup against whatever Unity actually left behind
    /// and write that.</b> The confirmation card's diff is therefore a
    /// PROJECTION (<see cref="UloopUninstallPlan.ManifestAfterProjected"/>),
    /// and says so.</item>
    /// </list>
    ///
    /// <para><b>Phase two is best-effort by design.</b> Tidying a dead
    /// registry stanza is housekeeping, not the removal. A failure there
    /// leaves the package gone and the stanza behind; the UI reports exactly
    /// what is left and where, instead of calling a successful removal
    /// failed.</para>
    ///
    /// <para><b>What this class will not touch.</b> A scope that is not an
    /// EXACT match for a known uLoop id (a broader prefix this panel never
    /// wrote), a scope another dependency still resolves through, and any
    /// registry that is not OpenUPM. See
    /// <see cref="PlanRegistryCleanup"/> for the full rule set.</para>
    /// </summary>
    public static class UloopUninstaller
    {
        // -- Caveat codes. The UI (SettingsView.DescribeUloopCaveat) maps these to
        // localized text; never add one here without adding the matching string. ----

        /// <summary>uLoop is not a dependency of this project at all -- nothing to remove.</summary>
        public const string CaveatCodeNotInstalled = "not-installed";

        /// <summary>
        /// The uloop allow / deny patterns and the instruction snippet this
        /// panel wrote into the user's own settings stay behind. Always
        /// raised (non-blocking): they are the user's settings, and this
        /// package's rule is that it does not silently rewrite what the
        /// user owns -- see the design note's section 6 table.
        /// </summary>
        public const string CaveatCodePanelSettingsKept = "panel-settings-kept";

        /// <summary>
        /// The OpenUPM scope stays because another dependency still resolves
        /// through it. Detail lists those dependency ids. Non-blocking: the
        /// removal itself is unaffected, but the card must not promise a
        /// cleanup it has decided not to perform.
        /// </summary>
        public const string CaveatCodeRegistryScopeKept = "registry-scope-kept";

        /// <summary>
        /// Some registry that is NOT OpenUPM lists a uLoop id in its scopes
        /// -- a company mirror, a VPM feed, something the project pointed
        /// there deliberately. Left untouched; detail names its url.
        /// </summary>
        public const string CaveatCodeForeignRegistryKept = "foreign-registry-kept";

        // -- Apply (phase one) failure codes --------------------------------------

        /// <summary>Apply() was called on a plan CanProceed() rejects; nothing was touched.</summary>
        public const string ApplyFailureRefused = "refused";

        /// <summary>
        /// manifest.json no longer matches the text <see cref="Plan"/>
        /// captured (the user edited a dependency while the card sat open, a
        /// git pull landed, the file became unreadable). Same guard, and the
        /// same byte-for-byte compare, as the install path's
        /// <see cref="UloopInstaller.ApplyFailureManifestChangedSincePlan"/>:
        /// a plan describing a project that no longer exists must be
        /// recomputed, not applied. Nothing is dispatched.
        /// </summary>
        public const string ApplyFailureManifestChangedSincePlan = "manifest-changed-since-plan";

        /// <summary>
        /// <c>Client.Remove</c> threw SYNCHRONOUSLY -- the request was never
        /// dispatched. Nothing to roll back: phase one writes nothing, which
        /// is precisely why the removal path has no rollback branches while
        /// the install path has four.
        /// </summary>
        public const string ApplyFailureClientRemoveThrew = "client-remove-threw";

        // -- CleanUpRegistry (phase two) failure codes -----------------------------

        /// <summary>manifest.json could not be read or parsed at cleanup time; nothing was written.</summary>
        public const string CleanupFailureManifestUnreadable = "manifest-unreadable";

        /// <summary>
        /// The package is STILL a dependency. Stripping the registry a live
        /// dependency resolves through is the worst thing this class could
        /// do, so it is the one condition phase two refuses outright rather
        /// than proceeding best-effort. Reached when the caller runs the
        /// cleanup before the removal has actually landed.
        /// </summary>
        public const string CleanupFailurePackageStillPresent = "package-still-present";

        /// <summary>Writing the pre-change backup failed; manifest.json was never touched.</summary>
        public const string CleanupFailureBackupWriteFailed = "backup-write-failed";

        /// <summary>Writing the new manifest failed, and the backup was successfully written back over it.</summary>
        public const string CleanupFailureManifestWriteFailed = "manifest-write-failed";

        /// <summary>
        /// The manifest write failed AND the restore-from-backup write also
        /// failed: manifest.json's on-disk state is no longer known. Same
        /// worst-case treatment, and the same unconditional Debug.LogError,
        /// as the install path's equivalent code.
        /// </summary>
        public const string CleanupFailureManifestWriteFailedRestoreFailed = "manifest-write-failed-restore-failed";

        // =========================================================================
        // Plan -- pure inspection. Reads disk, writes nothing, never touches the
        // network (unlike the install path, removal needs no registry to be
        // reachable, so there is no offline caveat here at all).
        // =========================================================================

        /// <summary>
        /// Computes the complete, side-effect-free answer to "what would
        /// pressing Remove do right now?" for the project at
        /// <paramref name="projectRoot"/>.
        /// </summary>
        public static UloopUninstallPlan Plan(string projectRoot)
        {
            var plan = new UloopUninstallPlan();
            if (string.IsNullOrEmpty(projectRoot))
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(UloopInstaller.CaveatCodeManifestUnreadable,
                    blocking: true, detail: "projectRoot was null or empty"));
                return plan;
            }

            string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            string manifestText;
            if (!UloopInstaller.TryReadManifestText(manifestPath, out manifestText))
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(UloopInstaller.CaveatCodeManifestUnreadable,
                    blocking: true, detail: manifestPath));
                return plan;
            }

            JsonNode root;
            string parseError;
            if (!JsonParser.TryParse(manifestText, out root, out parseError) || !root.IsObject)
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(UloopInstaller.CaveatCodeManifestUnreadable,
                    blocking: true, detail: parseError ?? "manifest.json root is not a JSON object"));
                return plan;
            }

            plan.ManifestPath = manifestPath;
            plan.BackupPath = manifestPath + UloopInstaller.BackupFileSuffix;
            plan.ManifestBefore = manifestText;

            plan.PackageId = FindInstalledPackageId(root);
            plan.Installed = !string.IsNullOrEmpty(plan.PackageId);
            if (!plan.Installed)
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(CaveatCodeNotInstalled, blocking: true, detail: string.Empty));
                return plan;
            }

            // Same advisory-not-blocking treatment as the install path: a
            // VPM-managed project's own resolve can revert a manual
            // manifest.json edit, but it neither happens silently nor
            // without a recovery path, so the user is warned and the button
            // still works (UloopInstaller.AddVccAndOfflineCaveats' comment).
            // No offline caveat: removal contacts no registry.
            string vpmManifestPath = UloopInstaller.VpmManifestPath(projectRoot);
            if (UloopInstaller.SafeFileExists(vpmManifestPath))
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(UloopInstaller.CaveatCodeVccProject,
                    blocking: false, detail: vpmManifestPath));
            }

            PlanRegistryCleanup(root, plan);

            // Always raised, last, so it reads as the closing "and this is
            // what stays" rather than a warning about the removal itself.
            plan.Caveats.Add(UloopInstaller.NewCaveat(CaveatCodePanelSettingsKept, blocking: false, detail: string.Empty));

            plan.ManifestAfterProjected = UloopInstaller.MatchLineEndings(
                BuildProjectedManifest(root, plan), manifestText);
            return plan;
        }

        /// <summary>
        /// The uLoop package id this project actually declares as a
        /// dependency, or empty. Reads the "dependencies" KEYS rather than
        /// scanning the whole file, for the same reason
        /// UloopInstaller.IsAlreadyInstalled does: a scopes array can carry
        /// the id without the package being a dependency at all.
        /// </summary>
        internal static string FindInstalledPackageId(JsonNode root)
        {
            JsonNode dependencies = root["dependencies"];
            if (!dependencies.IsObject)
            {
                return string.Empty;
            }
            string[] known = UloopDetector.KnownPackageIdList();
            foreach (string key in dependencies.Keys)
            {
                for (int i = 0; i < known.Length; i++)
                {
                    if (string.Equals(key, known[i], StringComparison.OrdinalIgnoreCase))
                    {
                        // The key as WRITTEN, not the constant: Client.Remove
                        // is given what the manifest actually says.
                        return key;
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Decides what phase two would do to "scopedRegistries", and fills
        /// the plan's cleanup fields and the caveats that explain a
        /// downgrade. The rules, in order (design note section 3):
        ///
        /// <list type="number">
        /// <item><b>Only the OpenUPM entry is ours to edit.</b> Matched by
        /// normalized URL via <see cref="UloopInstaller.FindMatchingScopedRegistry"/>,
        /// the same predicate the install used to find where to merge. Any
        /// OTHER registry that lists a uLoop id -- a company mirror, a VPM
        /// feed -- is something the project pointed there deliberately, so it
        /// is left alone and reported
        /// (<see cref="CaveatCodeForeignRegistryKept"/>).</item>
        /// <item><b>Only an EXACT id match is dropped.</b> A broader prefix
        /// scope (`io.github.hatayama`) was never written by this panel and
        /// would take unrelated packages under the same prefix down with it.
        /// Ordinal equality, no prefix logic, in this direction.</item>
        /// <item><b>A scope another dependency still needs stays.</b> Unity
        /// resolves a scope as a dot-delimited PREFIX, so a scope equal to
        /// the uLoop id still covers a hypothetical
        /// `io.github.hatayama.uloopmcp.extras`. If any non-uLoop dependency
        /// is covered, the scope stays and the card names those ids
        /// (<see cref="CaveatCodeRegistryScopeKept"/>).</item>
        /// <item><b>An entry left with no scopes goes entirely.</b> A
        /// registry with an empty scopes array can serve nothing, so keeping
        /// it would be leaving litter, not leaving a setting.</item>
        /// </list>
        /// </summary>
        internal static void PlanRegistryCleanup(JsonNode root, UloopUninstallPlan plan)
        {
            JsonNode ours = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);

            JsonNode foreign = UloopInstaller.FindForeignRegistryClaimingOurScope(root, ours);
            if (foreign != null)
            {
                plan.Caveats.Add(UloopInstaller.NewCaveat(CaveatCodeForeignRegistryKept,
                    blocking: false, detail: foreign["url"].AsString("?")));
            }

            if (ours == null)
            {
                return;
            }
            plan.RegistryName = ours["name"].AsString(string.Empty);
            plan.RegistryUrl = ours["url"].AsString(string.Empty);

            string[] scopes = ours["scopes"].AsStringArray();
            string[] known = UloopDetector.KnownPackageIdList();
            var retained = new List<string>();
            var holders = new List<string>();
            bool dropsAnything = false;

            for (int i = 0; i < scopes.Length; i++)
            {
                string scope = scopes[i];
                if (!IsExactlyAKnownUloopId(scope, known))
                {
                    retained.Add(scope);
                    continue;
                }
                List<string> covered = DependenciesCoveredByScope(root, scope, known);
                if (covered.Count > 0)
                {
                    retained.Add(scope);
                    holders.AddRange(covered);
                    continue;
                }
                dropsAnything = true;
            }

            if (holders.Count > 0)
            {
                plan.ScopeHolders.AddRange(holders);
                plan.Caveats.Add(UloopInstaller.NewCaveat(CaveatCodeRegistryScopeKept,
                    blocking: false, detail: string.Join(", ", holders.ToArray())));
            }
            if (!dropsAnything)
            {
                return;
            }

            plan.RegistryCleanup = retained.Count == 0
                ? UloopRegistryCleanup.DropRegistry
                : UloopRegistryCleanup.DropScope;
            if (retained.Count > 0)
            {
                plan.RetainedScopes.AddRange(retained);
            }
        }

        private static bool IsExactlyAKnownUloopId(string scope, string[] known)
        {
            for (int i = 0; i < known.Length; i++)
            {
                if (string.Equals(scope, known[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Dependency ids OTHER than uLoop's that <paramref name="scope"/>
        /// still covers under Unity's dot-delimited prefix rule (`com.foo`
        /// covers `com.foo` and `com.foo.bar`, never `com.foobar`).
        /// </summary>
        internal static List<string> DependenciesCoveredByScope(JsonNode root, string scope, string[] known)
        {
            var covered = new List<string>();
            JsonNode dependencies = root["dependencies"];
            if (!dependencies.IsObject || string.IsNullOrEmpty(scope))
            {
                return covered;
            }
            foreach (string key in dependencies.Keys)
            {
                if (IsExactlyAKnownUloopId(key, known))
                {
                    continue;
                }
                if (string.Equals(key, scope, StringComparison.Ordinal)
                    || key.StartsWith(scope + ".", StringComparison.Ordinal))
                {
                    covered.Add(key);
                }
            }
            return covered;
        }

        /// <summary>
        /// The manifest as it is EXPECTED to end up: the dependency key gone
        /// (Unity's doing) and the registry cleanup applied (this package's).
        /// Never written verbatim -- see the class doc comment -- it exists
        /// so the confirmation card can diff something real.
        /// </summary>
        private static string BuildProjectedManifest(JsonNode root, UloopUninstallPlan plan)
        {
            JsonNode projected = RebuildWithoutDependency(root, plan.PackageId);
            projected = ApplyRegistryCleanup(projected, plan.RegistryCleanup);
            return UloopInstaller.PrettyPrintManifest(projected);
        }

        /// <summary>
        /// A copy of <paramref name="root"/> with
        /// <paramref name="packageId"/> gone from "dependencies". JsonNode
        /// has no key removal (and adding one would touch a shared core type
        /// for a single caller), so the object is rebuilt in its existing
        /// order minus that one key.
        /// </summary>
        internal static JsonNode RebuildWithoutDependency(JsonNode root, string packageId)
        {
            JsonNode result = JsonNode.NewObject();
            foreach (KeyValuePair<string, JsonNode> member in root.Properties)
            {
                if (!string.Equals(member.Key, "dependencies", StringComparison.Ordinal) || !member.Value.IsObject)
                {
                    result.Set(member.Key, member.Value);
                    continue;
                }
                JsonNode dependencies = JsonNode.NewObject();
                foreach (KeyValuePair<string, JsonNode> dep in member.Value.Properties)
                {
                    if (string.Equals(dep.Key, packageId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    dependencies.Set(dep.Key, dep.Value);
                }
                result.Set("dependencies", dependencies);
            }
            return result;
        }

        /// <summary>
        /// A copy of <paramref name="root"/> with the OpenUPM entry's uLoop
        /// scopes dropped (<see cref="UloopRegistryCleanup.DropScope"/>) or
        /// the whole entry dropped
        /// (<see cref="UloopRegistryCleanup.DropRegistry"/>). Recomputes the
        /// per-scope decision from THIS root rather than trusting a list
        /// captured earlier, which is what makes it safe to call in phase
        /// two against the manifest Unity rewrote.
        /// </summary>
        internal static JsonNode ApplyRegistryCleanup(JsonNode root, UloopRegistryCleanup cleanup)
        {
            if (cleanup == UloopRegistryCleanup.None)
            {
                return root;
            }
            JsonNode ours = UloopInstaller.FindMatchingScopedRegistry(root, UloopInstaller.OpenUpmRegistryUrl);
            if (ours == null)
            {
                return root;
            }
            string[] known = UloopDetector.KnownPackageIdList();

            JsonNode result = JsonNode.NewObject();
            foreach (KeyValuePair<string, JsonNode> member in root.Properties)
            {
                if (!string.Equals(member.Key, "scopedRegistries", StringComparison.Ordinal) || !member.Value.IsArray)
                {
                    result.Set(member.Key, member.Value);
                    continue;
                }
                JsonNode registries = JsonNode.NewArray();
                foreach (JsonNode entry in member.Value.Items)
                {
                    if (!ReferenceEquals(entry, ours))
                    {
                        registries.Add(entry);
                        continue;
                    }
                    if (cleanup == UloopRegistryCleanup.DropRegistry)
                    {
                        continue;
                    }
                    registries.Add(RebuildEntryWithoutUloopScopes(entry, root, known));
                }
                result.Set("scopedRegistries", registries);
            }
            return result;
        }

        private static JsonNode RebuildEntryWithoutUloopScopes(JsonNode entry, JsonNode root, string[] known)
        {
            JsonNode rebuilt = JsonNode.NewObject();
            foreach (KeyValuePair<string, JsonNode> field in entry.Properties)
            {
                if (!string.Equals(field.Key, "scopes", StringComparison.Ordinal) || !field.Value.IsArray)
                {
                    rebuilt.Set(field.Key, field.Value);
                    continue;
                }
                JsonNode scopes = JsonNode.NewArray();
                string[] existing = field.Value.AsStringArray();
                for (int i = 0; i < existing.Length; i++)
                {
                    if (IsExactlyAKnownUloopId(existing[i], known)
                        && DependenciesCoveredByScope(root, existing[i], known).Count == 0)
                    {
                        continue;
                    }
                    scopes.Add(existing[i]);
                }
                rebuilt.Set("scopes", scopes);
            }
            return rebuilt;
        }

        // =========================================================================
        // Phase one -- dispatch Client.Remove. Writes NOTHING.
        // =========================================================================

        /// <summary>
        /// Asks Unity to remove the package. Refuses any plan
        /// <see cref="UloopUninstallPlan.CanProceed"/> rejects, and any plan
        /// whose manifest has changed since <see cref="Plan"/> read it.
        /// Never throws to the caller.
        /// </summary>
        public static UloopUninstallApplyResult Apply(UloopUninstallPlan plan, Action<string> log = null)
        {
            return ApplyWithSeams(plan, DefaultReadAllText, RealClientRemove, log);
        }

        /// <summary>Test seam behind <see cref="Apply"/>: the manifest re-read and the Client.Remove call are injectable.</summary>
        internal static UloopUninstallApplyResult ApplyWithSeams(UloopUninstallPlan plan,
            Func<string, string> readAllText, Func<string, RemoveRequest> clientRemove, Action<string> log)
        {
            if (plan == null || !plan.CanProceed())
            {
                return new UloopUninstallApplyResult { Success = false, FailureCode = ApplyFailureRefused };
            }

            string currentManifestText;
            try
            {
                currentManifestText = readAllText(plan.ManifestPath);
            }
            catch (Exception ex)
            {
                Log(log, "Refusing to remove: could not re-read '" + plan.ManifestPath
                    + "' to confirm it still matches the plan: " + ex.Message + " -- re-run Plan and try again.");
                return Failure(ApplyFailureManifestChangedSincePlan, ex.Message);
            }
            if (!string.Equals(currentManifestText, plan.ManifestBefore, StringComparison.Ordinal))
            {
                Log(log, "Refusing to remove: '" + plan.ManifestPath
                    + "' has changed since Plan() captured it -- re-run Plan and try again.");
                return Failure(ApplyFailureManifestChangedSincePlan,
                    "manifest.json content differs from the text Plan() captured; re-run Plan before Apply.");
            }

            RemoveRequest request;
            try
            {
                request = clientRemove(plan.PackageId);
            }
            catch (Exception ex)
            {
                // Nothing to undo: phase one has written nothing by design.
                Log(log, "Client.Remove('" + plan.PackageId + "') threw: " + ex.Message);
                return Failure(ApplyFailureClientRemoveThrew, ex.Message);
            }
            return new UloopUninstallApplyResult { Success = true, ClientRemoveRequest = request };
        }

        // =========================================================================
        // Phase two -- the manifest write. Best-effort housekeeping, run only after
        // the removal has been OBSERVED.
        // =========================================================================

        /// <summary>
        /// Tidies the now-dead OpenUPM scope / entry out of manifest.json.
        /// Recomputes everything from the CURRENT file (Unity rewrote it
        /// between the phases), refuses outright while the package is still
        /// a dependency, and otherwise follows the install path's
        /// backup-then-write-then-restore-on-failure discipline.
        /// </summary>
        public static UloopRegistryCleanupResult CleanUpRegistry(string projectRoot, Action<string> log = null)
        {
            return CleanUpRegistryWithSeams(projectRoot, DefaultReadAllText, DefaultWriteAllText, log);
        }

        /// <summary>Test seam behind <see cref="CleanUpRegistry"/>: every file read and write is injectable.</summary>
        internal static UloopRegistryCleanupResult CleanUpRegistryWithSeams(string projectRoot,
            Func<string, string> readAllText, Action<string, string> writeAllText, Action<string> log)
        {
            string manifestPath = string.IsNullOrEmpty(projectRoot)
                ? string.Empty
                : Path.Combine(projectRoot, "Packages", "manifest.json");
            string manifestText;
            try
            {
                manifestText = readAllText(manifestPath);
            }
            catch (Exception ex)
            {
                return CleanupFailure(CleanupFailureManifestUnreadable, ex.Message);
            }

            JsonNode root;
            string parseError;
            if (!JsonParser.TryParse(manifestText, out root, out parseError) || !root.IsObject)
            {
                return CleanupFailure(CleanupFailureManifestUnreadable,
                    parseError ?? "manifest.json root is not a JSON object");
            }

            // The one hard refusal in phase two: never strip a registry a
            // live dependency still resolves through.
            if (!string.IsNullOrEmpty(FindInstalledPackageId(root)))
            {
                Log(log, "Refusing the registry cleanup: uLoop is still a dependency in '" + manifestPath + "'.");
                return CleanupFailure(CleanupFailurePackageStillPresent,
                    "the package is still listed in dependencies; the removal has not landed yet");
            }

            var probe = new UloopUninstallPlan();
            PlanRegistryCleanup(root, probe);
            if (probe.RegistryCleanup == UloopRegistryCleanup.None)
            {
                return new UloopRegistryCleanupResult { Success = true, Wrote = false, Cleanup = UloopRegistryCleanup.None };
            }

            string backupPath = manifestPath + UloopInstaller.BackupFileSuffix;
            string after = UloopInstaller.MatchLineEndings(
                UloopInstaller.PrettyPrintManifest(ApplyRegistryCleanup(root, probe.RegistryCleanup)), manifestText);

            try
            {
                writeAllText(backupPath, manifestText);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to write manifest backup '" + backupPath + "': " + ex.Message);
                return CleanupFailure(CleanupFailureBackupWriteFailed, ex.Message);
            }

            try
            {
                writeAllText(manifestPath, after);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to write '" + manifestPath + "': " + ex.Message + " -- restoring backup.");
                try
                {
                    writeAllText(manifestPath, manifestText);
                }
                catch (Exception restoreEx)
                {
                    // The one message that must never depend on the caller
                    // having passed a logger -- same rule, and the same
                    // reasoning, as UloopInstaller.TryRestoreBackup's.
                    LogCritical(log, "CRITICAL: failed to restore manifest.json after a failed write: "
                        + restoreEx.Message + " -- recover manually from '" + backupPath + "'.");
                    UloopRegistryCleanupResult worst = CleanupFailure(
                        CleanupFailureManifestWriteFailedRestoreFailed,
                        ex.Message + " / restore also failed: " + restoreEx.Message);
                    worst.BackupPath = backupPath;
                    worst.Cleanup = probe.RegistryCleanup;
                    return worst;
                }
                UloopRegistryCleanupResult failed = CleanupFailure(CleanupFailureManifestWriteFailed, ex.Message);
                failed.BackupPath = backupPath;
                failed.Cleanup = probe.RegistryCleanup;
                return failed;
            }

            return new UloopRegistryCleanupResult
            {
                Success = true,
                Wrote = true,
                Cleanup = probe.RegistryCleanup,
                BackupPath = backupPath
            };
        }

        // -- plumbing -------------------------------------------------------------

        private static UloopUninstallApplyResult Failure(string code, string detail)
        {
            return new UloopUninstallApplyResult
            {
                Success = false,
                FailureCode = code,
                FailureDetail = detail ?? string.Empty
            };
        }

        private static UloopRegistryCleanupResult CleanupFailure(string code, string detail)
        {
            return new UloopRegistryCleanupResult
            {
                Success = false,
                Wrote = false,
                FailureCode = code,
                FailureDetail = detail ?? string.Empty
            };
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static string DefaultReadAllText(string path)
        {
            return File.ReadAllText(path);
        }

        private static void DefaultWriteAllText(string path, string content)
        {
            File.WriteAllText(path, content, Utf8NoBom);
        }

        private static RemoveRequest RealClientRemove(string packageIdentifier)
        {
            return Client.Remove(packageIdentifier);
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[UloopUninstaller] " + message);
            }
        }

        private static void LogCritical(Action<string> log, string message)
        {
            string prefixed = "[UloopUninstaller] " + message;
            Debug.LogError(prefixed);
            if (log != null)
            {
                log(prefixed);
            }
        }
    }
}

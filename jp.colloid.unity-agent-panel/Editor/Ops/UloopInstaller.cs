using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using Colloid.AgentPanel.Core.Json;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Machine-readable outcome of <see cref="UloopInstaller.Apply"/> -- never a thrown
    /// exception (design task requirement: "Never throw at the caller; return a result
    /// carrying success plus a machine-readable failure reason"). Plain mutable fields,
    /// matching this codebase's convention for small result values
    /// (see <see cref="UloopInstallPlan"/>/<see cref="UloopInstallCaveat"/>).
    /// </summary>
    public sealed class UloopInstallApplyResult
    {
        /// <summary>True when the requested action was carried out without error.</summary>
        public bool Success;

        /// <summary>
        /// Which route was attempted (<see cref="UloopInstallPlan.Method"/> of the plan
        /// that was applied). <see cref="UloopInstallMethod.None"/> when the plan was
        /// refused before any route-specific code ran.
        /// </summary>
        public UloopInstallMethod Method;

        /// <summary>
        /// Stable machine-readable failure identifier (one of the
        /// <c>UloopInstaller.ApplyFailure*</c> constants), or empty on success. The UI
        /// maps this to <c>SettingsUloopInstallFailed</c> today (a single generic
        /// message -- design section 2.5 does not ask for per-cause install-failure
        /// text the way caveats get per-cause text), but the specific reason is still
        /// captured here for logs/diagnostics and for a future per-cause message.
        /// </summary>
        public string FailureCode = string.Empty;

        /// <summary>Free-text detail for logs (an exception message, typically). Empty on success.</summary>
        public string FailureDetail = string.Empty;

        /// <summary>
        /// The in-flight request object returned by <c>Client.Add</c> on a successful
        /// dispatch (null on every failure path, since those either never reach the
        /// Client.Add call or the call threw before returning one).
        ///
        /// <para><b>2026-08-04: added.</b> Before this field existed,
        /// <c>RealClientAdd</c> called <c>Client.Add</c> and threw the
        /// returned <see cref="AddRequest"/> away -- the seam type backing it was a
        /// plain <c>Action&lt;string&gt;</c> (void), so no caller, real or test, could
        /// even ask "did the resolve actually finish, and did it succeed?" after
        /// dispatch. A measured real install takes about 30 seconds to resolve
        /// (2026-08-03 measurement, see the class doc comment); a 30-second window
        /// where this class has no way to observe or report a resolve failure is a real
        /// gap. This field closes exactly the "the result is discarded" half of that gap
        /// by keeping the request alive on the result object.</para>
        /// <para><b>2026-08-12: the poller this field was kept alive for now exists,
        /// but it lives in the UI, not here.</b> Editor/UI/SettingsView.cs stashes this
        /// request on a successful Apply and polls <c>IsCompleted</c> / <c>.Status</c> /
        /// <c>.Error</c> from an EditorApplication.update tick, feeding the pure
        /// UloopInstallProgress state machine (Editor/Model/UloopInstallProgress.cs;
        /// docs/design-notes/2026-08-12-uloop-install-progress.md). This class STILL
        /// does not poll, raise a completion event, or roll anything back on an async
        /// failure -- <see cref="ApplyManifestRoute"/>'s doc comment on why a
        /// post-dispatch resolve failure never triggers a rollback stands unchanged;
        /// what changed is only that the failure is now also rendered inside the panel
        /// instead of surfacing exclusively through Unity's own Package Manager UI.</para>
        /// </summary>
        public AddRequest ClientAddRequest;
    }

    /// <summary>
    /// One-click uLoop install (design section 2.5, plus section 8.3's risk bullet
    /// the uLoop one-click-install failure paths). Two independent halves, kept separate exactly
    /// as <see cref="UloopInstallPlan"/>'s own doc comment argues: <see cref="Plan"/> is
    /// pure inspection -- reads disk, NEVER writes, NEVER touches the network -- that
    /// answers "what would pressing Install do?"; <see cref="Apply"/> is the only member
    /// that writes anything, and it only ever acts on a caller-supplied plan (it never
    /// re-derives one internally), so a plan computed against a stale project snapshot
    /// cannot be silently re-validated and applied against a since-changed disk state
    /// without the caller explicitly calling <see cref="Plan"/> again first --
    /// <see cref="ApplyManifestRoute"/> enforces this itself now (2026-08-04, see the
    /// revision note below) by re-reading manifest.json and refusing outright the
    /// instant it no longer matches what <see cref="Plan"/> captured.
    ///
    /// <para><b>REVISION (2026-08-04): three independently-verified defects fixed after
    /// a real end-to-end run (30.5s, resolved io.github.hatayama.uloopmcp@2.2.0).</b></para>
    /// <list type="bullet">
    /// <item><b>Stale plan applied over changed disk.</b> Until this revision,
    /// <see cref="ApplyManifestRoute"/> never re-read <see cref="UloopInstallPlan.ManifestPath"/>
    /// -- it wrote <see cref="UloopInstallPlan.ManifestBefore"/> to the backup and
    /// <see cref="UloopInstallPlan.ManifestAfter"/> over the manifest UNCONDITIONALLY,
    /// even though an arbitrary amount of time (and user action -- adding a package in
    /// the Editor, a git pull landing) can pass between <see cref="Plan"/> being called
    /// and the confirmation card's Apply button being clicked. That window meant the
    /// backup could receive OLD text and the manifest could be overwritten with text
    /// computed before an intervening edit existed -- the intervening edit gone from
    /// BOTH the file and the backup, unrecoverable. The paragraph above claiming this
    /// "cannot be silently re-validated and applied against a since-changed disk state"
    /// was therefore describing an intent the code did not actually enforce.
    /// <see cref="ApplyManifestRoute"/> now re-reads the manifest via the same seam
    /// pattern as its writes (see <see cref="ApplyWithSeams"/>'s <c>readAllText</c>
    /// parameter) and compares it byte-for-byte against
    /// <see cref="UloopInstallPlan.ManifestBefore"/> before writing anything at all;
    /// any difference (or a read failure -- the file went missing, became unreadable,
    /// etc., which is just as much "not what Plan saw" as a textual difference) refuses
    /// with <see cref="ApplyFailureManifestChangedSincePlan"/> and touches neither the
    /// backup nor the manifest.</item>
    /// <item><b>The scoped-registry conflict check matched on registry NAME, not
    /// URL.</b> <see cref="FindMatchingScopedRegistry"/> (then called
    /// <c>HasConflictingScopedRegistry</c> -- see its own doc comment for the full
    /// history) used to compare <c>entry["name"]</c> against
    /// <see cref="OpenUpmRegistryName"/> first and only looked at the URL once the name
    /// already matched. That produced two opposite failures with the same root cause:
    /// a registry a project already had under a different display name (e.g. "OpenUPM
    /// Registry") but the SAME url produced no name match, so this class appended a
    /// second, duplicate scopedRegistries entry pointing at the exact same host; and a
    /// registry named exactly "OpenUPM" but with a single trailing slash on its url
    /// (a form OpenUPM's own docs emit) matched by name, then failed the url compare,
    /// and was reported as a BLOCKING conflict for a manifest that in fact needed no
    /// resolution at all. The predicate is now URL-first (normalized: trim a trailing
    /// slash, compare scheme+host case-insensitively) -- see that method's doc comment
    /// for the full replacement logic.</item>
    /// <item><b>Client.Add's returned request was discarded.</b> See
    /// <see cref="UloopInstallApplyResult.ClientAddRequest"/>'s doc comment.</item>
    /// </list>
    ///
    /// <para><b>REVISION (2026-08-03): measured data replaced every guess, and changed
    /// the design.</b> The FIRST version of this class (design section 2.5's stated
    /// preference order: Git URL first, manifest-registry-edit as a fallback) was
    /// written with no network access and therefore no way to confirm either route's
    /// exact shape -- everything below marked "UNVERIFIED" in that version was a guess.
    /// The coordinator subsequently measured a REAL working uLoop install (AITemp's
    /// Packages/manifest.json + packages-lock.json) and reported it verbatim:</para>
    /// <list type="bullet">
    /// <item>dependencies key: <c>"io.github.hatayama.uloopmcp"</c> -- NOT the renamed
    /// "unitycliloop" id this class originally guessed. (<see cref="UloopDetector"/>'s
    /// own known-ids list already covered both names for DETECTION purposes -- this
    /// class's mistake was only in which id it wrote as the INSTALL target.)</item>
    /// <item>scopedRegistries entry: <c>{"name":"OpenUPM","url":"https://package.openupm.com","scopes":["io.github.hatayama.uloopmcp"]}</c>
    /// -- the registry "name" field is the literal display string "OpenUPM", NOT the
    /// guessed "package.openupm.com" (that guess happened to get the URL right but the
    /// name wrong).</item>
    /// <item>packages-lock.json: <c>source "registry", url "https://package.openupm.com"</c>
    /// -- confirms the URL above.</item>
    /// <item>a SECOND real install was observed on a different resolved version
    /// (<c>3.0.0-beta.48</c> vs. <c>3.0.0-beta.71</c>) -- i.e. the version genuinely
    /// varies over time. Any constant this class could have hard-coded would have been
    /// stale the moment OpenUPM published a new build.</item>
    /// </list>
    /// <para>That last fact drives a DESIGN change, not just a data fix: this class no
    /// longer hand-writes a "dependencies" version line at all. It writes ONLY the
    /// scoped-registry stanza (which carries no version -- nothing to guess, nothing to
    /// go stale) and then calls <c>Client.Add(UloopPackageId)</c> with NO version
    /// suffix; Unity resolves "latest from OpenUPM" itself and writes the concrete
    /// "dependencies" line with whatever version that actually is. The version now
    /// comes from the registry, never from this class.</para>
    /// <para><b>Deviation from the design doc's stated preference order.</b> Design
    /// section 2.5 lists the Git URL route as the FIRST choice specifically to avoid
    /// hand-editing manifest.json at all. This class deliberately does not implement
    /// that preference: the measurement above shows the real-world distribution
    /// mechanism for this package IS OpenUPM (a working install's own
    /// packages-lock.json says <c>source: "registry"</c>, not "git"), and the
    /// registry-only edit this class makes is already minimal (one small stanza,
    /// version-free, mergeable into an existing OpenUPM entry) and, unlike a guessed
    /// Git URL, is built from measured fact rather than assumption. Shipping "one
    /// measured-correct route" was judged strictly better than "a verified-nothing Git
    /// route plus a fallback" -- see <see cref="UloopInstallMethod.GitUrl"/>'s doc
    /// comment for the mechanical detail of how that route is retired without touching
    /// the shared <see cref="UloopInstallPlan"/> type.</para>
    /// <para>Every remaining string/URL constant below is now MEASURED, not guessed --
    /// there is no more "UNVERIFIED" data in this class. The one thing that still
    /// cannot be confirmed offline is whether OpenUPM is REACHABLE at apply time (a
    /// genuine network question, not a data-correctness one); that is exactly what the
    /// non-blocking "offline" caveat already covers.</para>
    ///
    /// <para>The manifest edit remains a deliberate, user-triggered EXCEPTION to this
    /// package's standing rule (ARCHITECTURE.md risk #12: the panel does not edit
    /// manifest.json) -- the exception is only defensible because the user asks for it
    /// by pressing an explicit "Install uLoop" button (design section 2.5), never on the
    /// panel's own initiative.</para>
    /// </summary>
    public static class UloopInstaller
    {
        // -- Measured package identity (2026-08-03, from a real working install's
        // Packages/manifest.json + packages-lock.json -- see the class doc comment) ---

        /// <summary>
        /// The package id this route installs. MEASURED verbatim from a real project's
        /// "dependencies" key -- NOT the renamed "unitycliloop" id this class originally
        /// (and incorrectly) guessed. <see cref="UloopDetector"/>'s own known-ids list
        /// already recognizes this id (and the -- apparently not real -- renamed one)
        /// for DETECTION purposes; this constant is specifically the id this class
        /// WRITES as the install target, which must match what actually exists on the
        /// registry.
        /// </summary>
        public const string UloopPackageId = "io.github.hatayama.uloopmcp";

        /// <summary>
        /// The scoped registry's "name" field. MEASURED verbatim from a real project's
        /// manifest.json -- this is a literal display string ("OpenUPM"), NOT the
        /// registry's host/URL (that is <see cref="OpenUpmRegistryUrl"/>, a separate
        /// field). The original guess used "package.openupm.com" here, which is wrong:
        /// that string is the URL's host, not what this measured install actually wrote
        /// into "name".
        /// </summary>
        public const string OpenUpmRegistryName = "OpenUPM";

        /// <summary>
        /// The scoped registry's "url" field, paired with <see cref="OpenUpmRegistryName"/>.
        /// MEASURED twice in the same real project: once in manifest.json's
        /// scopedRegistries entry, and again in packages-lock.json's resolved
        /// "source: registry" record -- both agree on this exact string.
        /// </summary>
        public const string OpenUpmRegistryUrl = "https://package.openupm.com";

        /// <summary>Backup filename suffix (design section 2.5: "manifest.json.uap-backup").</summary>
        public const string BackupFileSuffix = ".uap-backup";

        // -- Caveat codes (must match the UI's existing localized strings verbatim --
        // see Editor/UI/L10n/UiStrings.cs SettingsUloopCaveat* -- never invent a new
        // code here without adding the matching UI text first) --------------------

        public const string CaveatCodeVccProject = "vcc-project";
        public const string CaveatCodeOffline = "offline";
        public const string CaveatCodeManifestUnreadable = "manifest-unreadable";

        /// <summary>
        /// Kept even though <see cref="Plan"/> no longer produces this caveat as of the
        /// 2026-08-04 revision (see <see cref="FindMatchingScopedRegistry"/>'s doc
        /// comment for why the conflict this used to describe -- a same-NAMED registry
        /// pointing somewhere else -- turned out to be the wrong thing to block on).
        /// The constant itself cannot be deleted: Editor/UI/SettingsView.cs (owned by a
        /// different stream) still switches on this exact string to pick a localized
        /// message, and removing it here would be a silent breaking change to a file
        /// this stream does not own. If a genuinely blocking scoped-registry situation
        /// is identified in the future, this is still the code to raise for it.
        /// </summary>
        public const string CaveatCodeScopedRegistryConflict = "scoped-registry-conflict";

        // -- Apply() failure codes ------------------------------------------------

        /// <summary>Apply() was called on a plan that CanProceed() rejects; nothing was touched.</summary>
        public const string ApplyFailureRefused = "refused";

        /// <summary>
        /// <see cref="ApplyManifestRoute"/> re-read <see cref="UloopInstallPlan.ManifestPath"/>
        /// at the top of Apply and found it no longer matches
        /// <see cref="UloopInstallPlan.ManifestBefore"/> -- the exact text
        /// <see cref="Plan"/> captured has changed on disk since the plan was computed
        /// (the user edited a dependency in the Editor while the confirmation card sat
        /// open, a git pull landed, etc.), or the file could no longer be read at all
        /// (deleted, permissions changed, mid-write by something else). Either way,
        /// <see cref="UloopInstallPlan.ManifestAfter"/> was computed against a snapshot
        /// that is no longer true, so applying it would silently discard whatever
        /// changed in between -- see the class doc comment's 2026-08-04 revision note
        /// for the concrete failure this closes. NOTHING is written on this path, not
        /// even the backup: the caller must call <see cref="Plan"/> again against the
        /// current disk state and re-confirm before retrying Apply.
        /// </summary>
        public const string ApplyFailureManifestChangedSincePlan = "manifest-changed-since-plan";

        /// <summary>Writing the pre-change backup copy failed; the real manifest.json was never touched.</summary>
        public const string ApplyFailureBackupWriteFailed = "backup-write-failed";

        /// <summary>Writing the new manifest.json failed, but the backup was successfully written back over it (restored).</summary>
        public const string ApplyFailureManifestWriteFailed = "manifest-write-failed";

        /// <summary>
        /// Writing the new manifest.json failed AND the subsequent restore-from-backup
        /// write also failed -- the worst case (design task: "A half-edited manifest.json
        /// is the worst possible outcome here"). The on-disk manifest.json state is now
        /// unknown; the backup file (still holding the ORIGINAL content, since only the
        /// restore WRITE failed, not the earlier backup write) is the user's recovery
        /// path.
        /// </summary>
        public const string ApplyFailureManifestWriteFailedRestoreFailed = "manifest-write-failed-restore-failed";

        /// <summary>
        /// <c>Client.Add(UloopPackageId)</c> threw SYNCHRONOUSLY -- the request was
        /// never even dispatched (Unity's async resolve never started), so this is
        /// treated the same as a failed manifest write: the manifest backup is restored
        /// (see <see cref="ApplyManifestRoute"/>'s doc comment for exactly why a
        /// synchronous throw is rolled back but a later async resolve failure is not).
        /// </summary>
        public const string ApplyFailureClientAddThrew = "client-add-threw";

        /// <summary>
        /// <c>Client.Add(UloopPackageId)</c> threw synchronously AND the subsequent
        /// restore-from-backup write also failed -- same "worst case, unknown on-disk
        /// state" reasoning as <see cref="ApplyFailureManifestWriteFailedRestoreFailed"/>,
        /// just triggered from the later step.
        /// </summary>
        public const string ApplyFailureClientAddThrewRestoreFailed = "client-add-threw-restore-failed";

        // =========================================================================
        // Plan -- pure inspection, writes NOTHING, touches the network only via the
        // cheap local heuristic documented on IsNetworkAvailable below.
        // =========================================================================

        /// <summary>
        /// Computes the complete, side-effect-free answer to "what would pressing
        /// Install do right now?" for the project at <paramref name="projectRoot"/>.
        /// Convenience entry point: wires the real <see cref="IsNetworkAvailable"/>
        /// probe. See the 2-argument overload for the pure, fully test-seamed version.
        /// </summary>
        public static UloopInstallPlan Plan(string projectRoot)
        {
            return Plan(projectRoot, IsNetworkAvailable);
        }

        /// <summary>
        /// Test seam: same as <see cref="Plan(string)"/>, but with the network probe
        /// passed in explicitly, so unit tests can exercise the offline-vs-online
        /// branch deterministically without depending on the test machine's actual
        /// network state.
        /// </summary>
        internal static UloopInstallPlan Plan(string projectRoot, Func<bool> isNetworkAvailable)
        {
            var plan = new UloopInstallPlan();

            if (string.IsNullOrEmpty(projectRoot))
            {
                plan.Caveats.Add(NewCaveat(CaveatCodeManifestUnreadable, blocking: true, detail: "projectRoot was null or empty"));
                return plan;
            }

            string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            string manifestText;
            if (!TryReadManifestText(manifestPath, out manifestText))
            {
                plan.Caveats.Add(NewCaveat(CaveatCodeManifestUnreadable, blocking: true, detail: manifestPath));
                return plan;
            }

            JsonNode root;
            string parseError;
            if (!JsonParser.TryParse(manifestText, out root, out parseError) || !root.IsObject)
            {
                plan.Caveats.Add(NewCaveat(CaveatCodeManifestUnreadable, blocking: true,
                    detail: parseError ?? "manifest.json root is not a JSON object"));
                return plan;
            }

            if (IsAlreadyInstalled(root))
            {
                plan.AlreadyInstalled = true;
                return plan;
            }

            AddVccAndOfflineCaveats(plan, projectRoot, isNetworkAvailable);

            // 2026-08-04: no more blocking branch here. Earlier revisions looked up a
            // same-NAMED registry first and only then compared its url, which produced
            // both a false negative (a same-url registry under a different display
            // name went undetected, so this class appended a duplicate entry) and a
            // false positive (a same-name, trailing-slash-url registry was declared an
            // outright conflict for a manifest that needed no resolution). Matching by
            // normalized URL instead -- see FindMatchingScopedRegistry's doc comment --
            // removes the false positive entirely: a registry whose url does not match
            // ours, however it is named, is simply unrelated and is left untouched
            // while our own entry is added alongside it. There is currently no known
            // scoped-registry situation left that this class can identify as
            // genuinely un-mergeable without guessing, EXCEPT the one below.
            JsonNode existingRegistry = FindMatchingScopedRegistry(root, OpenUpmRegistryUrl);

            // The one case URL-matching alone would walk straight into. If some
            // OTHER registry -- a company mirror, a VPM feed, anything not at
            // package.openupm.com -- already lists this package id in its scopes,
            // then adding ours alongside it leaves TWO registries claiming the same
            // scope. Unity does not merge those; which one serves the package is not
            // something this class can predict, so the install could silently
            // redirect a package the project is already resolving from somewhere
            // deliberate. That is un-mergeable for a concrete, checkable reason
            // rather than a guessed one, so it blocks -- and it is the reason
            // CaveatCodeScopedRegistryConflict still has a caller.
            JsonNode foreignClaim = FindForeignRegistryClaimingOurScope(root, existingRegistry);
            if (foreignClaim != null)
            {
                plan.Caveats.Add(new UloopInstallCaveat
                {
                    Blocking = true,
                    Code = CaveatCodeScopedRegistryConflict,
                    Detail = "another scoped registry (" + foreignClaim["url"].AsString("?")
                        + ") already lists " + UloopPackageId + " in its scopes"
                });
                return plan;
            }

            plan.Method = UloopInstallMethod.ManifestScopedRegistry;
            plan.ManifestPath = manifestPath;
            plan.BackupPath = manifestPath + BackupFileSuffix;
            plan.ManifestBefore = manifestText;
            plan.ManifestAfter = MatchLineEndings(
                BuildManifestAfterText(root, existingRegistry), manifestText);
            return plan;
        }

        /// <summary>
        /// True when <see cref="UloopDetector"/> considers uLoop already an installed
        /// dependency -- but deliberately scanning ONLY <paramref name="root"/>'s
        /// "dependencies" object, not the whole raw manifest.json text the way
        /// <see cref="UloopDetector.DetectInProject"/> does for its own (different)
        /// purpose (deciding whether to register overlapping UapOps tools, where a
        /// false positive is harmless -- worst case a tool is skipped that uLoop
        /// covers anyway). Re-serializing just the "dependencies" subtree via
        /// JsonWriter and handing THAT to <see cref="UloopDetector.IsPresentInManifest"/>
        /// still fully reuses its exact substring-match logic (including both ids on
        /// its known-ids list), only with a NARROWER input string.
        ///
        /// This narrowing fixes a real false positive found by this stream's own unit
        /// tests: a project can have a "scopedRegistries" entry whose "scopes" array
        /// already lists <see cref="UloopPackageId"/> (declaring the registry unlocked
        /// for that package) WITHOUT the package actually being a "dependencies" entry
        /// -- e.g. a shared team manifest template, or a leftover scope from a
        /// previously-removed dependency. Scanning the whole raw text (as
        /// UloopDetector.IsPresentInManifest does when fed the full file, and as this
        /// method's first implementation during this stream did) matches the quoted id
        /// inside that scopes array and incorrectly reports "already installed" --
        /// which would make the Install button disappear for a project where uLoop is
        /// NOT actually a dependency yet. Scoping the scan to "dependencies" only
        /// closes that gap while still being pure/offline/side-effect-free.
        /// </summary>
        private static bool IsAlreadyInstalled(JsonNode root)
        {
            return UloopDetector.IsPresentInManifest(JsonWriter.Write(root["dependencies"]));
        }

        private static void AddVccAndOfflineCaveats(UloopInstallPlan plan, string projectRoot, Func<bool> isNetworkAvailable)
        {
            // -- VCC/VPM (blocking? decided NON-blocking -- see justification below) --
            //
            // VCC/VPM projects declare their managed state via a sibling
            // vpm-manifest.json next to manifest.json (the same convention
            // ARCHITECTURE.md risk #12 and this class's own doc comment reference). A
            // VPM-managed project's package manager CAN overwrite or revert a manual
            // manifest.json edit the next time VCC resolves the project -- but it does
            // not happen immediately or silently, and re-running VCC's own resolve is
            // always available as a recovery path. The existing UI text already reads
            // as advisory ("...prefer installing through VCC") rather than prohibitive,
            // so this caveat is non-blocking to match: the button still works, the user
            // is warned first, exactly mirroring the caveat/warning split
            // UloopInstallPlan.CanProceed()'s doc comment describes ("a blocker disables
            // the button, a warning only annotates the confirmation card").
            string vpmManifestPath = VpmManifestPath(projectRoot);
            if (SafeFileExists(vpmManifestPath))
            {
                plan.Caveats.Add(NewCaveat(CaveatCodeVccProject, blocking: false, detail: vpmManifestPath));
            }

            // -- Offline: cheap, honest, best-effort only (task spec: "if you cannot
            // detect it reliably without network access, say so and do not emit a false
            // negative") -- also non-blocking: editing manifest.json's scoped-registry
            // stanza is a pure text operation that always succeeds regardless of
            // connectivity; only the SUBSEQUENT Client.Add resolve against OpenUPM needs
            // the network, and per ApplyManifestRoute's doc comment that happens
            // asynchronously after Apply() has already returned, surfacing through
            // Unity's own (recoverable, visible) package-resolution UI rather than
            // corrupting anything this class wrote. See IsNetworkAvailable's doc comment
            // for exactly what this heuristic can and cannot detect.
            if (!SafeIsNetworkAvailable(isNetworkAvailable))
            {
                plan.Caveats.Add(NewCaveat(CaveatCodeOffline, blocking: false, detail: string.Empty));
            }
        }

        /// <summary>
        /// Finds the "scopedRegistries" entry, if any, whose "url" refers to the same
        /// registry as <paramref name="targetUrl"/> -- returns null when none does.
        ///
        /// <para><b>History: this used to be <c>HasConflictingScopedRegistry</c>, and it
        /// matched on registry NAME first.</b> That version compared
        /// <c>entry["name"]</c> against <c>OpenUpmRegistryName</c> ("OpenUPM") and only
        /// looked at "url" once a name match was already found, treating a same-name/
        /// different-url entry as the one and only blocking conflict. Three independent
        /// adversarial reviews of a real end-to-end run (2026-08-04) found this
        /// backwards, in two directions at once:</para>
        /// <list type="bullet">
        /// <item><b>False negative (silent duplicate).</b> A project already on OpenUPM
        /// under any other display name -- the very common "OpenUPM Registry", or a
        /// lowercase "openupm" -- produced NO name match at all, even though its url
        /// was byte-for-byte <see cref="OpenUpmRegistryUrl"/>. The old code reported "no
        /// conflict, no existing entry" and <see cref="BuildManifestAfterText"/> then
        /// appended a SECOND scopedRegistries entry pointing at the exact same host --
        /// shown on the confirmation card as a safe additive change, silently.</item>
        /// <item><b>False positive (false block).</b> "https://package.openupm.com/" --
        /// one trailing slash, a form OpenUPM's own docs emit -- matched
        /// <c>OpenUpmRegistryName</c> by name, then failed the ordinal url compare
        /// against the un-slashed constant, and was declared a BLOCKING conflict. Apply
        /// was disabled for a manifest that needed no resolution whatsoever.</item>
        /// </list>
        /// <para>Both failures share one cause: the registry's "name" field is a
        /// user-chosen display label with no bearing on which host it actually points
        /// to, so matching on it first was always going to disagree with reality in
        /// both directions. The fix makes the URL the ONLY key: normalize by trimming a
        /// single trailing slash and comparing scheme+host case-insensitively (see
        /// <see cref="NormalizeRegistryHost"/>), which fixes the trailing-slash false
        /// block directly, and stop consulting "name" at all, which fixes the
        /// differently-named-same-url false negative directly (that entry is now FOUND
        /// and merged into, whatever it is called).</para>
        /// <para>There is no longer a "conflict" outcome from this method at all: once
        /// name is out of the equation, a scopedRegistries entry either points at our
        /// url (found -- <see cref="BuildManifestAfterText"/> merges our scope into it,
        /// leaving its name and every other scope untouched) or it does not (not found
        /// -- it is an unrelated registry, left completely untouched, and a brand new
        /// entry is appended for ours). Neither outcome is something this class needs to
        /// refuse; see <see cref="CaveatCodeScopedRegistryConflict"/>'s doc comment for
        /// why the caveat code itself is kept even though nothing here raises it any
        /// more.</para>
        /// <para><c>internal</c> (not <c>private</c>) as a TEST SEAM so this predicate
        /// can be pinned directly, independent of <see cref="Plan(string,Func{bool})"/>'s
        /// other branches -- the same reasoning that made this a seam in the first
        /// place (see this stream's git history): a full end-to-end Plan() call cannot
        /// tell "this predicate is wrong" apart from "a different, earlier check is
        /// wrong" when both would surface as the same single failing assertion.</para>
        /// </summary>
        internal static JsonNode FindMatchingScopedRegistry(JsonNode root, string targetUrl)
        {
            JsonNode registries = root["scopedRegistries"];
            if (!registries.IsArray)
            {
                return null;
            }
            foreach (JsonNode entry in registries.Items)
            {
                if (!entry.IsObject)
                {
                    continue;
                }
                if (UrlsMatchAsRegistry(entry["url"].AsString(string.Empty), targetUrl))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// The scopedRegistries entry, if any, that is NOT
        /// <paramref name="ours"/> and already lists <see cref="UloopPackageId"/>
        /// among its scopes -- a company mirror, a VPM feed, or any other registry
        /// the project has deliberately pointed this package at.
        ///
        /// Matching registries by URL made every non-matching entry "unrelated, leave
        /// it alone", which is right for a registry serving other packages and wrong
        /// for one serving THIS package: appending ours next to it leaves two
        /// registries claiming one scope, and which of them Unity resolves from is
        /// not something this class can predict. Blocking is the honest answer, and
        /// it is a checkable condition rather than a guess -- which is what separates
        /// it from the name-matching heuristic this replaced.
        /// </summary>
        internal static JsonNode FindForeignRegistryClaimingOurScope(JsonNode root, JsonNode ours)
        {
            JsonNode registries = root["scopedRegistries"];
            if (!registries.IsArray)
            {
                return null;
            }
            foreach (JsonNode entry in registries.Items)
            {
                if (!entry.IsObject || ReferenceEquals(entry, ours))
                {
                    continue;
                }
                if (ScopesContain(entry["scopes"], UloopPackageId))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> identify the same
        /// scoped registry host once a single trailing slash is trimmed from each and
        /// the remaining scheme+host is compared case-insensitively -- e.g.
        /// "https://package.openupm.com" and "https://package.openupm.com/" match;
        /// "https://package.openupm.com" and "https://PACKAGE.OPENUPM.COM" match;
        /// "https://package.openupm.com" and "https://example.invalid" do not.
        /// </summary>
        private static bool UrlsMatchAsRegistry(string a, string b)
        {
            return string.Equals(NormalizeRegistryHost(a), NormalizeRegistryHost(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reduces a registry url to "scheme://host" for comparison, trimming exactly
        /// one trailing slash first (both OpenUPM's own docs and at least one real
        /// project manifest measured during this stream write the trailing-slash form,
        /// so this is not a hypothetical). A url that fails to parse (rare -- would mean
        /// the manifest's "url" field is not actually a URL at all) falls back to the
        /// trimmed literal string rather than throwing: a malformed url should compare
        /// as "not equal to anything sensible", never crash Plan() outright.
        /// </summary>
        private static string NormalizeRegistryHost(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }
            string trimmed = url.Trim();
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '/')
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }
            Uri parsed;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out parsed))
            {
                return parsed.Scheme + "://" + parsed.Host;
            }
            return trimmed;
        }

        /// <summary>
        /// Builds the mutated manifest DOM and serializes it back to text via
        /// <see cref="PrettyPrintManifest"/>. Deliberately touches ONLY
        /// "scopedRegistries" -- never "dependencies" (2026-08-03 revision: see the
        /// class doc comment for why hand-writing a dependency version was dropped
        /// entirely). Two cases, both idempotent:
        /// <list type="bullet">
        /// <item><paramref name="existingRegistry"/> is null: no scopedRegistries entry
        /// points at <see cref="OpenUpmRegistryUrl"/> yet (or "scopedRegistries" itself
        /// is absent) -- append a brand new entry with "scopes": [<see cref="UloopPackageId"/>].
        /// Any OTHER pre-existing registries (including one that happens to share our
        /// display name but points elsewhere) are left exactly as they were.</item>
        /// <item><paramref name="existingRegistry"/> is non-null: a scopedRegistries
        /// entry already points at our url (found by
        /// <see cref="FindMatchingScopedRegistry"/>, whatever that entry's own "name"
        /// happens to be) -- MERGE by adding <see cref="UloopPackageId"/> to its
        /// existing "scopes" array only if not already present, leaving every other
        /// scope, and the entry's own "name", untouched. This is the "normal case" a
        /// project already using OpenUPM for other packages hits: its existing scopes
        /// must not be clobbered, duplicated, or renamed.</item>
        /// </list>
        /// </summary>
        private static string BuildManifestAfterText(JsonNode root, JsonNode existingRegistry)
        {
            if (existingRegistry == null)
            {
                JsonNode registries = root["scopedRegistries"];
                if (!registries.IsArray)
                {
                    registries = JsonNode.NewArray();
                    root.Set("scopedRegistries", registries);
                }
                registries.Add(JsonNode.NewObject()
                    .Set("name", OpenUpmRegistryName)
                    .Set("url", OpenUpmRegistryUrl)
                    .Set("scopes", JsonNode.NewArray().Add(UloopPackageId)));
            }
            else
            {
                JsonNode scopes = existingRegistry["scopes"];
                if (!scopes.IsArray)
                {
                    scopes = JsonNode.NewArray();
                    existingRegistry.Set("scopes", scopes);
                }
                if (!ScopesContain(scopes, UloopPackageId))
                {
                    scopes.Add(UloopPackageId);
                }
            }

            // No "dependencies" edit here on purpose: Client.Add(UloopPackageId) with
            // NO version, dispatched after this text is written (see ApplyManifestRoute),
            // is what adds the dependency line -- with whatever version Unity actually
            // resolves from OpenUPM. Hand-writing a version constant here would go
            // stale (measured: two real installs observed on different versions,
            // 3.0.0-beta.48 and 3.0.0-beta.71).
            return PrettyPrintManifest(root);
        }

        /// <summary>
        /// Rewrites <paramref name="text"/>'s line endings to CRLF when
        /// <paramref name="original"/> predominantly used them.
        ///
        /// PrettyPrintManifest always emits LF. On Windows -- this project's
        /// platform, with git core.autocrlf commonly on -- that silently
        /// rewrites EVERY line of a CRLF manifest, so `git status` reports the
        /// whole file changed for what the confirmation card presented as a
        /// three-line insert. The card normalized both sides before diffing,
        /// so it could not show this; the honest fix is to stop causing it
        /// rather than to disclose it, since nobody wants the line-ending
        /// churn either way.
        /// </summary>
        internal static string MatchLineEndings(string text, string original)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(original))
            {
                return text;
            }
            int crlf = 0;
            int bareLf = 0;
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != '\n')
                {
                    continue;
                }
                if (i > 0 && original[i - 1] == '\r') { crlf++; } else { bareLf++; }
            }
            if (crlf <= bareLf)
            {
                return text;
            }
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>True when <paramref name="scopesArray"/> (a JSON array node) already contains <paramref name="value"/> as a string element.</summary>
        internal static bool ScopesContain(JsonNode scopesArray, string value)
        {
            string[] scopes = scopesArray.AsStringArray();
            for (int i = 0; i < scopes.Length; i++)
            {
                if (string.Equals(scopes[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // -- manifest.json IO helpers ---------------------------------------------

        internal static bool TryReadManifestText(string manifestPath, out string manifestText)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    manifestText = null;
                    return false;
                }
                manifestText = File.ReadAllText(manifestPath);
                return true;
            }
            catch (Exception)
            {
                manifestText = null;
                return false;
            }
        }

        /// <summary>
        /// Where a VCC/VPM-managed project declares itself. One method so
        /// the uninstall path (UloopUninstaller) raises the same caveat
        /// against the same file rather than re-deriving the convention --
        /// a second copy of this path is exactly how the two halves would
        /// drift into warning about different things.
        /// </summary>
        internal static string VpmManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Packages", "vpm-manifest.json");
        }

        internal static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Cheap, local-only, best-effort "is this machine plainly offline" probe:
        /// <see cref="NetworkInterface.GetIsNetworkAvailable"/> inspects the local
        /// interface table only (no packets sent, no DNS/HTTP round trip), so it is
        /// safe to call synchronously from a pure planning function without risking an
        /// editor-thread stall.
        ///
        /// HONEST ABOUT ITS LIMITS (task spec: "if you cannot detect it reliably...say
        /// so and do not emit a false negative"): this can only ever produce a false
        /// NEGATIVE for "offline" in the soft sense (an interface is up -- e.g. a LAN
        /// with no route to the internet, or a captive portal -- but there is still no
        /// real connectivity); it never does so for the hard case this method actually
        /// targets (no non-loopback interface has an IP at all -- airplane mode, cable
        /// unplugged, Wi-Fi off), which it reports correctly. Actually confirming real
        /// internet reachability would require genuine network I/O, which this class
        /// deliberately never performs from a pure planning function -- the offline
        /// caveat this feeds is non-blocking specifically because of this known gap,
        /// not despite it.
        /// </summary>
        internal static bool IsNetworkAvailable()
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception)
            {
                // The probe itself failing is not evidence of being offline -- fail
                // open (report "available") rather than raise a caveat off a broken
                // heuristic.
                return true;
            }
        }

        private static bool SafeIsNetworkAvailable(Func<bool> probe)
        {
            try
            {
                return probe != null ? probe() : true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        internal static UloopInstallCaveat NewCaveat(string code, bool blocking, string detail)
        {
            return new UloopInstallCaveat { Code = code, Blocking = blocking, Detail = detail ?? string.Empty };
        }

        // -- Manifest pretty-printer -----------------------------------------------
        //
        // JsonWriter.Write (Colloid.AgentPanel.Core.Json) only ever emits a SINGLE
        // LINE of compact JSON -- correct for this repo's other JSON use (CLI stdin
        // payloads, the session cache file, which nobody hand-edits) but wrong for
        // manifest.json, a file the user reads and hand-edits themselves (design
        // section 2.5's own text: manifest.json is the user's asset). Re-emitting the
        // WHOLE file through JsonWriter.Write would collapse it to one unreadable
        // line -- the opposite of "preserve the user's existing formatting as far as
        // the JSON writer allows" (task spec).
        //
        // This 2-space indenter is the compromise the task spec asks for explicitly:
        // it walks the JsonNode DOM (built by the SAME JsonParser/JsonNode this whole
        // codebase uses -- "Core.Json only for JSON" is honored) and re-serializes
        // every value using JsonWriter.Write/EscapeString for the actual scalar
        // escaping (so string/number/bool/null formatting is Core.Json's, not
        // reinvented here) while handling only indentation and newlines locally. What
        // this does NOT preserve, stated plainly per the task's requirement:
        // - Key ORDER for pre-existing keys mirrors JsonNode.Properties' enumeration
        //   order, which mirrors the underlying Dictionary's insertion order in
        //   practice (every current .NET/Mono runtime preserves insertion order for a
        //   dictionary that only ever has keys ADDED, never removed -- which is all
        //   JsonParser ever does while building this tree) -- but this is an
        //   implementation detail of Dictionary<TKey,TValue>, not a language
        //   guarantee, exactly like every other JsonNode consumer in this codebase
        //   already implicitly relies on (e.g. SessionCacheFile's WriteSession).
        // - A NEWLY added "scopedRegistries" array is always APPENDED at the end of
        //   the top-level object, even though Unity's own generated manifest.json
        //   conventionally lists "scopedRegistries" before "dependencies" -- cosmetic
        //   only, semantically identical JSON. A newly added SCOPE inside an existing
        //   registry entry's "scopes" array is appended at the end of that array for
        //   the same reason.
        // - Blank lines, inline comments (JSON has none, and JsonParser does not
        //   tolerate them anyway -- a manifest.json containing any would already be
        //   reported as unreadable/malformed before reaching this code), unusual
        //   indent widths, or trailing whitespace in the ORIGINAL file are not
        //   preserved; every value is re-indented at a fixed 2 spaces per level (the
        //   width Unity itself writes when it rewrites this file).
        // - Line endings are always "\n", regardless of what the original file used.
        // Nothing is ever DROPPED: every original key/value survives the round trip
        // through the DOM (JsonNode holds the entire document), only its exact textual
        // LAYOUT can change.
        // ---------------------------------------------------------------------------

        internal static string PrettyPrintManifest(JsonNode root)
        {
            var sb = new StringBuilder(1024);
            WritePretty(root, sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WritePretty(JsonNode node, StringBuilder sb, int indent)
        {
            JsonNode safeNode = node ?? JsonNode.Null;
            switch (safeNode.Type)
            {
                case JsonNodeType.Object:
                    WritePrettyObject(safeNode, sb, indent);
                    break;
                case JsonNodeType.Array:
                    WritePrettyArray(safeNode, sb, indent);
                    break;
                default:
                    sb.Append(JsonWriter.Write(safeNode));
                    break;
            }
        }

        private static void WritePrettyObject(JsonNode node, StringBuilder sb, int indent)
        {
            if (node.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append("{\n");
            bool first = true;
            foreach (KeyValuePair<string, JsonNode> pair in node.Properties)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }
                first = false;
                AppendIndent(sb, indent + 1);
                sb.Append('"').Append(JsonWriter.EscapeString(pair.Key)).Append("\": ");
                WritePretty(pair.Value, sb, indent + 1);
            }
            sb.Append('\n');
            AppendIndent(sb, indent);
            sb.Append('}');
        }

        private static void WritePrettyArray(JsonNode node, StringBuilder sb, int indent)
        {
            if (node.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            bool first = true;
            foreach (JsonNode item in node.Items)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }
                first = false;
                AppendIndent(sb, indent + 1);
                WritePretty(item, sb, indent + 1);
            }
            sb.Append('\n');
            AppendIndent(sb, indent);
            sb.Append(']');
        }

        private static void AppendIndent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++)
            {
                sb.Append("  ");
            }
        }

        // =========================================================================
        // Apply -- the only member that writes anything. Refuses any plan
        // CanProceed() rejects; never throws to the caller.
        // =========================================================================

        /// <summary>
        /// Carries out <paramref name="plan"/> (refusing it outright when
        /// <see cref="UloopInstallPlan.CanProceed"/> is false, or when its Method is
        /// anything other than <see cref="UloopInstallMethod.ManifestScopedRegistry"/>
        /// -- the only route this class implements post-2026-08-03-revision; see the
        /// class doc comment). Convenience entry point wiring the real file-read,
        /// file-write, and Client.Add seams; see the internal overload for the fully
        /// test-seamed version used by unit tests to inject a read/write failure
        /// without touching a real file (task spec: "inject the failure rather than
        /// corrupting a real file").
        ///
        /// <para><paramref name="log"/> defaults to null because most of what this
        /// class logs is routine progress a caller may not care to capture. The one
        /// exception -- a failed restore-from-backup, the single worst outcome this
        /// class can reach -- does NOT depend on <paramref name="log"/> being supplied:
        /// see <see cref="TryRestoreBackup"/>'s doc comment for why that specific
        /// message always reaches <c>UnityEngine.Debug.LogError</c> regardless of what
        /// (if anything) the caller passes here.</para>
        /// </summary>
        public static UloopInstallApplyResult Apply(UloopInstallPlan plan, Action<string> log = null)
        {
            return ApplyWithSeams(plan, DefaultReadAllText, DefaultWriteAllText, RealClientAdd, log);
        }

        /// <summary>
        /// Test seam behind <see cref="Apply"/>: <paramref name="readAllText"/> replaces
        /// the re-read of manifest.json <see cref="ApplyManifestRoute"/> now performs
        /// before writing anything (added 2026-08-04 -- see that method's doc comment),
        /// <paramref name="writeAllText"/> replaces every manifest.json/backup file
        /// write (so a test can make any specific read or write throw without ever
        /// touching a real file), and <paramref name="clientAdd"/> replaces the single
        /// <c>UnityEditor.PackageManager.Client.Add</c> call (so a test can exercise
        /// success/throw without ever issuing a real package-manager request).
        ///
        /// <see cref="UloopInstallMethod.GitUrl"/> plans are refused here defensively
        /// (same <see cref="ApplyFailureRefused"/> code as a null/blocked plan) even
        /// though <see cref="Plan"/> never produces one: this class retired that route
        /// entirely (class doc comment) but did not remove the enum member itself
        /// (<see cref="UloopInstallPlan"/> is a shared type this stream does not own),
        /// so a defensive refusal is what keeps a hypothetically hand-constructed
        /// GitUrl-method plan from reaching code that no longer exists, rather than
        /// silently doing nothing or throwing.
        /// </summary>
        internal static UloopInstallApplyResult ApplyWithSeams(
            UloopInstallPlan plan, Func<string, string> readAllText, Action<string, string> writeAllText,
            Func<string, AddRequest> clientAdd, Action<string> log)
        {
            if (plan == null || !plan.CanProceed() || plan.Method != UloopInstallMethod.ManifestScopedRegistry)
            {
                return new UloopInstallApplyResult
                {
                    Success = false,
                    Method = plan != null ? plan.Method : UloopInstallMethod.None,
                    FailureCode = ApplyFailureRefused
                };
            }
            return ApplyManifestRoute(plan, readAllText, writeAllText, clientAdd, log);
        }

        /// <summary>
        /// The single install route (2026-08-03 revision): back up manifest.json,
        /// write the new manifest text (registry stanza only -- see
        /// <see cref="BuildManifestAfterText"/>), then call
        /// <c>Client.Add(<see cref="UloopPackageId"/>)</c> with NO version so Unity
        /// resolves "latest from OpenUPM" itself.
        ///
        /// <para><b>Step zero (2026-08-04): refuse before touching anything if disk has
        /// moved on since Plan().</b> <paramref name="plan"/>.ManifestAfter was computed
        /// by <see cref="Plan"/> against a specific snapshot of manifest.json
        /// (<paramref name="plan"/>.ManifestBefore). Between that call and this one, the
        /// confirmation card can sit open for an arbitrary length of time -- long enough
        /// for the user to add a package from the Editor, or for a git pull to land.
        /// Before this revision, nothing here checked for that: the backup received
        /// whatever ManifestBefore said (stale), and the manifest received whatever
        /// ManifestAfter said (computed from that same stale snapshot) -- so a package
        /// added in the intervening window was erased from BOTH the live file and the
        /// backup meant to protect it, unrecoverably. This method now re-reads
        /// <paramref name="plan"/>.ManifestPath via <paramref name="readAllText"/> first
        /// and compares the result to <paramref name="plan"/>.ManifestBefore
        /// byte-for-byte; any mismatch -- including the read itself throwing, which is
        /// just as much "not what Plan saw" as a text difference is -- refuses
        /// immediately with <see cref="ApplyFailureManifestChangedSincePlan"/> before the
        /// backup write, the manifest write, or Client.Add ever run. The fix is
        /// deliberately a full-text compare, not a timestamp or hash check: this class
        /// already holds the exact string Plan captured, so comparing against anything
        /// less precise would be trading a real guarantee for a cheaper approximation
        /// for no reason.</para>
        ///
        /// <para>Three write attempts at most beyond that: backup, new manifest, and
        /// (only if either the manifest write or the Client.Add call throws) a restore
        /// write of the ORIGINAL text back over manifest.json. A failure in the first
        /// write leaves manifest.json completely untouched. A failure in the SECOND
        /// write (or in Client.Add) is followed by the restore attempt; a failure in
        /// THAT restore attempt is reported with a DISTINCT failure code
        /// (<see cref="ApplyFailureManifestWriteFailedRestoreFailed"/> /
        /// <see cref="ApplyFailureClientAddThrewRestoreFailed"/>) precisely because
        /// that is the one case where manifest.json's on-disk state is no longer
        /// known-good -- the backup file (untouched by the failed restore attempt)
        /// remains the recovery path, and <see cref="TryRestoreBackup"/> makes sure that
        /// path is actually announced (see its own doc comment).</para>
        ///
        /// <para><b>Why a Client.Add failure rolls back but a later resolve failure
        /// does not.</b> Client.Add is asynchronous: a normal (non-throwing) return
        /// here only means the request was DISPATCHED, not that OpenUPM resolution
        /// actually succeeded. If that async resolution later fails (OpenUPM
        /// temporarily unreachable, a genuine network problem the best-effort offline
        /// check above did not catch, etc.), that failure happens AFTER this method has
        /// already returned success -- this class does not poll for it (the returned
        /// <see cref="AddRequest"/> is kept on the result -- see
        /// <see cref="UloopInstallApplyResult.ClientAddRequest"/> -- and as of
        /// 2026-08-12 SettingsView DOES poll it and renders the failure in the panel)
        /// and does NOT roll back the registry-stanza edit for a failure
        /// it cannot observe from here. Leaving the scoped-registry entry in place in
        /// that case is harmless (it grants no dependency by itself) and lets the user
        /// retry the add directly from Package Manager without re-running this flow. A
        /// SYNCHRONOUS throw from the Client.Add call itself is different: the request
        /// was never even dispatched, nothing is "in flight" for Unity to finish, so the
        /// whole operation is treated as having failed and the manifest edit is undone
        /// -- symmetric with the manifest-write-failure case above.</para>
        /// </summary>
        private static UloopInstallApplyResult ApplyManifestRoute(
            UloopInstallPlan plan, Func<string, string> readAllText, Action<string, string> writeAllText,
            Func<string, AddRequest> clientAdd, Action<string> log)
        {
            string currentManifestText;
            try
            {
                currentManifestText = readAllText(plan.ManifestPath);
            }
            catch (Exception ex)
            {
                Log(log, "Refusing to apply: could not re-read '" + plan.ManifestPath
                    + "' to confirm it still matches the plan: " + ex.Message + " -- re-run Plan and try again.");
                return Failure(ApplyFailureManifestChangedSincePlan, ex.Message);
            }

            if (!string.Equals(currentManifestText, plan.ManifestBefore, StringComparison.Ordinal))
            {
                Log(log, "Refusing to apply: '" + plan.ManifestPath
                    + "' has changed since Plan() captured it -- re-run Plan and try again.");
                return Failure(ApplyFailureManifestChangedSincePlan,
                    "manifest.json content differs from the text Plan() captured; re-run Plan before Apply.");
            }

            try
            {
                writeAllText(plan.BackupPath, plan.ManifestBefore);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to write manifest backup '" + plan.BackupPath + "': " + ex.Message);
                return Failure(ApplyFailureBackupWriteFailed, ex.Message);
            }

            try
            {
                writeAllText(plan.ManifestPath, plan.ManifestAfter);
            }
            catch (Exception ex)
            {
                Log(log, "Failed to write '" + plan.ManifestPath + "': " + ex.Message + " -- restoring backup.");
                string restoreError;
                if (!TryRestoreBackup(plan, writeAllText, log, out restoreError))
                {
                    return Failure(ApplyFailureManifestWriteFailedRestoreFailed,
                        ex.Message + " / restore also failed: " + restoreError);
                }
                return Failure(ApplyFailureManifestWriteFailed, ex.Message);
            }

            AddRequest request;
            try
            {
                // NO version suffix: the scoped registry stanza just written is what
                // lets Unity resolve "latest from OpenUPM" itself (see the class doc
                // comment for why this stream stopped hand-writing a version number).
                request = clientAdd(UloopPackageId);
            }
            catch (Exception ex)
            {
                Log(log, "Client.Add('" + UloopPackageId + "') threw: " + ex.Message + " -- restoring backup.");
                string restoreError;
                if (!TryRestoreBackup(plan, writeAllText, log, out restoreError))
                {
                    return Failure(ApplyFailureClientAddThrewRestoreFailed,
                        ex.Message + " / restore also failed: " + restoreError);
                }
                return Failure(ApplyFailureClientAddThrew, ex.Message);
            }

            return new UloopInstallApplyResult
            {
                Success = true,
                Method = UloopInstallMethod.ManifestScopedRegistry,
                ClientAddRequest = request
            };
        }

        /// <summary>
        /// Writes <see cref="UloopInstallPlan.ManifestBefore"/> back over
        /// <see cref="UloopInstallPlan.ManifestPath"/>. Shared by both rollback sites in
        /// <see cref="ApplyManifestRoute"/> so the "restore, and report distinctly if
        /// the restore itself fails" logic exists in exactly one place.
        ///
        /// <para><b>2026-08-04: the failure message here now ALWAYS reaches the Unity
        /// console.</b> Before this revision, the "CRITICAL: failed to restore
        /// manifest.json ... recover manually from '&lt;path&gt;'" message below went
        /// through <see cref="Log"/> only -- which is a no-op whenever the caller did
        /// not pass a logging delegate. <see cref="Apply"/> itself defaults
        /// <c>log</c> to null, so any caller (this stream's own tests found no
        /// SECOND real caller anywhere in this package besides
        /// Editor/UI/SettingsView.cs, but Apply's public contract does not require one
        /// to exist, and a future batch/automation caller is exactly the kind of caller
        /// likely to skip the optional parameter) that invokes
        /// <c>UloopInstaller.Apply(plan)</c> with no second argument would have this,
        /// the single most important message this class can ever produce, emitted
        /// nowhere at all. A message that says "your manifest.json may now be corrupt
        /// and here is the one file that can save you" is not something this class
        /// should ever let a missing optional parameter silence. This restore-failure
        /// path -- and ONLY this one, per the task's explicit scope; every other message
        /// in this class remains opt-in through <paramref name="log"/> -- now also goes
        /// through <c>UnityEngine.Debug.LogError</c> directly (see
        /// <see cref="LogCritical"/>), so it reaches the Unity console unconditionally,
        /// on top of whatever <paramref name="log"/> the caller supplied.</para>
        /// </summary>
        private static bool TryRestoreBackup(UloopInstallPlan plan, Action<string, string> writeAllText, Action<string> log, out string restoreErrorMessage)
        {
            try
            {
                writeAllText(plan.ManifestPath, plan.ManifestBefore);
                restoreErrorMessage = null;
                return true;
            }
            catch (Exception restoreEx)
            {
                LogCritical(log, "CRITICAL: failed to restore manifest.json after a failed write: " + restoreEx.Message
                    + " -- recover manually from '" + plan.BackupPath + "'.");
                restoreErrorMessage = restoreEx.Message;
                return false;
            }
        }

        private static UloopInstallApplyResult Failure(string code, string detail)
        {
            return new UloopInstallApplyResult
            {
                Success = false,
                Method = UloopInstallMethod.ManifestScopedRegistry,
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

        /// <summary>
        /// Real (non-test) <c>clientAdd</c> seam implementation. Returns the
        /// <see cref="AddRequest"/> <c>Client.Add</c> itself returns instead of
        /// discarding it (2026-08-04 fix -- see
        /// <see cref="UloopInstallApplyResult.ClientAddRequest"/>'s doc comment for the
        /// full history of why that mattered and what is deliberately still NOT done
        /// with it here).
        /// </summary>
        private static AddRequest RealClientAdd(string packageIdentifier)
        {
            return Client.Add(packageIdentifier);
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[UloopInstaller] " + message);
            }
        }

        /// <summary>
        /// Like <see cref="Log"/>, but for the one message in this class that must
        /// never depend on a caller having opted in: always writes to the Unity console
        /// via <c>UnityEngine.Debug.LogError</c>, IN ADDITION to invoking
        /// <paramref name="log"/> when the caller supplied one. See
        /// <see cref="TryRestoreBackup"/>'s doc comment for why this specific message
        /// earns that treatment and no other message in this class does.
        /// </summary>
        private static void LogCritical(Action<string> log, string message)
        {
            string prefixed = "[UloopInstaller] " + message;
            Debug.LogError(prefixed);
            if (log != null)
            {
                log(prefixed);
            }
        }
    }
}

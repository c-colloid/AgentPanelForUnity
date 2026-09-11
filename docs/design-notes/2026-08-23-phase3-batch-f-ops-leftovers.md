# 2026-08-23 -- Phase 3 batch F: the four OPS items Phase 1 planned but never landed

The Phase 1 roadmap listed OPS-1/2/3/11 in its first phase, but the
implementation sweep skipped them (the roadmap file said "planned";
nothing in the tree did). This batch pays that debt before Batch G
touches the same files again.

## OPS-1: uap_component_list emits the index its consumers accept

Every componentIndex-taking tool (remove / inspect / property_set /
prefab_revert_override) resolves `GetComponents(type)[componentIndex]`
-- a PER-TYPE ordinal. The list tool only emitted the all-components
position, inviting exactly one mistake: read "BoxCollider at index 2"
out of the list, pass componentIndex=2, and remove a different
component (or throw, if fewer of that type exist). Each list entry now
carries both `index` (all-components position, for display/order) and
`typeIndex` (per-type ordinal, the value consumers accept), the list
tool's Description says which is which, and all four consumers'
componentIndex schema descriptions state they take typeIndex. Tests pin
the dual numbering on a duplicate-BoxCollider GameObject and
cross-check the contract end to end: the typeIndex the list reports for
the second BoxCollider, fed to uap_component_remove, removes THAT
BoxCollider (the resized one), never the Rigidbody sitting at the same
all-components position.

## OPS-2: prefab override operations scope to the NEAREST instance root

`ResolveInstanceRoot` (shared by get_overrides / apply_overrides /
revert_overrides) used `GetOutermostPrefabInstanceRoot`. With nested
prefabs, an operation addressed at the inner instance resolved to the
OUTER root: apply wrote the outer asset, revert silently wiped every
override of the whole outer instance. It now walks
`GetNearestPrefabInstanceRoot` (keeping the Phase 5b ancestor-walk fix
for added-object targets, which resolve null on themselves), so the
operation lands on the instance the target actually belongs to; a
caller who means the outer instance addresses the outer root. Because
apply/revert can still legitimately sweep more than the caller pictured,
both result texts now name the resolved instance root and the number of
overrides swept (`AffectedOverrideCount`: non-default property
modifications + added components + added GameObjects, counted BEFORE
the operation clears them). Tests build a real nested fixture
(InnerRoot.prefab nested inside OuterRoot.prefab, instantiated in the
scene): ResolveInstanceRoot returns the inner root for an inner target
and the outer root for the outer.

MEASURED (batch F's first CI run, the designed-in probe): the hoped-for
"apply addressed at the inner instance writes the inner asset" does NOT
exist in Unity 2022.3 -- ApplyPrefabInstance on a nested instance root
returns without error and applies NOTHING, because a scene records all
overrides against the outermost instance handle. Nearest resolution
alone would therefore have traded the old silent wide-scope mutation
for a silent no-op. The landed behavior: the whole-instance mutating
tools (apply_overrides / revert_overrides) detect a nested target
(outermost root != nearest root) and refuse loudly, naming the
outermost root to address and pointing at uap_prefab_revert_override
for single-override work. Tests pin the refusal and that neither asset
changes; the read-side get_overrides keeps nearest resolution.

## OPS-3: asset write paths collapse dot segments before the Assets/ check

uap_asset_create / uap_anim_create_clip / uap_animator_edit gated their
target with a bare StartsWith("Assets/"), which "Assets/../Evil.mat"
walks straight past -- the string starts with Assets/ but resolves to
the project root. ScriptGate already had the correct pure collapse
logic for its own traversal guard; it moved verbatim to a new shared
`UapAssetPath` (ScriptGate now delegates), and the three tools call
`NormalizeUnderAssets`: backslashes normalized, dot segments collapsed,
THEN the case-insensitive Assets/ prefix check, with the error naming
where the path actually resolves. In-tree dot segments stay legal (they
collapse; the asset lands at the normalized path) -- the guard rejects
escapes, not notation. UapAssetPath is deliberately Unity-free and
compiled by the license-free smoke tier next to ScriptGate, so the
normalization table is pinned in both tiers (UapAssetPathTests in
EditMode, a Check table in the smoke Program). uap_asset_create and
uap_anim_create_clip additionally verify the asset actually EXISTS at
the created path before reporting "Created" -- AssetDatabase.CreateAsset
can fail without throwing, and a false success is worse than an error.
(uap_asset_delete's same normalization is Batch G's OPS-L2, kept
separate because its failure mode -- trashing by un-normalized path --
needs its own tests.)

## OPS-11: profile approvals are machine-bound tokens, not raw hashes

The extension-profile trust store (PanelSettings.approvedProfileHashes,
inside State.asset, inside the project folder) held raw content hashes.
A malicious project could therefore ship a pre-populated State.asset
next to matching .uap-profiles/*.json and have its instructions
injected into the system prompt without the victim ever approving
anything -- the pin survived transplantation because nothing in it was
local to the victim's machine. The store now holds
`HMAC-SHA256(machine salt, lowercased content hash)` tokens
(`ExtensionProfileTrust.ComputeApprovalToken`); the salt is a GUID
minted on first use into EditorPrefs (`MachineApprovalSalt` -- per OS
user, outside every project tree, so no project or package can ship
it). Content edits still invalidate (the hash feeds the MAC), and a
token minted under another machine's salt now fails too. Trust checks
take the salt as a parameter (resolved once per BuildStatuses pass;
`OverrideForTests` keeps EditorPrefs out of the suite), approval mints
the token, and revoke removes both the token and a legacy raw-hash
entry.

Migration is deliberately the cheap direction: legacy raw-hash entries
simply fail the token comparison, so previously approved profiles
surface as pending-approval once and the user re-approves. No
translation pass -- a raw hash in the store is exactly what the attack
ships, so honoring it in any form would keep the hole open. Losing the
EditorPrefs salt (new machine, cleared prefs) invalidates approvals the
same safe way.

## Honest residuals

- (Resolved during the batch, kept for the record:) the first CI run
  was deliberately used as the Unity-truth probe for nested
  ApplyPrefabInstance -- it measured the errorless no-op described in
  the OPS-2 section, and the refusal guard + rewritten tests landed in
  the follow-up commit.
- uap_prefab_revert_override (the single-override tool) still resolves
  the nearest root for its existence checks; its behavior on overrides
  of a NESTED instance is unmeasured (its suite covers non-nested
  instances). RevertPropertyOverride takes the live SerializedProperty,
  so it is expected to work regardless, but "expected" is not
  "measured".
- OPS-11 does not rotate the salt after exposure; EditorPrefs is
  readable by any local process running as the user. The threat model
  here is a malicious PROJECT, not a compromised machine -- a local
  attacker who can read EditorPrefs can already do worse.

# 2026-08-23 -- Phase 3 batch G: no more false successes, and writes that stay in their lane

The through-line of this batch is one defect class the review kept
finding in the UapOps write tools: a Unity API that refuses work by
returning null / silently doing nothing, wrapped by a tool that
reported success anyway. Plus two hygiene items (scoped saves, wider
bash-gate coverage). Items: OPS-4/5/6/7/8/9/10/OPS-L2, SEC-4.

## OPS-4: animator edits validate before the auto-create

UapAnimatorEditTool auto-creates the .controller on first write op --
but it did so BEFORE checking the op name and the op's required
arguments, so a call that then failed validation left an empty
controller on disk (Undoable=false; it stayed). `ValidateWriteArgs`
now mirrors the handlers' required-argument checks (same messages) and
runs before LoadAssetAtPath/auto-create; the deeper validation
(duplicates, motion resolution, conditions) stays in the handlers,
which already ran validate-then-mutate within an existing controller.
Tests pin: missing parameterType and an unknown op both throw with NO
controller left behind.

## OPS-5: component add detects Unity's refusal

GameObject.AddComponent returns null (Console error, no exception) for
a [DisallowMultipleComponent] duplicate or an unsatisfiable
[RequireComponent]; the tool passed that null to
RegisterCreatedObjectUndo and reported "Added ...". Now: a type that
is abstract in C# is refused by name up front, and a null AddComponent
return throws with the likely causes named. Tests use a
[DisallowMultipleComponent] fixture MonoBehaviour and an abstract
fixture MonoBehaviour (LogAssert absorbs Unity's own refusal error).

MEASURED (CI, same run): UnityEngine.Collider -- the obvious "abstract
base" spelling -- is declared CONCRETE in the C# API even though the
engine refuses to add it as abstract, so the IsAbstract pre-check
cannot see it and the null-return guard catches it instead. Both paths
refuse, which is what matters; the test suite now covers each with the
fixture that actually reaches it, and the null-return message names
this native-abstract case explicitly.

## OPS-6: scene destroy verifies the object died

Undo.DestroyObjectImmediate can be refused without an exception; the
tool now throws "Unity did not destroy ..." when the GameObject
survives, the same defense-in-depth UapComponentRemoveTool has had
since its review fix. (No new test can force Unity to refuse a plain
destroy; the success path stays covered by the existing suite and the
guard is line-for-line the remove tool's proven pattern.)

## OPS-7: the property writer refuses wrong-shaped values

The enum branch of UapPropertyValueWriter learned strict shape checks
in an earlier review round; the numeric and struct branches still used
the lenient As* accessors, so a bool on an int property wrote the
default 0 and a scalar on a Vector/Color/Rect property resolved every
component to its CURRENT value -- a no-op reported as success.
RequireInteger / RequireNumber / RequireObject now front every such
branch (numeric strings stay accepted, as the accessors always parsed
them; a fractional string on an int property -- AsLong's silent-zero
corner -- is refused). Partial struct objects remain a feature: {y: 9}
still merges over the current x/z. New UapPropertyValueWriterTests
drives the writer directly over a GameObject's m_Layer, a
BoxCollider's m_Size (whole and .x), and a Camera's m_BackGroundColor.

MEASURED (CI, first run of this batch): refusing a BOOLEAN on an
Integer property was too strict and would have broken a real call --
Unity serializes plenty of conceptually-boolean settings as ints, and
TextureImporter's m_EnableMipMap is exactly that, so `true` is the
natural value to send. Worse, the pre-OPS-7 code was wrong there in
the most dangerous direction: AsLong on a bool returned its silent
default 0, so "turn mipmaps on" reported success while writing OFF
(the existing importer test only passed because the default was
already on, making its !original flip land on the accidental 0).
Integer properties therefore map a boolean to 1/0 explicitly, and the
writer test pins both directions. ArraySize stays strict -- a boolean
array size is a shape error, not an idiom.

## OPS-8: prefab-stage creates land under the prefab root

With a prefab stage open and parentPath omitted, uap_scene_create_object
placed the new object as a SECOND ROOT of the stage's preview scene --
and a stage saves only its single prefab root, so the object silently
vanished on save. The parent now defaults to stage.prefabContentsRoot
in exactly that case (an explicit parentPath still wins) and the result
text says so. The create path also reparents FIRST when a parent
exists -- SetParent carries the object into the parent's scene, which
sidesteps MoveGameObjectToScene-into-preview-scene entirely. Tests
open a real prefab stage (PrefabStageUtility.OpenPrefab) and pin both
the default and the explicit-parent case.

## OPS-9: single-asset saves stop flushing the whole session

Six write-tool sites called the argument-less AssetDatabase.SaveAssets()
to persist ONE asset -- and flushed every other dirty asset in the
session with it, including edits other code (or the OPS-4 bug) left
deliberately unsaved. All six now call SaveAssetIfDirty(target):
UapPropertySetTool (asset branch), UapAssetSetPropertyTool,
UapMaterialSetTool, UapAssetCreateTool, UapAnimCreateClipTool,
UapAnimatorEditTool.SaveController (writing the controller file
serializes its state-machine sub-assets with it). The test dirties
material A, writes material B through uap_material_set, and asserts A
is STILL dirty.

## OPS-10: single-override revert verifies the override exists

RevertPropertyOverride / RevertAddedComponent / RevertAddedGameObject
are silent no-ops when the target is not actually an override; the tool
reported "Reverted ..." regardless. Now: kind="property" requires
SerializedProperty.prefabOverride; kind="component"/"object" require
membership in GetAddedComponents/GetAddedGameObjects of the (nearest,
per OPS-2) instance root. Tests pin the two refusals (never-overridden
m_IsTrigger; the prefab-source BoxCollider) alongside the existing
three positive reverts.

## OPS-L2: asset delete gets the same path guard as create

uap_asset_delete called MoveAssetToTrash on any string -- Packages/,
ProjectSettings/, or an "Assets/../..." escape included. It now runs
UapAssetPath.NormalizeUnderAssets first, same as the create tools.

## SEC-4: the bash gate sees argument-named writes

ExtractBashWriteTargets only knew redirection (>, >>) and tee, so
`sed -i`, `cp/mv/install <src> Assets/Evil.cs` and `dd of=` sailed
past the script gate. Added: `>|` (noclobber override) to the
redirection regex, and a token-based scan (quote-aware segment
splitter, no expansion) for `sed -i/--in-place` (non-flag args after
the script), the LAST argument of cp/mv/install (conservative by
design: only the final token can be a destination, so copying a gated
file OUT of Assets/ never trips), and `dd of=`. The limits are stated
in the doc comment and here: an interpreter one-liner (python -c,
node -e), command substitution, or a variable-built path cannot be
caught by any static token scan -- this layer makes bypass
inconvenient, and the Bash permission card remains the real backstop
for hostile commands. Both tiers pin the table: ScriptGateTests
(EditMode) and the smoke Program (license-free), including the
false-positive regressions (gated source copied out, sed script that
merely mentions a gated path, staging destination, sed -n).

## Honest residuals

- OPS-7 leaves Boolean and String properties on the lenient accessors
  (a number on a bool property, a number on a string property) -- the
  reviewed scope was numeric + struct shapes; the remaining two are a
  smaller trap (no silent no-op, just a coercion) and widening them
  risks breaking legitimate 0/1-for-bool callers. The Integer branch's
  bool->1/0 mapping above is the mirror of that same pragmatism, and is
  a correctness fix rather than leniency: the alternative was writing 0
  for `true`.
- SEC-4's command scan matches bare command words only ("sed", not
  "/bin/sed" or "sudo sed") and cannot see interpreter one-liners; both
  are documented in ScriptGate itself.
- OPS-8's stage tests assume PrefabStageUtility.OpenPrefab works in
  batchmode EditMode runs; CI is the authority (same stance as batch
  F's nested-prefab probe).

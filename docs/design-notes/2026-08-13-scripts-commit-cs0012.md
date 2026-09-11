# 2026-08-13 -- uap_scripts_commit: CS0012 on any script touching compiled project types

## Problem (user report, 2026-08-13)

The panel session in a real game project reported the script-apply tool
broken with:

    error CS0012: The type 'MonoBehaviour' is defined in an assembly that
    is not referenced. You must add a reference to assembly
    'UnityEngine.CoreModule...'

A leftover (already-emptied) UapStaging/ folder in that project confirmed
uap_scripts_commit was the failing path.

## Measured root cause (live probe, DevelopmentProject, 2026-08-13)

Probe: replicate the tool's exact AssemblyBuilder setup and compile two
sources under two reference configurations, dumping
`AssemblyBuilder.defaultReferences` for each. Results:

| source | referencesOptions | result |
| --- | --- | --- |
| `class P : MonoBehaviour` | default | 0 errors |
| `class Q : UnityEngine.UI.Selectable` | default | **CS0012 (verbatim match with the report)** |
| `class P : MonoBehaviour` | UseEngineModules | 0 errors (no CS0433 either) |
| `class Q : UnityEngine.UI.Selectable` | UseEngineModules | 0 errors |

Mechanism, proven by the defaultReferences dump:

- Default set (246 refs) carries the MONOLITHIC `UnityEngine.dll` facade
  and NO engine module dlls.
- Project assemblies (Assembly-CSharp, package assemblies, SDK dlls) are
  compiled against the MODULE dlls, so their metadata references
  `UnityEngine.CoreModule` by assembly identity.
- The facade satisfies source-level lookups (`class P : MonoBehaviour`
  compiles via type forwarding), but the moment the staged source uses a
  type whose metadata points into CoreModule (base class from an
  already-compiled assembly), csc demands the defining assembly by NAME
  -> CS0012.
- Phase 5a's verification staged only self-contained scripts, which is
  exactly the case the facade covers -- the gap was invisible to the
  existing fixture (5th instance of "test written from the code's own
  assumption").

## Why the old CS0433 lesson does not conflict

Phase 5a measured that ADDING UnityEngine.CoreModule.dll via
additionalReferences produced CS0433 (MonoBehaviour in both CoreModule
and UnityEngine) -- that experiment STACKED the module onto the default
monolithic facade. `ReferencesOptions.UseEngineModules` instead REPLACES
the monolithic reference with the module set (312 refs measured; the
`UnityEngine.dll` present there is the forwarder facade). Both probe
cases compile clean under it -- no CS0012, no CS0433.

## Fix

`builder.referencesOptions = ReferencesOptions.UseEngineModules;` in
UapScriptsCommitTool.BeginCommit. additionalReferences stays
UnityEditor-only (P1: the one reference AssemblyBuilder never
auto-resolves).

## Rejected alternatives

- additionalReferences += CoreModule (keep monolithic default): the
  measured CS0433 from Phase 5a.
- Mirroring the full reference list from
  CompilationPipeline.GetAssemblies(): heavier, duplicates what
  UseEngineModules already does, and drifts from Unity's own resolution.

## Regression guard

UapScriptsCommitToolTests.Poll_StagedScriptDerivingFromCompiledAssemblyType
_CompilesAndMovesIntoAssets stages `class X : UnityEngine.UI.Selectable`
(fails with CS0012 without the fix; skips with Assert.Ignore when the
project has no com.unity.ugui).

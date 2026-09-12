# 2026-09-12 -- uap_scripts_commit: CS0012 on editor-only plugin DLLs

## Problem (live session, RPG Maker Unite project, 2026-09-12)

`uap_scripts_commit` rejected a file that Unity itself compiles without
error:

    error CS0012: The type 'WithSerialNumberDataModel' is defined in an
    assembly that is not referenced. You must add a reference to assembly
    'LibForUniteForEditor, Version=1.0.0.0, ...'

The file was an Editor script under `Assets/Editor/` using
`MapDataModel` from `RPGMaker.CodeBase.CoreSystem.dll`. That type's base
class lives in `LibForUniteForEditor.dll`, an editor-platform plugin DLL
under `Assets/RPGMaker/Codebase/CoreSystem/LibDll/`.

Two things made this look like the already-fixed
2026-08-13 CS0012 (engine modules) but it is not:

- The gate had compiled that exact file successfully several times
  earlier in the same session. It started failing after two unrelated
  `.cs` files were deleted from the same folder -- i.e. the failure
  depends on what the staged compile happens to pull in, not on an edit
  to the file.
- `referencesOptions = UseEngineModules` (the 2026-08-13 fix) was
  already in place and is unrelated: the missing assembly here is a
  project plugin, not an engine module.

## Measured root cause (live probe, the affected project)

Replicated the tool's exact `AssemblyBuilder` setup and dumped the
reference sets:

| source | result |
| --- | --- |
| `AssemblyBuilder.defaultReferences` (UseEngineModules) | 361 refs -- contains `LibForUniteForRuntime.dll`, **not** `LibForUniteForEditor.dll` |
| `CompilationPipeline.GetPrecompiledAssemblyNames()` | 22 names -- **includes** `LibForUniteForEditor.dll` |
| Unity's own `Assembly-CSharp-Editor.compiledAssemblyReferences` | 302 refs -- **includes** `LibForUniteForEditor.dll` |

So `AssemblyBuilder.defaultReferences` carries a plugin's RUNTIME half
and drops its EDITOR half, while the predefined assembly Unity actually
compiles the same file into references both. The gate was therefore
strictly less capable than the Editor it is guarding, and rejected valid
code.

Diffing the two sets by file name, 17 precompiled assemblies are absent
from `defaultReferences` in this project -- all editor-only plugin DLLs
(`LibForUniteForEditor.dll`, Mono.Cecil's editor halves, Burst's Cecil
copies, VisualScripting's dependencies, Rider's path locator, uloop's
Roslyn plugins).

## Verification

Compiled the real failing file through the tool's builder setup under
both reference configurations, plus the two cases the 2026-08-13 note
protects:

| case | current refs | + precompiled sweep |
| --- | --- | --- |
| the failing Editor script | **CS0012** (verbatim match with the report) | **0 errors** |
| `class P : MonoBehaviour` | 0 errors | 0 errors |
| `class Q : UnityEngine.UI.Selectable` | 0 errors | 0 errors |

No CS0433 regression: the sweep only adds file names that are NOT
already in `defaultReferences`, so a duplicate cannot be introduced by
construction.

## Fix

`GatherAdditionalReferences` now takes the builder's `defaultReferences`
and appends every `CompilationPipeline.GetPrecompiledAssemblyNames()`
path whose file name is missing from it. `additionalReferences` is
assigned AFTER `referencesOptions`, because `defaultReferences` is
computed from it.

The UnityEditor.CoreModule entry (P1) stays. Engine references are still
never added by name -- that is what `UseEngineModules` is for.

Failure of the sweep itself (an API throw) is swallowed: the commit
falls back to the previous UnityEditor-only set rather than failing.

## Lesson

The gate must not be stricter than the Editor. When it rejects
something, the first check should be whether Unity itself compiles the
same file -- here `uloop control-play-mode --action Status` reported
`CompileErrorCount: 0` for the very file the gate refused, which is what
turned "my code is wrong" into "the gate is wrong" and pointed the probe
at the reference set instead of the source.

# New Scene destroys the reused UI FontAsset's atlas (2026-08-03)

Live report: "NewSceneを作成したらまたエラーが発生してしまってます" — the
v0.13.1/v0.14.0 font work stopped the *warning* flood, but creating a new
scene produced a fresh flood of `MissingReferenceException` /
`ArgumentNullException` / `NullReferenceException` out of TextCore during
every UI Toolkit repaint of the panel.

## 1. Symptom (live editor, DevelopmentProject)

`uloop get-logs --log-type Error` returned 12 distinct errors, all rooted in
`UIRStylePainter.DrawText` → `UITKTextHandle.Update` → TextCore:

| # | error |
|---|-------|
| 0,1 | `MissingReferenceException: The variable m_AtlasTextures of FontAsset doesn't exist anymore` (in `FontAsset.TryAddCharacterInternal`) |
| 2-5,7,8 | `ArgumentNullException: Value cannot be null` (in `MaterialManager.GetFallbackMaterial`) |
| 6 | `MissingReferenceException: The variable m_Material of FontAsset doesn't exist anymore` |
| 9,11 | `MissingReferenceException: The object of type 'Material' has been destroyed` (in `MaterialManager.GetFallbackMaterial`) |
| 10 | `NullReferenceException` in `UIRStylePainter.DrawTextInfo` |

## 2. Root cause, measured

### 2.1 The live asset's state

A read-only `execute-dynamic-code` probe against the running editor:

```
== 'UAP_JA_UI_FONT' hideFlags=HideAndDontSave popMode=DynamicOS persistent=False
   material:  refnull=False unityNull=True      <- dangling: destroyed
   atlasTextureCount=4 multiAtlas=True
   tex[0..3]: refnull=False unityNull=True      <- dangling: destroyed
FRESH CreateFontAsset: hideFlags=None / material hideFlags=None / tex[0] hideFlags=None
```

So the **FontAsset itself survived** (it carries `HideAndDontSave`, which
includes `DontUnloadUnusedAsset`), while **every child object it owns was
destroyed**. `FontAsset.CreateFontAsset` hands back children with
`hideFlags = None`; v0.14.0 stamped the parent only.

### 2.2 What destroys them — isolated in the sandbox

Sandbox `AITemp`, batch mode, `Assets/Editor/UapFontProbe.cs`. Asset **A**
= v0.14.0 behaviour (parent stamped only); asset **B** = parent *and*
children stamped. Both grown to 9 atlas textures via `TryAddCharacters`:

```
== step 1: EditorUtility.UnloadUnusedAssetsImmediate() ==
    A  mat=alive  all used textures alive
    B  mat=alive  all used textures alive       <- NOT the culprit
== step 2: EditorSceneManager.NewScene(DefaultGameObjects, Single) ==
    A  mat=DEAD   9/9 used textures DEAD        <- the culprit
    B  mat=alive  0/9 used textures DEAD        <- stamping is a complete fix
== step 3: second NewScene ==
    A  mat=DEAD   9/9 DEAD
    B  mat=alive  0/9 DEAD
== step 4: reuse the broken A ==
    A.TryAddCharacters(...) returned False      <- unusable, not recoverable
```

Three findings that shape the fix:

1. **`Resources.UnloadUnusedAssets` is innocent.** Only the scene unload
   performed by `NewScene` destroys the children. Guessing at
   "UnloadUnusedAssets probably did it" would have produced the wrong fix.
2. **`HideAndDontSave` on the children is sufficient and complete.**
3. **A broken asset cannot heal.** `TryAddCharacters` returns `false`, and
   because the FontAsset carries `HideAndDontSave` it also survives every
   subsequent domain reload — so once broken it stays broken for the whole
   editor session, and `FindExistingMarkedAsset` keeps handing it back.

### 2.3 Stamping once at creation is NOT enough

Same probe, asset B was stamped immediately after creation and then grown:

```
B atlasCount=9  texFlags=[HideAndDontSave, None, None, None, None, None, None, None, None]
```

TextCore's multi-atlas growth creates each **new** atlas texture with
`hideFlags = None`. Atlas textures appear lazily, during text generation, as
new glyphs are rasterised — there is no creation hook to piggyback on. The
fix therefore needs a cheap recurring re-stamp, not a one-shot.

### 2.4 The `atlasTextures` array is over-allocated

`atlasTextures.Length` was 16 while `atlasTextureCount` was 9; the trailing
7 entries are legitimately `null`. A health check must iterate
`atlasTextureCount`, not `atlasTextures.Length`, or every healthy asset
reads as broken. (The first draft of the probe made exactly this mistake.)

### 2.5 TextCore's fallback materials are NOT a second error source

Errors 2-5 and 7-11 come out of `MaterialManager.GetFallbackMaterial`, so
the obvious worry was that its static cache of per-atlas materials is
destroyed too — which we could not fix by stamping our own asset. Probe #3
drove that cache directly through reflection:

```
fallback[1..4] = hideFlags=HideAndDontSave (created that way by TextCore)
cache BEFORE NewScene: count=4 alive=4 destroyed=0
cache AFTER  NewScene: count=4 alive=4 destroyed=0
re-request fallback[1] = alive
```

TextCore already protects them. The `GetFallbackMaterial` failures were
about the **`sourceMaterial` argument** — our FontAsset's own destroyed
`material` — so fixing 2.2 fixes all 12 errors, not just the first two.

## 3. Options considered

| # | option | verdict |
|---|--------|---------|
| 1 | Go back to destroy-and-recreate on `beforeAssemblyReload` | **Rejected.** That is precisely the v0.13.x defect (1106× `MissingReferenceException`) that v0.14.0 fixed, and it would not help here at all: `NewScene` is not a domain reload. |
| 2 | Disable multi-atlas (`isMultiAtlasTexturesEnabled = false`) so only one, creation-time-stamped texture ever exists | **Rejected on measurement.** Probe #2: a single 1024×1024 SDFAA atlas at the default 90pt holds **104 CJK glyphs** (2896 of 3000 requested went missing). Unusable for Japanese UI. |
| 3 | Re-stamp only from scene-lifecycle callbacks (`sceneClosing`, `newSceneCreated`, …) | **Rejected.** Requires enumerating every code path that can unload a scene; a missed one silently reintroduces the bug. |
| 4 | **Stamp children at creation/reuse + cheap recurring guard + reject unhealthy assets at resolve** | **Chosen.** |

## 4. Chosen design

1. **`StampSubObjects(FontAsset)`** — sets `HideFlags.HideAndDontSave` on
   the asset's `material` and on every non-null entry of `atlasTextures`.
   Idempotent; only writes when the flag differs.
2. **Stamp at both entry points** — right after `CreateFontAsset`, *and* on
   the re-find-and-reuse path (an asset created by an older version arrives
   with unstamped children).
3. **Recurring guard** — an `EditorApplication.update` hook, installed
   lazily the first time an asset resolves, that re-stamps when
   `atlasTextureCount` has changed since the last stamp. The guard is an
   `int` comparison per editor frame in the common case. A count change is
   the only way a new atlas texture can appear, and it also catches
   `ClearFontAssetData` (which resets the count to 1).
4. **`IsHealthy(FontAsset)`** — `material` alive and the first
   `atlasTextureCount` atlas textures alive (see 2.4).
5. **`FindExistingMarkedAsset` rejects unhealthy assets.** A marked asset
   whose children were destroyed by a pre-fix session is provably unusable
   (2.2 step 4) and would otherwise persist for the whole editor session, so
   it is destroyed at the same point the dedupe path already destroys
   extras, and a fresh asset is created. This is safe for the same reason
   dedupe is: resolve only runs on the first request of a domain load, before
   any live `VisualElement` has been styled with the result.

Users already running a broken editor therefore recover on the domain
reload that installs this version — no editor restart required.

## 5. Regression guards

`Tests/Editor/FontLoaderTests.cs`:

- `StampSubObjects_MarksMaterialAndEveryAtlasTexture` — the property that
  2.2 proves implies survival, plus idempotency (the guard runs per frame).
- `GrownAtlas_NewTexturesComeBackUnstamped_AndSpareSlotsStayHealthy` — one
  growth pass pinning both 2.3 and 2.4 (rasterising is the slow part, so
  the two assertions share it).
- `IsHealthy_FalseWhenMaterialDestroyed` / `...WhenUsedAtlasTextureDestroyed`
  / `IsHealthy_FalseForNull`.
- `FindExistingMarkedAsset_RejectsAndDestroysUnhealthyAsset` — pins 4.5.
- `AtlasGuardTick_ReStampsOnlyWhenAtlasCountChanged` — pins the guard's
  cheap-path contract in both directions.
- `JapaneseUiFontAsset_ResolvedAssetHasStampedSubObjects` — end-to-end.

The `NewScene` causation itself is **not** in the suite: a scene-unloading
test would disturb the other 1450+ EditMode tests. It is pinned by the
sandbox probe transcript reproduced in section 2.2 instead.

## 6. Verification

- Sandbox `AITemp`: compile clean, full EditMode suite **1466 total /
  1461 passed / 0 failed / 5 skipped** (was 1458 before the 8 new tests;
  all 8 ran, none ignored).
- Live editor (`DevelopmentProject`), after `uloop compile`:
  - The broken asset was rejected and destroyed, a fresh one created
    (`JapaneseUiFontSource = "osasset:Yu Gothic UI"`, i.e. the create path,
    not the reuse path), `material` and `tex[0]` both `HideAndDontSave`,
    exactly one marked asset alive. **Recovery needs no editor restart.**
  - Recurring guard, end to end: forcing 200 CJK glyphs grew the atlas to 3
    textures and `tex[1] tex[2]` read `None` in that same frame; a later
    frame read `HideAndDontSave` on all three. The spare slot
    (`arrayLen=4`, `atlasTextureCount=3`) stayed null and the asset stayed
    healthy, exercising 2.4 in production.
  - `uloop get-logs --log-type Error` went from 12 errors to **0**.

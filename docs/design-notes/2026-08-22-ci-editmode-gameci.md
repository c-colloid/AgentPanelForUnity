# 2026-08-22 -- CI: EditMode tests on real Unity (INFRA-1)

Roadmap item INFRA-1: the ~2,200 EditMode tests had no automated runner, so
every change (including the Phase 1 security fixes) shipped unverified by
machine. This adds that runner as the safety net every later fix relies on.

## Why CI (not in-environment Unity)

The Claude Code remote environment's egress policy blocks Unity's download and
licensing hosts (`download.unity3d.com`, `license.unity3d.com`, ... all return
proxy 403), so Unity cannot be installed or activated there. GitHub's runners
have open network access and consume the Unity credentials as repo secrets, so
CI is the supported place to run the real tests.

## Two tiers

- **`dotnet-smoke.yml`** — license-free. Compiles the package's Unity-free
  source (JSON core / ScriptGate / GateHookInstaller) verbatim and asserts
  against the real production methods (`ci/SmokeTests`); seconds per run,
  green today. Catches SEC-1/CORE-1-class regressions without Unity.
- **`editmode-tests.yml`** — full EditMode suite on a real Unity Editor.

## How the full job activates Unity (the load-bearing detail)

Runs **inside the GameCI editor image** (`unityci/editor:ubuntu-2022.3.22f1-base-3`)
and activates a **Personal seat directly with the Unity Licensing Client**:
`Unity.Licensing.Client --activate-all --include-personal --username --password`,
then `Unity -batchmode -runTests -testPlatform EditMode`, then `--deactivate-all`.

This works from **UNITY_EMAIL + UNITY_PASSWORD alone** (no serial, no `.ulf`).

### Journey (why not game-ci/unity-test-runner)

First attempts used `game-ci/unity-test-runner@v4` and a `game-ci/unity-license-activate`
acquire job. Real runs proved:
1. `game-ci/unity-request-activation-file` hard-errors "This action is no longer
   supported" (Unity removed the manual web activation it automated).
2. `unity-test-runner@v4` fast-fails with "Missing Unity License File and no
   Serial was found" even with UNITY_EMAIL/PASSWORD present — its legacy
   activation path requires UNITY_LICENSE (a `.ulf`) or UNITY_SERIAL and does
   NOT accept credential-only Personal.

The user's own working repo (`c-colloid/UITKFontFix`, same Unity 2022.3.22f1)
showed the right pattern: skip the GameCI actions, use the editor image as the
job container, and call the Licensing Client directly with `--include-personal`.
This file's approach is adapted from it. Lesson: credential-only Personal
activation IS possible, just not via unity-test-runner's activation path.

## Host project

`ci/HostProject/` embeds the package as a `file:` dependency, lists it in
`testables`, includes `com.unity.test-framework` + the built-in modules, and
commits a full default `ProjectSettings/` for the pinned version so Unity opens
deterministically. `Library/` etc. are git-ignored.

## Operator setup (one-time)

Add `UNITY_EMAIL` + `UNITY_PASSWORD` as **Actions repository secrets** (not
Environment/Codespaces/Dependabot — those are invisible to the workflow; a run
whose `env:` echo shows them blank rather than `***` means wrong scope).
Full steps in `ci/README.md`.

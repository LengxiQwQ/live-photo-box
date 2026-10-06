# P5 Verify — Fresh Verification and External Gate Handoff

> **Role:** Fresh Verifier; production/test/spec changes are out of scope.
> **Order:** R0–R6 → independent Audit → Verify → External Chief Gate. Verify does not accept or complete P5.
> **Audit applicability base:** final Audit HEAD `9dd03aed2e46ddaca29beb139756958e12516092`; Roadmap revision `317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7`. The actual Verify execution HEAD is established after durable publication. It must be a clean descendant whose documentation-only delta from this base is independently proven non-result-affecting and covered by required push CI on that exact HEAD.

This document derives verification duties from the P5 main Roadmap, P5 overview, R0–R6 details/TASKs, Neutral Media Contract V4.0, the final Audit report, and the repository bootstrap. It does not add or change product acceptance criteria. If the final Audit or Roadmap revision changes, rebind applicability before starting.

## 1. Verifier role and result boundary

Audit asks whether the implementation, call paths, and evidence credibly satisfy P5. Verify asks whether a fresh verifier can reproduce the required P5 build, scoped tests, RealSample routes, independent validation, and runtime/package evidence on one exact final HEAD.

- The Fresh Verifier is read-only for production code, tests, profiles, manifests, validators, thresholds, Roadmap acceptance, and public Contract/ABI.
- A required failure is reported to its owning R/TASK/Implementer. Do not repair source as Verifier.
- A result-affecting repair invalidates affected Verify evidence; review its applicability and return through the appropriate Audit/owner gate before fresh verification.
- The only local Verify outcomes are `P5 VERIFICATION: READY FOR EXTERNAL CHIEF GATE` and `P5 VERIFICATION: FAIL`.
- `READY FOR EXTERNAL CHIEF GATE` is not P5 `ACCEPT`, completion, or authorization to start P6.

## 2. Authority and entry gate

Use, in order, the P5 main Roadmap and Neutral Media Contract; P5 overview/INDEX and R0–R6 details; the active Verify TASK; then source, tests, versioned profiles, immutable samples, validators, and execution evidence. P4 Verify is a role/process precedent only; P4-specific acceptance is not imported into P5.

Before the independent Verify begins, require:

1. Fresh Audit closeout independently reviewed with the exact conclusion `READY FOR VERIFICATION`.
2. The orchestrator has separately transitioned `.ai/state.json`'s `active_task` to the Verify TASK. Drafting/review of this document alone does not activate it.
3. Roadmap revision remains `317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7`.
4. After durable publication, `HEAD == origin/master == <current Verify execution HEAD>`, the tree is clean, and P5 remains `in_progress`.
5. The final Audit report, final-head identity, evidence index, publication applicability bridge, and required push CI on the exact Verify execution HEAD are readable and bind this tree.
6. The publication bridge confirms the Audit applicability base is exactly `9dd03aed2e46ddaca29beb139756958e12516092`; the descendant delta contains only this durable 09 detail; no production, tests, profile, sample, validator, threshold, Contract/ABI, workflow, or acceptance file changed; and the final Audit semantic conclusions remain applicable. The new required push CI must succeed on the actual Verify execution HEAD.

Run and retain the actual output of:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .ai/reconcile.ps1
git status --porcelain=v2 --branch
git rev-parse HEAD
git rev-parse origin/master
git diff --check
```

If HEAD, upstream, Roadmap revision, active task, Audit identity, or tree status differs, stop. Reconcile and rebind before using any result; never combine results from different trees into one PASS.

## 3. Cost-aware evidence policy

Apply identity checks before rerunning anything. Keep the distinction between a fresh verifier run and a previously passing result.

| Evidence | Verify treatment |
|---|---|
| Release/x64 build and the P5-scoped current-head test filters below | Fresh-run once on the bound HEAD. Confirm actual artifacts/load paths and the exact test scope/counts. |
| Required hash-locked RealSample routes and frozen R3/R4/R5 independent validators | Execute the phase-owned routes/validators specified below with zero skips. Preserve fixed output locations and prior attempts; do not alter profiles, tools, or thresholds. |
| Build & Release run `37452709386` and CodeQL run `37452709481` | These successful runs bind the Audit applicability base `9dd03aed2e46ddaca29beb139756958e12516092` only. The durable Verify-authority publication changes HEAD; require new push-triggered Build & Release and CodeQL success on the exact current Verify execution HEAD. Do not rerun the old same-SHA runs. If Verify HEAD changes again, require CI on the new HEAD. |
| R6 retained-artifact matrix | Reuse the final-head F-03 matrix only through its exact final-head/canonical-content identity binding. The current `P5R6_` filter is still fresh-run. |
| Historical R2–R5 independent evidence | Reuse an item only when source/test/profile/sample/tool/validator/threshold identities and assumptions are unchanged and the evidence covers the claim. The final Native repair invalidates old Native/package binary hashes; rebuild and prove current runtime/package identity. |
| Audit's E/R/N matrix | Use as an applicability map, not as Verify execution evidence. Verify must map its own fresh commands, filters, outputs, RealSamples and validators to the unchanged criteria. |

Never rerun a passing same-SHA remote CI workflow, broad R0–R6 campaigns, or immutable independent inputs just to refresh timestamps. Never claim a required current-head test/RealSample route was executed when only historical evidence exists. Do not run the P0–P10 suite. In particular, do not use `scripts/verify.ps1 -Scope Fast`, `Full`, or `Release`; they include repository-wide tests/integration outside this P5 Verify scope.

## 4. Build and current artifact identity

Run exactly one canonical Release/x64 build, using the accepted R5 sidecar prerequisite identity for the entire fresh Verify campaign. Keep these commands in the same PowerShell session so `$verifyHead`, `$shortSha`, and the derived record IDs stay bound to the entry HEAD. Use the existing component build scripts explicitly; the generic Build wrapper does not pass the selected sidecar identity to its Native build.

```powershell
$verifyHead = (git rev-parse HEAD).Trim()
$shortSha = $verifyHead.Substring(0, 7)
$nativeAttempt = 1
$nativeRecordId = "p5-verify-$shortSha"
while (Test-Path ".ai-tmp/workspace/P5-R5/$nativeRecordId-native-Release-build.log") {
    $nativeAttempt++
    $nativeRecordId = "p5-verify-$shortSha-attempt$nativeAttempt"
}

dotnet restore LivePhotoBox/LivePhotoBox.csproj --nologo
dotnet restore LivePhotoBox.CLI/LivePhotoBox.CLI.csproj --nologo
dotnet restore tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj --nologo
dotnet restore tools/p5-r5-video-validation/route-driver/P5R5.RouteDriver.csproj --nologo

powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts/native/build-native.ps1 `
  -Configuration Release `
  -Architecture x64 `
  -RunTests `
  -VideoSidecarBuildId r5-main10-ci `
  -ExecutionRecordId $nativeRecordId

dotnet build LivePhotoBox/LivePhotoBox.csproj `
  -c Release -p:Platform=x64 `
  -p:SkipNativeBuild=true `
  -p:EnableMsixTooling=false `
  --no-restore --nologo

dotnet build LivePhotoBox.CLI/LivePhotoBox.CLI.csproj `
  -c Release -p:Platform=x64 `
  -p:SkipNativeBuild=true `
  --no-restore --nologo
```

`build-native.ps1 -RunTests` builds the registered Native CTest tests, runs CTest, builds and runs the managed `NativeRuntimeTests` harness, and emits Native ABI/runtime evidence. The following commands build GUI/Core and CLI without rebuilding Native. This is the bounded Release/x64 Build gate; it does not invoke the full Core/CLI test suites. Record every command's real exit code/output, Native/Core/CLI artifact hashes, configuration/platform, Native ABI/version/capability identity, and the actual Native DLL/runtime paths loaded by tests. A stale DLL or a file merely present in an old output directory is not evidence of the current build. The Native script's create-only log is the file `.ai-tmp/workspace/P5-R5/<ExecutionRecordId>-native-Release-build.log`; the unused-ID selection above prevents overwriting prior records.

Run the fresh Core filters below with `-p:SkipNativeBuild=true` only after the canonical Native build, so test runs use the bound Release artifacts, except for the R5 Core filter: that command must build/copy the frozen minimal-libav sibling using the same `VideoSidecarBuildId=r5-main10-ci`. The R5 step may rebuild Native, but it uses the same frozen prerequisite identity as the initial canonical build. Its Native/sidecar outputs are the final artifacts that runtime/package identity checks must attest, so perform those checks after R5. Save TRX where a validator consumes it.

## 5. R0–R6 execution matrix

All test commands run from repository root, in Release/x64, with the P5 sample cache and deterministic output paths selected by the referenced R/TASK/profile. Restore is explicitly completed by the four `dotnet restore` commands in Section 4; retain `--no-restore` on scoped Core and route-driver commands. For every command record exact argv, exit code, actual discovered/executed/passed/failed/not-executed counts, filter, and evidence output. Inspect the filter scope before interpreting counts.

### R0 — contract and baseline

No product test campaign is needed. Confirm the current R0 authority, required semantic vocabulary, owner/policy/no-partial-output matrix, and boundary references still resolve to the same approved P5/R0–R6/Neutral criteria. Report documentation consistency; do not infer runtime behavior from this check.

### R1 — converter contract and execution truth

Fresh current-head contract filter; require the existing eight contract tests, all executed, zero skips:

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:SkipNativeBuild=true --no-restore `
  --filter "FullyQualifiedName~ConverterContractTests" `
  --nologo --verbosity minimal
```

### R2 — ordinary image reliability

Use the existing deterministic sample cache used by the current-head Audit replay. It contains the hash-locked `华为-Mate80.jpg` and the Vivo `vivo双文件.jpg` / `vivo双文件.mp4` pair. Do not create another cache or profile. Set:

```powershell
$env:LIVEPHOTOBOX_TEST_SAMPLES_DIR = (Resolve-Path ".ai-tmp/cache/samples").Path
```

Fresh-run all four audited canonical operations and the final ICC declared-length regression; require each required hash-locked sample route, unchanged source hashes, and zero skips. This set covers same-container structure copy, perfect-DCT rotation, JPEG→HEIC reencode, non-GainMap HEIC→JPEG, and `Convert_VivoJpegIccIgnoresBytesBeyondDeclaredProfile`.

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:SkipNativeBuild=true --no-restore `
  --filter "FullyQualifiedName~Convert_JpegToJpeg_PerformsStructureCopyWithoutReencoding|FullyQualifiedName~Convert_JpegToJpeg_Rotate90_UsesPerfectDctAndPreservesObservedComponents|FullyQualifiedName~Convert_JpegToHeic_ConvertsPixelsAndEmitsTruthfulRecord|FullyQualifiedName~Convert_NonGainMapHeicToJpeg_ConvertsPixelsAndEmitsTruthfulRecord|FullyQualifiedName~Convert_VivoJpegIccIgnoresBytesBeyondDeclaredProfile" `
  --nologo --verbosity minimal
```

Use the already-authoritative independent metadata/image readers and exact R2 assumptions. Bind their source/profile/tool/output identities; reuse historical independent results only where the exact claim and inputs remain applicable. Do not count product self-read as independent evidence, infer an absent output check, or claim an ICC transform from byte carriage.

### R3 — HDR / GainMap semantic conversion

Fresh-run the two existing Neutral route filters; require all four canonical RealSample routes, 4/4 executed, zero skipped, and unchanged source hashes. Set `LIVEPHOTOBOX_TEST_SAMPLES_DIR` to `.ai-tmp/cache/samples/P5-R3-v1` and `LPB_P5_R3_OUTPUT_DIR` to the attempt's deterministic artifact directory.

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:SkipNativeBuild=true --no-restore `
  --filter "FullyQualifiedName~NeutralPipeline_HeicGainMapToJpeg|FullyQualifiedName~NeutralPipeline_JpegGainMapToHeic" `
  --nologo --verbosity minimal
python tools/p5-r3-gainmap-validation/validate_p5_r3_v2.py `
  --sample-dir .ai-tmp/cache/samples/P5-R3-v1 `
  --artifact-dir .ai-tmp/workspace/P5-Verify/R3/attempt-01/artifacts `
  --work-dir .ai-tmp/workspace/P5-Verify/R3/attempt-01/validator-work `
  --evidence-dir .ai-tmp/workspace/P5-Verify/R3/attempt-01/validator-evidence
```

The validator must remain byte-identical to the frozen v2 validator and use the checked-in matrix, reference policy and source-structure policy. The paths above are create-only; if an attempt path already exists, preserve it and use the next fixed sequential attempt directory. Require the independent JPEG XMP/MPF/GainMap and HEIF graph/metadata/decode/render/reference results in the R3 profile; do not tune assumptions or thresholds.

### R4 — color and high-bit-depth policy

Fresh-run all seven `P5R4_` tests. The five RealSample rows must all be present and execute with zero skips; the two fixed negative/adversarial rows do not substitute for those routes. This frozen R4 validator enforces fixed output locations, so use the R4-owned workspace and evidence roots below as a path exception to the general P5-Verify attempt directory. Preserve prior R4 evidence; formal evidence is create-only and must not be overwritten. Do not change the validator.

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:SkipNativeBuild=true --no-restore `
  --results-directory .ai-tmp/workspace/P5-R4/test-results `
  --logger "trx;LogFileName=P5-R4-Verify.trx" `
  --filter "FullyQualifiedName~ImageColorHighBitDepthTests.P5R4_" `
  --nologo --verbosity minimal
python tools/p5-r4-color-high-bit-depth-validation/source_lock.py `
  --mode verify --manifest tools/p5-r4-color-high-bit-depth-validation/profiles/P5-R4-v1/manifest.json
python tools/p5-r4-color-high-bit-depth-validation/validate.py `
  --mode formal `
  --manifest tools/p5-r4-color-high-bit-depth-validation/profiles/P5-R4-v1/manifest.json `
  --workspace .ai-tmp/workspace/P5-R4 `
  --evidence .ai-tmp/evidence/P5-R4 `
  --test-results .ai-tmp/workspace/P5-R4/test-results/P5-R4-Verify.trx
```

The source lock is offline. Use P5-R4-v1, existing decoder/tool identities and frozen render thresholds. Do not refresh sources, edit the profile, or reuse an R4 validator report that does not bind the current test outputs.

### R5 — video converter reliability

Fresh-run the current-head Core `P5R5_` filter and Native `P5R5_` CTest filter from the Release Native build directory. Require all R5-v1 mandatory RealSample routes (9/9), all required negative routes (5/5), exact Huawei parent/extraction/derived-video identity, and zero skips. The Core command intentionally enables the frozen minimal-libav sidecar build/copy; do not add `SkipNativeBuild=true`. The explicit Native build in Section 4 runs CTest and the managed `NativeRuntimeTests` harness through `build-native.ps1 -RunTests`: use that output/count to prove all 17 current NativeRuntime tests were discovered, executed, and passed with zero skipped/not-executed. Only if the Section 4 output does not prove the full scope, run the focused NativeRuntime filter after checking the harness exists. Run the existing frozen independent runner; do not invent a second R5 build/package/validator procedure:

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:VideoSidecarBuildId=r5-main10-ci --no-restore `
  --filter "FullyQualifiedName~P5R5_" --nologo --verbosity minimal
ctest --test-dir <Release-Native-build-directory> -C Release `
  -R "^P5R5_" --output-on-failure
dotnet build tools/p5-r5-video-validation/route-driver/P5R5.RouteDriver.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:SkipNativeBuild=true `
  --no-restore `
  --nologo `
  --verbosity minimal
$r5Attempt = 1
$r5ValidationRecordId = "verify-final-$shortSha"
while (Test-Path ".ai-tmp/workspace/P5-R5/$r5ValidationRecordId") {
    $r5Attempt++
    $r5ValidationRecordId = "verify-final-$shortSha-attempt$r5Attempt"
}
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/p5-r5-video-validation/Invoke-P5R5Validation.ps1 `
  -RecordId $r5ValidationRecordId
```

Resolve `<Release-Native-build-directory>` from the canonical build script's actual output; do not guess a stale build path. The frozen runner requires `tools/p5-r5-video-validation/route-driver/bin/x64/Release/net9.0-windows10.0.19041.0/P5R5.RouteDriver.dll`. Require the fresh Section 4 restore and the Release/x64 route-driver build above to succeed; record that exact DLL path and SHA-256 and verify the runner consumes this just-built artifact, never a prior/stale DLL. This managed driver build uses the already-built `r5-main10-ci` Native/sidecar artifacts and `SkipNativeBuild=true`, so it does not rebuild Native. The frozen R5 validator owns its output at `.ai-tmp/workspace/P5-R5/<RecordId>/` and does not expose an output-root override. Preserve that owner-defined path and prior attempts; do not edit the runner merely to relocate its output. Require independent stream, packet, timing, audio, rotation, color, HDR/bit-depth, backend, no-retry/no-output and package/import checks exactly as the R5 detail/profile requires.

Use the `$shortSha` derived from the Verify entry HEAD in Section 4; do not hard-code a prior HEAD. The loop selects the first unused create-only validator path, preserving every prior attempt; never overwrite it.

### R6 — Neutral consumption and exact-object publication

Set `LIVEPHOTOBOX_P5R6_PUBLIC_OUTPUT_ROOT` and `LIVEPHOTOBOX_P5R6_NEUTRAL_OUTPUT_ROOT` to fresh deterministic subdirectories of `.ai-tmp/workspace/P5-Verify/R6/`. Fresh-run every test selected by the current `P5R6_` filter with zero skips:

```powershell
dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj `
  -c Release -p:Platform=x64 -p:SkipNativeBuild=true --no-restore `
  --filter "FullyQualifiedName~P5R6_" --nologo --verbosity minimal
```

Require current assertions/evidence for all 14 Merge and 7 Split available cells; five Merge Neutral tuples; four `SplitProtocolNone` Neutral tuples; Apple/Vivo target-writer fail-fast rows; `TargetFps` Unsupported/no-output; and exact-object rollback, late-occupant, replacement and alias cases. Retain the final-head F-03 matrix only as bound historical evidence; do not invent a new R6 independent validator or sample threshold.

## 6. Runtime/package, independent checks, and same-HEAD CI

Because the final Audit repair changes Native media code, verify the current Release Native DLL, current exact-sibling minimal-libav sidecar, ABI/version/import identities, and actual managed runtime load path. Reuse existing R5 package/import tooling and logic. Confirm the package does not acquire `ffmpeg.exe`, `ffprobe.exe`, or another general external media CLI, and frozen notices/source identities remain intact. Do not create a new package architecture, tag, release, or draft GitHub Release.

Perform these identity/package assertions after the sidecar-enabled R5 Core command and bind them to the resulting current artifacts. The old iteration-28 MSIX helper and its historical `r5-v4` packaging invocation describe verification logic only; do not replay those commands verbatim or hardcode the obsolete build id. Apply the same frozen assertions to the current-HEAD package/runtime and current frozen sidecar prerequisite.

Before finalizing, verify successful push-triggered CI on the actual Verify execution HEAD established after durable publication:

- Build & Release for that exact `headSha`: `verify=success`.
- CodeQL for that exact `headSha`: `Analyze csharp=success` and targeted C/C++ `success` on attempt 2.
- Both workflows must be `push` events bound to the exact Verify execution HEAD. Record their run IDs and attempts; the cancelled first C/C++ attempt remains history, not a PASS.

The prior runs `37452709386` and `37452709481` bind the Audit applicability base only. Do not rerun those old successful workflows. If any identity is missing, a conclusion is unsuccessful, or HEAD differs, record a blocker and obtain required same-HEAD evidence; workflow-dispatch evidence is not a substitute.

## 7. Cross-cutting acceptance matrix

Fresh Verifier reports a row-by-row final matrix for all P5 Exit Criteria E1–E8, every applicable accepted R0–R6 item, Neutral Media Contract N1–N14, build/runtime identity, RealSample rows, independent validators, package/runtime, required CI, and filter/skip/blocker counts. Use only `PASS`, `FAIL`, `NOT RUN`, or `BLOCKED`. Every status cites the exact current-head command/evidence or the precisely bound reusable identity and its applicability reason. Do not copy the Audit matrix as proof that Verify ran.

Any mandatory `FAIL`, `NOT RUN`, `BLOCKED`, missing sample, excluded filter, skip, stale artifact, unresolved identity, or incomplete independent validation prevents readiness. Synthetic, unit, self-read, or mock evidence cannot replace a required RealSample or independent validator.

## 8. Evidence and immutable samples

Keep daily/fresh outputs under stable, deterministic `.ai-tmp/workspace/P5-Verify/` subdirectories (`build/`, `R1/`…`R6/`, `runtime/`, `closeout/`). Preserve prior attempts; use fixed sequential attempt names only when a create-only tool refuses an existing path. The Native build script's internal create-only file `.ai-tmp/workspace/P5-R5/<ExecutionRecordId>-native-Release-build.log`, R5 validator-owned directory `.ai-tmp/workspace/P5-R5/<RecordId>/`, and frozen R4 paths are documented tool-owned path exceptions. Keep the outer Verify command transcript and summary under `.ai-tmp/workspace/P5-Verify/build/`; do not edit scripts merely to relocate their output.

At successful formal Verify closeout, create a formal snapshot under `.ai-tmp/evidence/P5-Verify/` without replacing existing evidence. Bind at minimum: HEAD/upstream/status and Roadmap revision; command/argv and exit code; exact test filter/scope/counts/skips; source/profile/sample/validator/tool hashes; before/after and output identities; actual build/runtime/package artifacts; CI run/attempt identities; reused-evidence applicability; doctor and diff-check results; final matrix and remaining coverage limits.

`designs/各个机型测试/` is permanently read-only. Use only hash-locked copies in the existing deterministic sample cache. Do not create date/time/randomized directories, modify source samples, or force-add ignored `.ai` task files.

## 9. Failure handling and return path

On any required failure, report exactly `P5 VERIFICATION: FAIL`, with the blocker, affected E/R/N item, exact command/HEAD/evidence, owning R/TASK, and minimum required repair/reverification path. Verifier stops without source repair. If a source/profile/sample/validator/threshold/Contract/ABI change is proposed, route it to the proper owner and strategic authority before continuing; do not lower acceptance to save time.

## 10. External Chief Gate and P6 boundary

Only after all required fresh and applicable reused evidence is complete may the Verifier report `P5 VERIFICATION: READY FOR EXTERNAL CHIEF GATE`. The current user / External Chief Gate then explicitly issues `ACCEPT` or `REJECT`. Only explicit `ACCEPT` may complete P5 and make P6 eligible. Under the current user instruction, stop after P5 and do not activate or start P6.

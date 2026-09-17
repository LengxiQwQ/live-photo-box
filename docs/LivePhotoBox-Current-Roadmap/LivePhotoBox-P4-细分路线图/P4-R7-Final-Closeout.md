# P4-R7 Final Closeout — Capability Identity / Portable Core

## Status

**IMPLEMENTATION READY FOR FRESH INDEPENDENT AUDIT**

This is an Implementer closeout, not a Fresh Audit, Verifier result, P4
acceptance, or authorization to start P5.

| Item | Value |
|---|---|
| Starting HEAD | `063dc7255abfa75ac0ea868b998386efd17dbc4f` (`chore(insights): update repository traffic data [skip ci]`) |
| Branch | `master` |
| Ending HEAD | The commit containing this closeout; its exact identity is recorded in the implementation handoff. |
| Repository state at start | clean; `origin/master` was fetched and fast-forwarded before implementation |

## Architecture closure

```text
livephotobox_portable_core (static)
  ├─ binary/ISO-BMFF + HEIF structural truth
  ├─ TIFF/EXIF/JPEG metadata truth and in-memory rewrites
  ├─ Apple, Huawei, Samsung and vivo protocol parsers/mutators
  └─ SHA-256/fingerprint algorithm
          ↓ same source objects, no copies
LivePhotoBox.Native.dll
  ├─ Windows SHA file/path adapter
  ├─ Windows transaction-owned MP4/cleaner publication adapters
  ├─ Windows PlatformFilesystem
  └─ JPEG, HEIC and Media Foundation backends
```

`livephotobox_portable_core` is a real CMake static target, not a source
group. Both the production Native DLL and `lpb_portable_io_smoke` link it.
It compiles the production protocol/container/metadata/hash sources directly,
with no copied implementation and no direct `windows.h`, filesystem, WIC, or
Media Foundation dependency. `portable_internal.h` carries only the opaque C
ABI context plus error/authority/output-buffer callbacks; the Windows context
retains the actual capability token. `sha256_core.cpp` is the algorithm;
`sha256.cpp` is exclusively the Win32 HANDLE/path reader. The Windows product
remains Windows x64: PlatformFilesystem, handle ownership, atomic publication
and Media Foundation are deliberate lower-layer backends.

## Runtime capability identity

ABI is now v8 because a new versioned POD query,
`lpb_get_runtime_capability_identity`, was added. It exposes fixed-width
enums, explicit `struct_size`, UTF-8 fixed buffers, and no C++/Win32/COM or
codec-private type. Managed interop probes every operation at startup.

| Capability class | Current backend / codec | Version or path | HW / fallback truth |
|---|---|---|---|
| JPEG codec | libjpeg-turbo / JPEG | build-time `libjpeg-turbo 3002000` | not applicable; no fallback |
| HEIC codec | libheif / HEIC-HEVC | build-time `libheif 1.23.4` | not applicable; no fallback |
| Video remux | project-owned ISO-BMFF / container copy | product Native version | not applicable; no fallback |
| SDR H.264 transcode | Windows Media Foundation / H.264 | software-forced MF path | hardware transforms disabled by frozen policy; no fallback |
| SDR HEVC transcode | Windows Media Foundation / HEVC | software-forced MF path | hardware transforms disabled by frozen policy; no fallback |
| HDR/10-bit transcode | minimal libav (frozen P5 owner) / **codec unknown** | **not packaged** | unavailable; no retry or fallback is implied |

The final row deliberately reports the approved P5 backend as unavailable in
this runtime. No libav DLL, library linkage, or package sidecar was added.
Actual invocation diagnostics remain the existing versioned video diagnostics
POD, including selected encoder and fallback reason.

## Dependency and runtime-purpose report

| Dependency/runtime | Actual caller and capability | Production role | Duplicate assessment / removal posture |
|---|---|---|---|
| libjpeg-turbo (static) | Native JPEG pixel codec and lossless transform | required, result-affecting | sole Native JPEG codec owner; retain |
| libheif + libde265 + x265 (static) | Native HEIC codec gateway / HEVC decode and encode | required, result-affecting | HEIC-specific; retain. HEIF/vendor structure remains project-owned |
| Media Foundation (`MFPlat`, `MFReadWrite`) | Native SDR H.264/HEVC video transcode | required, result-affecting | distinct video capability from HEIC codec stack; retain |
| project ISO-BMFF engine | probe, remux and protocol/container truth | required, result-affecting | not a generic codec duplicate; retain |
| minimal libav research output | R6 differential evidence only | not in production package | frozen P5 HDR/10-bit decision; do not package in R7 |
| Magick.NET / `Magick.Native-Q16-x64.dll` | GUI HEIC thumbnail providers | UI-only package dependency | 23,949,488-byte native runtime; not a result-affecting backend owner. Retain until the UI thumbnail decision changes |
| MagicScaler / PhotoSauce `heif.dll` | alternative GUI thumbnail provider | UI-only package dependency | its 3,152,384-byte `heif.dll` is distinct from static Native libheif. Retain while selectable thumbnail provider exists |
| WIC / Windows imaging APIs | GUI preview/thumbnail path | UI-only | no Native result-affecting JPEG/HEIC WIC path |
| lcms2 | none | not shipped by Native P4 graph | no current ICC transform owner; not added |

The first temporary self-contained GUI publish inspected for this record had
380 files and 268,228,560 bytes. The like-for-like final MSIX baseline is now
`LivePhotoBox_2.2.2.0_x64.msix`: 120,754,160 compressed bytes, 625 entries,
and 290,171,856 uncompressed bytes, versus R1's 118,928,770 / 625 /
282,439,820. The delta is +1,825,390 compressed bytes and +7,732,036
uncompressed bytes; it is recorded, not treated as a size optimization claim.
Its Native data-plane DLL was 8,619,008 bytes; only
`bcrypt.dll`, `ole32.dll`, `MFPlat.dll`, `MFReadWrite.dll`, `KERNEL32.dll`,
and `ADVAPI32.dll` appear in its PE dependency list. The JPEG/HEIC codec
libraries are statically linked. This is a package-footprint baseline, not a
claim that the GUI-only managed dependencies have been eliminated.

## Production external-CLI audit

The production `LivePhotoBox.Core` tree contains zero `Process.Start` or
`ProcessStartInfo` calls. The remaining process starts in CLI/GUI are updater,
Explorer, URL, and system-settings actions, not media processing. Mentions of
`ffmpeg`, `ffprobe`, `ExifTool`, `jpegtran`, `heif-enc`, `heif-dec`, and
`magick.exe` were traced as one of: comments/history, Rebuilt compatibility
methods that throw `NotSupportedException`, UI-only naming, or research/test
scripts. R6 differential tools remain under `tools/r6-video-differential`
and are not in the production CMake graph or package.

## Filesystem, CMake, ABI and contamination closeout

`windows_owned_output` is the sole result-writing publication primitive found
in Native media paths: it owns the created staging handle, flushes, verifies
identity, and calls handle-based `publish_no_replace`. There is no production
`MoveFileExW` pathname publication path. Direct `CreateFileW` residuals are
classified as source observation/identity, owned staging writes, or the
canonical Windows backend; they do not enter `livephotobox_portable_core`.

CMake remains canonical: `scripts/native/build-native.ps1` configures via the
vcpkg manifest and now explicitly builds/runs `lpb_portable_io_smoke` whenever
`-RunTests` is requested. The compatibility `.vcxproj` is not used as the
canonical artifact source. `vcpkg.json` continues to resolve the actual
current feature set: `libheif[core,hevc]` with static libde265/x265 support;
no unobserved codec/plugin binary was introduced by R7.

## Test and build evidence

| Scope | Command/result | Passed | Failed / skipped |
|---|---|---:|---:|
| Debug Native runtime/ABI | CMake Debug build + `NativeRuntimeTests` | 16 | 0 / 0 |
| Release Native runtime/ABI | CMake Release build + `NativeRuntimeTests` | 16 | 0 / 0 |
| Portable core | `lpb_portable_io_smoke` linked to `livephotobox_portable_core` | 1 | 0 / 0 |
| P1–P3/R2/R4–R6 scoped Debug regression | Inspector, Extractor, Cleaner, PlatformFilesystem, Image/HEIC/GainMap and Video filters | 285 | 0 / 0 |
| Package baseline | current-head local MSIX: 120,754,160 compressed B, 625 entries, 290,171,856 uncompressed B | completed | R1 delta recorded; no Store publish |

The 285-test scoped result includes the existing `RealSamples` tests for JPEG,
HEIC/HDR/GainMap and video. The original sample corpus was not written; test
workspace and TRX evidence are under `.ai-tmp/workspace/P4-R7/`.

## P4 exit-gate matrix

| # | Gate | Implementation evidence | Test / RealSample evidence | Status | Residual risk |
|---:|---|---|---|---|---|
| 1 | R1 actions closed/residualized | protocol/container/metadata/hash B-class sources moved to portable core; Windows file/transaction adapters classified | source/build graph inspection | PASS | UI-only dependencies retained intentionally |
| 2 | Backend responsibility clear | dependency table and runtime identity | ABI probe | PASS | none |
| 3 | filesystem publish consolidated | `windows_owned_output` / handle publish | scoped transaction tests | PASS | correctness-critical reads remain Windows backend |
| 4 | canonical CMake build | CMake production targets | Debug and Release builds | PASS | none |
| 5 | reproducible dependency restore | manifest-mode vcpkg configure | fresh configure during both builds | PASS | host toolchain still required |
| 6 | JPEG/HEIC/video frozen | CMake graph and R4–R6 decisions | JPEG/HEIC/video scoped tests | PASS | libav awaits P5 packaging |
| 7 | lcms2/WIC policy frozen | no Native lcms2/WIC codec path | package/call graph inspection | PASS | UI WIC remains allowed |
| 8 | runtime identity diagnosable | ABI v8 POD identity; generic HDR/10-bit codec is unknown until P5 invocation | Debug/Release 16 ABI tests | PASS | no P5 outcome record yet |
| 9 | duplicate report complete | dependency table above | actual publish/runtime inspection | PASS | UI codecs intentionally duplicate UI-only functions |
| 10 | runtime feature purpose known | package purpose table above | PE imports and publish inventory | PASS | managed package is large |
| 11 | external media CLI clear | production call-path audit | source scan | PASS | research tools remain offline-only |
| 12 | footprint baseline complete | actual current-head MSIX compared like-for-like to R1 | 120,754,160 B / 625 / 290,171,856 B inventory and CLI absence scan | PASS | no Store publish |
| 13 | portable core independent proof | real static target contains protocol/container/metadata/hash truth; shell has only Windows adapters | Release portable smoke, same production sources | PASS | non-Windows compile smoke intentionally not added |
| 14 | public ABI no leakage | C ABI v8 POD review | ABI layout and export test | PASS | none |
| 15 | P1–P3 regressions | no authority/publish semantic change | 285 scoped tests | PASS | none observed |
| 16 | R4–R6 RealSamples reproducible | no sample mutation, affected graph rebuilt | scoped RealSample tests, 0 skip | PASS | corpus coverage remains the recorded R4–R6 corpus |
| 17 | P5 need not redesign foundation | frozen backend and build decisions exposed | runtime identity / package proof | PASS | P5 still owns conversion semantics and GainMap policy |

## Non-blocking residuals and P5 handoff

1. `StandardHdrConversionService` still contains a Magick-based gain-map
   implementation, but no production caller was found. It is a pre-existing
   P5 semantic migration item, not an alternative P4 production backend.
2. Magick.NET and MagicScaler remain packaged because GUI thumbnail providers
   actively call them. They are explicitly UI-only and must not become a
   result-affecting fallback.
3. The frozen minimal-libav HDR/10-bit owner remains research-only until P5
   connects, packages, and proves its explicit dispatch. Its unavailability is
   exposed rather than hidden.

Fresh Audit should challenge these claims from the source and built artifacts;
it should not treat this document as independent acceptance evidence.

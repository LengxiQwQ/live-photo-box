# P4-R5 Final Closeout Report

## Status

**PASS — local gate ready for external review.** This is not a phase-completion declaration: P4-R5 may advance only after the current user or external Chief Gate explicitly accepts it.

## Baseline

| Field | Value |
|---|---|
| Branch | `master` |
| Starting HEAD | `7b9d767cd230db5b3d7c0ca78713ab89dfc45a61` |
| Remote check | `git fetch origin`; `origin/master` matched starting HEAD before this closeout work |
| Previous closeout commit | `1a983e8922051352e02b438de1a277519b16b8f4` (`fix(native): close P4-R5 HEIC HDR validation gaps`), committed and pushed to `origin/master` |
| Source samples | `designs/各个机型测试/`, read only; temporary diagnostic copies only in `.ai-tmp/cache/samples/P4-R5/` |

## Sample inventory

`ImageConverterTests.FormalHeicRealSampleInventory_IsExplicitAndComplete` enumerates the formal corpus and asserts that it contains exactly the following three HEIC files. The separately copied `designs/谷歌自己合成的.heic` is explicitly visible to the test but excluded as synthetic/non-device evidence.

| Sample | SHA-256 | Primary item / visual size | Primary source / decoded signal / storage | ICC / nclx | Project HDR/GainMap | Auxiliary item / type / bits | Primary / auxiliary decode |
|---|---|---|---|---|---|---|---|
| Apple `苹果双文件.HEIC` | `868F…6998` | 49 / 4032×3024 | 8 / 8 / 8 | yes / no | yes: embedded Apple `urn:com:apple:photo:2020:aux:hdrgainmap` | 63 / same Apple type / 8 / 8 / 8 | pass / pass (2016×1512) |
| Huawei `华为Mate80.heic` | `904E…834A` | 15 / 3072×4096 | 8 / 8 / 8 | yes / no | no HEIF GainMap auxiliary | none | pass / not applicable |
| Samsung `三星.heic` | `DBFB…4C4` | 49 / 3000×4000 (orientation-applied) | 8 / 8 / 8 | yes / no | yes: embedded Samsung `urn:com:samsung:photo:2024:aux:hdrgainmap` | 55 / same Samsung type / 8 / 8 / 8 | pass / pass (750×1000) |

`tests/LivePhotoBox.Core.Tests/RealSampleManifest.json` records the full hashes, expected dimensions, primary depth, HDR representation, and auxiliary identity without adding or regenerating media. The RealSample tests assert the same primary hashes and exact codec facts, so a same-name but different local file fails rather than silently becoming evidence.

## HDR representation findings

The real Apple and Samsung HDR samples are **8-bit SDR primary + embedded 8-bit HDR GainMap auxiliary**. They are genuine HDR/GainMap representations even though their primary `SourceBitDepth` is 8. Huawei's file is a successful 8-bit primary decode but has no HEIF auxiliary; its HLG/BT.2020 track metadata is not used as a claim about static HEIC primary HDR.

The Native `pixel_surface` remains high-bit capable: it retains `signal_bit_depth` and uses 16-bit storage for sources above 8-bit. The current formal corpus has no >8-bit primary, so it supplies no real-device >8-bit-primary compatibility claim. This is a coverage fact, not a fabricated R5 failure condition.

For actual HDR samples, the project-owned inspector identifies the GainMap relationship, the project-owned structural graph supplies the auxiliary item ID, Native decodes that exact item, and the shared `ImageConversionEligibility` rule rejects HEIC-to-JPEG before the generic primary-only codec path. `ImageConverter` independently re-inspects the actual source container immediately before codec conversion, so an incorrectly declared managed artifact cannot bypass this rule. `HeicConverterService` likewise routes HDR/GainMap work to P5. Until P5 provides an explicit semantic transform, these paths fail closed; they cannot return a plain SDR JPEG as success or preserved output.

## Auxiliary codec foundation

`lpb_encode_heic_primary_and_secondary_jpegs` is the minimal P5-facing codec seam. It takes two project-owned JPEG inputs through the approved Native JPEG boundary and writes two distinct HEVC (`hvc1`) image items through libheif/x265. The second item deliberately has **no auxiliary, vendor, or GainMap semantic** until project-owned structural code validates and attaches `auxC`/`auxl` and references.

The closeout test uses controlled codec inputs and an independent HEIF graph parser to verify two distinct non-empty `hvc1` payloads and no accidental auxiliary relation. This is codec-foundation evidence only; Apple/ISO mapping and final auxiliary graph construction remain P5 work. Apple and Samsung RealSamples separately prove real auxiliary discovery and decode.

## Primary decode and ABI

`lpb_decode_heic_primary_image` is a read-only ABI operation added in ABI v7. It returns only POD primary item, visual dimensions, source depth, decoded signal/storage depth, and color facts. It never gives libheif protocol or GainMap authority. `NativeHeicPrimaryDecodeInfo` has explicit managed layout verification: size 40, `NclxPrimaries` offset 28, `HasAlpha` offset 34. Existing HEIC image, auxiliary, and two-image structs retain their checked layouts.

The same production-like Native decode helper now covers Apple, Huawei, and Samsung; each checks exact visual dimensions, exact 8-bit source/storage facts, and immutable source SHA-256.

## Legacy audit

| Item | Result-affecting R5 HEIC/HDR role |
|---|---|
| WIC / WindowsCodecs | None in Native JPEG/HEIC data plane; UI/system previews are outside this policy. |
| Magick.NET | Legacy `StandardHdrConversionService` P5 semantic pixel code only; not a Native HEIC codec fallback and not used to claim R5 HDR success. |
| `heif-enc` / `heif-dec` | No production subprocess fallback. Legacy entry points fail closed. |
| ExifTool | Independent test verification only; no production result path. |
| libheif / libde265 / x265 | Canonical codec gateway / decoder / encoder, respectively. Project structural parser remains protocol authority. |

## Tests

| Command / scope | Result |
|---|---|
| `scripts/native/build-native.ps1 -Configuration Debug -RunTests` | Native Debug rebuild and harness: 15/15 passed, 0 skipped. |
| `scripts/native/build-native.ps1 -Configuration Release -RunTests` | Native Release rebuild and harness: 15/15 passed, 0 skipped. |
| Release `ImageConverterTests|NativeRuntimeTests|NeutralMediaServiceTests` | 56/56 passed, 0 skipped; includes Apple/Samsung fail-closed, Huawei ordinary codec success, real decode, and neutral manifest protection. |
| Release CLI conversion tests | 6/6 passed, 0 skipped; includes actual Apple GainMap HEIC→JPEG CLI rejection. |
| Release P1–P4 regression filter (Inspector, Extractor, Cleaner, PlatformFilesystem, converters, GainMap, Native runtime, Neutral pipeline) | 489/489 passed, 0 skipped |
| Visual Studio bundled `ctest --test-dir .ai-tmp/workspace/P4-R3/cmake-build -C Release --output-on-failure` | `portable_io_smoke` 1/1 passed |
| `scripts/native/sync-native-project.ps1 -Check` | passed after regenerating the compatibility source ordering |
| `.ai/verify.ps1` | passed |

`ctest` is not globally on `PATH` in this host, so the checked Visual Studio CMake binary was used. `doctor.ps1 -ActiveOnly` reports two pre-existing non-phase tool configuration issues (Microsoft Learn MCP load and missing `.agents/mcp_config.json`); all R5 build/test dependencies were available.

## P5 mandatory handoff

- HEIC-to-JPEG ICC/profile and cross-format color policy.
- JPEG orientation and metadata to HEIC transform policy.
- GainMap mathematics migrated to the Native pixel host.
- Apple/ISO/vendor GainMap mapping rules.
- Final project-owned auxiliary structural semantics (`auxC`, `auxl`, references, metadata placement) and target validation.

## Gate table

| # | Gate | Result | Evidence |
|---:|---|---|---|
| 1 | Canonical libheif/HEVC integration | PASS | vcpkg/CMake `libheif -> libde265/x265` static graph |
| 2 | Real primary decode | PASS | Actual Native pixel decode, Apple/Huawei/Samsung matrix |
| 3 | Primary encode + independent validity | PASS | Native JPEG→HEIC plus structural/ExifTool tests |
| 4 | Auxiliary discovery, decode, encode foundation | PASS | Apple/Samsung real auxiliary decode; independent two-image HEVC codec proof |
| 5 | HDR / bit depth for applicable samples | PASS | Apple/Samsung real 8-bit-primary + GainMap HDR path, exact auxiliary decode, and fail-closed no-SDR-fallback behavior; high-bit architecture retained |
| 6 | Project structural authority | PASS | Codec API consumes project-selected IDs; vendor semantics remain project-owned |
| 7 | Auxiliary ownership clarity | PASS | Embedded primary-owned real GainMaps; no duplicate materialization |
| 8 | x265 distribution record | PASS | R5 implementation record distinguishes GPL software license from HEVC patent/distribution review |
| 9 | Minimal runtime set | PASS | Static codec dependencies only; no plugin/CLI runtime |
| 10 | lcms2 decision | PASS | Not shipped: facts transport is not a color transform |
| 11 | WIC policy | PASS | No result-affecting Native JPEG/HEIC WIC path |
| 12 | RealSamples not skipped | PASS | Exact inventory and decode tests require the three formal samples |
| 13 | No external HEIF CLI fallback | PASS | Production audit; legacy calls fail closed |
| 14 | Truthful failure/degradation | PASS | Apple and Samsung GainMap HEIC→JPEG requests fail before output publication; the non-GainMap Huawei HEIC→JPEG codec proof still succeeds |

## Final decision

The previous “no 10-bit primary therefore HDR proof is absent” conclusion was too rigid and is replaced by the demonstrated product representation: **8-bit primary + HDR GainMap auxiliary**. This does not erase >8-bit support or pretend the current corpus includes a >8-bit real device sample. It applies Roadmap “where applicable” to the actual samples, while preserving P5's semantic and color-policy work.

**P4-R5: PASS locally; ready for external acceptance review. P4-R6 is not automatically started.**

# P4-R6 Evidence Remediation — Video Backend Differential / Windows Backend Freeze

## Status

**Evidence remediation complete locally; external Chief Gate re-review is required.** This document does not mark P4 complete, does not start R7, and does not change the already implemented Media Foundation production path.

The remediation corrects the four evidence defects identified by the external gate: Unicode argument handling, a genuine automated differential matrix, quality measurements, and package-size terminology. The original MF C ABI, software-forced policy, and fail-closed audio/rotation checks remain intact.

## Existing production ownership (unchanged pending P5 implementation)

```text
project-owned ISO-BMFF probe / stream-copy remux = LivePhotoBox Native
codec decode / encode / cross-codec transcode       = Windows Media Foundation
hardware policy                                    = software forced
libav* candidate and ffmpeg/ffprobe                = offline research only
```

This is not a runtime retry tree. The existing codec transform has one current production owner: Media Foundation with `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = FALSE`. The versioned Native v2 ABI returns POD diagnostics, including backend and hardware policy; managed code does not infer them from a string. A source that declares audio fails rather than silently dropping it, and the managed path re-probes output audio and rotation before reporting success.

The evidence below deliberately records that the research candidate produces different output characteristics. It does not silently promote that candidate into production or erase the MF implementation. An external gate must make any backend-selection change.

## 1. Unicode path boundary repaired

The candidate previously entered through narrow `main(char**)`, so the Windows CRT code page could corrupt a UTF-16 path before libav received it. The candidate now uses `wmain(int, wchar_t**)` and converts every argument with `WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS)` before calling libav.

Release candidate probes passed directly, without ASCII staging, for:

| Input | Result |
|---|---|
| `dual/苹果-双文件_jpg/苹果-双文件_video.mov` | PASS |
| `single/华为-Mate80_jpg/华为-Mate80_video.mp4` | PASS |
| `.ai-tmp/workspace/P4-R6/unicode-inputs/📷-补充平面-苹果.mov` | PASS |

The emoji input is a temporary copy of an already project-extracted video; the immutable samples in `designs/各个机型测试/` were not modified. The prior claim that the candidate could not open Chinese paths is withdrawn.

## 2. Actual differential matrix

`tools/r6-video-differential/matrix.json` fixes four real, project-extracted inputs. `Invoke-R6Differential.ps1` refuses to overwrite evidence, then for each row invokes the production CLI conversion and candidate conversion on the same input and target codec/container. It saves both conversion logs, elapsed time, output byte count, independent `ffprobe` JSON, and decoded PSNR/SSIM logs. A conversion, probe, or metric failure terminates the run.

The final minimal-build run is `.ai-tmp/workspace/P4-R6/differential-evidence-v4-minimal/r6-differential-summary.json`. `ffmpeg`/`ffprobe` are offline validators only; production never launches either executable.

| Fixed real case | Target | MF ms / bytes | libav* ms / bytes | MF PSNR / SSIM | libav* PSNR / SSIM |
|---|---|---:|---:|---:|---:|
| Apple dual HEVC MOV | H.264 / MOV | 2906.34 / 2,802,013 | 2034.45 / 2,913,605 | 15.966 / 0.617393 | 15.430 / 0.602258 |
| vivo dual H.264 MP4 | HEVC / MP4 | 1879.29 / 1,073,552 | 2412.93 / 956,517 | 24.088 / 0.925553 | 44.959 / 0.984696 |
| Huawei Main10 HLG MP4 | H.264 / MP4 | 2106.88 / 3,821,390 | 3116.16 / 2,428,294 | 27.920 / 0.982528 | 43.854 / 0.984618 |
| Samsung HEVC MP4 | H.264 / MP4 | 2108.59 / 3,474,638 | 2551.85 / 3,574,008 | 18.004 / 0.778923 | 42.745 / 0.985989 |

For the metric, both primary streams use stored (non-auto-rotated) frames, normalise time bases, and the source is explicitly scaled to the encoded output dimensions before conversion to `yuv420p`. Thus alignment resize is visible rather than silently making the measurement fail. PSNR/SSIM are not HDR or rotation evidence; those properties come separately from `ffprobe`.

Apple is a major resize case, so both its frame scores are low and are not comparable to the same-dimension cases. The other three rows are material quality evidence: the research candidate scored higher. This is recorded as a selection risk, not re-labelled as an MF success.

Independent `ffprobe` also records the expected output differences:

- MF emits H.264 Constrained Baseline / 8-bit output for Huawei and lacks its BT.2020 / ARIB HLG tags. That is negative preservation evidence.
- The candidate emits H.264 High 10 and retains BT.2020 / ARIB HLG for the Huawei comparison. It packet-copies eligible first audio streams, whereas MF re-encodes AAC, so audio rates/channels can differ by design.
- The candidate still does not own Live Photo vendor metadata/data tracks, extraction semantics, or mutation authority. It receives only an already extracted video and therefore cannot make a preservation claim for those structures.

## 3. Broad-vcpkg baseline: distinct from the deployable candidate

The research manifest now omits `swresample`; the candidate packet-copies audio and its CMake link boundary rejects `swresample.lib`. A fresh manifest install confirms the enabled feature set is `avcodec;avformat;core;gpl;swscale;x264;x265` and no `swresample.lib` exists.

| Category | Measured value | Meaning |
|---|---:|---|
| Feature-constrained vcpkg research archives | 249,727,200 B | Sum of `avcodec`, `avformat`, `avutil`, `swscale`, `libx264`, `x265-static`; not a production delta |
| Research executable | 25,717,248 B | Standalone `lpb_r6_libav_probe.exe`; includes only what its linker retained, but is not a product DLL |
| Existing product Native Release DLL | 8,649,728 B | Current MF product binary |
| Actual product delta from this candidate | **0 B** | Candidate is outside production `vcpkg.json`, CMake graph, runtime package, and Native DLL |
| Internally `--disable-everything` minimal FFmpeg build | **now measured separately** | See the production-shaped candidate record below |

The vcpkg port is **feature-constrained, not internally minimal**. Its logged configure options disable applications, avfilter, swresample and many external libraries, but the build still compiles broad codec sources (for example Huffyuv and VVC). Therefore the archive total must never be described as a “minimal production delta.” The separately reproducible `--disable-everything` candidate and its production-shaped DLL measurement are recorded in [P4-R6-Minimal-Libav-Distribution-Record.md](./P4-R6-Minimal-Libav-Distribution-Record.md).

## 4. Minimal production-shaped candidate completed

`Invoke-MinimalLibavBuild.ps1` builds FFmpeg `n9.0.1` from the pinned source
tarball with `--disable-everything`, then enables only the exact MOV/MP4,
H.264/HEVC, AAC packet-copy and swscale closure exercised by the fixed matrix.
The resulting FFmpeg archive subtotal is 15,581,052 B, not 249 MB. CMake then
links those archives plus the required static x264/x265 libraries into a
12,785,664 B DLL exposing a UTF-16 C ABI; an independent smoke loader
successfully transcodes Huawei Main10 HLG through that DLL.

`dumpbin` records only `KERNEL32.dll`, `ADVAPI32.dll`, and `ncrypt.dll` as
runtime imports. The candidate contains no third-party runtime DLL and no
production subprocess. The 12,785,664 B sidecar is therefore the actual
package increment for the declared hybrid shape; it is intentionally **not**
the current product delta, which remains 0 B because P5 has not added it.

The candidate is GPL-covered because it enables GPL FFmpeg code and statically
links x264/x265. Its source/configuration, notices, GPL distribution
obligations and separate H.264/HEVC patent question are recorded in
[P4-R6-Minimal-Libav-Distribution-Record.md](./P4-R6-Minimal-Libav-Distribution-Record.md).

## 5. Verification record

| Check | Result |
|---|---|
| Reconcile before remediation | PASS — P4 / in-progress at `82df651` |
| Candidate Release reconfigure/build after dependency change | PASS, Windows x64/MSVC |
| Chinese + emoji direct candidate probes | PASS |
| Fresh minimal-build four-case differential matrix | PASS — all 8 conversions, 8 probes, 8 quality runs exit 0 |
| Quality logs | PASS — no time-base mismatch or conversion-failed line |
| Research manifest re-install without swresample | PASS |
| `--disable-everything` minimal FFmpeg build | PASS — static libraries only, no CLI programs |
| Production-shaped candidate DLL + UTF-16 smoke | PASS — real Huawei Main10 HLG transcode and exported C ABI |
| Production source/dependencies | unchanged by this remediation |

The earlier focused production evidence remains applicable: Native Debug and Release builds passed; Debug `VideoConverterTests` + `NativeRuntimeTests` passed 36/36; Release `VideoConverterTests` + `NativeRuntimeTests` + `ExtractorRealSampleTests` passed 50/50 with no skips. The present remediation changes only the isolated candidate, its harness, and its closeout evidence.

## Local backend freeze and external gate handoff

The original blockers 1 (Unicode), 2 (true differential automation), 3 (quality/bitrate-size evidence), and 4 (misleading FFmpeg footprint claim) now have concrete evidence. The minimal production-shaped candidate resolves the former Gate 1/Gate 8 unknown: it has a measured 12,785,664 B sidecar runtime form and a recorded distribution posture.

The local R6 freeze proposal is **Hybrid**: project-owned ISO-BMFF truth/remux remains project-owned; ordinary SDR compatibility-first conversion remains software-forced MF; 10-bit or HDR/color-preservation-required conversion is owned by the minimal libav* backend. Dispatch is a preselected capability owner, never a hidden retry fallback. The detailed ownership record is in [P4-R6-Minimal-Libav-Distribution-Record.md](./P4-R6-Minimal-Libav-Distribution-Record.md).

The following facts still require external Chief Gate judgment, not a local phase-PASS declaration:

1. The existing MF path remains intentionally software-forced and fail-closed, but its measured output quality and Huawei HDR metadata are weaker than the minimal candidate in several cases.
2. The minimal candidate has a measured static sidecar/package shape, but it still does not own Live Photo vendor metadata, extraction semantics or mutation authority; those remain project-owned.
3. P4 must remain in progress and R7/P5 must not begin until the external Chief Gate accepts this decision record.

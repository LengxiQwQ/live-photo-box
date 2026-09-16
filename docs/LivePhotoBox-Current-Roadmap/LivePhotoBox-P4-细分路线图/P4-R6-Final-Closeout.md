# P4-R6 Final Closeout — Video Backend Differential / Windows Backend Freeze

## Local status

**PASS — ready for external gate review.** This is a local implementation and
evidence closeout only; it does not mark P4 complete or start R7.

## Frozen P5 decision

```text
WindowsVideoBackend = hybrid by fixed capability ownership

project-owned ISO-BMFF probe / stream-copy remux = LivePhotoBox Native
codec decode / encode / cross-codec transcode       = Windows Media Foundation
hardware policy                                    = software forced
generic libav*                                     = not in the production package
ffmpeg.exe / ffprobe.exe                           = never a production subprocess
```

This is not a retry tree. A codec transform has exactly one P5 owner: Media
Foundation with `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = FALSE`. The Native
v2 ABI returns `lpb_video_backend_diagnostics` (backend, hardware mode,
fallback flag, selected encoder, and reason). C# consumes that POD record;
it no longer infers hardware/fallback from an encoder string. The old ABI is
retained for compatibility.

The frozen software policy makes both hardware-present and hardware-absent
hosts deterministic: neither is permitted to silently select a hardware MFT
or fall back to software with changed output semantics. The diagnostic states
`SoftwareForced`, `HardwareFallbackOccurred = false`, and the explicit
reason. A future P5 hardware capability may be added only as a separately
diagnosed owner/policy, not by changing the fallback behaviour of this path.

## Ownership and fail-closed changes

- `probe_video_reader` and `remux_video_reader` remain the project-owned
  ISO-BMFF structural authority. No generic candidate becomes protocol,
  vendor-metadata, MOV/MP4 mutation, or preservation authority.
- A source that declares audio now fails before publication if Media
  Foundation cannot read/configure its audio route; it is no longer allowed to
  proceed by clearing `has_audio_stream`.
- Managed conversion re-probes the output and fails/cleans it if source audio
  or rotation is not retained. It does not report a semantic success with a
  false preservation flag.
- HDR/color/10-bit remains a P5 converter-contract capability gate. R6 found
  that the present MF path changes Main10/HLG when asked for ordinary H.264;
  that result is negative evidence, not a preservation claim.

## Candidate comparison

The repeatable, non-production candidate is in
`tools/r6-video-differential/`. Its own manifest is intentionally separate
from the product manifest and selects FFmpeg 9.0.1 libraries only:
`avformat`, `avcodec`, `avutil`, `swresample`, `swscale`, `libx264`, and
`libx265`; `ffmpeg`, `ffprobe`, `ffplay`, filters, devices, networking, and
unrelated external codec features are disabled. Its C++ executable is a real
library decode/transcode experiment, not an FFmpeg CLI wrapper. The reusable
`Invoke-R6Differential.ps1` writes candidate probe evidence and optional
independent `ffprobe` reports.

| Dimension | Windows Media Foundation (selected) | minimal libav* candidate |
|---|---|---|
| Container/protocol ownership | Project retains probe/remux/mutation truth | Candidate explicitly only receives an already extracted video |
| Apple HEVC MOV → H.264 | Independent output: H.264 Constrained Baseline, AAC, 2.069 s | H.264 High, PCM copied, 2.065 s; only primary video/audio emitted |
| vivo H.264 → HEVC | Independent output: HEVC Main, AAC LC, 2.067 s | HEVC Main, HE-AAC copied, 2.043 s |
| Huawei Main10 HLG | H.264 result is 8-bit and lacks color tags: negative evidence | libx264 produced H.264 High 10 with BT.2020/HLG; x265 build explicitly rejected Main10 HEVC because it is 8-bit |
| Rotation/audio handling | Output audio/rotation are post-checked and conversion fails on loss | Candidate preserves only its selected video + first audio; it does not own auxiliary/data tracks |
| Real path handling | Native accepts project UTF-8 paths | Candidate `avformat_open_input` failed for every direct non-ASCII project path; ASCII staging was required for the experiment |
| Hardware/fallback | Software forced and ABI-diagnosed; no silent fallback branch | libx264/libx265 software-only in this build |
| Measured wall time, warm local single run | Apple 2,143 ms; vivo 1,717 ms (includes CLI host) | Apple 1,905 ms; vivo 2,343 ms |
| Production static-library delta | none | 251,181,526 B for selected libraries; research executable 25,867,264 B |

The timing numbers are only local observations, not a benchmark winner. They
were not used as the decision criterion.

## Real-sample matrix and independent evidence

The corpus was extracted through the product's structural path into the fixed
workspace `.ai-tmp/workspace/P4-R6/extracted-videos`; original files under
`designs/各个机型测试/` were not mutated. `ExtractorRealSampleTests` exported
and checked 14 real extraction cases. Candidate decode was exercised against
Apple dual-file MOV, vivo dual-file H.264 MP4, Huawei Main10 HLG MP4, OPPO,
Redmi H.264, Samsung, and vivo Main10 HLG. The broader extracted corpus also
contains Xiaomi, Honor, OnePlus, and Apple HEIC-paired sources.

Independent `ffprobe` validation was applied to MF and candidate outputs. It
verified output container, codec/profile, pixel format, dimensions, duration,
audio mapping/sample rate/channel count, frame rate, and color metadata where
present. It also independently established the MF Huawei degradation and the
candidate Huawei H.264 High 10 + BT.2020/ARIB HLG result. This validator is
offline evidence only and is not invoked by production code.

Coverage gaps are explicit: the available corpus has no demonstrated odd-size
sample and no controlled hardware-enabled encoder result, because the frozen
P5 policy is software-only. Those are not relabelled as device-compatibility
success.

## Build, tests, and package evidence

| Check | Result |
|---|---|
| minimal libav* vcpkg install | PASS, `ffmpeg[avcodec,avformat,core,gpl,swresample,swscale,x264,x265]:x64-windows-static@9.0.1#1`; no CLI applications enabled |
| candidate CMake build | PASS, Windows x64/MSVC Release |
| candidate probe/transcode and reusable harness | PASS; evidence in `.ai-tmp/workspace/P4-R6/harness-evidence/` |
| Native Debug build | PASS, `scripts/native/build-native.ps1 -Configuration Debug -Architecture x64` |
| Native Release build | PASS, same command with `Release` |
| Debug video/ABI tests | PASS, 36/36, 0 skipped (`VideoConverterTests` + `NativeRuntimeTests`) |
| Release video/ABI/extraction tests | PASS, 50/50, 0 skipped (`VideoConverterTests` + `NativeRuntimeTests` + `ExtractorRealSampleTests`) |
| Production manifest | unchanged: `libjpeg-turbo`, `libheif`; no libav* dependency and no FFmpeg application runtime |

The full R1-sized Debug filter was launched after the focused gates. The
testhost completed outside the command capture window, so no pass/fail count
is recorded here; it is deliberately not used as acceptance evidence. The
focused commands above are the recorded evidence.

## R6 acceptance mapping

| # | Gate | Status | Evidence |
|---:|---|---|---|
| 1 | MF and viable minimal libav* compared fairly | PASS | Separate minimal library candidate, same extracted inputs |
| 2 | Real Live Photo video | PASS | 14 extraction cases and multi-vendor matrix |
| 3 | Independent structural/output validation | PASS | `ffprobe` output facts |
| 4 | Timing/audio/rotation/profile compared | PASS | Matrix and post-conversion checks |
| 5 | HDR/color/10-bit evidence | PASS | Huawei Main10/HLG positive and negative results |
| 6 | Hardware/software/fallback diagnosable | PASS | Versioned POD diagnostic and software-forced policy |
| 7 | Performance not sole decision | PASS | Correctness/ownership/package lead the decision |
| 8 | Package delta measured | PASS | 251,181,526 B selected static libraries |
| 9 | ISO-BMFF authority retained | PASS | Existing project probe/remux remains selected |
| 10 | P5 Windows backend frozen | PASS | Fixed hybrid ownership above |
| 11 | Hybrid ownership clear | PASS | Explicit single owner per capability |
| 12 | Unselected runtime absent | PASS | Product `vcpkg.json` unchanged |
| 13 | No production CLI subprocess | PASS | Candidate/tools are research-only; production path is Native MF |
| 14 | P5 need not re-open selection | PASS | Decision, diagnostics, limitations, and P5 handoff recorded |

## P5 handoff (not P5 implementation)

P5 must implement its public converter contract using only the frozen owners
above. It must expose request/output semantics for HDR/color/10-bit and reject
any requested preservation it cannot independently demonstrate; it must not
re-run the MF-vs-libav selection. In particular, the current MF H.264 path
cannot be advertised as HDR-preserving. A future hardware path requires a new
explicit diagnostics contract and real evidence before it can be enabled.

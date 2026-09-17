# P4-R6 — minimal libav* production-shaped candidate record

## Scope and status

This is an R6 research and decision record, not a production dependency
change. `LivePhotoBox.Native.dll` continues to contain no libav*, x264 or x265
code. The only product change proposed by this record is a future, explicit
P5 capability owner decision; it must not be implemented as an automatic
fallback.

## Reproducible minimal build

The build is reproducible through
`tools/r6-video-differential/Invoke-MinimalLibavBuild.ps1`. Its completed
local evidence root is `.ai-tmp/workspace/P4-R6/minimal-libav-build-v12`.

| Item | Exact record |
|---|---|
| FFmpeg source | `n9.0.1`, cached tarball SHA-256 `195D54BEBE1A27F84D77F4B989D193466F305B355DA92292766A69F16880B18A` |
| Toolchain | Windows x64/MSVC, static `/MT`; vcpkg-provided MSYS/NASM only to build the libraries |
| Global constraint | `--disable-everything`, static libraries, no programs, no network, no shared libraries |
| Explicitly enabled | `avutil`, `avcodec`, `avformat`, `swscale`; `file`; MOV demuxer; MOV/MP4 muxers; H.264/HEVC/AAC/PCM S24LE decoders; H.264/HEVC/AAC parsers; `libx264`/`libx265`; `aac_adtstoasc` |
| Explicitly disabled | `ffmpeg`/`ffprobe`/`ffplay`, avdevice, avfilter, swresample, autodetect, networking, docs and debug |
| Dependency closure | FFmpeg automatically adds required internal parser/bitstream-filter closure; it does not add another externally shipped codec runtime |

The static archives are build inputs, not a package-delta claim:

| Archive | Bytes |
|---|---:|
| `avcodec.lib` | 7,633,088 |
| `avformat.lib` | 2,591,050 |
| `avutil.lib` | 2,320,400 |
| `swscale.lib` | 3,036,514 |
| **minimal FFmpeg archive subtotal** | **15,581,052** |

`libx264.lib` and `x265-static.lib` are linked from the pinned x64 static
vcpkg prefix. `swresample` is neither built nor linked: the candidate
packet-copies its eligible first audio stream.

## Measured runtime/package shape

The Release target is `lpb_r6_minimal_libav_backend.dll`, not a command-line
program. It exports one C ABI entry point accepting UTF-16 paths; the separate
smoke loader successfully transcoded the real Huawei Main10 HLG sample through
that export.

| Measured artifact | Bytes | Meaning |
|---|---:|---|
| Current MF `LivePhotoBox.Native.dll` | 8,649,728 | Existing product baseline |
| minimal libav* research DLL | 12,785,664 | Static sidecar payload for the declared future libav capability |
| minimal libav* research CLI | 12,788,736 | Harness only; never a package candidate |
| UTF-16 smoke loader | 139,264 | Harness only; never packaged |
| Sidecar package increment | **12,785,664** | Actual bytes added if the hybrid ships this DLL beside the existing Native DLL |
| Sidecar minus current Native DLL | 4,135,936 | Size comparison only; not a replacement estimate |

`dumpbin /imports` on the candidate DLL records only Windows system DLLs:
`KERNEL32.dll`, `ADVAPI32.dll`, and `ncrypt.dll`. There is no third-party DLL,
no VC runtime DLL, no `ffmpeg.exe`, and no `ffprobe.exe` runtime dependency.
The DLL statically retains only code reached by its H.264/HEVC transcode ABI;
this is the realistic package/runtime number for the declared sidecar form,
not the prior 249 MB broad-vcpkg archive total.

## License and distribution record

The repository is GPL-3.0. This candidate deliberately enables GPL code and
links `libx264` and `x265`; the installed vcpkg SPDX metadata records both
codec libraries as `GPL-2.0-or-later`. The FFmpeg legal page states that
`--enable-gpl` makes the FFmpeg build GPL-covered. Accordingly a future
distribution must ship the corresponding source/build scripts and notices for
the exact source and configuration above; it may not describe the candidate
as LGPL-only.

This record is not legal advice and does not resolve codec patent obligations.
H.264/HEVC patent/distribution obligations remain a separate release gate,
including for Microsoft Store and every distribution channel. x265 documents
that its GPL license and any patent rights are separate matters. No binary is
distributed by this R6 research commit.

## R6 backend freeze record (local; external Chief acceptance required)

The evidence no longer supports a universal MF-only freeze. The locally
frozen P5 ownership proposal is the allowed **hybrid**, with no retry tree:

```text
project-owned ISO-BMFF structure, vendor metadata, extraction and remux
  → LivePhotoBox Native only

ordinary SDR / compatibility-first cross-codec transcode
  → Windows Media Foundation, software forced

10-bit or HDR/color-preservation-required cross-codec transcode
  → minimal libav* static backend DLL
```

The capability is selected before a conversion from source facts and explicit
requested preservation policy. An unsupported selected capability fails with
its selected-backend diagnostic; it must not silently retry the other backend.
The minimal libav DLL is not added until the P5 Converter contract implements
that explicit dispatch and distribution obligations are carried with it.

This record does not mark P4 complete or start R7/P5. External Chief Gate
acceptance is still required.

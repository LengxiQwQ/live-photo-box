# P4-R6 libav* differential research candidate

This directory is a research-only candidate used to decide the Windows video
backend. It is intentionally outside the production `vcpkg.json` and native
DLL CMake graph.

Its vcpkg research manifest selects the library components used by this
experiment: `avformat`, `avcodec`, `avutil`, `swscale`, `x264`, and `x265`.
`swresample` is deliberately absent because the candidate packet-copies audio
instead of resampling it. It deliberately does **not** enable `ffmpeg`,
`ffprobe`, `ffplay`, filters, devices, or networking.

The vcpkg manifest remains a feature-constrained research baseline; it is
not described as an internally `--disable-everything` build. The separately
reproducible minimal build is `Invoke-MinimalLibavBuild.ps1`: it starts from a
pinned FFmpeg source tree, disables everything, and explicitly enables only
the MOV/MP4, H.264/HEVC, AAC packet-copy and swscale closure used by
`matrix.json`. It produces static libraries only—never `ffmpeg`, `ffprobe`,
or another production subprocess.

When CMake receives `LPB_R6_MINIMAL_PREFIX` and
`LPB_R6_EXTERNAL_CODEC_PREFIX`, it builds three research artifacts from that
minimal prefix:

- `lpb_r6_libav_probe.exe`, the differential CLI;
- `lpb_r6_minimal_libav_backend.dll`, a static, production-shaped DLL with
  only the UTF-16 C ABI `lpb_r6_transcode_video_utf16`;
- `lpb_r6_libav_backend_smoke.exe`, a loader that proves the DLL ABI.

None is in the production CMake/vcpkg/runtime graph. The DLL is the measured
sidecar-package cost for a future hybrid integration; it is not an assertion
that the current product binary already contains libav*.

The executable establishes independently repeatable library-level input
compatibility and stream facts. It uses `wmain`, converts Windows UTF-16
arguments to UTF-8 with `WideCharToMultiByte`, then calls libav; Chinese and
emoji paths are therefore tested at the real library boundary rather than
through the CRT narrow-argument code page. It opens every discovered decoder
and has a real H.264/HEVC transcode path using `libx264`/`libx265`; the first
audio track is packet-copied where the target container permits it. It deliberately does
not mux vendor metadata/data tracks or interpret a Live Photo outer container:
the R6 workflow extracts a video range through the project-owned structural
path first, then passes that isolated video to this candidate. Any candidate
output must be independently validated and its metadata-track limitation is a
negative comparison result, not a preservation claim.

`Invoke-R6Differential.ps1` consumes `matrix.json` and, for each fixed real
sample, runs both the production Windows Media Foundation path and this
candidate into separate fresh outputs. It records both conversion logs,
elapsed time, output bytes, independent `ffprobe` facts, and PSNR/SSIM against
the decoded source frames. The source is explicitly scaled to each encoded
output's stored dimensions for the metric; rotation and HDR facts remain
separate structural observations. The harness fails if either conversion,
probe, or metric collection fails and will never overwrite a prior evidence
directory.

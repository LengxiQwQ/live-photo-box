# P4-R6 minimal libav* differential candidate

This directory is a research-only candidate used to decide the Windows video
backend. It is intentionally outside the production `vcpkg.json` and native
DLL CMake graph.

Its manifest selects only library components needed for a real H.264/HEVC
decode/encode experiment: `avformat`, `avcodec`, `avutil`, `swresample`,
`swscale`, `x264`, and `x265`. It deliberately does **not** enable `ffmpeg`,
`ffprobe`, `ffplay`, filters, devices, networking, or unrelated codecs.

The executable establishes independently repeatable library-level input
compatibility and stream facts. It opens every discovered decoder and has a
real H.264/HEVC transcode path using `libx264`/`libx265`; the first audio track
is packet-copied where the target container permits it. It deliberately does
not mux vendor metadata/data tracks or interpret a Live Photo outer container:
the R6 workflow extracts a video range through the project-owned structural
path first, then passes that isolated video to this candidate. Any candidate
output must be independently validated and its metadata-track limitation is a
negative comparison result, not a preservation claim.

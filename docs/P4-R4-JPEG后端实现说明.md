# P4-R4 JPEG Native Backend implementation notes

## Ownership

`LivePhotoBox.Native/src/media/jpeg_backend.cpp` is the Native codec seam. It uses libjpeg-turbo 3.2.0 for JPEG RGB pixel decode, RGB JPEG encode, and perfect-only DCT-domain transforms. No libjpeg/turbojpeg implementation type appears in the public C ABI or managed interop.

Project-owned code remains authoritative for JPEG marker hierarchy, APP segments, Exif, XMP, MakerNote, vendor payloads, GainMap/Ultra HDR carriers, and live-photo protocol semantics. A codec result is never treated as a preservation verdict by itself.

## Production data paths

- JPEG → HEIC: Native libjpeg-turbo decodes the delimited JPEG codestream to project-owned RGB. WIC is used only to encode the still-Windows-specific HEIC container. The managed execution record reports cross-container conversion as `PartiallyPreserved`; R5 owns HEIC metadata/auxiliary detach-and-reattach policy.
- HEIC → JPEG: WIC currently decodes HEIC pixels, then Native libjpeg-turbo alone produces the JPEG result. WIC does not select or execute a result-affecting JPEG encoder.
- JPEG DCT transform: `lpb_transform_jpeg_losslessly` uses TurboJPEG transform with `TJXOPT_PERFECT`. It never falls back to decode/re-encode. It refuses a non-default/unverifiable Exif orientation, non-perfect MCU alignment, malformed codestreams, and appended protocol/GainMap data. Those cases require project-owned structural reassembly before a transform can be safely offered.

Both codec writers use the existing `windows_owned_output` create/write/publish-no-replace path; neither overwrites a source or replaces an existing destination.

## Capability and dependency identity

`vcpkg.json` declares `libjpeg-turbo`; CMake resolves it through `find_package(JPEG REQUIRED)` and `find_package(libjpeg-turbo CONFIG REQUIRED)`. The Native runtime exposes `LPB_CAPABILITY_JPEG_BACKEND` and the project-owned `lpb_get_jpeg_backend_version` diagnostic string (`libjpeg-turbo 3002000` for the current 3.2.0 port). This is availability/identity reporting only, not a third-party ABI surface.

## Legacy-path audit

| Path | Current role | Production result authority | Closure |
| --- | --- | --- | --- |
| WIC JPEG decoder | None in `convert_image_file`; JPEG inputs decode through Native libjpeg-turbo | No | Removed from the R4 JPEG source decode path |
| WIC JPEG encoder | None in `convert_image_file`; JPEG outputs encode through Native libjpeg-turbo | No | Removed from the R4 JPEG output path |
| WIC HEIC decode/encode | Temporary HEIC container boundary | Yes, for HEIC only | R5 owns libheif/backend policy and metadata/auxiliary reattachment |
| Magick.NET | UI thumbnail and legacy HEIC service paths outside `ImageConverter` | Not used by the R4 Native JPEG backend | R5/R7 audit and closure |
| jpegtran / external JPEG CLI | No Rebuilt Native production call path | No | Historical text/tests only; not a R4 fallback |
| ExifTool | Offline independent validation test only | No | Never a writer/runtime dependency |

## Preservation boundary

For cross-container pixel conversions, metadata success is intentionally not inferred from codec success. The current managed record remains `PartiallyPreserved` unless a same-container structure copy is performed. The DCT transform preserves its JPEG marker stream only for a standalone, structurally complete JPEG; it fail-closes rather than dropping protocol tails or applying a physical rotation while a non-default orientation remains.

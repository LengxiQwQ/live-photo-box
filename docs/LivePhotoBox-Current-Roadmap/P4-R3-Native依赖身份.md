# P4-R3 Native dependency identity

The canonical Windows x64 Native build declares every third-party C++ dependency in `vcpkg.json`. Its built-in Microsoft vcpkg registry baseline is pinned to commit `9e44ec0e9f247d77c230ced0ee66c76296837807` (observed 2026-09-15). Dependencies are resolved through vcpkg manifest mode and CMake imported targets; they are not manually downloaded into `.vcxproj` or machine library paths.

| Dependency | Source/version | Features / disabled | Linkage/runtime | License | Why |
|---|---|---|---|---|---|
| Windows SDK system libraries: `bcrypt`, `windowscodecs`, `ole32`, `shlwapi`, `mfplat`, `mfreadwrite`, `mfuuid` | Installed Windows SDK selected by VS/MSVC toolchain; no floating third-party download | Windows OS APIs only | System DLLs, import libraries from SDK; no shipped third-party runtime binaries | Windows SDK/OS license | Cryptography, WIC image operations, COM/path API, Media Foundation video |
| MSVC C++ runtime | Installed VS MSVC x64 toolchain | `/MTd` Debug, `/MT` Release | Static runtime, no extra VC runtime DLL in native artifact | Microsoft Visual Studio license | Match established `.vcxproj` runtime policy |
| `libjpeg-turbo` | vcpkg port `3.2.0` at the pinned baseline | Default port feature set; no patches | `x64-windows-static`, linked through `JPEG::JPEG` and `libjpeg-turbo::turbojpeg-static` CMake imported targets | BSD-3-Clause and IJG/MIT-style license as declared by the vcpkg port | R4 Native JPEG pixel decode/encode and perfect-only DCT-domain transforms; project code remains marker/Exif/XMP/vendor/protocol authority |
| vcpkg ports | Microsoft built-in registry pinned at the manifest baseline | `libjpeg-turbo` only; no patches | Static codec libraries are linked into the Native target; no PATH or machine-local codec dependency | Per-port license above | Reproducible codec dependency authority |

The build uses no `FetchContent`, vendored patch or hand-downloaded library. CMake rejects non-MSVC or non-Windows production builds in R3; future non-Windows support must first supply a real platform/media backend and verification.

# P4-R3 Native dependency identity

The canonical Windows x64 Native build currently uses **no third-party C++ package**. `vcpkg.json` therefore declares an empty dependency set instead of fetching a speculative codec. Its built-in Microsoft vcpkg registry baseline is pinned to commit `9e44ec0e9f247d77c230ced0ee66c76296837807` (observed 2026-09-15). R4/R5 codec dependencies belong in this manifest with explicit port features and versions; they are not manually downloaded into `.vcxproj` or machine library paths.

| Dependency | Source/version | Features / disabled | Linkage/runtime | License | Why |
|---|---|---|---|---|---|
| Windows SDK system libraries: `bcrypt`, `windowscodecs`, `ole32`, `shlwapi`, `mfplat`, `mfreadwrite`, `mfuuid` | Installed Windows SDK selected by VS/MSVC toolchain; no floating third-party download | Windows OS APIs only | System DLLs, import libraries from SDK; no shipped third-party runtime binaries | Windows SDK/OS license | Cryptography, WIC image operations, COM/path API, Media Foundation video |
| MSVC C++ runtime | Installed VS MSVC x64 toolchain | `/MTd` Debug, `/MT` Release | Static runtime, no extra VC runtime DLL in native artifact | Microsoft Visual Studio license | Match established `.vcxproj` runtime policy |
| vcpkg ports | Microsoft built-in registry pinned at the manifest baseline | None enabled; no patches | No package binary or license payload today | N/A | Single reproducible dependency entry for later codec rounds |

The build uses no `FetchContent`, vendored patch or hand-downloaded library. CMake rejects non-MSVC or non-Windows production builds in R3; future non-Windows support must first supply a real platform/media backend and verification.

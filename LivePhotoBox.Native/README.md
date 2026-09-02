# LivePhotoBox.Native

`LivePhotoBox.Native` is the x64 Windows C++20 runtime execution engine used by `LivePhotoBox.Core`, the WinUI application, and the CLI. It is a first-class Visual C++ DLL project in `Live Photo Box.sln`.

---

## 1. Architecture and Boundary

The product strictly follows the **"C# = Control Plane, C++ Native = Execution / Data Plane"** architectural principle:

- **C# Control Plane (`LivePhotoBox.Core`)**:
  - Handles WinUI & CLI user interaction and task orchestration.
  - Owns DTOs, request models, and immutable facts (`SourceMediaFacts`, `ExtractedMediaBundle`, `NeutralMediaBundle`).
  - Manages transaction workspace lifecycles (`IMediaWorkspace`) and enforces source file SHA256 immutability before and after processing.
  - Bridges Native C ABI via `[LibraryImport]` with async marshaling, progress reporting, and cancellation forwarding.
- **C++ Native Execution Plane (`LivePhotoBox.Native`)**:
  - Parses binary container structures: JPEG APP segments, TIFF/EXIF, XMP/RDF, HEIF items/boxes, ISOBMFF/QuickTime trees, MakerNotes, and Samsung SEF trailers.
  - Performs byte-level source inspection, isolated chunked slice extraction, WIC image conversion, and container-aware ISOBMFF video probing/remuxing/transcoding.
  - Implements vendor-specific protocol cleaners and writers.
- **No Silent Fallback**:
  - The product provides only two global modes: `rebuilt` (default) and `legacy` (v2.2.1 golden baseline).
  - If a capability is not yet implemented or fails in `rebuilt`, it explicitly throws `RebuiltPipelineNotReadyException` / `Unsupported`. **There is no automatic runtime fallback to Legacy.**

---

## 2. Directory Organization

The source code is organized into modular layers:

- `include/livephotobox_native.h`: Public C ABI declarations (the only header exposed to C#).
- `src/foundation/`: Context management, UTF-8 path conversions (`utf8_to_path`), error reporting, diagnostics, and cancellation checks (`internal.h`, `context.cpp`).
- `src/binary/`: Low-level binary IO, memory buffers, and endianness utilities (`binary_io.h`, `endian.h`).
- `src/containers/`: Standard format container parsers and writers (`isobmff.cpp`, `heif.cpp`, `mp4_strip.cpp`).
- `src/metadata/`: Image metadata handlers (`jpeg.cpp`, `exif.cpp`, `exif_rewrite.cpp`).
- `src/media/`: High-level media execution plane implementations:
  - `media_inspector.cpp`: Byte-level source protocol inspection across 10 manufacturer formats.
  - `media_extractor.cpp`: Non-modifying chunked byte range extraction into workspace files.
  - `image_converter.cpp`: Direct structure copy and Windows Imaging Component (WIC) transcode pipeline.
  - `video_converter.cpp`: ISOBMFF box traverser for video probing and container stream remuxing.
  - `media_api.cpp`: Coarse-grained C ABI export implementations.
- `src/protocols/`: Vendor-specific live photo protocol handlers (`apple.cpp`, `apple_mebx.cpp`, `huawei.cpp`, `samsung_sef.cpp`, `vivo_legacy.cpp`).

---

## 3. Build

Build `LivePhotoBox.Native` in Visual Studio, build the full solution, or run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/build-native.ps1 -Configuration Release -Architecture x64 -RunTests
```

The project uses MSVC directly. Visual Studio 2026 selects toolset `v145`; Visual Studio 2022 and CI images select `v143`. Outputs are written to:

```text
artifacts/native/{Configuration}/win-x64/
```

Normal `dotnet build` operations build the Native project through the project reference in `LivePhotoBox.Core.csproj`.

---

## 4. ABI Rules

- **Pure C ABI**: C++ classes, templates, STL types, and exceptions never cross the boundary.
- **Opaque Handles**: All handles (`lpb_context*`, etc.) are opaque pointers managed by their respective creator APIs.
- **Extensible Structs**: All extensible C structs start with `struct_size` for forward compatibility.
- **Memory & Buffers**: Destination buffers and output strings are explicitly sized and caller-allocated or static.
- **UTF-8 Strings**: All path and string parameters are UTF-8 encoded; Windows file operations convert to UTF-16 wide paths natively.
- **Exception Safety**: All C++ exceptions are caught internally and mapped to `lpb_result` error codes before returning.

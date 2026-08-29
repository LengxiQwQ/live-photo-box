# LivePhotoBox.Native

`LivePhotoBox.Native` is the x64 Windows C++ runtime used by `LivePhotoBox.Core`, the WinUI application, and the CLI. It is a first-class Visual C++ DLL project in `Live Photo Box.sln`, so Visual Studio provides the normal C++ project model, IntelliSense, build properties, debugging, and error reporting.

Phase 1 intentionally contains only the stable C ABI foundation:

- ABI and product version negotiation
- opaque context ownership
- result codes and per-context diagnostics
- logging and cancellation callback contracts
- capability discovery
- managed runtime smoke tests

Protocol and media implementations will be migrated behind this ABI in later changes. Existing C# protocol behavior remains the reference until each Native implementation passes differential and real-device validation.

## Build

Build the `LivePhotoBox.Native` project in Visual Studio, build the full solution, or run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/build-native.ps1 -Configuration Debug -Architecture x64 -RunTests
```

The project uses MSVC directly. Visual Studio 2026 selects `v145`; Visual Studio 2022 and current CI images select `v143`. The product version is generated from `LivePhotoBox/Package.appxmanifest`, and ignored outputs are written to:

```text
artifacts/native/{Configuration}/win-x64/
```

Normal `dotnet build` operations build the Native project through the non-managed project reference in `LivePhotoBox.Core.csproj`. Set `SkipNativeBuild=true` only when a caller has already produced the matching artifact.

## ABI rules

- The public surface is C ABI only; C++ classes, STL types, and exceptions never cross the boundary.
- All handles are opaque and released by the API that created them.
- Extensible structs start with `struct_size` and `abi_version`.
- Diagnostic strings are UTF-8 and remain owned by the caller-provided buffer or by static Native storage.
- Native exceptions must be converted to `lpb_result` before an API returns.
- The initial runtime and release pipeline support x64 only.

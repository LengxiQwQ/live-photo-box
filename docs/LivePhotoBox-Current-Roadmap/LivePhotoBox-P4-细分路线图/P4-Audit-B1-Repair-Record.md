# P4 Audit B1 Repair Record

> **Role:** Implementer remediation record, not a replacement for the Fresh
> Independent Audit.  It records the local repair evidence only; P4 remains
> in progress and requires a new independent audit before any Verify decision.

## Scope and root cause

The Fresh Audit found B1 in the TestHarness build graph.  `apple.cpp` is in
`LPB_PORTABLE_SOURCES`, so it was compiled into `livephotobox_portable_core`
before `LPB_NATIVE_TEST_HARNESS` was applied to the TestHarness DLL.  The raw
HEIC MakerNote success test consequently linked the production-gated object
and failed with `LPB_RESULT_AUTHORITY_VIOLATION`.

## Architecture repair

`CMakeLists.txt` now creates two static targets from the one and only
`LPB_PORTABLE_SOURCES` list:

```text
livephotobox_portable_core
  LPB_NATIVE_BUILD
  -> LivePhotoBox.Native.dll

livephotobox_portable_core_test
  LPB_NATIVE_BUILD + LPB_NATIVE_TEST_HARNESS
  -> LivePhotoBox.Native.TestHarness.dll only
```

No protocol source was copied or moved out of the portable core.  The
production DLL continues to link only `livephotobox_portable_core`; the test
variant is configured only when `LPB_BUILD_TEST_HARNESS=ON` and is linked only
by the separately named TestHarness DLL.  Neither static library has an
install/package rule.

## Production authority safety

The production `apple.cpp` authority gates are unchanged.  A new direct P/Invoke
test invokes `lpb_apple_inject_makernote_heic` in `LivePhotoBox.Native.dll`
with a real HEIC input and no cleanup-plan authority.  It requires and observed
`LPB_RESULT_AUTHORITY_VIOLATION`, with an authority diagnostic.

## Real-sample success and preservation evidence

`AppleMakerNoteInjection_CreatesOrUpdatesHeifExifItem` uses the read-only
`oppo.jpg` sample.  The TestHarness now successfully writes the returned HEIC
bytes, then proves:

- a valid HEIF Exif item is located;
- the Apple MakerNote and expected ContentIdentifier are present;
- ExifTool independently opens the rewritten HEIC;
- the source sample SHA-256 is unchanged before and after the operation.

The assertion remains a successful raw TestHarness mutation assertion; it was
not weakened to expect authority rejection.

## Local validation

| Scope | Result |
|---|---:|
| Release CMake production Native + portable smoke | pass (1/1 smoke) |
| Release Native runtime/ABI | pass (16/16) |
| B1 direct success + production rejection | pass (2/2) |
| Release ImageConverter, Apple MakerNote, HEIC/GainMap | pass (51/51) |
| Release authority / real filesystem transaction / trust chain | pass (80/80) |
| Debug CMake production/TestHarness + portable smoke | pass (1/1 smoke) |
| Debug Native runtime/ABI | pass (16/16) |

All listed runs had zero skipped tests.  The Release TestHarness binary used by
the test process matched the freshly built artifact by SHA-256:

```text
LivePhotoBox.Native.dll              06775E49F576F77D247C43C53A074929620F1266BBF2F4CEA7B43BBE7215B23F
LivePhotoBox.Native.TestHarness.dll  739E82CA5129C5FDEA1863D879AAD9D014E732A97F1B3334C85513A878538C07
```

A fresh temporary self-contained Release publish contained 630 files / 290,329,892
bytes and contained neither `TestHarness` nor `portable_core_test` artifacts.

## Remaining limits

This repair does not re-run package/MSIX footprint verification and does not
change its status.  It does not declare P4 complete, does not run Verify, and
does not replace a fresh independent audit.

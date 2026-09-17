# P4 Audit B2 Remediation Record

## Scope

This record closes the test-fixture defect found by Fresh Audit Round 2.  It
does not alter production Native code, the TestHarness build graph, or the
production cleanup-authority model.

## Root cause and historical context

`NativeAppleMakerNoteContractTests` passed naked `Apple iOS` MakerNote byte
blobs directly to Native mutation entry points.  They were not JPEG/Exif input
and therefore did not provide the unique `APP1 Exif -> TIFF IFD0 (0x8769) ->
ExifIFD -> MakerNote (0x927C)` ownership required by
`locate_formal_makernote_owner`.  B1 made the TestHarness compile the intended
raw-authority semantics, which exposed this pre-existing invalid fixture; B1
did not weaken or introduce the production structural validation.

## Fixture repair

`AppleMakerNoteFixture` is a fixed-shape test-input builder, not an Exif or
MakerNote parser/mutator.  It emits exactly one big-endian APP1 Exif/TIFF
hierarchy with one ExifIFD MakerNote owner.  The repaired strip/write test
checks that this ownership metadata remains unchanged while Native removes the
old ContentIdentifier and writes one new one.

The historical multiple-bare-MakerNote scenario was replaced with one legal
owner plus an unowned `Apple iOS` lookalike after EOI.  The test proves Native
changes the legal owner only and leaves the lookalike byte-for-byte unchanged.
This matches the production ambiguity rule instead of asserting that arbitrary
magic bytes are mutable.

## Negative coverage

The contract suite now explicitly proves that a naked Apple MakerNote without
formal ExifIFD ownership is rejected by the TestHarness.  TestHarness bypasses
cleanup authority only; it does not bypass media structure validation.

## Required verification

Run the B2 contract class, affected Apple MakerNote tests, the B1
success/rejection pair, and Release Native runtime/ABI sanity from a fresh
Native production/TestHarness build.  A fresh B2-focused auditor must make the
next acceptance decision.

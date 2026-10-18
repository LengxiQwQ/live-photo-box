# P4-R5 HEIC Native codec foundation

## Ownership and production paths

`LivePhotoBox.Native/src/media/heic_backend.cpp` is the codec gateway.  It keeps all `libheif` handles private and exchanges only project-owned `pixel_surface`, `pixel_color_facts`, `heic_image_facts`, and `heic_auxiliary_facts` with the rest of Native code.

```text
JPEG -> libjpeg-turbo -> pixel_surface -> libheif -> x265 -> HEIC
HEIC -> libheif -> libde265 -> pixel_surface -> libjpeg-turbo -> JPEG
```

`LivePhotoBox.Native/src/containers/heif.cpp` remains the authority for item graphs, extents, references, Exif/XMP placement, vendor semantics, and GainMap identity.  `libheif` codec evidence never assigns an Apple, Samsung, Huawei, or GainMap semantic role.

`decode_heic_primary_file` and `decode_heic_auxiliary_file` apply libheif image transformations once and report the resulting visual dimensions. `lpb_decode_heic_primary_image` exposes read-only primary pixel-surface facts (including source, decoded signal, and storage bit depth) as POD. The auxiliary API accepts the project-owned structural item id; it does not infer an item from a string match. `lpb_decode_heic_auxiliary_image` proves a selected auxiliary can decode into the Native pixel host while returning only POD facts across the ABI.

`lpb_encode_heic_primary_and_secondary_jpegs` is the R5 closeout codec seam for P5: it decodes two 8-bit RGB JPEG inputs through the project-owned JPEG boundary and encodes two distinct HEVC image items through libheif/x265 into one owned-output HEIF file.  Its second item is intentionally only a **secondary coded image**, not an auxiliary semantic.  Only the project structural layer may validate and add `auxC`/`auxl`, item graph edges, and Apple/vendor GainMap meaning.

## Pixel and HDR boundary

The pixel host records dimensions, channels, stride, source signal bit depth, storage bit depth, color facts, and owned bytes.  HEIC sources above 8-bit are decoded to 16-bit interleaved RGB storage with their signal depth retained.  `convert_image_file` refuses HEIC-to-JPEG conversion for 10-bit or HDR-relevant input until P5 supplies an explicit transform/degradation policy; it never silently quantizes to SDR.

ICC bytes and nclx primaries, transfer, matrix, and range are transported as facts. JPEG-to-HEIC carries a valid source ICC as a HEIF `prof` color property; only an untagged SDR JPEG receives the explicit sRGB nclx output policy. R5 does not perform an ICC A -> pixels -> ICC B transform.

## Frozen decisions

| Topic | Decision |
|---|---|
| HEIC codec | `libheif 1.23.4` through the canonical vcpkg/CMake target `heif` |
| HEVC decode | `libheif -> libde265 1.1.1` |
| HEVC encode | `libheif -> x265 4.3` |
| WIC | No result-affecting Native JPEG/HEIC codec path. It may remain outside Native for UI/system preview only. |
| Magick.NET | Not a `HeicConverterService` fallback. Its remaining `StandardHdrConversionService` pixel code is legacy/P5 semantic work and must not be used as the long-term codec or HDR data-plane foundation. |
| `heif-enc` / `heif-dec` | Not a production dependency; legacy methods remain fail-closed. |
| lcms2 | **NOT SHIPPED.** R5 transports profile facts but has no approved production color transform requiring lcms2. P5 must revisit only if it introduces an explicit source-profile to target-profile transform. |

## Legacy audit

| Backend / code | Production role after R5 | Remaining? | Closure |
|---|---|---|---|
| WIC / WindowsCodecs | None in result-affecting Native JPEG or HEIC codec work | No Native link; UI/system integration is outside this data-plane policy | Frozen: JPEG is libjpeg-turbo and HEIC is libheif |
| Magick.NET | Legacy P5 semantic pixel work in `StandardHdrConversionService`; not a HEIC codec fallback | Yes | P5 must replace result-affecting HDR pixel math with the Native host before semantic completion |
| `heif-enc` / `heif-dec` | None; `StandardHdrConversionService` legacy methods throw `NotSupportedException` | Names remain in diagnostic/comments only | Frozen fail-closed; no process launch |
| `HeifAuxImageWriter` | Project-owned P5 structural assembly seam, not a codec process wrapper | Yes | It receives/edits project-controlled HEIF structure; it cannot confer auxiliary semantic authority on libheif |

## Dependency and distribution record

The manifest declares only `libheif` with its actual default `hevc` feature.  At the pinned vcpkg baseline this produces static `libheif`, `libde265`, and `x265` linkage for `x64-windows-static`; no codec DLL, plugin, CLI, PATH entry, or machine installation is a runtime requirement.

| Component | Software-license fact from installed vcpkg port | Runtime purpose |
|---|---|---|
| libheif 1.23.4 | `LGPL-3.0-only AND MIT` | Generic HEIF gateway, primary/auxiliary decode and HEIC encode plumbing |
| libde265 1.1.1 | LGPL-2.1-or-later | HEVC image decode used by libheif |
| x265 4.3 | GPL-2.0-or-later | HEVC image encode used by libheif |

The repository is GPLv3; this is a compatibility observation, not legal advice.  Static linkage and any source/binary redistribution require release owners to preserve the applicable upstream license, copyright, source/offering, and notice obligations.  HEVC patent licensing is separate from open-source software licensing.  Microsoft Store, GitHub binary release, and portable-package distribution each require release-owner legal/distribution review; this record makes no conclusion that patent rights are cleared or that distribution is approved.

## Evidence boundary

Current verification uses the provisioned RealSample corpus described by `tests/fixtures/realsamples-manifest.json`; `scripts/testing/setup-test-fixtures.ps1` verifies its supplied archive and every required file before tests run. The R5 inventory test makes the formal HEIC subset explicit: Apple, Huawei, and Samsung. All three primary images perform an actual Native pixel decode and are currently 8-bit source / 8-bit storage. Apple and Samsung are nevertheless real HDR samples: each has an embedded 8-bit HDR GainMap grid that the project parser identifies and Native decodes via its project-owned item identity. The corpus contains no confirmed >8-bit HEIC primary; this limits only a >8-bit **real-device coverage claim**, not the actual HDR/GainMap proof for the representation the corpus contains. See `docs/verification/RealSample-Corpus.md` and the canonical manifest for setup, exact identities, and the R5 codec matrix.

When the project-owned Native inspector reports a HEIC GainMap, the shared `ImageConversionEligibility` rule rejects HEIC→JPEG before generic primary-only codec conversion; `HeicConverterService` also routes to the P5 semantic path. Until that path is implemented, the failure is surfaced and a plain-SDR JPEG fallback is forbidden. R5 regressions exercise this with both the Apple and Samsung HDR real samples while retaining Huawei as the ordinary non-GainMap HEIC→JPEG codec proof.

P5 owns GainMap mathematics, vendor semantic mapping, final HDR preservation/degradation policy, and structural assembly of any newly encoded auxiliary graph. R6 owns video work.

## Validation and acceptance assessment

The following commands were run from the repository root after the final ICC and HDR fail-closed changes:

| Command / scope | Result |
|---|---|
| `scripts/native/build-native.ps1 -Configuration Debug -RunTests` plus `ImageConverterTests|NativeRuntimeTests` | 15/15 Native harness; 46/46 managed tests; 0 skipped |
| `scripts/native/build-native.ps1 -Configuration Release -RunTests` plus `ImageConverterTests|NativeRuntimeTests` | 15/15 Native harness; 46/46 managed tests; 0 skipped |
| P1--P4 focused regression filter (`SourceInspector`, extractor, cleaner, image/video converter, Native runtime, platform filesystem and gain-map reassembly) | 297/297; 0 skipped |
| CMake Release `ctest --output-on-failure` | `portable_io_smoke` 1/1 |
| `scripts/native/sync-native-project.ps1 -Check` | project and filters synchronized |

| Gate | Assessment | Evidence |
|---|---|---|
| 1. Canonical dependency graph | PASS | `vcpkg.json`, `CMakeLists.txt`, static vcpkg build graph |
| 2. Native primary decode | PASS | Apple, Huawei and Samsung RealSample primary-fact/decode tests |
| 3. Native primary encode | PASS | JPEG-to-HEIC uses Native libheif/x265; project structural inspection and independent ExifTool readability test validate output |
| 4. Auxiliary foundation | PASS | Project-selected Apple/Samsung auxiliary grid item IDs decode through the Native host; the closeout seam also emits a separately verified second HEVC image item for project-owned auxiliary assembly |
| 5. HDR / bit-depth | PASS | Real Apple/Samsung HDR is 8-bit primary plus an embedded HDR GainMap auxiliary; identity, exact-item decode, and fail-closed routing are proven. The >8-bit architecture remains; no >8-bit-primary RealSample claim is made for this corpus. |
| 6. Structural authority | PASS | `containers/heif.cpp` and vendor protocol code remain authoritative; codec APIs accept structural IDs only |
| 7. Ownership | PASS | Primary and selected auxiliary surfaces are separate owned Native values; GainMap semantics remain project-owned |
| 8. x265 record | PASS | Dependency/distribution facts above; no legal conclusion invented |
| 9. Minimal runtime set | PASS | Static libheif/libde265/x265 only; no plugin, CLI, or machine installation dependency |
| 10. lcms2 decision | PASS | Explicit **NOT SHIPPED** decision above |
| 11. WIC policy | PASS | Result-affecting Native JPEG/HEIC paths: none; WIC is not linked by the Native target |
| 12. RealSample integrity | PASS | Required existing samples ran without skip; their source hashes remain unchanged |
| 13. No external HEIF CLI | PASS | No production `heif-enc`/`heif-dec` invocation; legacy CLI path remains fail-closed |
| 14. Truthful failure/degradation | PASS | Truncated HEIC fails without output; Apple and Samsung GainMap conversion fail closed rather than emitting plain SDR JPEG, while Huawei still succeeds as ordinary HEIC codec proof |

The closeout test independently parses the resulting HEIF item graph, verifies two distinct `hvc1` item identities and non-empty payload ranges, and explicitly verifies that no auxiliary relation exists before project-owned assembly. This establishes the missing auxiliary **codec encode foundation** without pretending that a generic second image already has GainMap semantics.

**Current local decision: P4-R5 is PASS and ready for external acceptance review.** Gate 5 is evaluated against the actual HDR representation in the formal corpus, not an invented requirement that every HDR HEIC must have a >8-bit primary. This local decision does not mark the phase complete or automatically start R6; that requires explicit current-user/external-gate acceptance.

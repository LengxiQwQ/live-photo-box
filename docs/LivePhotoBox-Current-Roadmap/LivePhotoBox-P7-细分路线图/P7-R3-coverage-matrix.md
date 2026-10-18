# P7 R3 coverage matrix

Final candidate v8 execution and fresh independent closure review PASS. This
matrix does not change acceptance. LOCAL_OWNER; stop before R4/P8.

| Area | Approved owner / path | Required evidence | Status |
|---|---|---|---|
| Capability identity | NativeRuntime -> native runtime capability identities, six operations | All six fields and actual runtime module hashes; bind to executed routes | PASS |
| JPEG/HEIC copy | Project structural copy | Request/capability/actual operation and byte hashes, no reencode | PASS |
| JPEG transform | libjpeg-turbo perfect DCT | Real transformed dimensions, component truth and no fallback | PASS |
| JPEG/HEIC codec | Native libjpeg-turbo/libheif | Existing real fixtures + external decode, truthful loss/preservation | PASS |
| HDR/GainMap | Bound native semantic conversion | Both directions, strict refusal, semantic graph and component truth | PASS |
| Video copy/remux | ProjectIsoBmffRemux | MOV->MP4, byte-identical MOV, nonidentical MP4 cases; hashes distinguish remux/copy | PASS |
| SDR H264/HEVC | Software-only Media Foundation | Both real routes, capability and runtime identity, independent streams/decode | PASS |
| HDR Main10 | Profile-selected minimal-libav Stage A + Native Stage B | Real canonical Huawei motion, versioned diagnostics, independent bit depth/CICP/timing/decode | PASS |
| Unavailable backend | Fresh process, isolated deployment copies | Absent/corrupt native and sidecar, real loader failure, category/stage, no output/temp/retry, unchanged source | PASS |
| Failure truth | Unsupported request/profile and stale/ambiguous inputs | Existing strict fail-closed regressions and exact failure diagnostics | PASS |
| Hardware encoder failure/differential | Frozen software-only policy | Native synchronous software enumeration; no hardware approved | NOT_APPLICABLE_CURRENT_CAPABILITY |
| MF/minimal-libav differential | Source semantic domains select disjoint approved owners | SDR MF and HDR/Main10 sidecar do not expose an approved interchangeable-owner request | NOT_APPLICABLE_CURRENT_CAPABILITY |
| WIC differential | WIC is not a production conversion owner | Offline independent decoder can cross-check outputs without enabling production backend | Reference only |
| Final candidate aggregate | Bounded image/video truth and R3 probes | Discovery == actual rows, zero fail/skip; exact source/runtime/input identity | PASS |
| Independent closure | Fresh local read-only reviewer | run-1/independent-review.json, PASS / DONE_FOR_R3 | PASS |

Preliminary read-only source audit identified MF capability unconditional availability
and image failure stage/category consistency gaps. Neither is considered repaired
or accepted until actual candidate source and relevant evidence are reviewed.


## Fixed candidate execution (2026-10-16)

- Native product/harness v5, managed probe/tests v8: built successfully. Current
  native source graph matches the built artifacts; public Native ABI unchanged.
- q8: 120 discovered/executed/passed; zero fail/skip/notExecuted/missing/unexpected;
  direct child exit 0. Source/runtime/sample identities stable; TEMP files empty.
- p8: 16 fresh child cases passed: six normal successes, absent/corrupt sidecar HDR
  failure plus SDR control, absent/corrupt Native video/image/trusted-image failures.
  Eight successful outputs have independently parsed stream semantics and full
  video decode; six video outputs also have independent audio decode. Failure
  cases have no output, no fallback, unchanged sources and empty TEMP.
- Native absence/truncated PE and sidecar absence/truncated PE are real isolated
  deployment/load failures. No installed system codec was removed or disabled.
  No fault-injection result is described as a real loader failure.
- Current Native image ExecutionRecord names the Native owner. Per-library image
  ownership is bound using the six runtime identities, exact DLL and actual native
  dispatch source; no new codec-specific record fields/ABI are claimed.
- Source sample-entry NCLX and encoded VUI are independently inspected. Unspecified
  NCLX value 2 is resolved only with actual encoded VUI; explicit conflicts reject.
  MF AAC audio is an approved reencode, not payload-preservation evidence.
- Malformed GainMap is rejected at Neutral semantic inspection before bound engine
  dispatch. Four original policy combinations still execute the ordinary converter
  guard with no binding and require complete output-root emptiness. A separate
  valid binding control requires FallbackAllowed=true and FallbackOccurred=false.
- HDR-stage missing-entrypoint catch is a static classification consistency repair;
  absent-Native child evidence covers actual reachable ordinary inspection paths.
- Diagnostics remain preserved: qualification-v2 88/119; q3 111/119; q7 120/120.
  Final q8 includes the independent-review correction to the no-output assertion.
- Doctor/AI sync/verify/reconcile/diff-check all exited 0. Historical R1 439 and R2
  2,063 indexed payloads rehashed without mismatch. No new tools, runtime packages,
  production dependencies, hardware capability or fallback policy were introduced.

Final closure: R3 CLOSED / PASS; see P7-R3-Closeout.md and run-1/closeout-index.json. Stop before R4/P8.

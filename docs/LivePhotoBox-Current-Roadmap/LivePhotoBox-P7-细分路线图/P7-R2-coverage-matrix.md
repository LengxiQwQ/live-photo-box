# P7-R2 current fixed candidate coverage

Status: CLOSED / PASS / DONE_FOR_R2. R2 remains IN_PROGRESS until durable run-3 and a fresh read-only independent PASS. P7 remains in_progress; stop before R3/P8; no Git publication. Acceptance unchanged.

Current product: Native product/harness v7, Core v20, CLI v8, WinUI v5. Final elevated Core `qualification-final-v22`: 216 discovered/executed/passed; CLI `qualification-cli-final-v22`: 1/1 with six subcases. Zero fail/skip/notExecuted; direct child exits 0. Full source/runtime/sample/env checks passed, TEMP file residue empty. `qualification-fix20`: 45/45 targeted regressions passed. All required cases are in the final selector; no diagnostic exclusion is used.

| Requirement | Current actual path/evidence | Status before independent review |
| --- | --- | --- |
| Cancellation at pre/mid/commit/cleanup stages | Real Native Extract/image/JPEG/HEIC/remux, real-plan Cleaner, Split/Merge/editor; CTS callbacks distinguished from labelled injected errors | Executed PASS |
| Atomic publication and rollback | Held-handle managed/Native journals, existing target restore, no-replace missing target publication, final locks and late occupants | Executed PASS |
| Real filesystem failures | Scoped ACL deny, share locks, nonwritable/occupied destinations, actual symlink, mapped-view delete failure | Executed PASS |
| Real disk full | Five writers: Extract, Cleaner, image copy, Split, editor still-range on exact 128 MiB NTFS volume, OS 112 / HResult -2147024784; sources and owned-only rollback checked | Executed PASS |
| Namespace/alias/foreign preservation | Actual root/child replacement, canonical/hardlink/symlink aliases, external foreign sentinels, Cleaner staging attacks | Executed PASS |
| Workspace ownership | Creating-root pin, producer-issued Native/managed receipts, empty-root exact proof, unknown child preservation, lock failure and bounded retry, overwrite identity gate before first byte | New regressions executed PASS |
| Authority handoff and receipt retirement | Native record insertion before authority flag; managed owner/bundle before Native issue; unhanded plan release; same-volume FileId retirement only on exact delete proof | Real production chain PASS; separately labelled synthetic allocation-failure rollback PASS |
| Native cleanup truth | Mapped views block exact deletion; original failure/cancellation, path and object identity retained, owned-handle/directory release probes | Executed PASS |
| Object-gone proof | Exact retained-handle zero-link proof, strict FileId/volume identity; real different-volume probe | Executed PASS |
| Runner/CLI/UI routes | Actual RunAsync success/fail/cancel; CLI six scenarios; production source preparation helper and ImageFormatService publication | Executed PASS; WinUI build PASS; picker interaction not claimed |
| Provenance | Frozen samples, actual loaded runtime, exact source before/after, runner selftest-v6, candidate qualification-v3, R1 postflight and native public-header guard | Source/runtime/sample/env checks PASS; durable copy/review pending |

The test-only fixture workspace is used only for isolated harness/direct-Native output lifetimes. Actual SourceExtractor workspaces, product ImageFormatService workspaces, real Cleaner staging attack namespaces and their assertions remain production paths. Foreign attack objects are checked first and explicitly cleaned only afterward by their test owner; test cleanup is never cited as product cleanup proof.

Historical failures are retained: run-1 failed seal-console encoding; run-2 was independently REJECTED for actual MediaWorkspace replacement-root deletion. Workspace v17 integration failures, q-v18 long-path/fixture diagnostics, q19 190/214, and full v20 214/216 and v21 215/216 remain complete scratch evidence. The latter failed before the intended mapped cleanup seam because descriptive evidence paths plus variable-width Native temp names were too long. The final runner uses deterministic short child TEMP under this same task workspace, with an empty-file precondition and complete post-run residue check; product/test source, samples, assertions and the full final selector are unchanged. Arbitrary long Native path support is not claimed.

The previous candidate and review below are preserved as historical context, not the closing evidence.

---

# P7-R2 coverage and findings

Status: REJECT / R2_BLOCKERS_REMAIN (run-2). Current user delegates R2 only to local Codex with authorized bounded agents and fresh read-only review. No ChatGPT/C2C, Git publication or R3/P8 work. P7 remains in_progress. Acceptance and public Contract/ABI are unchanged.

Final fixed candidate: Native product/harness v5; Core v15; CLI v7; WinUI v4. Final elevated aggregate qualification-final-v15: 207 discovered/executed/passed, zero fail/skip/notExecuted, direct child exit 0. CLI qualification-cli-final-v15: 1/1 test and six recorded subcases passed, direct child exit 0. Exact selectors/parameterized rows, complete process streams, TRX and case JSON are retained for the formal copy. All source/runtime/sample and parent-environment checks passed; TEMP file residue is empty. The preceding 180-row v12 aggregate was incomplete and is not the closing candidate.

| Requirement | Actual production path / evidence | Category and result | Remaining blocker |
| --- | --- | --- | --- |
| Pre/mid-stage cancel | Native Extract/image/JPEG/HEIC/remux; real-plan Cleaner Inspector -> Extractor -> issued CleanupPlan at planning/staging/verification/publication/bundle barrier; Split/Merge/editor range copy | RealSamples and CTS-only callbacks; separately labelled synthetic/injected state-machine support. PASS | Independent formal review |
| Rollback / no partial publish | Cleaner journal; dual/single-target Windows publisher; exact existing-target restore and missing-target no-replace publication | Real locks and namespace changes, exact identity/length/hash. PASS | Independent formal review |
| Locked source/output / temp/staging failure | Extract create ACL; Cleaner Native stage ACL; Split stage ACL; real output locks; editor preparation source lock and owned-temp cleanup lock | Actual Windows errors, scoped restoration, unchanged sources/foreign objects. PASS | Independent formal review |
| Real disk full | Extract, Cleaner staging, image copy, Split stage and editor still-range writer on dedicated 128 MiB NTFS VHDX | Five actual filesystem failures; OS code 112 / HResult -2147024784 plus source/rollback/residue assertions. Some cases write zero new filler bytes because the same bounded volume is already constrained; the target failure is captured separately. PASS | Independent formal review |
| Publish/partial publish failure | Native remux collision; Cleaner/Split first publication followed by failure/cancel; Merge/CLI final publish; editor common publisher | Real kernel no-replace/lock/race evidence, separately labelled injected errors. Original restored or foreign occupant retained. PASS | Independent formal review |
| Replacement / aliases / races | Merge image/video canonical/hardlink aliases (16 rows); late foreign final occupant; CLI same-byte artifact substitution; UI temp replacement; actual symlink under elevated token | Creating-handle/Native-issued identity reaches publication and cleanup. Foreign identity/hash preserved. PASS | Independent formal review |
| Truthful cleanup failure | Native image/remux mapped views block disposition; dynamic Native error read; editor lock/cleanup + original-cancellation aggregation | Actual cleanup failure, original error and owned path/volume/fileId recorded, file/directory handle-release probes. Expected residue recorded before exact test-owned cleanup after lock/mapping release. PASS | Independent formal review |
| Object-gone proof | Existing same-volume live/deleted/error-classifier/directory proofs; real different-volume probe on VHD | Opened volume serial and returned object fileId/volume match; wrong volume returns Unknown. PASS | Independent formal review |
| Top-level MergeRunner | RunAsync real success, absent-video failure and pre-cancel with pre-existing Temp sentinel/foreign subtree | Shared Temp create/recursive-delete removed; public per-pair signature compatible; sources/sentinels identity+hash preserved. PASS | Independent formal review |
| CLI publication | Real image/video success, final lock, same-byte foreign source artifact, late foreign final occupant and labelled injected precommit error | Six subcases; actual loaded Native DLL hash and owned-only cleanup. PASS | Independent formal review |
| UI source preparation / final copy | Actual ImageFormatService final publication; EditExportService uses linked ExportSourcePreparation; held source read handle verifies Native source SHA before range copy | Eight preparation rows plus two final-copy rows PASS. Cleanup ref retained until successful Dispose, final cleanup error propagated/aggregated. WinUI build PASS. Picker interaction not executed or claimed | Independent formal review |
| Reconciliation / provenance | Full source/runtime before/after; 17 frozen samples; R1 run-28 index + 439 payloads; one public Native ABI header; unchanged HEAD | Candidate qualification/postflight/selftests and doctor/sync/verify/reconcile/diff-check PASS. Exact baseline limits recorded in final-r2-delta-v3.json | Independent formal review |

Fresh read-only review blocked closure on UI unregistered preparation, top-level shared Temp deletion, remux cleanup-report bypass, wrong-volume gone proof and clearing prepared-source ownership before successful disposal. All were repaired before this candidate, with static rechecks and executed regressions. Review reports/corrections remain create-only. Managed v13 new-test syntax failure and older scratch failures are retained; no formal number was consumed for helper debugging.

Preparation tests execute the linked production helper with Native Inspector/Extractor facts. Product reader/writer agreement is not claimed as independent media-format preservation evidence. Independent review evaluates transaction/ownership coverage. WinUI picker interaction and full UI feature acceptance are not claimed.

Observed later-phase boundary: neutralized Honor input can be rejected before Merge's transaction by GainMap binding/descriptor validation; neutral OPPO without authoritative CID can be rejected by Native pairing. These upstream rejections are not counted as successful transaction execution; raw Apple pair is used for Runner transaction coverage. Repair mutation remains unavailable (RebuiltPipelineNotReadyException), so R2 introduces no Repair writer. R3 backend truth, R4 load/soak, R5 dependency/portability and P8/P9 format/architecture work remain unstarted.


Fresh final review rejected run-2 despite the 207/207 and six CLI subcases. A real production-DLL probe proved MediaWorkspace.Dispose deletes a foreign replacement root by path, leaves relocated owned artifacts, and swallows cleanup failure. The independent reports and actual repro are durable in run-2/independent-review and review-follow-up, bound by review-index.json. Global transaction/foreign preservation acceptance is FAIL until the workspace ownership gap is repaired and a new candidate/run independently reviewed. No R2 closure, no R3/P8. Earlier row-level PASS results remain their exact executed scope; they do not prove this missing workspace boundary.


Run-3 final independent closure: PASS / DONE_FOR_R2; exact index SHA256 D97FC5D9BBB99094FD42156A2B674E52D36435315088180810831DFC36AEC285. All current R2 requirements above closed. The historical run-2 rejection remains historical and immutable. VHDX detached/mount removed; P7 in_progress; stopped before R3/P8.

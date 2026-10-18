# P7 R4 coverage and interpretation

**最终状态（2026-10-18）：R4 CLOSED / PASS / DONE_FOR_R4。** fresh只读独立审查已核验run-6全部3107payloads，零不一致。最终候选v63-r3；[正式收口报告](../../../.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-6/closeout-report.md)。以下施工/失败/待审文字保留历史时点，不覆盖本最终状态。P7仍in_progress，停在R4，不进入R5/P8，无Git发布。

Current execution: LOCAL_OWNER, 2026-10-16. Stop after R4 before R5/P8.
This working matrix does not change Roadmap acceptance or any capability budget.

| Requirement | Actual public path / independent evidence | Interpretation |
|---|---|---|
| Supported large correctness | Frozen Apple MOV, unchanged original boxes/media plus legal extended-size top-level free; total 128 MiB. SourceInspector → authoritative ExtractionPlan → SourceExtractor → Cleaner → NeutralMediaService. Final primary/video retained, workspace removed, input hash unchanged | Supported large production success; independent hierarchy, compressed audiovisual packet identity and decode |
| Oversized resource failure | 4,500,005,000-byte same-recipe sparse MOV; actual Windows Job process commit limit 512 MiB, attached and queried before gate opens. Eight alternating primary/secondary calls, typed Unsupported/Read, exclusive source reopen after each | Actual allocation failure safety; not a new static size cap and not proof of successful production >32-bit extraction |
| 64-bit arithmetic diagnostic | Existing ExtractorScaleTests sparse >4 GiB range through TestFactsExtractor / Native test harness | Synthetic harness diagnostic only; does not replace production Inspector authority |
| Real batch | GUI/CLI production LivePhotoMergeRunnerService.RunAsync, four same-basename inputs, degree three, exact started/completed/success counters, unique outputs, third-party Temp sentinel preserved | Repeated transaction/name-reservation/workspace behavior; no shared-temp deletion authority |
| Split | Three concurrent public SplitAsync real Redmi sources; exact six outputs, primary/video inspected independently, test-owned outputs retired | Success/cleanup under repeated mixed activity |
| Mixed routes | Fourteen frozen-corpus Neutral routes, in groups of three. Thirteen successful routes plus the existing explicit Xiaomi final-orientation refusal | Refusal is asserted as refusal, never counted as successful neutralization |
| Approved conversion | JPEG→HEIC→JPEG, MF software H264 SDR, minimal-libav HEVC Main10/HDR strict path. Exact backend truth and output artifact hashes | Existing approved backends only, no fallback or dependency expansion |
| Continuous workload | Three complete warmup epochs; then ≥30 min AND ≥12 measured full epochs, no idle waits to manufacture duration | Finite correctness/leak observation; no product performance SLA |
| Resources | One-second raw private/working/managed bytes, handles, threads, pool/pending observations; natural full-epoch checkpoints and quarter windows. Forced-GC diagnostics separately marked at warmup/end | Natural trend is primary; GC low point cannot alone establish stability |
| Post-load cancellation | Filesystem-observed creation during actual 128 MiB Neutral operation triggers token cancellation. Dispose removes workspace, then 12 paused batch cancellations before any Set, followed by a successful production merge batch | Real cancellation and recovery, no injected exception/mock stage |
| Preservation/identity | Every complete epoch rehashes all 17 frozen inputs. Every manifest checks whole artifacts or correct embedded owner ranges, including materialized auxiliary distinction | Product readback is additional consistency proof; independent retained-media checks are separate |
| Regression aggregate | SourceInspector, routing, scale, workspace, R2 transaction/ownership/handoff and P6 Neutral tests; discovery must equal executed rows, zero fail/skip/missing | Bounded R4/repaired-path aggregate, not whole-product suite |
| Independent closure | Fresh local read-only reviewer after immutable formal snapshot, source/runtime/input hashes and raw evidence | Required before CLOSED; primary implementer does not self-sign independent approval |

## Preserved qualification failures and test repairs

- An initial long mixed campaign raised a real native/private and handle-retention
  concern despite successful business assertions. MF-only 24-call isolation was
  preserved before/after a third bounded product repair: activation-list teardown
  now calls IMFActivate::ShutdownObject before Release. This follows the existing
  COM object lifecycle, with no new backend/ABI/selection behavior. Final resource
  acceptance requires a new full campaign using this Native binary, not reuse of
  the initial successful workloads or a single diagnostic GC endpoint.
  Official lifecycle reference:
  https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nf-mfobjects-imfactivate-shutdownobject
- Final regression deployment copies the actual soak Core DLL/PDB and Native DLL
  into a fresh test deployment. Native and sidecar hashes must match the soak
  runtime. Earlier separately built managed flavors remain preserved; they are
  not presented as identical binaries. MF converter/contract and runtime-identity
  regressions are added to the final bounded scope for the activation teardown.

- `pause-old`: real public paused cancellation returns but leaves 12 active
  workers before any Set. `pause-fixed` observes zero before any Set. The bounded
  product repair uses a registered wait and unregisters it on completion/cancel.
- `large-memory-old`: real Job constraint reaches Native snapshot allocation and
  process terminates with `0xC0000409`. Fixed Native closes the opened source handle
  on resize exceptions and maps primary/secondary bad_alloc/length_error to typed
  Unsupported/Read. GainMap removes the inner noexcept trap and catches allocation
  failures at its outer ABI boundary. Runtime checks cover primary/secondary
  bad_alloc; length_error/GainMap allocation paths remain source-reviewed branches.
- Qualification runner originally treated owner offset as an offset into a
  materialized auxiliary file. Correct verification reads an owner range only
  when Path is the actual primary owner; materialized files are whole-file hashed.
- Old SourceInspector/P6 orientation/scale tests wrote their fixture bytes directly
  inside strict production workspaces. Fixture-only scopes now use the existing
  TestFixtureWorkspace. Production output workspaces retain strict receipt rules;
  all media assertions remain intact.
- R2 regressions require LPB_R2_EVIDENCE; R4 supplies an isolated child environment
  value and captures those raw per-case records. No process-global setting remains.
- Still-routing static regression follows ResolveStillPhotoSourceAsync's existing
  owner migration to EditExportService and its owned-temporary return type. Native
  facts/ranges and forbidden-scanner assertions are retained; source hash and
  PrepareStillRangeAsync checks strengthen the same method requirement.
- Independent decoder qualification initially mapped an empty unknown-codec Vivo
  audio track. Final tooling explicitly proves zero packets for such a track and
  records it as empty, while independently decoding every actual coded audio
  stream. Nonempty unknown-codec audio remains a validation failure.

## Deferred integrated reachability finding (outside R4 repair boundary)

`EditViewModel.UpdatePreviewForTimelineFrameAsync` and `EditExportService` still
call `EditTimingService.ReadOriginalPhotoBytes` for an Original item. That helper
uses ReadMetadataTextSync, ItemLengthRegex, ordered lengths and FindPrimaryJpegEnd
to derive Original offset/length. It is a separate route from the migrated
Primary-still method checked above. Preserve for integrated reachability review
and phase ownership assignment; do not claim all edit Original/preview/export
routes are Native-authoritative, and do not opportunistically rewrite this parser
or UI during large/batch/soak work.

The unused LivePhotoBatchRunnerService definition is not the current GUI/CLI merge
owner. Its shared Temp recursive cleanup needs future reachability/ownership
review if enabled. R4 exercises the actually called LivePhotoMergeRunnerService.

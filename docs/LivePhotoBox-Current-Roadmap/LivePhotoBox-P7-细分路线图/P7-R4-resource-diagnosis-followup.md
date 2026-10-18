# P7 R4 resource diagnosis after failed run 4

**最终状态（2026-10-18）：R4 CLOSED / PASS / DONE_FOR_R4。** fresh只读独立审查已核验run-6全部3107payloads，零不一致。最终候选v63-r3；[正式收口报告](../../../.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-6/closeout-report.md)。以下施工/失败/待审文字保留历史时点，不覆盖本最终状态。P7仍in_progress，停在R4，不进入R5/P8，无Git发布。

## 当前已完成验证：v63（2026-10-18，等待正式收口审查）

此节覆盖后文旧候选的当前状态；历史失败结果不改写。v63 的正常 Release 打包、387/387 受影响测试（0 skip）、11 条实际视频路线与 5 类拒绝、4 条 chroma-siting 回归、R3 backend truth、Native coded-order/MEBX/static/SEI、CLI、大文件及受限内存拒绝均通过。独立验证覆盖 33 个长测保留产物、3 个大文件产物及 4 条真实视频路线，包含 codec/depth/CICP/HDR/audio/rotation/timing/VFR/container/清理检查。

完整混合长测：3 warmups + 53 measured epochs / 1806.329 秒，随后自然 quiet 60.612 秒；产品子进程及外层采集进程均直接退出 0，无超时。测量包含 742 次 Neutral、212 merge pairs、159 split inputs、106 图像转换、212 视频转换。操作边界 Semaphore 中位数 16，各轮没有按调用累增；quiet handles 545→536、threads 20→14，Semaphore 16→17 为一次晚期变化。

Private Bytes 并未回到进程启动基线：自然边界首末 146.98→281.27 MiB，后半段斜率 +2.23365 MiB/min，必须披露。完整每秒样本后两个五分钟窗的 Private 中位数 604.43→599.99 MiB、峰值 1022.94→1019.08 MiB，Working Set 中位数 639.00→641.89 MiB；自然季度低点约 50–53 MiB，quiet 279.62→279.34 MiB。资源判断基于完整曲线后段收敛及 quiet，不以 forced-GC 数据或任意新阈值通过；有限观察不证明无限运行稳定。

补充合成格式矩阵 `pixel-layout-v63-r6` 14/14 通过：gray / 4:2:2 / 4:4:4 的 8/10-bit 各转 H.264/HEVC 共 12 条真实成功，12-bit 4:2:0 两种目标均 Unsupported 且零输出。x264 固定构建为 X264_BIT_DEPTH=0（8/10），旧工具只假设 8-bit 是验证计划错误，已按真实构建事实修正并保留失败尝试。灰度 H.264 的 raw VUI matrix=2，完整 nclx=1/1/1；复用未改动的既有独立结构/VUI 对账证明有效 CICP 相等，保留 ffprobe 缺字段原文。其他失败尝试分别为开发工具 deps.json 和合成源 nclx/VUI 冲突，均非产品缺陷通过记录。

当前精确候选 `candidate-v63-r3.json` SHA256 `93ECF97A79B0E6F0AABA46287A68F103D966B64CE2EEAB3A6065459D733A1EA5`；Core 修复 NeutralMediaService CleanupPlan 所有权释放，Native resource-v18 不变。完整正式证据尚待 create-only run-6 和 fresh read-only review；此时 R4 仍 OPEN。无 Worker、系统 HEVC 更新、隐藏 fallback、新运行依赖或 Git 发布。完成后止于 R4，不进入 R5/P8。


Status: OPEN. This is engineering diagnosis, not R4 acceptance. The production
candidate is the v60 Core/Native/sidecar recorded in run 4. No Worker, OS codec
update, production forced GC, heap compaction, or additional backend change is
authorized or implemented by this investigation.

## Immutable failure

`.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-4/evidence-index.json`
has SHA256 `C5AA7420A1D9574BC972D6D076154E38CAAA3FCE8AD23C4F25F34412E7EF8A91`,
2,135 payloads, 2,380,243,421 bytes, status
`FAIL_RESOURCE_STABILITY_NOT_PROVEN`. Operational checks, 383 tests, 11 supported
video routes and five rejection cases, independent output verification and
post-load cancellation passed. Measured mixed load lasted 1,808.086 seconds over
48 epochs after three warmups, followed by 60.670 seconds of natural quiet.
Private Bytes grew from 178.32 to 361.66 MiB, with late slope +3.5202 MiB/min;
quiet remained about 363 MiB. Semaphore/handle/thread counts did not show the
former per-HEVC-call growth. The failure must not be reclassified from subsequent
short or forced-GC observations.

## Completed six-round VM trace (2026-10-18 local)

Stable working evidence:
`.ai-tmp/workspace/P7/R4-large-batch-soak/hevc-migration/vm-trace-baseline-codecs-v60/`.
The wrapper checks exact product, diagnostic executable and tool source hashes
before and after execution. Media child and offline analyzer both exited 0.

| Check | Observed result | Limit |
|---|---|---|
| Raw events | 79,382 / 131,072 capacity; zero dropped/suppressed | Selected NtAllocateVirtualMemory/NtFreeVirtualMemory only |
| Control | Partial decommit/recommit, repeat commit and same-address reuse balanced | Diagnostic calibration only |
| Baseline | 102.344 MiB committed private ranges | Live walk is not stop-world; includes preallocated trace buffer |
| Concurrent free/reuse ambiguity | None detected in captured operations | Does not prove coverage of other VM APIs |
| Final ledger / VirtualQuery | 233.039 / 226.180 MiB | Ledger overestimates by 6.86 MiB; not exact live allocation accounting |
| Retained new committed ranges | 132.102 MiB | Heap backing, not busy/live object bytes |
| Symbol provenance | Local product PDBs; exact PE CodeView ntdll/CoreCLR public PDB cache | Other Windows nearest exports are not reliable function attribution |

Final committed-range stack groups containing MF H.264 total about 50.926 MiB;
sidecar about 30.578 MiB; CoreCLR about 17.891 MiB. These groups identify calls
that caused heap backing to be committed, not the current owner of live data.
Exact ntdll stacks include `RtlpFindAndCommitPages -> RtlpExtendHeap ->
RtlpAllocateHeap`. The older three-round trace also shows commits during
`RtlpCoalesceFreeBlocks -> RtlpFreeHeap`. Exact CoreCLR stacks include JIT code
heaps and GC virtual commits. x265 stacks include per-instance BitCost storage;
the pinned 4.3 source owns this storage in BitCost and frees it in its destructor.
Do not misdescribe it as an unbounded static cache or call empty x265_cleanup
a missing cleanup fix.

Private MiB at disposed boundaries: 127.16, 124.46, 123.30, 214.22, 231.87,
231.48 (trace buffer included). Within round 3, HEIC raises memory by about
84 MiB, HEVC8 lowers it by about 89 MiB, then MF raises it again by about
85 MiB. In rounds 4 and 5 MF lowers memory by about 20 and 29 MiB. This is
evidence of substantial heap reuse, not proof of a bounded plateau.

Fresh read-only `r3_backend_audit` independently read raw trace, baseline,
analysis, exact-PDB results, per-stage snapshots and tool source. It confirms
the diagnostic scope and retains run 4's failed disposition.

## Finite path separation

Working evidence: `hevc-migration/route-isolation-v60/`, exact driver v60.

MF-only uses the existing driver's MfRepeat with 24 actual public VideoConverter
H.264-to-H.264 calls and explicit MF/no-fallback truth/output identity/workspace
cleanup checks. All 24 succeeded. Natural Private MiB endpoints are 19.95 and
146.28, with intermediate drops to 29.29, 37.06, 56.23 and 27.51 MiB. Semaphores
remain 12. The driver's existing diagnostic GC sequence reduces 146.28 to
22.96 MiB, while managed memory changes only about 5.86 to 0.99 MiB. It cannot
be explained as 123 MiB of managed object contents alone. It does not distinguish
native finalizer cleanup, runtime/heap behavior or coincident asynchronous work.
Forced GC is not acceptance evidence or a proposed production policy.

The eight-round no-MF control completed with direct child exit 0. Its observer throws an offline sentinel
after hevc8 completes, before the MF call. The driver's using workspace unwinds
normally. This isolates five completed real product conversions, but skips the
driver's trailing combined hash/results block; it is not a full CodecRound PASS.
Disposed Private MiB: 19.85, 20.88, 26.97, 22.49, 112.91, 146.59, 25.38,
161.64. Natural quiet ended at 161.54 MiB with four semaphores. MF is therefore
not necessary for the large memory steps. Product source was unchanged in this control.

`r4-mf-lifetime-probe` is an offline public-API consumer that splits the
natural quiet and three diagnostic GC steps. It references the exact v60 Core,
never runs ffmpeg.exe and does not implement a second backend. Its build passed
with zero warnings/errors. It executed legacy-counter and VM-buffer-counter
24-call controls sequentially, each with 60-second natural quiet before GC.
Both counters were prewarmed, compared, and checked against a known +2 semaphore
and subsequent release. The initial unprewarmed qualification failed before any
media operation; that tool failure remains preserved.

| Counter control | Last conversion Private MiB | Natural quiet end MiB | First Collect / WaitFinalizers / Second Collect MiB |
|---|---:|---:|---|
| Existing shared-heap handle-table buffer | 140.31 | 27.13 | 20.07 / 20.07 / 20.63 |
| Separate VirtualAlloc/Release buffer | 147.82 | 26.59 | 19.97 / 19.97 / 20.53 |

All natural-stage GC collection counts were zero. Legacy quiet dropped to
22.55 MiB at second 26. Thus GC was not necessary for this large release.
The new counter did not remove the memory steps; do not blame the historical
growth on the sampler or call this a resource fix. The sequential A/B has OS
timing/order limitations and does not establish identical allocation histories.

A subsequent MF-only trace captured 97,008 events, zero loss/suppression, one
concurrent allocation/free ambiguity. Release-stack ledger cross-checks the
primary ledger. Exact ntdll stacks show `RtlpSecMemFreeVirtualMemory`,
`RtlpDeCommitFreeBlock`, `RtlpCollectFreeBlocks`, and `RtlpFreeHeap` around large
natural per-call drops. Windows nearest-export symbols and compiler-folded
Native destructor labels must not identify particular live object ownership.
Cumulative freed-byte group estimates reuse addresses over time and are not
retained memory or exact individual-object accounting. This confirms real heap
release activity but still does not establish a stable long-run bound.

## Candidate resource experiment, not adopted production repair

Following read-only scope/risk review, a narrow candidate changes only existing
sidecar encoder concurrency: workers clamp hardware_concurrency to 1..4;
AVCodecContext.thread_count uses that value; x265 pools uses that value and
frame-threads min(workers, 2). This is not a total process-thread limit or an
application scheduler change. Decoder, HEIC, MF, codec, preset, CRF, pixel format,
timing, metadata policy, backend selection, dependencies and ABI are untouched.
Bitstream, measured quality, speed and cancellation latency may still change,
so existing independent output thresholds must be rerun if this candidate is kept.

The reason for this finite A/B is resource demand: baseline x264 logged 30 frame
threads plus five lookahead threads on this 20-logical-CPU host; x265 also used
automatic CPU-wide pools. Original mixed peak was about 1.6 GiB. The candidate
may reduce per-operation working buffers and shared-heap fragmentation; it is
not evidence that an unbounded leak was identified or fixed.

Baseline source and all normal Native/sidecar DLLs/PDBs were copied before editing
to `hevc-migration/resource-thread-ab-v61/baseline/`, with original identities.
Normal Release+harness and v61 driver builds completed successfully. The same
eight-round no-MF control completed with source/runtime identity guards and
60-second natural quiet. Candidate quiet ended at 68.62 MiB versus baseline
161.54 MiB; both retained four semaphores. Real logs confirm x264 threads=4,
lookahead_threads=1 and x265 pool=4/frame-threads=2. This sequential engineering
comparison supports further qualification, not a claim that R4 or a leak is fixed.

Fresh read-only GPT 6 Luna max reviewer `hevc_resource_cap_review` identified that
the unconditional thread_count also affected existing non-HEVC sidecar routes,
and that frame-threads=2 increased the baseline's effective one-frame parallelism.
The final candidate narrows thread_count to source HEVC OR target HEVC and uses
x265 frame-threads=1. The same read-only reviewer verified these exact corrections
against the preserved v61 source. Final sidecar source SHA256:
`C7A28E41CFA297E7E3A5D3EA1BC573E7380E438F069F0BD114169F742B7ED1B2`.
H.264-to-H.264 sidecar threading and the MF control retain their prior settings.

Final candidate v62 normal Native Release, test harness and normal Core package
builds completed with zero errors; the normal Core build has zero warnings.
Shipped DLL identities, three GPL notices and actual imports were checked.
Native binding: `hevc-migration/release-candidate-resource-v18.json`.
Full affected v62 pipeline is running, with four derived non-left chroma routes
already passing. Aggregate tests, full video matrix, short mixed qualification,
30-minute mixed load, independent output/resource checks and fresh final review
remain prerequisites. No new formal run is allocated.

## Local tools and remaining boundary

Microsoft Detours 4.0.1 source, official DbgEng/DbgX NuGet packages and exact
ntdll/CoreCLR public PDBs were downloaded only to `.ai-tmp/cache/debugger-tools/`.
They are local diagnostic caches, not globally installed tools or shipped
dependencies. No OS codec, registry, admin service or machine heap policy changed.
The public symbol server receives binary symbol identifiers, not private media.

Next: finish the bounded encoder-resource A/B; retain the candidate only if
actual resource and correctness evidence supports it. A new formal run requires qualified natural resource evidence
and fresh independent review; traced or forced-GC results cannot replace it.
R4 remains OPEN; stop before R5/P8 and do not publish Git changes.

## v62 full workload result and capture recovery

This section supersedes the earlier running-pipeline checkpoint. The unchanged
v62 product passed the bounded 383/383 aggregate (zero skips), all 11 video
routes and five expected failure cases, four derived chroma routes, R3 backend
truth and Native metadata/timing regressions, CLI and both large-file cases.
Independent checks passed 33 retained outputs, three large outputs and all four
actual video routes. These are correctness results, not resource acceptance.

The actual workload completed three warmups and 50 measured epochs over
1827.850 seconds. Private bytes rose from 163.246 to 368.504 MiB; after all
post-load cancellation, retained-output production and 60.637 seconds of quiet,
the process remained at approximately 375.223 MiB. Semaphores stayed at 16.
Periodic large drops occurred, but both the high and low envelopes increased;
the descriptive late median pairwise slope was +4.5045 MiB/min. A bounded cache
or a live-allocation leak has not been established. R4 remains OPEN.

The outer pipeline capture had an incorrectly retained 1800-second timeout.
It killed the Python pipeline supervisor while the product consumer continued
and wrote the remaining workload and quiet records. The supervisor exit is 1;
the product consumer's direct exit was not captured and is explicitly unknown.
No exit 0 or ordinary completed.json was reconstructed. Future whole-pipeline
captures need a sufficiently long explicit timeout, independently of the
5400-second per-soak child timeout.

Immutable failed run:
`.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-5/evidence-index.json`,
SHA256 `BC20F1B4789E0870794F512E7FECFD331849169F5F1FC03E83D93F83303F2454`;
2160 payloads, 2381942620 bytes, copied and hash-verified.
Status: `FAIL_RESOURCE_STABILITY_NOT_PROVEN_CAPTURE_INCOMPLETE`.
Candidate-v62-r2 SHA256
`499E2967A8714E7367A631A1BEECDBDB4EE2E162F4FC13953EC254F3426F5CEA`.
Fresh read-only reviewer hevc_resource_cap_review accepted the honesty of the
recovery record and failure-only archive path, explicitly not R4 acceptance.

After sealing run-5, a separate offline full-mixed HeapWalk/VirtualQuery probe
was added under `tools/p5-r5-video-validation/r4-mixed-heap-probe/` and
`r4_mixed_heap.py`. It invokes the exact existing v62 Mixed implementation,
including the labeled diagnostic GC already present outside measured processing,
and does not change production allocation or lifetime policy. Its owned 8 MiB
process-heap allocation/free qualification passed. The initial diagnostic is
running at `hevc-migration/mixed-heap-v62/`, with explicit 3600-second capture.
HeapWalk is intrusive and its busy/free figures do not represent all process
private memory; this probe cannot replace uninstrumented resource acceptance.

## Confirmed cleanup-authority ownership leak and bounded repair

The mixed-heap-v62 diagnostic finished with direct exit 0, no timeout and
unchanged captured source/runtime identities. It completed three warmups and
12 measured epochs (448.3 seconds), retained production outputs and natural
quiet. Unlike codec-only observations, comparable mixed boundaries showed
growing live-busy bytes and block counts, so a pure free-page explanation was
not justified. The probe initially bound only 17 top-level runtime files;
separate during/after supplements verified all 22 files against the historical
driver and sealed candidate. Original probe sources are preserved in its
source-snapshot directory. These are diagnostic limitations, not acceptance.

Fresh read-only reviewer hevc_resource_cap_review identified a concrete
production ownership defect. SourceExtractor transfers a CleanupPlan to the
ExtractedMediaBundle. That plan retains a NativeContext operation lease.
SourceProtocolCleaner borrows a bundle-owned plan and correctly leaves its
disposal to the owner. NeutralMediaService owned the extracted bundle but
never disposed this authority. InspectedSource disposal therefore requested
context destruction while the retained lease kept it from completing.
Mixed reaches this path approximately 21 times per successful epoch through
direct neutralization, merge, split and the codec-round neutral input.
This proves a real project defect; it does not quantify all Private Bytes.

The bounded fix in NeutralMediaService scopes the extracted CleanupPlan with
`using` immediately after successful extraction. It releases the authority on
success, failure-result, exception and cancellation, before the outer inspected
source is disposed. No backend, codec, output semantics, public ABI, system
dependency or process boundary changes.

Four new lifecycle regression cases use real inspection/extraction and Native
authority. The success route uses the production cleaner; three controlled
cleaner boundaries exercise failure-result, IOException and cancellation.
They assert both the plan token and NativeContext handle are zero immediately
after the call, without forced GC. All four failed against exact historical
v62 DLLs and all four passed after the repair (zero skip). Logs, TRX, runtime
identities and red-green-summary.json are in cleanup-lifetime-v63. The three
controlled failures are lifecycle coverage, not real filesystem failure proof.

The next candidate v63 needs the final affected aggregate (now four additional
rows), full conversion matrix, normal package identity, short qualification,
30-minute natural mixed load, independent output/resource checks and fresh
read-only closure review. The old run-5 remains an immutable failure.

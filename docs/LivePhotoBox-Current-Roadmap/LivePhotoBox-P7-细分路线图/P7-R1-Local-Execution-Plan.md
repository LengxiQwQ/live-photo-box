> **当前有效收口状态（2026-10-16）：P7-R1 CLOSED / PASS。** 最终固定候选 run-28：146/146 通过，失败/跳过/未执行为 0；十二类攻击面均完成适用路径核查；17 个冻结样本全部找到、实际使用且身份不变（14 条专门配对 Inspector 对照，不代表 17 条全流水线）；独立 bridge 217 项及 D 组 15 cases / 13 结构检查通过。fresh 只读独立复核为 PASS / DONE_FOR_R1，绑定 candidate-index SHA256 `DF12BE1B38BC216C82DDE902D0E55890B873AE56C1E083572525B767B48BBA38`。正式证据：`.ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-28/evidence-index.json`。P7 仍为 in_progress；已停止，未激活 R2 或 P8；不构成发布验收。下方暂停、ACTIVE、run-28 尚未执行等内容保留为历史 checkpoint，由本段取代。

> 当前用户已明确授权本地负责 R1 到完成：取代旧暂停和 ChatGPT planning/review 依赖。普通工程问题自主修复；保留验收、Contract、ABI、历史证据。须完成十二类覆盖核查、实际汇总执行、正式证据和 fresh 本地只读独立复核。R1 完成后停止，不进入 R2/P8。以下旧 checkpoint 为历史记录。

# P7 R1 local execution plan

## Authority and scope

2026-10-14: the current user explicitly authorized local planning/review and completion of P7 R1, including tests and bounded corrections. This supersedes the paired ChatGPT planning dependency and the previous checkpoint's hold on R1-A and new runs. Existing sealed runs remain immutable. P7 R2 and P8 are outside this authorization. No acceptance criterion, public ABI, dependency, or preservation contract changes are authorized.

Local Windows execution owns implementation and test execution. Fresh read-only local reviewers review this plan, completed attack groups, product defect closures, and final R1 evidence. They must not edit the candidate they review.

## Ordered work

1. Reconcile current HEAD/roadmap/TASK; retain existing BX-007 source/test changes and P4 document moves. Read all protocol fact documents. Verify the 17 frozen manifest inputs from the approved cache, hash existing sealed evidence, and bind current source/test/native binary identities.
2. R1-A: inventory existing arithmetic/truncation tests; add deterministic bounded mutations at JPEG segment, XMP numeric range, MP4 header/table/payload, HEIF header/directory/extent, and SEF directory/payload boundaries. Exercise actual SourceInspector and applicable conversion rejection paths; retain existing ExtractorFailureTests separately as facts-level unit evidence. Assert precise failures, unchanged source, no published output/residue. Derive only copied inputs; regenerate every mutation and compare bytes/hash.
3. R1-B: execute existing namespace/duplicate/conflict/fake-marker tests and add missing cross-protocol combinations. Record valid controls and unique structural ownership or deterministic ambiguity rejection. Include applicable Inspector/Extractor/Cleaner paths; document operations blocked upstream because no inspection authority exists.
4. R1-C: execute and extend range/HEIF/ISO-BMFF matrices covering ordinary, extended, permitted zero-size, undersized/truncated headers, parent/child boundaries, overlapping extents, and unsupported versus malformed distinctions. Independent structural validation must traverse hierarchy, not search arbitrary bytes.
5. R1-D: execute and extend SEF corruption, conflicting pairing/cross-protocol identity and format-specific trailing-data cases, with legal controls and no-output/source/residue checks.
6. Reuse already built Native artifacts only when their exact source and binary identity is established against run-27/current inputs. Managed targeted builds may use SkipNativeBuild=true with explicitly bound native dependencies. If production Native changes are required, rebuild through the existing CMake Windows configuration and record the real commands. Use deterministic fresh CLI-only BuildId only if required; preserve historical outputs.
7. Before each formal attack group run, complete scratch build/discovery/test/tool checks. Allocate the next actually absent run directory create-only. Capture direct child exits, stdout/stderr, discovered/executed/passed/failed/skipped/not-executed and TRX, corpus/base/operator/replay manifest, source/sample before/after, native/tool identities, independent structure results, and output/temp inspection. Failed runs stay consumed.
8. Fresh read-only review each attack group and any product fix. Apply bounded corrections and repeat affected checks when review or runtime evidence demands it. Final bounded R1 aggregate regression includes the four groups, BX-007 regression and required unchanged RealSamples route checks. No whole-product suite.
9. Publish the twelve-row attack matrix with precise test identities and evidence category/applicability, source/sample integrity and residue findings. Fresh read-only final review may close R1 only after all required rows pass. Update private state/TASK and stop before R2; P7 remains in progress.

## Failure handling and completion

Preserve failed outputs and identity. Diagnose ordinary engineering failures autonomously. Replan material scope/assumption changes; ask the user only for major product/contract/ABI/dependency/acceptance/security decisions or unavailable exclusive inputs. No weakening assertions, skipping required samples, production external parsers or unauthorized cleanup. Three materially different ineffective repairs or two consecutive formal runs consumed by the same root cause trigger the existing escalation rule.

Complete means all twelve attack areas have actual applicable production-path evidence, repeatable mutation identities, meaningful independent structure evidence, zero required skips/missing samples, no crash/silent acceptance/unauthorized mutation/residue, and fresh read-only closure review. A local test pass alone does not close R1 or P7.

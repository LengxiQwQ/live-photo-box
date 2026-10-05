# P5-Audit — Fresh Independent Converter Reliability Audit

> **性质：** 独立审计；Auditor 默认只读，必须与 Implementer 的实现上下文独立。
> **基线：** Roadmap revision `317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7`；R6 post-accept HEAD `cc54298524bc441f9644b0bba6cc6f5a23deaa14`。
> **状态：** P5 仍为 `in_progress`；R6 已获明确 ACCEPT 并完成提交、推送和要求的 CI；Audit 尚未执行，Verify 与最终 P5 Gate 尚未完成。
> 本文只细化 P5 Audit，不修改 P5、Neutral Media Contract、公开 Contract/ABI、任何 R0–R6 验收条件或阶段顺序。

## 1. 权威与角色边界

按以下权威审计，不因下位 TASK、报告或实现改变上位要求：

1. `docs/LivePhotoBox-Current-Roadmap/00-重构总纲-唯一执行路线.md` 与 `06-P5-转换器可靠性.md`；
2. `12-中性媒体契约.md`；
3. `LivePhotoBox-P5-细分路线图/00-P5-细分路线图总览.md`、`INDEX.json` 与 R0–R6 细分路线；
4. 对应 `.ai/tasks/P5-R*-*/TASK.md`、当前 production code、测试、真实执行输出和版本化 evidence。

P4 Audit 文档仅作为 Fresh Auditor 的角色隔离和结果格式参考；不得把 P4 专属的 Exit Gate 复制成 P5 验收条件。协议事实以 `docs/实况照片协议分析文档/` 的全部现有文档及已锁定真实样本为准；不得通过任意字节命中推导容器关系，也不得重复逆向已确认的协议事实。

Fresh Auditor 必须从当前代码、真实调用链和可复核证据反向取证，不信任 Implementer 自述、测试名称、提交说明、旧 AI 结论或旧的“全部通过”文字。审计中不得修改 production source、测试、profile、Roadmap、TASK 验收条件或 P4–R6 冻结资产。发现缺陷时记录证据、归属到原有 R/TASK，并提出返回 owner 的修复方向；Auditor 不实施修复。

## 2. 审计范围与停止边界

审计对象是当前精确 HEAD 上的完整 P5 Converter 能力：R0 契约基线、R1 execution truth、R2 普通图像、R3 HDR/GainMap、R4 颜色/高位深、R5 视频转换以及 R6 到 Neutral consumption 的真实公开调用路径。

Audit 是 source/data-flow/evidence 审查，不是 Verify。可进行有明确问题驱动的少量高价值 targeted replay；不要求干净重建、完整验证 campaign、重复执行所有已接受的 RealSample 矩阵或 package/import campaign。后续 Verify 独立承担其 Roadmap/TASK 规定的可复现 build、scope、test、RealSample、validator 与 runtime/package 验证。P0–P10 未全部获 ACCEPT 前，不得运行覆盖整个产品的全量 suite。

Audit 不重开 P4 backend 选择，不新增 capability，不改公共 Contract/ABI，不扩大或缩小 P5 scope，不开始 Verify、最终 P5 Gate 或 P6。R1–R6 的既有明确外部决议和 CI 是证据来源，不替代对当前代码与证据适用性的独立检查。

## 3. 绑定当前审计对象

审计开始时记录并核实：

- workspace、branch、精确 HEAD、upstream、ahead/behind、完整 Git 状态；
- Roadmap revision、`.ai/state.json` 中 P5 状态和 active task；
- `.\.ai\reconcile.ps1` 的真实结果；
- 审计读取的源文件、测试、profile/manifest、报告及其所绑定的 commit/hash。

期望基线为 P5 `in_progress`，R6 post-accept HEAD `cc54298524bc441f9644b0bba6cc6f5a23deaa14`。若审计目标在进行中改变、revision/state/task 与权威不一致，或证据无法绑定到所审 HEAD，应停止结论并报告精确差异；不得把不同树的结果拼成一个 PASS。R6 快照中 Windows 工作树 CRLF 与归档 LF 造成的原始文件 SHA 差异，必须按已保存的 Git blob 身份及换行归一化绑定判断；不得谎称原始字节 SHA 相同。

## 4. P5 Exit Criteria 审计矩阵

逐项反查 `06-P5-转换器可靠性.md` §10 的八项既有 Exit Criteria，原标准如下，不得改写为更宽松的条件：

| # | Roadmap Exit Criterion | Auditor 必须从当前事实确认 |
|---|---|---|
| 1 | 每种公开 conversion path 有明确语义 | 从公开 façade/request 经真实调用链到 result/artifact，核实 operation/outcome 和适用 policy 均明确；无未分类成功路径。 |
| 2 | backend identity 可诊断 | ExecutionRecord/result 真实记录所选及实际 capability/backend、必要版本和诊断；identity 不冒充语义正确性。 |
| 3 | passthrough/remux/reencode 区分真实 | 对照调用点、实际变换、容器/codec 结构和独立证据，核实 operation 记录与实际数据路径一致。 |
| 4 | HDR/color/audio/preservation 不静默降级 | 核实每个适用 component 的 before/after evidence、Strict/BestEffort、Unsupported、degraded/no-output 行为；不以 backend 成功、文件可打开或产品 self-read 代替语义证据。 |
| 5 | approved Windows backend 覆盖产品需要 | 核对 P4 冻结 owner、R5 capability/profile 与真实 pre-dispatch；缺少冻结 capability 时如实 Unsupported，不猜测、重选或跨 backend retry。 |
| 6 | 无 production external CLI | 从生产调用链、项目/打包/runtime 和依赖入口确认 ExifTool/FFmpeg/ffprobe 等只处于离线验证工具，不回到 production conversion path。 |
| 7 | Converter 不直接拥有平台 API | 检查 C# orchestration、Native C ABI 与 C++ data-plane 边界；不让公开语义依赖 WIC/MF/COM/平台类型或让 C++ 类型/异常跨 ABI。 |
| 8 | P6 可把 Converter 当作稳定通用能力 | 结合前七项及 Neutral contract，确认 P6 输入可依赖 vendor/backend-neutral、可验证、保真状态明确且失败 fail-closed 的 Converter 输出；不得仅凭阶段名称推断。 |

任何一项没有可追踪的当前代码和适用证据，均不能记为通过。

## 5. R0–R6 细分路线交叉检查

Auditor 必须读取当前磁盘上的 R0–R6 细分 Roadmap 与对应 TASK，从其现行验收矩阵中抽取证据责任，并检查它们在最终 HEAD 上是否仍成立。不得只照抄本表，也不得用本表替代细分文件。

| 轮次 | 必须反查的 P5 事实 |
|---|---|
| R0 | 公开 conversion class、operation/outcome、owner、policy、no-partial-output 和 RealSample/independent-validator 责任矩阵完整；语义成功与 backend 成功分离。 |
| R1 | request、`ConversionExecutionTruth`、image/video execution records、trusted minimal facts、policy/fallback、component outcomes、failure classification 和兼容 projection 在真实调用路径中一致；`NotEvaluated` 不是 preservation evidence。 |
| R2 | JPEG/HEIC passthrough、lossless transform、reencode 的 observed EXIF、orientation、ICC/preservation 结果真实；Strict/BestEffort 与 post-validation/no-publish fail-closed；HDR/GainMap 不绕过 R3 gate。 |
| R3 | GainMap semantic identity、owner、representation、relationship、必要 metadata/headroom 与 artifact identity 端到端绑定；Native 拥有 result-affecting pixel math；Strict 不降 SDR；BestEffort 只在专门授权下如实 degradation；真实样本和独立 JPEG/HEIF graph、decode/render/reference 证据有效。 |
| R4 | ICC 字节携带、颜色 metadata mapping、像素 transform 不混称；不 relabel 伪造转换；高位深路径不隐式量化；固定 profile、RealSample identity、独立 parser/decode/render 假设和阈值未放宽。 |
| R5 | 按冻结 profile 在执行前选择 capability；project-owned remux 与 MF/minimal-libav transcode 真实区分；video/audio/timing/rotation/color/HDR/bit-depth 结果由独立验证支撑；缺失 sidecar/Unsupported/TargetFps/失败路径无 retry、无半发布。 |
| R6 | 所有 14 个 Merge、7 个 Split 可用格与不可用/越界拒绝符合 typed requirement；真实调用方将 requirement 交给 Neutral；同 requirement 不因 protocol/vendor 改变转换语义；实际 Neutral artifact 的内容、manifest、preservation 与 no-source-live-binding 证据正确；TargetFps 等 Unsupported/no-output 边界仍成立。 |

检查真实生产入口，不只检查 mapper、model、ABI 中存在字段。重点调用链与文件按实际源码确认，至少包括：

- `LivePhotoBox.Core/Media/Models/ConversionSemantics.cs` 及 image/video request、result、execution-record 模型；
- `LivePhotoBox.Core/Media/Image/ImageConverter.cs`、`LivePhotoBox.Core/Media/Video/VideoConverter.cs`、`LivePhotoBox.Core/Media/NeutralMediaService.cs`；
- 真实 Merge/Split caller、目标消费者的职责边界、相关 Native/backend/C ABI dispatch；
- R2–R6 对应测试、versioned profile/manifests、validator 源码与正式 evidence。

任何实际路径都不得按来源 vendor 选择协议真相，不得因 backend 名称决定 preservation，不得复活 managed pixel/data-plane work、production CLI、隐藏 fallback/retry 或 writer 旁路。

## 6. Neutral Media Contract N1–N14 交叉检查

Neutral Media Contract V4.0 适用于 P1–P10。Auditor 应逐条判断 P5 converter 生产/消费的 Neutral artifact 是否满足下列不变量；只有用当前调用链证明某条确实不适用于所审公开路径时，才可标注 `N/A` 并写明理由。

| 不变量 | P5 审计关注点 |
|---|---|
| N1 No source live binding | Neutral 输出不再携带会被 Source Inspector 识别为来源 Live/Motion 的 binding。 |
| N2 Independent media validity | Primary image、motion video、GainMap/auxiliary 各自满足真实结构与媒体有效性。 |
| N3 SourceProvenance is not target authority | 来源 provenance 只用于诊断/历史/evidence，不决定目标 correctness 或 protocol layout。 |
| N4 Target consumes semantics, not source quirks | requirement、media facts、orientation/timing/color/HDR/auxiliary 等语义进入目标路径；不按厂商来源分支。 |
| N5 Preservation must be truthful | 每个声称的 preservation/degradation/discard/unsupported 状态由对应 before/after evidence 支撑；文件可打开不等于 Preserved。 |
| N6 No unnecessary re-encode | 满足目标时使用合法 passthrough/remux，不无理由 decode→encode；不得把重编码伪报为 remux。 |
| N7 Orientation has one semantic | EXIF、HEIF transform/property、video matrix 等输入表达最终转换成一致视觉/媒体语义。 |
| N8 Timing has one semantic | cover/key timestamp、duration、frame/timing 按 P5 contract 统一；真实时间轴不可静默改变。 |
| N9 Unknown/non-target metadata preservation | 未证明是来源 binding 或与 Neutral validity 冲突的元数据默认保留；不可表示的跨容器/codec loss 明确记录并遵守 policy。 |
| N10 Neutral success is verifiable | 同时具备 post-clean source-inspector 结果、artifact 结构/媒体有效性、manifest/evidence；self-read 不能替代独立证据。 |
| N11 Auxiliary/HDR representation is unambiguous | Embedded/detached/materialized ownership 与 relationship 明确；同一 semantic GainMap 不重复 materialize/append。 |
| N12 Platform-neutral semantics | Neutral semantic correctness 不要求 Windows path/handle 或某平台 API。 |
| N13 Backend-neutral semantics | backend 可诊断，但不能改变 bundle 语义、writer correctness、preservation 或 protocol authority。 |
| N14 Backend success is not semantic success | codec/backend 成功之后仍执行 P5 contract 所要求的结构、metadata、color/HDR、timing/preservation 验证。 |

还须检查 Neutral Contract §§3–8 的禁止项、media combination、manifest identity/length/hash/container/codec/semantic/ownership 字段和架构 guard 与 P5 代码交互时没有退化。Audit 不扩展为重审 P1–P4 的全部验收，但发现 P5 调用链令其不变量退化时必须作为 P5 blocker 报告。

## 7. Evidence lineage 与 RealSample 规则

对每条被引用的 R1–R6 测试、profile/manifest、RealSample、独立 validator、命令输出和外部决定，记录：

- source/profile/validator 的精确版本和身份；RealSample 原件 SHA、字节数、缓存副本、派生物 provenance/hash；
- 实际测试 filter/scope、包含的类别、真实样本执行数、skip/exclusion 计数；
- 独立工具实际验证的结构/metadata/pixel/media facts、命令与退出码；
- evidence 所绑定的 HEAD 与本次审计 HEAD 之间相关源文件、测试、profile、样本及阈值的变更关系；
- 该证据能支撑的具体 claim 与其不能支撑的范围。

R2–R6 RealSample/independent validation 要求以对应细分 Roadmap、TASK、profile 为准。真实样本缺失、filter 排除、skip、sample identity 不符、validator 假设/阈值事后变宽、source hash 改变或 evidence 无法绑定到最终 HEAD，都是未满足的 coverage/evidence blocker；不得用 synthetic、mock、产品 self-read 或旧总结替代。已有 ACCEPT 证据只表示该轮被接受，不自动证明后续 R6 修改未影响其适用性；必须做实际文件与调用链/commit 影响分析。

`designs/各个机型测试/` 永远只读；任何所需复核只操作 `.ai-tmp/cache/samples/` 中的副本，工作/派生产物留在确定性的 `.ai-tmp/workspace/P5-Audit/` 路径。除项目规则允许的正式 Gate 归档或用户明确要求外，不创建、覆盖或清理 `.ai-tmp/evidence/` 快照；不得将关键长期结论只放在易失 `.ai-tmp/`。

R6 正式快照存在于 `.ai-tmp/evidence/P5-R6/`。应阅读它的 manifest/checksum 和可读 CI/审查输出，并核实最终提交、推送、Build & Release 与两项 CodeQL 的 identity。R6 的历史 ACCEPT、工作树/归档换行差异或 CI 通过本身不能替代本 Audit 对 P5 全部标准的判断。

## 8. 执行步骤与有限验证

1. 从当前精确 HEAD、Roadmap revision、P5 state 和 Git scope 开始；运行 reconcile，遇到 stale/mismatch 先停止。
2. 阅读 `06-P5-转换器可靠性.md`、Neutral Contract、P5 总览、INDEX、R0–R6 detail/TASK；协议/媒体审计还须读取 `docs/实况照片协议分析文档/` 的所有文档。
3. 对 P5 八项 Exit Criteria、R0–R6 细分验收和 N1–N14 建立逐项矩阵；从真实公开入口沿调用链追踪到 backend、post-validation、manifest/result 与 artifact。
4. 反查对应测试源码和真实 execution output，确认 scope/filter 真正覆盖宣称类别；从 R2–R6 versioned evidence 验证样本/profile/independent-validator 对当前 HEAD 的适用性。
5. 仅当静态与既有证据不能判定一个高风险语义时，选择有限 targeted replay，写明问题、精确命令、scope、输出、exit code、样本 hash 和结果；复核产物写入固定 `.ai-tmp/workspace/P5-Audit/`。Replay 不能替代完整 Verifier campaign，也不允许修改代码来获得通过。
6. 形成只读审计报告及 blocker/返回 owner 计划。将实际读取的文件、调用点、证据位置和 HEAD 绑定到逐项结果；保留失败/矛盾证据，不删除或重分类。

不要求 Audit 执行 clean/full build、所有 R0–R6 tests、全部 RealSample conversion matrix 或 P5 Verify package campaign。若 Audit 为判断具体缺陷执行了 targeted build/test/replay，必须记录真实命令、退出码、scope 和可读输出；其 PASS 仍不替代 Verify。

## 9. 输出与判定

报告至少包含以下逐项表格：

| Gate / Contract | 当前代码/数据流证据 | 测试/RealSample/独立证据 | final-HEAD binding | 状态 | 严重性 | blocker / 返回 owner |
|---|---|---|---|---|---|---|

状态只允许 `PASS`、`FAIL`、`BLOCKED` 或有具体代码路径依据的 `N/A`。实际实现违反 acceptance 是 `FAIL`；缺失、过期、不可读或无法绑定的必需样本/validator/profile/evidence 是 `BLOCKED`。对任何适用强制 criterion 存在 `FAIL`、`BLOCKED` 或未解释 `N/A` 时，最终只能为：

```text
REJECT — BLOCKERS REMAIN
```

只有八项 P5 Exit Criteria、适用的 R0–R6 条件、Neutral invariants、证据 lineage、fail-closed 与 architecture boundary 全部有当前 HEAD 的独立证据且不存在 blocker，Auditor 才可输出：

```text
READY FOR VERIFICATION
```

该结果仅代表可以进入独立 Verifier；不代表 Verify PASS、P5 ACCEPT/COMPLETED，也不授权 P6。发现 blocker 时列明确切 owning R/TASK、缺失或错误证据、最小修复/复核路线；修复后必须重新使用 fresh Auditor context。

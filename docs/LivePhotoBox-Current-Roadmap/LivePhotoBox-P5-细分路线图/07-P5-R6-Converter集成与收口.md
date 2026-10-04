# P5-R6 — Converter 集成与收口

> 本文细化 P5 主路线图与 P5 细分总览中已定义的 R6，不新增 capability、不改变验收标准或阶段顺序。
> 本文基线：Roadmap revision 317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7；HEAD d8b8425c064c3566ae1a8c927adfe70b4dd69cfe。
> P5 保持 in_progress。R5 已在该基线上完成外部门禁、提交/推送和要求的 Build & Release、CodeQL 检查；这不等于 R6 或 P5 已接受。

## 1. 权威与目标

遵循以下上位要求：

1. docs/LivePhotoBox-Current-Roadmap/06-P5-转换器可靠性.md，尤其 Converter Contract、Required Semantics、Backend Selection、ExecutionRecord 和 Exit Criteria。
2. LivePhotoBox-P5-细分路线图/00-P5-细分路线图总览.md 的 R6 定义与阶段顺序。
3. P5-R0 至 P5-R5 的当前细化文件和已接受的结果；R6 只做集成收口，不重开这些轮次。
4. docs/LivePhotoBox-Current-Roadmap/12-中性媒体契约.md。
5. 当前真实生产调用链、测试和 hash-locked P4/P5 证据。

R6 的唯一目标是闭合当前公开 ProtocolFormatMatrix → ProtocolMediaRequirements → P5 Neutral consumption 路径矩阵，并运行实际受 R6 改动影响的 P1–P4 回归。证明已有链路正确时保存测试与证据；只有某一行确实失败时才允许做最小管理层集成修复。

R6 不实现新的 codec、格式能力、协议 Writer、backend 选择或 preservation 语义。转换结果继续由 R1–R5 的 canonical converters、Neutral contract 和被冻结的 P4 backend owner 决定。

## 2. 施工边界

R6 可检查和修改的范围限于直接参与该矩阵的 managed integration/orchestration 与测试：

    ProtocolFormatMatrix
    → ProtocolMediaRequirements
    → LivePhotoMergeRunnerService / LivePhotoSplitService
    → NeutralMediaService
    → canonical R1–R5 converters
    → truthful NeutralMediaBundle / manifest

约束：

- Converter 只消费 typed MediaFormatRequirement 和可信 Neutral media facts；不得根据 vendor/protocol index 选择 conversion semantics。
- 保持 R1 operation/preservation truth、R2 metadata/orientation 结果、R3 HDR/GainMap authority、R4 color/high-bit-depth 规则、R5 video dispatch/remux/transcode 规则。
- Strict、BestEffort、unsupported、cancel、post-validation 和 publish failure 必须继续遵循已有语义；不能产生虚假成功或半发布结果。
- P5 不负责实现 Apple/Vivo target protocol writer。Split availability、requirement mapping、Neutral consumption 和 Target Writer enablement 是不同边界。
- HeicConverterService 的既有 legacy-façade 义务按 R0/R2 执行；它不是新的 R6 requirement-matrix 行。若改动共享 image conversion 行为，再运行其受影响的回归。
- EditExportService / ImageFormatService 没有单独的 R6 acceptance row。若改动共享 Neutral image-consumption 行为，可将相应测试作为受影响回归；不得由此扩展 R6 的接受范围。
- 不改公开 MediaFormatRequirement、Converter request/result/enums、VideoBackend、Native C ABI/POD layout、ProtocolFormatMatrix 可用性、P4 backend owner、样本身份、manifest、validator profile 或冻结阈值。

## 3. 公开 requirement 映射清单

本节可用组合以基线源码 ProtocolFormatMatrix.Matrix、SplitMatrix 和 ProtocolMediaRequirements 为准。R6 测试必须显式覆盖全部可用格和不可用/越界格；不得静默省略。

### 3.1 Merge：14 个可用格，5 种 typed requirement

格式索引：0=JPEG+MP4、1=JPEG+MOV、2=HEIC+MP4、3=HEIC+MOV、4=HEIC+MP4 (HEVC)。

| Protocol index / name | 可用 format index | 每个可用格映射到的 requirement |
|---|---:|---|
| 0 — Fusion | 0, 1 | 0 → JPEG / MP4 / Copy；1 → JPEG / MOV / Copy |
| 1 — V1 | 0, 1 | 0 → JPEG / MP4 / Copy；1 → JPEG / MOV / Copy |
| 2 — V2 | 0, 1, 3 | 0 → JPEG / MP4 / Copy；1 → JPEG / MOV / Copy；3 → HEIC / MOV / Copy |
| 3 — OPPO | 0 | 0 → JPEG / MP4 / Copy |
| 4 — VIVO | 0 | 0 → JPEG / MP4 / Copy |
| 5 — Samsung | 0, 2 | 0 → JPEG / MP4 / Copy；2 → HEIC / MP4 / Copy |
| 6 — HUAWEI | 0, 2, 4 | 0 → JPEG / MP4 / Copy；2 → HEIC / MP4 / Copy；4 → HEIC / MP4 / HEVC |

必须验证的 5 种 distinct tuple：

| ImageContainer | VideoContainer | VideoCodec |
|---|---|---|
| JPEG | MP4 | Copy |
| JPEG | MOV | Copy |
| HEIC | MP4 | Copy |
| HEIC | MOV | Copy |
| HEIC | MP4 | HEVC |

同一 typed tuple 出现在不同 protocol index 时，映射结果必须相同；下游 Neutral conversion 不得因 vendor/protocol identity 改变语义。

### 3.2 Split：7 个可用格，4 种 typed requirement

格式索引：0=keep、1=JPEG+MOV、2=HEIC+MOV、3=JPEG+MP4。

| Split protocol index / name | 可用 format index | Requirement mapping | Rebuilt Neutral consumer |
|---|---:|---|---|
| 0 — none | 0, 1, 2, 3 | 0 → Unknown / Unknown / Copy (KeepSourceIfSame=true)；1 → JPEG / MOV / HEVC；2 → HEIC / MOV / HEVC；3 → JPEG / MP4 / H264 | 四行都进入 LivePhotoSplitService 的 Neutral 路径 |
| 1 — Apple | 1, 2 | 1 → JPEG / MOV / HEVC；2 → HEIC / MOV / HEVC | 维持现有 fail-fast target-writer 边界；不进入 Neutral |
| 2 — Vivo | 3 | 3 → JPEG / MP4 / H264 | 维持现有 fail-fast target-writer 边界；不进入 Neutral |

映射表中所有可用 Split 格必须经过 ProtocolMediaRequirements 的 table-driven 覆盖。只有 SplitProtocolNone 的四行属于当前 rebuilt Neutral consumption acceptance。Apple/Vivo 三个可用格必须保持在转换和输出创建前拒绝；不得为了“完成矩阵”启用 target Writer。

KeepSourceIfSame 没有独立于当前实现的上位语义定义。只验证已有 keep 行为：清除来源 live binding 后保留请求的源 image/video 格式、Neutral media 仍有效；不得把保留 source protocol residue 当成成功。

### 3.3 Reject 与 Unsupported

- Merge/Split 不可用组合及负数、超出矩阵边界的 index 必须在 conversion/publication 前被拒绝。
- MediaFormatRequirement.TargetFps > 0 保持 R0/R1/R5 的 Unsupported 与 no-output 语义；不得以 fallback 绕过。
- Conversion、取消、post-validation 或 publish 失败不得产生成功的 Neutral result、final artifact 或半发布输出。

## 4. R6 Acceptance / Evidence Matrix

| Gate | 必须证明 | 不满足时 |
|---|---|---|
| 完整映射 | 所有 14 个 Merge 与 7 个 Split 可用格映射到上表的 typed requirement；不可用和越界格拒绝。 | 缺格即 R6 未完成；不得从 filter 中排除。 |
| Tuple 等价 | 相同 typed requirement 不会因 protocol/vendor index 获得不同 converter/Neutral 语义。 | 出现 vendor-dependent conversion behavior 是架构 blocker。 |
| Merge Neutral consumption | 五种 distinct Merge tuple 通过真实调用链到达 canonical Neutral consumer，并保留 requested container/codec 和 R1–R5 truth。 | 只测试 mapper、不能证明 caller 把同一 requirement 传给 consumer，或只能靠新 capability 才能通过时，记录 blocker。 |
| Split Neutral consumption | SplitProtocolNone 的四种 tuple 通过 rebuilt split path 到达 Neutral consumer。 | 缺任一 tuple 即未完成。 |
| Target Writer 边界 | Apple/Vivo split target-protocol rows 在创建输出前 fail-fast，且无输出。 | 任一 row 进入 protocol Writer 或留下 output 即 blocker。 |
| Neutral validity / preservation | final PrimaryImage、MotionVideo、auxiliary ownership、artifact identity/hash 与 post-conversion artifact 一致；Neutral image 无来源 live binding。使用已有独立证据，不以 product self-read 单独证明 preservation。 | identity、结构、binding 或 preservation 无真实证据即 blocker。 |
| Image/video semantics | requirement 路径继续满足 R2–R4 的 operation、GainMap、ICC/color、高位深 truth，以及 R5 的 Copy/remux/transcode/backend/preservation truth。 | 不允许 integration-specific fallback、retry 或 silent degradation。 |
| Fail closed | unsupported requirement、转换错误、cancel、后验检查错误、publish 错误均不能形成成功 Neutral result 或 request-owned partial/final output。 | 任一 partial publication 即 blocker。 |
| RealSample / independent validation | 复用对应 R2–R5 已接受、hash-locked 的 source、validator profile 和冻结阈值；R6 记录实际被 integration 调用的路线。 | 没有能证明必需语义的现有样本/validator 时标为 coverage blocker；不得造样本、换样本或放宽阈值。 |
| P1–P4 regression | 仅运行真实 R6 diff 影响到的 authority/call-path 回归，并记录 filter 实际纳入的测试类别。 | 不得以全量 suite、synthetic-only 或 skip 代替 targeted proof。 |

Unit/table-driven mapping 测试、Synthetic 状态机测试、RealSample route、IndependentValidation 必须分别报告。Mock/fault injection 不能替代任何已接受 R2–R5 RealSample 或真实外部验证要求。R6 不重跑完整旧 phase validator；若 diff 使已接受证据前提失效，只重跑对应冻结 profile，并记录原因。

## 5. 执行顺序

1. 在改动前记录 HEAD、git status、Roadmap revision、R5 gate/CI 基线；运行 .\.ai\reconcile.ps1。
2. 先增加 P5R6 table-driven mapping/invalid-cell 测试，覆盖 14 个 Merge 与 7 个 Split 可用格、不可用/越界格和 tuple 等价性。
3. 增加五种 Merge tuple 与四种 SplitProtocolNone tuple 的 Neutral-consumption 证明；测试须证明真实 caller 传递的 requirement 与表格一致，不能只单测 mapper。
4. 对 Apple/Vivo split target-protocol rejection、TargetFps > 0、unsupported/conversion/cancel/post-validation/publish failure 验证 fail-closed 行为。
5. 对所有 result-affecting routes 绑定已有 hash-locked R2–R5 RealSamples 和独立 validator evidence；遇到缺样本或无法证明的行先记录 coverage blocker 并停止该 claim。
6. 只修复由上面失败直接证明的 managed orchestration defect。任何必需修复触及公开 Contract/ABI、ProtocolFormatMatrix availability、P4 ownership、R1–R5 accepted semantics 或 target Writer 边界时立即停止并报告。
7. 运行 P5R6 filter 与实际受 diff 影响的 P1–P4 filters，保存完整命令、退出码、输出、测试 scope、RealSample identities、独立 validator 结果和失败后的文件清理证据。
8. 完成本轮 closeout：检查 scope/skip、运行 .\.ai\doctor.ps1 -ActiveOnly、git diff --check；只有当 runtime/package 被合法范围内的差异影响时才运行其对应 package/import 检查。
9. 完整本地门禁后报告 READY FOR FRESH EXTERNAL VERIFICATION 并等待 R6 的明确外部门禁。R6 ACCEPT 后，按授权提交/推送并等待要求的 CI 成功，再进入 P5 Audit。R6 ACCEPT 不能把 P5 标为 completed。

## 6. Targeted 验证命令

新增 Core R6 集成测试统一使用可审查的 P5R6_ 命名，并确认 test filter 实际收录全部新测试、无 skip：

    dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~P5R6_" --nologo --verbosity minimal

P1–P4 回归按实际 diff 选择以下既有 test type；未触及的类别不运行，也不声称已验证：

    # P1：仅当 trusted/source/final Neutral inspection flow 被改动
    dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~SourceInspectorRoutingRegressionTests|FullyQualifiedName~LivePhotoDiscoveryRebuiltTests" --nologo --verbosity minimal

    # P2：仅当 extraction authority/handoff 被改动；RealSample extraction filter 另按实际 test 名显式加入
    dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~ExtractorAuthorityTests" --nologo --verbosity minimal

    # P3：Neutral/cleaner/split handoff 被改动时，选择实际受影响的类型
    dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~NeutralMediaServiceTests|FullyQualifiedName~CleanerBoundaryArchitectureTests|FullyQualifiedName~AppleSplitRegressionTests" --nologo --verbosity minimal

    # P4：仅当 Native invocation/runtime boundary 被改动
    dotnet test tests/LivePhotoBox.Core.Tests/LivePhotoBox.Core.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~NativeMediaServiceIoContractTests|FullyQualifiedName~NativeRuntimeTests" --nologo --verbosity minimal

始终记录 git diff --check。阶段末运行 .\.ai\doctor.ps1 -ActiveOnly；将与当前 R6 prerequisite 无关的诊断失败如实分类，不能静默忽略，也不能用它降低产品 gate。P0–P10 尚未全部完成前禁止运行覆盖全项目所有功能的 suite。

## 7. Evidence 与停止条件

- 日常多轮证据放在确定性路径 .ai-tmp/workspace/P5-R6/；仅正式 R6 gate 归档到 .ai-tmp/evidence/P5-R6/。
- 每次 evidence 绑定 exact HEAD、roadmap revision、active TASK、命令/退出码/可读输出、实际测试 filter 与零 skip 声明、sample SHA-256、profile/validator 版本、输入输出 artifact hash、manifest truth 和 failure cleanup 结果。
- 缺样本、独立 validator、受影响回归证据或任何 required route 时 R6 = 未完成；不得以旧报告、mock、synthetic-only、skip、产品 self-read 或文字声明补足。
- 任何必需实现会改变公开 MediaFormatRequirement、Converter result/enum、Native ABI、P4 backend owner、ProtocolFormatMatrix availability、P1–P5 semantics、Target Writer scope 或 R6 acceptance 时，停止并报告具体边界，不自行扩展。
- Local PASS 只表示 ready for external gate。只有明确 R6 ACCEPT 后才可完成 R6 并进入 Audit；只有独立 Audit、Verify 以及指定 P5 Final Gate 明确 ACCEPT 后 P5 才完成。绝不进入 P6。

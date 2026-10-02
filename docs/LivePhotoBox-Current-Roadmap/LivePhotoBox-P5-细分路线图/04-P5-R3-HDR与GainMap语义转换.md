# P5-R3 — HDR 与 GainMap 语义转换

## 目标

把 JPEG Ultra HDR / ISO 21496-1 GainMap 与 HEIC GainMap auxiliary 之间的正式像素转换、语义映射和容器表示纳入 Native Data Plane，并通过 Neutral Converter contract、Strict/BestEffort policy 和真实独立证据闭环。只细化 P5、R0/R1/R2 与 Neutral Media Contract，不改变其 acceptance。

## 范围

- JPEG Ultra HDR / ISO GainMap → HEIC GainMap auxiliary。
- HEIC 中已由项目结构检查确认的 GainMap auxiliary → JPEG Ultra HDR / ISO GainMap。
- 将 GainMap 的身份、owner、representation、关系、范围、hash 与 metadata 以 backend-neutral 绑定从 Neutral pipeline 传到 converter；目标 container graph / XMP / MPF 仍由项目语义层负责。
- Public converter contract 不暴露 Apple/Samsung vendor enum；已验证的 relationship 可作为诊断信息保留，但不能单独作为 dispatch authority。
- Native 执行所有会影响转换结果的 primary/GainMap pixel math 和 codec 数据路径。可使用 P4 已冻结且已打包的 codec primitive，不新增或重新选择 backend。
- Native ABI 使用高层语义 conversion request/result 和事务式输出，不跨 ABI 传裸 RGB/float pixel buffer；ABI struct 必须有 version/size/layout checks，校验完整 authority/binding 后才允许转换与发布。
- 以转换后重新检查和独立 reader/decoder 证明目标语义与容器关系；无法证明时 fail closed。
- 对每个支持/不支持的源-目标表示给出显式 capability、failure/degradation 结果。

## 明确边界

- 不重开 P4 codec/backend 选择，不增加生产外部 CLI 依赖；ExifTool/libheif/其他独立工具仅可作离线验证。
- 不从来源厂商协议重新解析语义，不让 vendor name 决定 Neutral contract。Apple 与 Samsung HEIC GainMap 仅在 Native facts 与映射所需参数完整、且独立证据成立时作为输入覆盖；否则明确 Unsupported，不能推断或套用另一厂商参数。
- 不实现 R4 色彩变换、ICC policy 或高位深承诺；P4-v1 8-bit HEIC 样本不能作为高位深证据。
- 不改变 Live Photo writer/协议合成、视频、P5 R5/R6 或 P4 authority/transaction 语义。
- 不允许 R2 普通图片路径绕过 GainMap eligibility gate。

## 语义与策略

1. HDR/GainMap 只有在主图、GainMap 数值、版本/metadata 与输出 container representation 一致且可验证时才可报告 preserved；codec 成功、可解码或仅找到了 auxiliary item 不足以证明语义转换正确。
2. `Strict` 永远不得以 SDR 代替所请求的 HDR/GainMap 语义；所需 metadata、目标 representation 或 capability 缺失/不确定时，失败且不发布 output artifact。
3. `BestEffort` 仅当独立 HDR 专用授权（例如 `HdrOutputPolicy.AllowSdrDegradation`）明确允许时才可输出 SDR；`AllowDiscard` 不构成授权。结果必须标记 `ActualOperation=DegradedOutput`、`HdrGainMap=Lost`、`PreservationOutcome=DegradedToSdr`、`FallbackOccurred=true`，并记录原因；不得把 SDR 误报为 HDR preserved。
4. HDR→HDR 重新编码可使 GainMap component 为 `Reencoded`，不能因此谎称字节 preserved；aggregate semantic preservation 只能依据真实 before/after evidence 报告 `Preserved` 或 `PartiallyPreserved`。
5. 不得以启发式/默认值填补缺失 headroom、gamma、offset、version 或其他必要 metadata；不得静默 clamp Apple representability，也不得把 warning 当 success。不可映射时报告 `Unsupported`；BestEffort 采用可记录、可验证的近似且改变 HDR range 时必须标记 degraded。
6. 中性 GainMap artifact 及其 artifact identity、owner protocol、role、selector、semantic、fingerprint、representation、relationship 与真实 before/after evidence 必须端到端保持；embedded/detached/materialized 不得重复 materialize。

## 实施顺序

1. **语义绑定与分派：** 扩展 backend-neutral request/contract 传递已验证的 Neutral GainMap binding、target semantic 和 preservation/degradation intent；明确 supported/unsupported matrix，保持 R1 execution truth。
2. **Native JPEG 路径：** 将 JPEG Ultra HDR ↔ ISO GainMap 的 result-affecting pixel math 从 C# Magick.NET 移入 Native；通过受约束 C ABI/POD/显式 buffer 实现，不跨 ABI 暴露 C++ 类型/异常。
3. **先完成 HEIC→JPEG，再完成 JPEG→HEIC：** 先利用 exact HEIF auxiliary item identity/decode 实现 Apple、Samsung HEIC GainMap → JPEG Ultra HDR；随后实现 JPEG GainMap binding/primary+GainMap decode → HEIC encode 和 Native auxC/auxl assembly。
4. **Orchestration 与后验验证：** 接入 NeutralMediaService/ImageConverter；任何策略拒绝、Native 错误、semantic post-validation 失败或 temp cleanup 失败均不得发布半成品或谎报成功。
5. **测试、真实样本与独立验证：** 使用 hash-locked P5-R3 sample extension 验证四条 canonical real-sample route、退化/拒绝及源文件不变；记录可重放的独立结构、decode/render 与 evidence。

## Canonical RealSample matrix

正式验收必须完成以下四条真实输入路径；hash 在只读检查原始目录时核对，所有处理只发生于 `.ai-tmp/cache/samples/` 副本：

| Route | RealSample | SHA-256 |
|---|---|---|
| JPEG Ultra HDR → HEIC GainMap | `荣耀.jpg` | `970658F835ADD139247694A67803E21A0CA0421290A3A2D269D52D970CABF759` |
| JPEG Ultra HDR → HEIC GainMap | `vivo.jpg` | `631CF8DAC983A9F58FC1D8CB63AAF8CC0569C45B80F046F1B995936D608320B9` |
| HEIC Apple GainMap → JPEG Ultra HDR | `苹果双文件.HEIC` | `868F29D1408D090193D04EBE5C71FEA139705381B9C1B8673C40C62E1A456998` |
| HEIC Samsung GainMap → JPEG Ultra HDR | `三星.heic` | `DBFB8AD846A16291B0B09599FCF588A3E6C20D58B96782E357D5652E3EC544C4` |

`一加.jpg` / `三星.jpg` / `小米.jpg` 是兼容性扩展，不替代上面四条 canonical evidence。`华为Mate80.heic` 是普通 HEIC negative control，不是 HDR positive sample。任何 neutral-cleaned derivative 必须记录 source hash 和精确 extraction/clean provenance。

## 验收条件

- 目标支持矩阵中每一条有证据的 JPEG Ultra HDR ↔ HEIC GainMap 路径通过真实样本双向转换；unsupported 行为可预测、分类正确且不产生 output。Samsung/Apple-specific 输入只有在单独具备合法关系和映射证据后才算支持。
- Native Data Plane 负责所有改变结果的 HDR/GainMap pixel math；production conversion path 不调用 C# Magick.NET 做 pixel math，也不调用外部媒体 CLI。
- Native Data Plane 负责所有改变结果的 HDR/GainMap pixel math；Native 通过项目拥有的目标 JPEG/HEIF representation/graph 实现写出，不调用 C# Magick.NET 做 pixel math，也不调用外部媒体 CLI。
- `Strict` 在完整 HDR/GainMap 语义可验证时通过，否则失败且无已发布 artifact；Strict 永不降 SDR。BestEffort 只有显式 HDR degradation permission 才能降级，结果包含 `DegradedOutput`、`DegradedToSdr`、fallback reason，并由独立检查证明无 GainMap/HDR claim 残留；`AllowDiscard` 不足以授权。
- HDR→HDR 时分别报告 GainMap component 的字节级 `Reencoded` 与 aggregate semantic preservation；semantic preservation 必须有可追踪的 before/after 数值、metadata/headroom 及独立结构/decode/reference 证据。
- metadata 缺失/映射未知时不臆造默认值；Apple representability 不得静默 clamp；无法映射则 `Unsupported`，warning 不能升级为 success。任何改变有效 HDR range 的近似必须报告 degraded。
- GainMap identity、owner、relationship、representation、hash、metadata/headroom/gamma/offset 等与目标格式有关的必要数值均被验证；无法映射者明确 unsupported，不填假值。
- JPEG Ultra HDR 输出由独立 JPEG/XMP/MPF/GainMap parser 验证合法关系与实际 bytes，并由独立 decoder/render/reference 证据确认可读；HEIC 输出由独立 HEIF graph/metadata/auxiliary reader 和 independent decode/render/reference 证据确认。
- 独立 reference reconstruction 的样本集、色域/transfer 假设、有效 overlap、数值阈值（如 MAE/PSNR/headroom-relative error）必须在验收执行前固定记录，不得在观察失败后放宽阈值。
- RealSample 来源 hash 始终一致；P4-v1 样本与 `designs/各个机型测试/` 原件保持只读；使用静态 `.ai-tmp/cache/samples/` 与 `.ai-tmp/workspace/` 路径，正式验收证据进入 `.ai-tmp/evidence/P5-R3/`。RealSample 缺失、filter 排除、skip 或只有 synthetic/unit evidence 均不通过。
- 覆盖 positive/negative/edge/adversarial case：GainMap identity/relationship/metadata 缺失或不一致、duplicate/ambiguous item、decode/encode/post-validation 失败、Strict 与 BestEffort、output 未发布和临时文件清理。
- R3 targeted Core/CLI build/test、Native ABI/runtime test、独立样本验证与阶段 doctor 均有可复现结果；`git diff --check` 通过。不得运行覆盖 P0–P10 的全项目 suite。

## 非目标

此 R3 的 PASS 仅表示 ready for fresh audit/verification，不代表当前用户/External Gate ACCEPT，也不自动进入 R4。

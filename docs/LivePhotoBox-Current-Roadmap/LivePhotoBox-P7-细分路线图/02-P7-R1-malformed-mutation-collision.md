> **当前有效收口状态（2026-10-16）：P7-R1 CLOSED / PASS。** 最终固定候选 run-28：146/146 通过，失败/跳过/未执行为 0；十二类攻击面均完成适用路径核查；17 个冻结样本全部找到、实际使用且身份不变（14 条专门配对 Inspector 对照，不代表 17 条全流水线）；独立 bridge 217 项及 D 组 15 cases / 13 结构检查通过。fresh 只读独立复核为 PASS / DONE_FOR_R1，绑定 candidate-index SHA256 `DF12BE1B38BC216C82DDE902D0E55890B873AE56C1E083572525B767B48BBA38`。正式证据：`.ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-28/evidence-index.json`。P7 仍为 in_progress；已停止，未激活 R2 或 P8；不构成发布验收。下方暂停、ACTIVE、run-28 尚未执行等内容保留为历史 checkpoint，由本段取代。

> 当前用户已明确授权本地负责 R1 到完成：取代旧暂停和 ChatGPT planning/review 依赖。普通工程问题自主修复；保留验收、Contract、ABI、历史证据。须完成十二类覆盖核查、实际汇总执行、正式证据和 fresh 本地只读独立复核。R1 完成后停止，不进入 R2/P8。以下旧 checkpoint 为历史记录。

# P7-R1：Malformed、Mutation 与 Collision

> **当前状态（2026-10-15；非追溯 checkpoint）：P7=`in_progress`；R0=`CLOSED`；R1=`IN_PROGRESS`；BX-007 narrow closure=`CLOSED`；R1-A=`REVIEWED / DONE_FOR_NEXT_R1_UNIT`；R1-B=`REVIEWED / DONE_FOR_NEXT_R1_UNIT`；R1-C=`REVIEWED / DONE_FOR_R1_C`；R1-D=`REVIEWED / DONE_FOR_R1_D`；R1 Closeout=`PAUSED_BY_USER before PLAN142; r1Closed=false`。** run-27（BuildId `p7-r1-bx007-run27`）仍只关闭 BX-007；R1-D 为经独立复核的 scratch attack-group closure，R1/P7 正式 closeout 未完成。PLAN142 aggregate discovery has not run; no run-28 is allocated.
> 本 checkpoint 绑定后续 scratch evidence 与独立审查，不回写 run-26/run-27 历史：R1-A 由 PLAN109 `09-execution-index.json`（SHA-256 `43B12D4947C480BC3CF721CD2ECA22B87112496534FD8FA01BECE0C7E7B38AFA`）确认 PLAN105 targeted 20/20、qualification 8/8、独立 validator 97/97、0 findings；R1-B 由 PLAN112 `09-plan112-index.json`（SHA-256 `9E2E0AB9121337A4CC9ADF33E4706F6ADEFB807F9A15C436165AEA27FA130E6F`）确认 qualification 15/15、独立 validator 89/89、0 findings；R1-C 由 PLAN117 result（SHA-256 `732EC504D66AD176371646C35BDC8E1168FA53A797D44872F5E3A3A5EBE4D9A1`）、TRX（SHA-256 `02ADB61D712302347DC0BB38F93C6EFC0E1CB31A2E900A9539E439285ABB82DB`）、PLAN119 self-check/qualification/validator 及 ChatGPT C2C iteration 32 `DONE_FOR_R1_C` review 确认。上述 A/B/C 是 scratch attack-group closure，不是 R1 正式 closeout evidence。
> R1-D reviewed binding（不回写 run-1…run-27）：PLAN137 targeted 25/25，TRX SHA `8534E76CC6303ED5FC0E2E503AF650E76A1C9832311458007B3F35394E4583F4`；PLAN138 cache-bound RealSample 2/2，execution record SHA `AAFCE8C723A76D2CF0DC6AC58A48583C2500EA8C5E7DF8CA671EE64D2285B04F`、TRX SHA `2323ABEA584CC76A534EE65DB1CD5446E0528341FD222EBAA33AD6E46FE9FA8A`、independent evidence record SHA `AA6AF3642ECC58E180DD711EACFD19AA339E23DDF57AACE44BFE56329E08BC0D`；cache 17/17 unchanged；ChatGPT C2C `c2c_df54` iteration 51=`DONE_FOR_R1_D`。该 scratch closure 不等于 R1 formal closeout；run-28 remains unallocated.
> 2026-10-12 所写“R1-A 为下一单元、R1-B/C/D 未开始”是历史 checkpoint，已由后续用户授权的执行顺序和 A/B/C 复核结果取代；旧 PLAN98 仍为 `REJECTED / SUPERSEDED`。后续顺序为 R1-A → R1-B → R1-C → R1-D → bounded R1 aggregate → twelve-row attack matrix → independent R1 closeout review。本路由更新不改变 acceptance criteria、P7 总路线图、九项退出标准、公开 Contract 或既有历史 evidence。

## 1. 基线与权限边界

本细分路线图绑定以下已核对基线：

| 项目 | 基线 |
|---|---|
| 当前分支 / HEAD | `master` / `47df638cbae0c957904348cbf30f63af1e1c564f` |
| P7 Roadmap revision | `317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7` |
| 当前 state | P7 in_progress；R0 CLOSED；R1 IN_PROGRESS；active task 为 `.ai/tasks/P7-R1-malformed-mutation-collision/TASK.md`；BX-007 CLOSED |
| 当前下一步 | 按当前用户要求暂停，尚未执行 PLAN142（ChatGPT C2C iteration 55）。明确恢复后仅执行其一次 aggregate `--ListTests` discovery，随后停下等待 fresh review；当前不跑产品测试、不分配 run-28、不进入 R2/P8。R1/P7 仍 in_progress |
| BX-007 最新正式 evidence / review | `run-27`，BuildId `p7-r1-bx007-run27`；配对 ChatGPT `c2c_df54` iteration 9=`REVIEW / DONE`；只关闭 BX-007 narrow closure |
| R0 正式证据 | `.ai-tmp/evidence/P7/P7-R0-governance-corpus-baseline/run-26/evidence-index.json`，SHA-256 `8A31C7B4715F92DEA473EE8741811601080CED5BF661A73D5B84686818C05C34` |
| R0 closeout 结果 | 同一 run 的 `r0-closeout-result.json`，SHA-256 `7DF21AA46C65C15602976EB1427E411042C7DC23A18B051701D3E75E27390C68` |
| R0 语料 / 攻击面快照 | `corpus-matrix.json`：7 行，SHA-256 `5DC757F9474FA9655D09281CE91DF10A96D3CD21F854C4864469B42001948308`；`attack-surface-matrix.json`：20 行，SHA-256 `46B82B79743FD7DF7A878D6313045958BA3F0DDC601A252C7674DEAEB1DF3AC1` |
| 冻结 RealSamples 清单 | `tests/fixtures/realsamples-manifest.json`，run-26 记录为 11,129 bytes、SHA-256 `7149B13D84A305450704722475A4ED670DACD8D258F305051E54479C603C50DE`；清单中的 17 个 P4-v1 样本是唯一可用 RealSample 集 |

run-26 在当时给出的准确状态是 **“P7-R0 READY — 有界 P7 攻击单元可开始”**。那是 R0 closeout 时点的状态快照：`r0Closed=false`、P7 九项标准均未通过、P7 未完成，且当时 R1 尚未激活或实施。此后，R1 过渡包完成独立审查，active task 已切换到当前 R1 TASK，并开始正式 R1 执行；因此 run-26 的“R1 未激活”只作历史记录，不再是当前指令。run-26 没有执行 build、test 或 discovery；其中出现的测试名和源码身份是基线目录证据，不代表那些测试在 run-26 中运行过。

历史过渡规则：在本路线图最初的过渡草案审查完成前，不得切换 `.ai/state.json` 的 active task、实施 R1 或创建 R1 正式证据目录；当时的文档审查/激活准备也不运行产品命令、不构造变异、不改代码或测试。该前置审查和激活现已完成，当前 state 明确将本 TASK 列为 active，后续工作必须遵循本路线图第 6 节的逐单元计划与正式证据规则。

## 2. R1 目标与范围

R1 为下列 malformed 输入、确定性变异及多协议歧义建立可复现的固定语料，并证明当前生产路径按现行 Contract 失败关闭。既有 `COVERED_EXISTING` 仅说明 R0 找到了局部证据，**不豁免** R1 的组合、确定性、真实调用路径和输出/清理检查。测试名、源码存在、mock/fault injection 或产品自身读取自身输出都不能单独构成通过证据。

只覆盖 R0 攻击矩阵归属 R1 的十二类：

| 攻击面 | R0 分类 | R1 必须补足的确定性组合与结果 |
|---|---|---|
| integer overflow | `COVERED_EXISTING` | 以格式/阶段边界构造加法、乘法、范围终点及有符号/无符号转换边界；拒绝越界，不分配失控的大缓冲区，并检查输出、源身份及残留。 |
| truncation | `COVERED_EXISTING` | 对相关协议结构边界截断，包括头、长度字段、目录、子结构和数据范围；不能只复用 JPEG 单例，记录每个变体的预期拒绝阶段/类别及无输出条件。 |
| duplicate/conflicting metadata | `COVERED_EXISTING` | 覆盖同协议重复、值冲突、跨协议同时声明及不同所有权解释；凡契约无法唯一判定的情况必须拒绝，不能猜测。 |
| namespace collision | `COVERED_EXISTING` | 将错误 namespace、相同 local-name、自定义属性与其他协议标记组合；只有结构和 namespace 共同确立的关系才可认领或修改。 |
| fake markers | `COVERED_EXISTING` | 在不合法层级/位置放置像真标记，并与合法容器层级对照；必须按 JPEG/HEIF/MP4 等真实结构关系判定，不得依赖任意字节命中。 |
| cross-protocol collision | `P7_GAP` | 新建可复现的交错协议标记/元数据冲突语料；结果必须是契约规定的确定性拒绝，或有唯一、结构性依据的所有权。不得静默择一、fallback 或猜协议。 |
| overlapping ranges | `COVERED_EXISTING` | 组合重叠、越界、重复、父子范围不一致以及跨协议范围解释；依当前容器/协议 Contract 拒绝歧义，并检查源与临时/输出文件。 |
| invalid box sizes | `PARTIAL` | 建立独立矩阵：普通 32-bit size、large-size/extended size、协议支持时的 zero-size、被截断 header、size 小于 header、文件边界溢出、父子边界矛盾、与其他范围重叠。按格式规则区分合法形式和损坏形式。 |
| unsupported HEIF layouts | `COVERED_EXISTING` | 分开验证“结构有效但当前实现不支持”和“结构损坏”；记录契约规定的 Stage/Category，拒绝路径不得发布输出，也不得破坏保留内容。 |
| corrupt SEF | `COVERED_EXISTING` | 覆盖损坏目录、总长度/偏移/count 不一致、截断及与重复目录/其他 marker/尾随数据组合；与明确不支持的布局分开断言。 |
| bad pairing | `COVERED_EXISTING` | 覆盖 identity 不匹配、多个冲突候选、跨协议身份混淆；仅相邻、文件名相似或表面兼容不得配对。含糊时失败关闭。 |
| trailing garbage | `P7_GAP` | 对每个相关协议按其容器语义定义有效/无效尾随数据及长度/结构变体；JPEG EOI 后 fake XMP/ICC 不能代表所有格式的尾随数据规则。 |

适用的生产路径要依语料与契约建立可追踪矩阵，覆盖 Inspector，以及相关的 Extract、Clean、Convert 路径。并非每一类变异都需要套用与它无关的操作；但每个省略的组合必须有协议/阶段理由，不能只因现有 filter 不方便而省略。拒绝的 Extract/Clean/Convert 操作必须证明没有发布不完整输出，且清理后无未解释的临时文件或残留。

R1 不承担 clean/extract/convert 的完整真实文件系统故障与 publish 矩阵、任意阶段取消、backend 初始化/选择故障、批量内存/handle/soak、依赖/Portable Core 或 P7 总体 criteria adjudication。这些仍由 P7 总路线图中各自责任单元处理；R1 不得借此扩大到其他单元。保留发现 `EXIFTOOL-EXPORT-FACADE`、`CORE-MAGICK-PACKAGE-PLACEMENT` 和 R0 step 8 backend gaps，不在 R1 顺手修复。

## 3. 固定语料与证据规则

每一条基础输入及其派生变体都必须在语料清单中有稳定 ID，并记录：

1. canonical base/fixture 的路径、来源类别、字节数与 SHA-256；
2. 变异操作 ID、版本、明确参数/边界值及确定性排序；
3. 派生字节数与 SHA-256，以及重复生成是否字节一致；
4. 预期 Stage、Category、结果码/契约结果；
5. 是否必须禁止输出、源对象身份和清理/残留期望；
6. 它覆盖的攻击矩阵行、协议结构依据、实际生产调用路径及测试/独立验证引用。

语料生成使用确定性静态工作区 `.ai-tmp/workspace/P7/R1-malformed-mutation-collision/`。同一输入、变异 ID 和参数必须生成完全相同字节与 hash；禁止时间戳/随机目录名、不可重放随机数或无界尺寸。每个 mutation 都要有限且可解释，不得为触发整数边界而申请巨量内存。

`designs/各个机型测试/` 永远只读。RealSample 仅从 `.ai-tmp/cache/samples/` 读取上表固定清单中的样本，不下载、不替换、不从未批准来源补样本。不得原位变更冻结样本。基于 RealSample 派生的变异属于 `Synthetic/Derived`，不能计成原始 RealSample 执行；Mock/Unit、Synthetic/Derived、未变更 RealSample、IndependentValidation 分开计数。P7/R1 所需未变更样本检查必须明确报告清单包含数、找到数、实际执行数、缺失/跳过数；Roadmap 要求的样本缺失、skip 或 scope/filter 排除均为 FAIL/未完成。

独立验证仅在对应结构确有独立、离线解析依据时使用。ExifTool、ffprobe、MP4Box 或其他参考解析器不得变成生产写入依赖，也不得替代产品在真实 malformed 输入上的失败关闭行为。容器/HEIF 关系证据来自合法 box/item/marker 层级，不能用全文件字符串搜索充当结构证明。

## 4. run-26 绑定的代码与测试基线

以下 25 条身份取自 run-26 的对应证据记录：24 条见 `attack-surface-matrix.json` 的相关攻击行；`tests/LivePhotoBox.Core.Tests/Media/NativeStrictMediaValidationTests.cs` 这一条见 `corpus-matrix.json`，并由 `summary.json` / `postflight.json` 佐证。所有身份都只证明 R0 时观察到的文件身份，不证明运行结果。R1 activation preflight 必须再次核对相关身份与工作区状态。若实施前基线漂移、Roadmap revision/HEAD/TASK 绑定不一致或样本身份不符，停止并重新规划；R1 运行中经授权修改的文件须另列 before/after 身份。

| run-26 路径 | bytes | SHA-256 |
|---|---:|---|
| `LivePhotoBox.Core/Interop/NativeHeifBoxParser.cs` | 5114 | `A8ECEC73A89A507D550DD38BD2015B3230AA74FA3CFA6C39FCC2C1E1A9CAAF02` |
| `LivePhotoBox.Core/Media/Image/ImageConverter.cs` | 47661 | `09D684F8B138E6905A41A975BF68AC60A8DC6C74C3898215B5273D70FD2B5AAC` |
| `LivePhotoBox.Core/Media/Inspection/SourceInspector.cs` | 984 | `F7EA0EB20A5857FD24849FAEBCC60CB196B6767056CCA88968251988A4D4978D` |
| `LivePhotoBox.Core/Protocols/Cleaning/SourceProtocolCleaner.cs` | 138808 | `DE164AE9AF579AE9E577968E768C3EA4F5AEFA84AE4422C14326734BC680E93E` |
| `LivePhotoBox.Core/Services/Protocols/HeifBoxParser.cs` | 19303 | `5C4947706597CF6C486BDE34BF116C9119466D9CB8E25C82A73755B6E2A7ED2C` |
| `LivePhotoBox.Native/src/containers/heif.cpp` | 97106 | `A9E1F97B7BAB6D69900B62B6A431832DB489ECF409CB8ECC6A0723D6E31339E1` |
| `LivePhotoBox.Native/src/containers/isobmff.cpp` | 57047 | `3797387383003FB6B1ADCCF58B340667FBD1C39778D415F691E7A6712B3ECD2B` |
| `LivePhotoBox.Native/src/media/media_extractor.cpp` | 77958 | `56708262E649DDDBF25D563937A8C71AA567C1CED065353961B167EC4979DF69` |
| `LivePhotoBox.Native/src/media/media_inspector.cpp` | 272337 | `28F82F36ACCD55979E26E5266F230A39602BD2CAA34A4111D0F3E55CC6D852F7` |
| `LivePhotoBox.Native/src/media/preservation_observation.cpp` | 120897 | `5B2A07346188E0EFD09B00E077AD23B14F38846DFAB3C22F9DEB3A6743F56039` |
| `LivePhotoBox.Native/src/metadata/jpeg.cpp` | 13219 | `956F5EC3018B99A3CD892A4F313F40F255F14D96EEFE883CA6FD0D240E25CCEE` |
| `LivePhotoBox.Native/src/protocols/apple.cpp` | 84396 | `90E9C2A38E4F1DFA67538498B60D2ABAB66274F2531760F00AB01D353A9EDFA5` |
| `LivePhotoBox.Native/src/protocols/clean/samsung_sef_cleaner.cpp` | 21441 | `62906D2C81D5B6DFC70C4B848F3CCFDEAB5D5CB999F06A8A792DFB3B8816494A` |
| `LivePhotoBox.Native/src/protocols/huawei.cpp` | 9821 | `270028B215B5FA9982C7B5FC7BFC615270ED3306DF50290A9D3E709A5FE80219` |
| `LivePhotoBox.Native/src/protocols/samsung_sef.cpp` | 16014 | `84D29AB96078527CF330EC379987A93A3AE760B3780EDBE6D6CC5F857D735F1E` |
| `LivePhotoBox.Native/src/protocols/vivo_legacy.cpp` | 12264 | `5D483E8D578D0BF94D41B15D2FFA7AD06950699F557F01962A18FBD91650188A` |
| `tests/LivePhotoBox.Core.Tests/HeifBoxParserDifferentialTests.cs` | 12997 | `2B8DB917FAD81C4ACB700E509F572D52D9D02EA2E7018812121958841D204E23` |
| `tests/LivePhotoBox.Core.Tests/Media/ExtractorFailureTests.cs` | 10727 | `3AAF546E46EB41369D596D7815365A1776FC5C32F072AC3AE0AEECEDBEECCBB4` |
| `tests/LivePhotoBox.Core.Tests/Media/HeifCompoundGainMapTests.cs` | 22132 | `DD524550008087E63E1D7DEA64B228FADCB4080F318F279B1390CBEBE118A081` |
| `tests/LivePhotoBox.Core.Tests/Media/ImageConverterTests.cs` | 96247 | `2119125A7C533513E90A0D27A237AC18DDB6B962CA5332B18B102ACB6F5E459C` |
| `tests/LivePhotoBox.Core.Tests/Media/SourceInspectorTests.cs` | 68477 | `79C9CB58439E41875940F98764F8CE68FD8AF487472C2943EAC2287C9E0D652B` |
| `tests/LivePhotoBox.Core.Tests/Media/NativeStrictMediaValidationTests.cs` | 2827 | `CA21C5312ABC823F5A34FA1BF84D612CAB3510B82736727C94E92676F5FC1FAC` |
| `tests/LivePhotoBox.Core.Tests/Protocols/CleanerTrustChainTests.cs` | 163901 | `552C035B1D7315FF8A1121524BC2DE907EC9BA139D6307377D66931AD44BE08E` |
| `tests/LivePhotoBox.Core.Tests/Protocols/NativePreservationObservationAdversarialTests.cs` | 43163 | `55253DC30E70ACC6E0E2091E207FC049AF241F07BD2F71FB69CBF5F76AE7989E` |
| `tests/LivePhotoBox.Core.Tests/SamsungSefDifferentialTests.cs` | 3532 | `4FF24AD92012903D746F413038C8F974E035724A7B3F61F65E48DE85126D047D` |

R0 矩阵中的代表性既有声明包括：`ExtractorFailureTests.Extract_RangeArithmeticOverflow_ThrowsInvalidFacts`；`SourceInspectorTests.Inspect_TruncatedJpeg_ThrowsInvalidArgument`、`Inspect_ConflictingXmpAttributes_ThrowsInvalidArgument`、`Inspect_GoogleV2_DuplicateMotionPhoto_ThrowsInvalidArgument`、`Inspect_DuplicateStandardXmpPacket_FailsClosed`、`Inspect_SamsungCorruptedSef_ThrowsInvalidArgument` 及 pairing tests；`HeifBoxParserDifferentialTests.LocateExifItem_RejectsDuplicatePrimaryCdscRelation`、`LocateExifItem_RejectsUnsupportedOrOutOfRangeIloc`；`HeifCompoundGainMapTests.SyntheticOverlappingExtent_FailsClosed`；`NativePreservationObservationAdversarialTests` 中 fake marker、unsupported HEIF 与尾随数据用例；`SamsungSefDifferentialTests.TryParse_RejectsWrongTotalSizeAndOutOfRangeDirectory`。这些只是待核对的现有测试入口。未来必须验证实际声明、filter discovery 数、执行数、pass/fail/skip/notExecuted 数和直接退出码，并证明测试确实经过目标生产路径；不能只引用名字或源码行号。run-26 标记这些测试并未在 run-26 执行。

## 5. 必须保持的失败关闭不变量

- 不发生 crash、未处理异常、静默成功或模糊结果；契约规定的 Stage/Category/结果必须稳定且可复现。
- 不因 marker、同名属性、文件相邻或首个候选而猜测协议、identity、配对关系或 metadata ownership。
- 拒绝的提取/清理/转换不发布半成品；确认输出不存在或仍是原有对象，并检查 temp/staging/output 残留。
- mutation 前后冻结源身份一致；除显式派生副本外绝不原位写 RealSample 或 canonical fixture。
- 每个可写操作按当前授权/transaction Contract 执行；本阶段不得创建绕过权限门的测试或生产入口。
- Neutral Media / HDR / GainMap / Exif / ICC 与其他非 live 内容按现有 Contract 保留。唯一有界例外见本路线图 §8：仅当结构证明为 Xiaomi `MiCamera:XMPMeta` 的 `subimage` 与 `lenswatermark` 两项时，跨格式转换目标可按用户明确授权省略；同容器 Extract/Clean 必须保留，源不得改写。不得据此泛化丢弃其他 auxiliary 或非 live 内容；失败关闭不能通过过度删除或扩大成功条件实现。
- 结构比较及 preservation 断言必须有可追踪的 before/after 内容/身份依据；mock/fault injection 只能证明状态机，不能冒充真实 parser 输入结果。

## 6. 未来 R1 执行顺序与证据

当前 R1 已激活。每个新的逻辑单元开始前，须由新的有限 PLAN 确认精确命令和范围，再按以下有限单元推进；每个单元均按 PLAN → 实施 → 针对性验证 → 保存真实证据 → EXECUTED → 独立审查。不得用一个无法区分结果的大总命令替代这些步骤。

1. **治理与 preflight：** reconcile；核对当前 Roadmap revision、active R1 TASK、HEAD、run-26 绑定、现有差异和只读样本；运行阶段要求的环境 preflight。漂移即停。
2. **语料 inventory：** 从冻结 manifest 和当前协议/Contract 建立清单，明确每一攻击面、协议、阶段、类别、fixture/base 与期望结果；清单缺项不得进入生成。
3. **确定性变异生成：** 只生成有界派生文件；至少独立重生成并逐条比对字节数/hash，记录 mutation parameters、无输出要求和残留规则。
4. **测试 discovery 验证：** 对实际将运行的 selector/filter 先证明预期测试已声明并被发现；任何必需 filter 为空、错误排除样本或发生 skip 都是 FAIL。
5. **分组攻击运行：** 按整数/截断、metadata/namespace/marker/cross-protocol、range/box/HEIF、SEF/pairing/trailing 等有界组运行针对性测试，分别保存完整退出状态及发现/执行/通过/失败/跳过/未执行计数。
6. **独立结构交叉检查：** 对适用的合法层级、box/item/marker 及 before/after 数据使用独立离线依据复核；独立解析结果不能替代产品拒绝 malformed 数据的运行。
7. **有界 aggregate regression：** 仅运行 R1 与其直接生产路径所需回归范围；记录 scope/filter 实际包含哪些类别。P7 尚未整体关闭，不运行全软件功能总套件。
8. **样本/source postflight：** 核对 17 个冻结样本身份、派生文件归属、源码差异、输出/temp 残留及工作树范围。
9. **证据 closeout：** 先读 `.ai-tmp/evidence/README.md`，在 `.ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-<N>/` create-only 保存正式结果；失败尝试不得覆盖。报告真实命令、完整直接退出状态、实际 filter/discovery/执行计数、源码/样本 before-after、变异 manifest、独立核验、残留检查和偏差。

仅在历史过渡草案期间，不创建上述正式目录、不消耗 `run-1`；该限制只适用于过渡准备期，不再适用于当前 active R1。当前正式证据目录 `run-1` 至 `run-19` 均已存在，不能把其中任何编号视为可复用。未来每次正式执行都必须先按实际目录、索引和计划目标完成 fresh absence preflight，再确定新的未占用 `run-<N>` 并以 create-only 方式写入；不得预先假定下一个编号，不得覆盖或重用失败 run。R1 的最终状态最多可为“R1 campaign unit complete / ready for 独立 R1 审查及后续已授权 P7 单元”，不能声称 P7 criteria 通过、P7 complete 或获得外部 Chief Gate ACCEPT。P7 九项标准只能由 P7 总路线图规定的最终独立 Audit、独立 Verify 和外部 Chief Gate 明确接受来判定。

### R1 停止条件

Roadmap revision、HEAD、state/active TASK、run-26 索引身份或冻结样本基线不符；输入/派生 hash 不稳定；必需测试 filter 发现为空、skip 或未执行；进程 crash；malformed/ambiguous 输入被静默接受或被猜测；源文件或原始样本意外变更；失败操作留下未解释输出/临时文件；生产调用路径未被证明；或有人试图弱化 Roadmap/Contract，任一发生即停止该攻击组并记录真实状态，不得用重跑或改断言掩盖。

## 7. BX-007 中途 BuildId 决策与待办（非验收记录）

2026-10-09，用户针对 P7/R1 Unit 2 的 BX-007 验证明确授权：可为一次有界 P7 验证使用一个新的、固定且确定性的 `VideoSidecarBuildId`，以避免复用历史 `r5-main10-v1` BuildId 的陈旧 raw manifest identity。该授权只适用于 BX-007 当前验证任务，不构成项目默认值、长期构建策略或打包策略变更。

已封存的 main R1 `run-7` 保持 `FAIL_STOP`：适用的 Core.Tests Release build 在 Native pre-build 阶段直接 exit 1，托管测试编译未完成；test discovery、targeted test、Inspector、Extract、Clean 均未执行，TRX 不存在。不得把 `run-7` 重新解释为产品路径通过，也不得覆盖或重用该 run。

失败原因的当前身份事实为：历史 `r5-main10-v1-codec-identity.json` 绑定的 dependency-manifest raw SHA-256 为 `58F3523DBFD51D5219E44C6C08F4FEDE90C15F4F29561AFDEB8927FB40BA3C16`，而当前 pinned manifest 的后续有效 identity 绑定为 `347020781D151A6D4248334098BBBC7B7469E0F92FB13CBBADAD52A0088BB970`。与此同时，共享 codec prefix 中记录的 `libx264.lib` 与 `x265-static.lib` archive identities 仍分别为 `EC0D1948BB0B03E6AAB037BA361AA625269BC8B356A75EA2EDFDBA065E34CD5C` 和 `40FFBDD1B7C47FD0F5F67BD4A5C2A47FFF444BE4844F7C6601EFAF4ED1D513F1`，与既有记录一致。因此不得修改或覆盖历史 `r5-main10-v1` identity 来消除冲突。

本授权保持以下内容全部不变：`LivePhotoBox.Core.csproj` 的项目默认 BuildId、当前 dependency manifest、P5-R5 profile/pins、Native prerequisite 的 fail-closed identity 检查，以及 P7/R1 的全部 Roadmap/Contract 验收语义。后续验证只能通过命令行 MSBuild property 为单次构建指定新 BuildId；不得把新值写成项目默认值，不得跳过或抑制 Native target。正常构建可以在既有构建语义下生成 BuildId 专属 prerequisite/identity/build-record，并生成或复制 Native sidecar 到正常 Release 输出。

该记录本身不授权构建或测试。具体新 BuildId 必须由新的有限执行 PLAN 明确指定，并在执行前证明其所有 BuildId-owned identity、prerequisite 和 build-record 目标均不存在；任何冲突均 fail-stop。

状态更新 — main R1 run-8 / run-9：

run-8 已永久消费并保持其独立审查确认的 FAIL_STOP_BEFORE_DOTNET_BUILD。其失败发生在任何 dotnet 子进程启动之前；直接 build、test discovery、targeted test、Inspector、Extract、Clean、Convert 均未执行，TRX 不存在。不得覆盖、重用或重新解释 run-8。

随后分配的 main R1 run-9 亦已永久消费。其经审查的正式证据位于 .ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-9/；最终 evidence-index.json 为 5,085 bytes、SHA-256 0063BCE959DDFB80E7C4D22788E724F133E48BB6D96AFDFD83A6E5DFDD65E70E，08-postflight.json 为 4,424 bytes、SHA-256 6D00ECB260D2CFD7BE2B55D6C6043D8B90258219D4D7D8FC28FEE061C26F084B。独立审查结果为 REVIEW / DONE。

run-9 的真实停止边界为 FAIL_STOP_BEFORE_CONTROL_GENERATION：唯一 control-generation shell submission 在 PowerShell parse 阶段因脚本语法错误被拒绝，脚本 body execution count 为 0；没有创建 derived control 或 bx007-product-run-9 workspace。直接 dotnet build、test discovery、targeted test、Inspector、Extract、Clean、Convert 和 independent parser invocation 均为 0，托管编译未建立，TRX 不存在。后续五次 postflight/closeout collector 失败只属于证据采集工具失败，均没有执行或重试任何产品、build 或 test 路径。run-9 的 14 个 reached-only payload 已由最终 create-new index 封存，不得修改、覆盖或重试该 run。

因此 BX-007 的 Inspector / Inspector-issued Extract / CleanupPlan-authorized Clean 产品路径仍未验证；BX-007 不得报告为 PASS，Unit 2/R1 亦未完成。

用户曾批准一次性固定 deterministic VideoSidecarBuildId `p7-r1-bx007-run8`。run-8 与 run-9 当时均未启动使用该 ID 的 dotnet / Native build，run-9 postflight 也确认当时尚无该 ID 的 BuildId-owned 输出；这是历史时点事实，已被 run-11 的后续实际使用取代，不能再作为当前授权或再次使用该 ID 的依据。run-11 已使用该 ID 并生成 BuildId-owned 输出。

2026-10-10，用户明确要求：对已授权工作范围内、可逆且仅供测试/开发、经判断不影响产品功能/逻辑、公开 Contract/ABI、用户数据、生产/打包配置、验收或安全边界的选择，Agent 可自行权衡并继续，事后说明决定和影响。按此当前用户指令，R1/BX-007 后续有界验证可由 Agent 自主选择一个与已消费 ID 不同的 fresh、deterministic、command-line-only `VideoSidecarBuildId`，无需逐值再请示；这只适用于本地 test/build 验证，不改变项目默认 BuildId、dependency manifest、P5-R5 profile/pins、Native fail-closed identity checks、生产/打包依赖或 P7/R1 Roadmap/Contract acceptance。

每次使用前仍必须在新有限 PLAN 中指定该 ID，并 fresh-preflight 证明其全部 BuildId-owned identity、prerequisite、minimal-libav、build-log/build-record 目标及相关 wildcard 命中均不存在；任何冲突立即 fail-stop，不得删除、覆盖、改名或复用已有 outputs。PLAN/evidence 必须记录选值、理由及 absence proof，使用后向用户说明实际影响、产生的 outputs、build/test 结果和未完成项。若选择或使用要求改变项目默认、P5-R5 配置、生产/打包依赖、产品功能、Contract/acceptance 或安全边界，则此自主权不适用，须先向用户说明并取得决定。该规则不追溯改变 run-11：run-11 仍为 FAILED、永久消费且不得重试。

run-10 已永久消费，正式证据位于 `.ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-10/`，独立审查结果为 `REVIEW / DONE`。最终 `evidence-index.json` 为 10,199 bytes、SHA-256 `6E04DFA9D4A1DA6210F9FD3ADF1E6F79604E5EC5280A3584A8A88126A4A85A50`；原始 `08-postflight.json` 为 161,874 bytes、SHA-256 `D97FDABE9F781F6FDCF86A31F5B2CC0EC04E8239909498F2F0A62F63E6498B8C`；追加的 `08-postflight-correction.json` 为 2,362 bytes、SHA-256 `5DD0C10FE4785B63141FEFF40321CC0C321AF471A871DD7FEA11DF52C526A926`。原始 postflight 保持不变；追加更正记录了 collector 输入的预期 Roadmap hash 有误，以及重新核对 live identity/reconcile 后的正确治理结果。

run-10 的真实停止边界为 `FAIL_STOP_BEFORE_DOTNET_BUILD`：一次 control helper 调用直接 exit 0，生成两份与 canonical source byte-identical 的 Synthetic/Derived identity control 和 manifest；一次 build-wrapper submission 在 PowerShell parse 阶段因脚本语法错误被拒绝，wrapper body 未执行。直接 dotnet build、test discovery、tests、Inspector、Extract、Clean、Convert 均为 0；没有产品路径结果或 TRX。run-10 不得重试、覆盖或重新解释为 build/test/产品验证通过。

### 历史状态更新 — main R1 run-11（2026-10-09；后续状态见 run-12）

用户于 2026-10-09 明确批准：为一次有界 BX-007 命令行构建使用固定 ID `p7-r1-bx007-run8`。作出该决定的原因是历史项目默认 ID `r5-main10-v1` 绑定的 raw dependency-manifest identity 已过期；本次临时 ID 用来避免覆盖历史身份并验证当前 pinned manifest。该决定仅授权一次有界验证，不改变项目默认值或长期打包策略。

该一次性授权已在 main R1 `run-11` 实际使用：经审查的 build wrapper 只启动了一次 Core.Tests Release/x64 build，命令行覆盖为 `VideoSidecarBuildId=p7-r1-bx007-run8`，direct exit code 为 1，状态为 `FAILED_BUILD_EXIT`。结果文件 `04-build-result.json` 为 2,320 bytes、SHA-256 `1773A6787ED2BD70930144973E35561C0A42BF0DD7D7B328B05D82AB07BF8568`；stdout 为 60,772 bytes、SHA-256 `029DB4975783D74C7529603C806721F74D31D428B59B4A58DEC253D7F3CC9A24`；stderr 为空，SHA-256 为 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`。独立审查确认 Native prerequisite、minimal-libav 和 Native CMake/Release build 已成功到达；失败发生在 Core.Tests C# 编译，`P7R1Bx007ProductPathTests.cs` 中的 `AssertDeclaredWorkspaceFilesOnly` 引用无法解析。

这次失败已经生成真实的 BuildId-owned 输出，以下是已独立确认并封存的结果；必须保留，不得删除、覆盖或继续声称目标不存在：`p7-r1-bx007-run8-codec-identity.json`（1,506 bytes，SHA-256 `409C042B60A6A7885E3A519E009E158EF5718DEBE939A4CB7CBD22E70173A7C3`）、`p7-r1-bx007-run8-libav-prerequisites.json`（844 bytes，SHA-256 `779E626EF8AE0C3DCEC216D49C7E62753701E909129DBB495F356A948EA007A5`）、`minimal-libav-p7-r1-bx007-run8/` build record（5,845 bytes，SHA-256 `698D9A12BC3D6169CD661F299CA2B300A988EEB6AFC15114FA4AFF702A2C8D38`）、`p7-r1-bx007-run8-minimal-libav-initial.log`（71,374 bytes，SHA-256 `FD9C9E58AFB1D493FB891593687F1DC5BC7F1C144582A2BF2A7FECAE4D3F48F1`）、`p7-r1-bx007-run8-vcpkg-codecs-install-1.log`（1,680 bytes，SHA-256 `1AA1A08589FBFCD08C7D3068938E88BC1C4A0348472940320980ED70E012A114`）及 `r5-native-main10-v90-native-Release-build.log`（4,583 bytes，SHA-256 `7778CCAF7EF49BD5FF6DA61CCC05B1E5A864C7172B31DB7A81E1311813D8188E`）。run-11 的最终 postflight/index 已封存完整目录、BuildId-owned outputs 与 P5-R5 的 123-row before/after 身份；minimal-libav tree 也有 deterministic digest 记录。

`run-11` 已永久消费，不得重试；原先的一次性 ID 不能再次用于新 build。后续若在已授权的 P7/R1 BX-007 范围内继续本地 test/build 验证，可按上文 2026-10-10 用户决定自主选择不同的 fresh deterministic CLI-only BuildId，但必须先完成所有 owned destination/wildcard 的 absence preflight，并在新有限 PLAN/evidence 中记录与事后报告；不得复用或覆盖已有输出。项目默认 `r5-main10-v1`、dependency manifest、P5-R5 profile/pins、Native fail-closed identity gate、P7/R1 Roadmap/Contract 验收标准均保持不变。

run-11 的 discovery、测试执行、Inspector、Inspector-issued Extract 和 CleanupPlan-authorized Clean 均未执行，TRX 不存在；Convert 对 BX-007 不适用。失败 closeout 已 create-only 封存：`08-postflight.json` 为 127,462 bytes、SHA-256 `FEE79922143BCD8D770BED1498125C3468756DFD7E37F0ECB3D856FAAF6605E2`；最终 `evidence-index.json` 为 19,586 bytes、SHA-256 `306EEB7FF42C03A2B4ACCA359730BCF41DB54BD968D8CB0FFBB90EDE99DBA099`。该 index 是 run-11 最终 filesystem write，绑定 12 个 pre-index payload；run-11 共 13 个文件。配对 ChatGPT task `c2c_2263` 对失败 closeout 独立审查为 `REVIEW / DONE`。run-11 仍为 `FAILED / INCOMPLETE`，不得重试；BX-007、Unit 2、R1 均未通过或完成。后续如继续新的 BX-007 build attempt，须遵守上文 2026-10-10 决定：fresh CLI-only BuildId、完整 absence preflight、新有限 PLAN/evidence 和事后报告。本文只记录决策和状态，不替代正式 run evidence、验收或执行授权。

### 历史状态更新 — main R1 run-12（2026-10-10；失败构建已封存并独立审查；被 2026-10-12 checkpoint supersede）

run-12 已永久消费，BuildId `p7-r1-bx007-run12` 仅通过一次命令行构建使用，不得重试。该单元仅按已审查 PLAN 对 `P7R1Bx007ProductPathTests.cs` 做一处 test-only assertion-helper 替换；该源码身份已记录，但编译验证没有到达。唯一 planned Release build 确实启动；异步 launcher 未采样到 dotnet 进程的直接退出码，正式结果如实记为 `null`。Native/MSBuild 输出报告 Native child exit 1、FFmpeg make exit 2；构建在 Native FFmpeg prerequisite 的 `libavutil/x86/tx_float.o` 处以 segmentation fault 停止，汇总为 54 warnings / 1 MSBuild error。

Core.Tests 编译、discovery、tests 和 TRX 均未到达/产生；Inspector、Extract、Clean 调用均为 0，Convert 对 BX-007 仍为 N/A。run-12 的失败不得掩盖为 test compile success，也不得重试。BuildId-scoped 输出只到达 codec identity、partial minimal-libav tree 和 initial log；libav prerequisite record 未生成，预期 Native v91 Release build log 未创建。P5-R5 的 123-row before/after 均 unchanged，8 个既有 Release outputs 未变，项目默认仍为 `r5-main10-v1`；protected inputs/settings、Contract 与 acceptance 均未改变。

run-12 失败 closeout 已 create-only 封存：`08-postflight.json` 为 73,614 bytes、SHA-256 `6F35905D50BE9D8E55031182034F844E30518221398E024C05614168C7929961`；最终 `evidence-index.json` 为 9,401 bytes、SHA-256 `C10A975DD46620330F1F9D24055BDCDE511390E3EAD3A9478152D9DA33AC05A0`。index 绑定 10 个 pre-index payload，run-12 共 11 个文件且 index 是最终 filesystem write。配对 ChatGPT task `c2c_2263` 对本次执行 closeout 独立审查为 `REVIEW / DONE`。run-11 的 sealed postflight/index 与 `FAILED / INCOMPLETE` 事实保持原样。

run-12 封存时的历史待办是先取得新 PLAN 再考虑后续构建；后续 Run19 已独立完成结构验证并封存，2026-10-10 用户决定已解除其两项 Xiaomi 元素的 authority blocker。当前实现仍未完成：应先在 P7/R1 内实现精确 MiCamera classification 与同容器 preservation，再单独推进 conversion-only 例外。任何后续 build 仍须由有效有限 PLAN 指定 fresh BuildId 并完成所有目标及 wildcard 的 absence preflight；本状态记录不分配 Run 编号或 BuildId，不替代执行 PLAN。Inspector、Inspector-issued Extract 和 CleanupPlan-authorized Clean 产品路径仍 required-but-unexecuted；BX-007、Unit 2、R1 仍未通过或完成。

本节不是 BX-007 PASS、Unit 2/R1 完成、P7 Audit/Verify 结果或最终 External Chief Gate ACCEPT。

## 8. 2026-10-10 Run19 用户授权决定：仅转换时省略两个 Xiaomi 元素

Run19 的封存索引 `.ai-tmp/evidence/P7/P7-R1-malformed-mutation-collision/run-19/evidence-index.json` 保留当时的 `STRUCTURE_CONFLICTS_WITH_EXISTING_ACCEPTANCE / AUTHORITY_DECISION_REQUIRED` 结果；独立只读审查 `121-run19-independent-review.md` 为 `REVIEW / DONE`，确认了结构事实，但没有替用户决定语义。Run19 目录及索引均为 sealed，不得修改。

2026-10-10，当前用户明确决定：当且仅当 fresh Inspector 与结构化 `MiCamera:XMPMeta` 证明源中的两个已知元素为 `subimage` 和 `lenswatermark`，可在跨格式/跨容器图像转换输出中省略这两项 Xiaomi 私有附件。该授权：

- 只适用于跨格式/跨容器转换；不得扩展到厂商名称、尾随字节、通用 `protocol_tail` 或 `HasAuxiliaryMedia` 布尔值。
- 不授权修改源文件，也不授权在同容器 passthrough、Extract 或 Clean 中删除它们；这些路径必须按真实 before/after 证据保留两项数据。
- 不影响 Google V1 MotionVideo、其他 MiCamera 元素、其他 auxiliary、HDR/GainMap、Exif、ICC 或其他 Neutral Media 内容。身份、范围或 XMP 结构不完整/有歧义时必须 fail closed。
- 不改变已有 preservation 结果模型或公开 ABI。若获准的转换实际省略两项，应如实报告现有语义：`ConversionExecutionTruth.Auxiliary=Lost`、整体 `PreservationOutcome=PartiallyPreserved`；Strict 仍可按现有完整保留规则拒绝，BestEffort 只能在其现有授权/结果规则允许时继续。

此项用户决定解决的是 Run19 的 authority decision blocker，不改变 Run19 的历史结论或索引，不代表实现已符合、BX-007/R1 已通过、P7 已完成，也不修改 P7 九项退出标准。实现仍须由新的有限 PLAN 和真实针对性验证证明。

历史过渡状态（截至 2026-10-10；已由 2026-10-12 checkpoint supersede）：配对 ChatGPT 首轮独立只读审查指出，过渡规则和 `run-1` 初始分配语句必须明确标成历史状态；本次修正已标明 R1 已激活、run-1 至 run-19 目录已存在，并要求未来 run 在 fresh absence preflight 后 create-only 分配。配对 ChatGPT `c2c_a199-continuation` iteration 5 对修正后的 TASK/路线图给出 `REVIEW / DONE`，确认该文档过渡无剩余状态或范围 blocker。下一逻辑单元仍须先取得满足 active TASK 计划要求的有限 P7/R1 PLAN；优先实现并验证精确 MiCamera classification 与同容器 preservation，再单独处理 conversion-only policy。文档审查或单个逻辑单元完成均不代表 BX-007、R1 或 P7 验收通过。

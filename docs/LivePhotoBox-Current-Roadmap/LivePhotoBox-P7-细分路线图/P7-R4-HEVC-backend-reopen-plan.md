# R4 HEVC backend 重开与实施计划

**最终状态（2026-10-18）：R4 CLOSED / PASS / DONE_FOR_R4。** fresh只读独立审查已核验run-6全部3107payloads，零不一致。最终候选v63-r3；[正式收口报告](../../../.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-6/closeout-report.md)。以下施工/失败/待审文字保留历史时点，不覆盖本最终状态。P7仍in_progress，停在R4，不进入R5/P8，无Git发布。

## 当前已完成验证：v63（2026-10-18，等待正式收口审查）

此节覆盖后文旧候选的当前状态；历史失败结果不改写。v63 的正常 Release 打包、387/387 受影响测试（0 skip）、11 条实际视频路线与 5 类拒绝、4 条 chroma-siting 回归、R3 backend truth、Native coded-order/MEBX/static/SEI、CLI、大文件及受限内存拒绝均通过。独立验证覆盖 33 个长测保留产物、3 个大文件产物及 4 条真实视频路线，包含 codec/depth/CICP/HDR/audio/rotation/timing/VFR/container/清理检查。

完整混合长测：3 warmups + 53 measured epochs / 1806.329 秒，随后自然 quiet 60.612 秒；产品子进程及外层采集进程均直接退出 0，无超时。测量包含 742 次 Neutral、212 merge pairs、159 split inputs、106 图像转换、212 视频转换。操作边界 Semaphore 中位数 16，各轮没有按调用累增；quiet handles 545→536、threads 20→14，Semaphore 16→17 为一次晚期变化。

Private Bytes 并未回到进程启动基线：自然边界首末 146.98→281.27 MiB，后半段斜率 +2.23365 MiB/min，必须披露。完整每秒样本后两个五分钟窗的 Private 中位数 604.43→599.99 MiB、峰值 1022.94→1019.08 MiB，Working Set 中位数 639.00→641.89 MiB；自然季度低点约 50–53 MiB，quiet 279.62→279.34 MiB。资源判断基于完整曲线后段收敛及 quiet，不以 forced-GC 数据或任意新阈值通过；有限观察不证明无限运行稳定。

补充合成格式矩阵 `pixel-layout-v63-r6` 14/14 通过：gray / 4:2:2 / 4:4:4 的 8/10-bit 各转 H.264/HEVC 共 12 条真实成功，12-bit 4:2:0 两种目标均 Unsupported 且零输出。x264 固定构建为 X264_BIT_DEPTH=0（8/10），旧工具只假设 8-bit 是验证计划错误，已按真实构建事实修正并保留失败尝试。灰度 H.264 的 raw VUI matrix=2，完整 nclx=1/1/1；复用未改动的既有独立结构/VUI 对账证明有效 CICP 相等，保留 ffprobe 缺字段原文。其他失败尝试分别为开发工具 deps.json 和合成源 nclx/VUI 冲突，均非产品缺陷通过记录。

当前精确候选 `candidate-v63-r3.json` SHA256 `93ECF97A79B0E6F0AABA46287A68F103D966B64CE2EEAB3A6065459D733A1EA5`；Core 修复 NeutralMediaService CleanupPlan 所有权释放，Native resource-v18 不变。完整正式证据尚待 create-only run-6 和 fresh read-only review；此时 R4 仍 OPEN。无 Worker、系统 HEVC 更新、隐藏 fallback、新运行依赖或 Git 发布。完成后止于 R4，不进入 R5/P8。


2026-10-17 当前用户明确批准重开 P4/P5 已冻结 HEVC backend 决策，目标是所有受支持 HEVC 重编码迁到现有 libav，先修 AVIO、8/10-bit、MEBX 集成，再跑完整受影响测试。LOCAL_OWNER，独立只读收口；重复执行子代理优先 GPT-6 Luna max。

## 授权与边界

- COPY/容器 remux 继续由 Native 承担；显式 codec 请求继续真实重编码。
- 输入 H.265/HEVC 或目标 HEVC 的受支持重编码，以及原 HDR/10-bit preservation 路线，执行前唯一选择现有 minimal-libav；不做失败后 backend fallback。
- 普通 H.264→H.264 SDR 暂保留 MF；本轮发现的 MF H264 timing failure 需要诊断并验证实际受影响的生产调用，不能称已通过。
- 可修改现有生产集成、窄化静态构建的8/10-bit配置及相应分发身份；不引入第二套backend/CLI/新codec，不改公开ABI或降低验收。
- 不实施Worker、不修改系统HEVC扩展、不进入R5/P8、不提交或推送。
- 旧 P4/P5 及 R4 run1–3 sealed evidence 保持原样；旧成功只证明旧断言。所有修复候选重新绑定源码、构建、样本、输出身份。

## 有限施工与验证矩阵

| 单元 | 实施 | 必需验证 |
|---|---|---|
| AVIO | 按实际buffer所有权释放input/output AVIO buffer及context | 同进程真实成功/拒绝/取消/失败路径，PrivateBytes/Handle/Semaphore/Thread与自然静置；修复前后对照 |
| 8/10-bit | 同版本x265静态8+10bit能力，真实bitstream与ABI/Native事实一致；保留窄化FFmpeg及版本身份 | 原Apple8 HEVC→H264/HEVC、vivo8 H264→HEVC、Huawei10→HEVC/H264；独立decode、depth/CICP/pixels；无静默升降位深 |
| 多MEBX | 扩展现有Native StageB严格图/引用/时间表/字节移植与复读，拒绝ambiguous/malformed；sidecar仅defer经Native验证的tracks | Apple三轨、Huawei单轨、无metadata；每轨payload/timing/引用/metadata、ID冲突、未知引用、篡改及清理；不丢弃普通元数据 |
| 唯一owner与truth | Native严格源profile与target预分派；Corecanonicaltruth按真实profile/backend/capability报告 | P5R5、R3 backendtruth、missing-sidecar/noMF retry、COPY不变、Malformed/TargetFps/拒绝无输出 |
| 生产完整集成 | 保留已有ownedhandles/transaction/cancel/publish/postvalidation | P2受影响发布/取消/故障；P6消费/merge/split；P4/P5完整必需RealSample矩阵0skip；WindowsRelease/包身份/许可资料 |
| R4收口 | 唯一最终候选boundedaggregate+完整mixed soak与独立像素检查 | ≥既有R4要求的负载/时长/恢复/取消及资源回落；fresh只读review；新create-only正式run，不以历史223/223替代 |

构建/测试失败先诊断，不消耗正式run反复调工具；验证范围按当前P4/P5/R4要求，禁止提前运行全软件suite。新构建profile使用新身份，不重写旧冻结manifest使旧hash变绿。正式验收仍遵守当前Roadmap Gate。

## 当前进度

2026-10-18 更新：以下 v59 等条目是历史施工记录。当前 Native 为 resource-v18（HEVC-only 1..4 worker、x265 frame-threads=1），当前 Core 候选 v63 修复 NeutralMediaService 所拥有 CleanupPlan 未释放造成的 NativeContext 生命周期泄漏。v62 的 383 项及完整视频正确性通过，但资源未收敛，run-5 已封存失败/采集不完整状态。v63 已完成正常 Release 包检查及四个生命周期回归的红绿验证，完整 387 项/视频/资源流水线进行中。R4 仍 OPEN，未分配 run-6；不以旧绿灯或代码修复本身宣称通过。详细当前事实见 P7-R4-resource-diagnosis-followup.md。

- 起点：run3 对比报告，确认MF信号量残留/绿色像素、sidecar AVIO缺失buffer释放、8bit结果与Main10 bitstream不符、多MEBX入口限制。
- AVIO当前buffer释放、固定x2654.3静态8+10bit、Native多MEBX、显式CICP私有ABI1.2和唯一HEVC owner已接入。普通metadata/meta/udta/tapt及真实Huawei逐帧HDR Vivid/静态HDR SEI保留已增加；不以10-bit/CICP单独冒充HDR完整保留。动态HDR转H264当前明确Unsupported，不降级、不重试。
- 当前 Native 候选为 bounded-proof-v14；运行候选为 v59。最终流水线覆盖383项受影响回归、11条实际转码和5个失败类、R3 truth/Native/CLI/大文件、短资格运行与完整混合长测。每轮实际包含四条视频路线，增加普通 H264 MF 对照及8-bit HEVC encode。中间 v58 的383项和11+5矩阵通过，但其后默认构建入口身份发生变化，不充当最终 v59 验收。
- MF对照发现帧尾时间戳及普通metadata丢失。当前修复仅在实际Baseline66/POC2、逐AU连续参考I/P/noMMCO、精确全流end-stamp与一致DTS条件下回填；保留原严格timing gate。所有最终MF输出复制到deny-write owned StageB；MOV需在最终remux后验证metadata/MEBX/timing、清理旧stage后再发布。独立审查发现的问题正在逐项补齐。
- P5独立矩阵历史工具失败保留。质量比较当前使用 codec cropping，保留 SPS coded-padding 裁剪并忽略容器 clap，避免把编码填充行重采样进真实画面；固定公式、timing规则与验收阈值未放宽。MF source90kHz与输出100ns的舍入另用逐画面序号质量比较补充，原时间轴检查仍单独严格执行。
- 正常 Release 构建不带 SkipNativeBuild 已通过；PS5构建入口的哈希模块和JSON序列化兼容性已修复，既有收据未覆盖。正常包 Native/sidecar 与 v59 测试运行 DLL 字节一致，许可文件已核对；该包单独编译的 Core 与 soak Core 分别绑定，不冒称同一二进制。
- R4仍OPEN。剩余：最终候选完整11路线+5失败矩阵、R3 truth/缺依赖、受影响P4/P5/P2/P6聚合与包身份/许可检查、30分钟混合soak/60秒自然静置资源、独立像素/逐帧HDR等正确性、fresh只读收口及新create-only run4。run1–3不改写，不进入R5/P8。

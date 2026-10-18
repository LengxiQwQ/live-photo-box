# R4 HEVC 迁移实施与验证记录

**最终状态（2026-10-18）：R4 CLOSED / PASS / DONE_FOR_R4。** fresh只读独立审查已核验run-6全部3107payloads，零不一致。最终候选v63-r3；[正式收口报告](../../../.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-6/closeout-report.md)。以下施工/失败/待审文字保留历史时点，不覆盖本最终状态。P7仍in_progress，停在R4，不进入R5/P8，无Git发布。

## 当前已完成验证：v63（2026-10-18，等待正式收口审查）

此节覆盖后文旧候选的当前状态；历史失败结果不改写。v63 的正常 Release 打包、387/387 受影响测试（0 skip）、11 条实际视频路线与 5 类拒绝、4 条 chroma-siting 回归、R3 backend truth、Native coded-order/MEBX/static/SEI、CLI、大文件及受限内存拒绝均通过。独立验证覆盖 33 个长测保留产物、3 个大文件产物及 4 条真实视频路线，包含 codec/depth/CICP/HDR/audio/rotation/timing/VFR/container/清理检查。

完整混合长测：3 warmups + 53 measured epochs / 1806.329 秒，随后自然 quiet 60.612 秒；产品子进程及外层采集进程均直接退出 0，无超时。测量包含 742 次 Neutral、212 merge pairs、159 split inputs、106 图像转换、212 视频转换。操作边界 Semaphore 中位数 16，各轮没有按调用累增；quiet handles 545→536、threads 20→14，Semaphore 16→17 为一次晚期变化。

Private Bytes 并未回到进程启动基线：自然边界首末 146.98→281.27 MiB，后半段斜率 +2.23365 MiB/min，必须披露。完整每秒样本后两个五分钟窗的 Private 中位数 604.43→599.99 MiB、峰值 1022.94→1019.08 MiB，Working Set 中位数 639.00→641.89 MiB；自然季度低点约 50–53 MiB，quiet 279.62→279.34 MiB。资源判断基于完整曲线后段收敛及 quiet，不以 forced-GC 数据或任意新阈值通过；有限观察不证明无限运行稳定。

补充合成格式矩阵 `pixel-layout-v63-r6` 14/14 通过：gray / 4:2:2 / 4:4:4 的 8/10-bit 各转 H.264/HEVC 共 12 条真实成功，12-bit 4:2:0 两种目标均 Unsupported 且零输出。x264 固定构建为 X264_BIT_DEPTH=0（8/10），旧工具只假设 8-bit 是验证计划错误，已按真实构建事实修正并保留失败尝试。灰度 H.264 的 raw VUI matrix=2，完整 nclx=1/1/1；复用未改动的既有独立结构/VUI 对账证明有效 CICP 相等，保留 ffprobe 缺字段原文。其他失败尝试分别为开发工具 deps.json 和合成源 nclx/VUI 冲突，均非产品缺陷通过记录。

当前精确候选 `candidate-v63-r3.json` SHA256 `93ECF97A79B0E6F0AABA46287A68F103D966B64CE2EEAB3A6065459D733A1EA5`；Core 修复 NeutralMediaService CleanupPlan 所有权释放，Native resource-v18 不变。完整正式证据尚待 create-only run-6 和 fresh read-only review；此时 R4 仍 OPEN。无 Worker、系统 HEVC 更新、隐藏 fallback、新运行依赖或 Git 发布。完成后止于 R4，不进入 R5/P8。


当前状态：**施工与验证中，R4 OPEN**。本记录不替代正式证据索引或独立收口结论。当前用户已批准重开受影响的 P4/P5 HEVC backend freeze；旧 run1–3 保持原样，不将旧绿灯反向改写成新实现的验收。

最新恢复点：旧 v60/v62 的视频正确性通过，但资源稳定性未证明，已分别封存为 run-4 / run-5 的失败记录；run-5 还明确记录外层超时导致产品进程退出码未观测。2026-10-18 的完整混合堆诊断与独立源码审查找到真实项目缺陷：NeutralMediaService 未释放抽取返回的 CleanupPlan，持续保留 NativeContext lease。v63 已在原所有者处补上 using 释放；4 个真实 Native authority 生命周期回归在旧 v62 全红、修复后全绿，独立审查通过此修复。当前运行 v63 的完整受影响流水线（预期 387 项、11+5 视频矩阵、完整 mixed/quiet），尚不能宣称资源修复或 R4 通过。Native 沿用 resource-v18；具体身份、失败与恢复见 [资源诊断后续记录](P7-R4-resource-diagnosis-followup.md)。以下旧候选结果保留其历史适用范围。

## 最终执行分派的目标

| 请求 | 执行前唯一 backend | 说明 |
|---|---|---|
| Copy / 容器重封装 | 现有 Native ISO-BMFF | 保留原有授权、事务、身份和严格后验检查 |
| 输入 HEVC，或目标 HEVC 的受支持重编码 | 已分发的 minimal-libav C++ sidecar | 不运行 ffmpeg.exe，不依赖系统 HEVC MFT，不在失败后改换 backend |
| HDR / 10-bit preservation-required 重编码 | 同一 minimal-libav sidecar | 真实 bit depth、CICP/range、色彩、HDR metadata、音频、方向和时间轴均须满足原 Contract |
| 普通 SDR H.264→H.264 | MF software | 保留现有非 HEVC owner；需要独立正常对照和最终混合持续负载 |

不新增公开 ABI、第二套 FFmpeg backend 或运行时可执行文件，不实施 Worker、不更新本机 HEVC 扩展。本轮 private sidecar ABI 升为 1.2，以显式传递 Native 验证的源深度/CICP，避免把缺失属性当默认色彩。公共 legacy backend enum 对 sidecar 仍为 Unknown；canonical truth 报告实际 minimal-libav、版本和 selected/actual capability。

## 已实施的修复

- AVIO 销毁先释放 libav 当前拥有的 buffer，再释放 context，覆盖原 buffer 被替换的情形。
- 固定版本 x265 4.3 使用静态 8+10-bit 构建，默认8-bit且实际 API depth 必须精确匹配源深度；保留原固定 FFmpeg n9.0.1/x264、精简静态功能范围和禁用 program/network/shared library 的路线。
- 修正 avcC High 扩展把已解析 SPS 与未赋值输出字段比较的错误；不放宽冲突深度检查。
- Native StageB 严格移植所有受支持 MEBX 轨，核验 ID、cdsc 引用、sample/timing/payload，并拒绝歧义、额外引用、篡改与冲突。
- 复用 Native 模块保留 movie/video 的普通 meta/udta/tapt、裁剪与静态 HDR box，重定位 chunk offsets，读回检查；未知 offset graph 不能以任意复制代替授权保留。
- 真实 Huawei 样本除了 Main10/HLG/CICP，还含 hvcC 静态 HDR SEI 与逐帧 HDR Vivid。现按源 PTS 对应输出 packet 保留已支持的 SEI；预算、未知 provider/NAL、字段/重复帧语义和不可映射 metadata 均 fail closed。HDR Vivid/HDR10+ 转 AVC 的拒绝依据原始 SEI，不依赖可选 AVFrame side data。
- MF 正常对照原来收到 source-frame end timestamps。仅当实际编码 SPS/PPS/AU 证明 Baseline66、progressive/POC2、连续参考 I/P、无重排/MMCO/展示时序 SEI，且整流精确满足源帧尾时间、一致 DTS convention 时回填源起始时间；原严格帧数/interval gate 仍执行。顺序 proof 保留完整 EPB 检查，但仅缓存固定64字节解析前缀，避免复制整张压缩 picture。
- MF 静态 metadata 原来遗漏。最终输出无条件复制到禁止外部写入的 owned StageB，重新检查 profile、timeline、metadata/MEBX；MOV 也在最终 remux 后检查，并在发布前清理旧暂存文件。
- 转换全程固定只读源文件和目录祖先，防止 MF 视频、音频和 metadata 分别读到不同版本。实际打开的目录句柄须是普通目录，不要求 LIST 权限、不依赖 UNC 路径字符串比较；已有源写入句柄时在创建输出前拒绝。
- 独立审查进一步发现 encoder/frame 未显式传递 chroma sample location。center/top-left 派生源分别转 AVC/HEVC 的四条实际产品路线，旧 v59 均变成 left，确认是普通色彩保真缺陷。当前补齐编码器及帧位置属性，并在解码/flush 两条路径拒绝已知位置的流中变化；独立 profile 检查增加该属性。派生回归明确不冒充原始厂商非 left 样本。

## 工程验证与适用性

| 结果 | 实际范围 | 适用限制 |
|---|---|---|
| v55 bounded aggregate 228/228、零跳过 | 包含真实 H.264 VFR 正常对照、8/10-bit 并发、Native 源 writer 占用拒绝 | 后续候选修改需要补最终受影响验证 |
| P5 v55 matrix 11 路线、5 失败类、零跳过 | 独立 codec/depth/CICP/color、音频、rotation、timing/VFR、metadata/HDR、容器和失败清理 | 是该轮候选的工程证据，不是 R4 PASS |
| v56 expanded aggregate 310/310、零跳过 | 新加入完整 R3 image converter 类 | 尚遗漏的受影响 P5R6 consumer 类已由独立审查定位，下一轮补齐 |
| v54 同进程27次+60秒静置 | 8-bit HEVC→HEVC 的 Semaphore=4；MF H.264 正常对照 Semaphore=12，无逐次增加 | Private Bytes 存在 allocator/末次阶跃，须结合最终混合长测判定，不能叫零资源残留 |
| v55 四轮短混合+静置 | 原14 neutral、4 merge、3 split、2 image、2 video负载 | 两条视频迁移后均为libav，不能冒充 MF 持续负载；最终每轮增加 MF H.264 与8-bit HEVC encode |

独立矩阵工具曾出现 clap 自动裁剪导致比较维度不等、误截合法 btrt 尾部零字节、旧缺 sidecar 版本前缀以及旧9路线集合未增至11的失败。这些原始结果保留；修复的是工具对合法结构、明确 profile 和新路线集合的处理，没有修改冻结的比较公式、timing 规则或成功条件。

## 收口前仍必须完成

当前恢复点为 **v60 / Native chroma-v16**。v59 的383项和11+5矩阵均通过后，因上述真实色度位置缺陷进入修复；旧流水线 source guard 停止，未跑长测，不作为修复后的最终证据。v60 正在先验证新增四条派生回归，再运行完整受影响集和长测。正常 Release v60 构建、DLL/许可核对已通过。

2026-10-17 23:15 恢复核对：v60 流水线已完整执行，383/383零跳过、11路线+5失败类、四条非left派生回归、R3/Native/CLI/大文件/资格运行均通过。完整长测有3轮热身、48个measured epochs、1808.086秒有效负载及60.670秒自然静置。独立检查33个保留输出和3个大文件通过；四条视频路线的深度/CICP/chroma location、HDR、音频、rotation、时间轴、容器和像素补充检查通过。

**资源门仍未通过，不能关闭R4。** Semaphore在轮末基本固定16（偶17），handles/threads无逐轮累积；但Private Bytes首/末measured为178.32/361.66MiB，round23–50仍约+3.52MiB/min，quiet为363.05→362.75MiB，未见后半程平台或明显回落。fresh只读审查同意“资源稳定性未证明”；这只确认工作后的commit持续留存，尚未定位为live-allocation leak。完整业务通过不能消除该疑点。

有限诊断正在区分来源：同一v60 driver的8轮逐stage隔离、exact Snapshot的无媒体采样器控制、只读HeapWalk busy/free分类、GC committed以及VirtualQuery private-commit分类。HeapWalk会影响分配时序，不能替代无HeapWalk正式soak；sampler-only的2034次调用约7.5→17.2MiB，不能单独解释原始约183MiB增长。尚未据此修改生产生命周期、换Worker或放宽资源门。新增探针仅位于离线验证工具，未加入运行/打包依赖；run4仍未分配。

1. 最终同一 Core/Native/sidecar 字节候选：完整受影响 P5R6 consumer/映射集成、R3 truth/依赖检查、P4/P5矩阵、CLI/Native回归和包/许可身份。不能把同源但不同字节的 Core 编译产物混称同一 runtime。
2. 最终每轮14 neutral、4 merge、3 split、2 image，以及至少4 video（HEVC8→AVC、HEVC10→HEVC、HEVC8→HEVC、MF H264→H264），3轮热身后持续至少30分钟且12个完整 measured epochs，最后自然静置60秒及真实 post-load cancel/recovery。
3. 独立解码/结构/逐帧 metadata 与像素检查，结合自然资源序列判定；forced-GC 或业务断言不能单独证明资源稳定。
4. 完整构建输入、样本、runtime、独立工具、原始结果和最终输出身份；doctor/sync/verify/reconcile；新 create-only 正式 run4；fresh只读审查。

Worker 是否需要，只能在最终资源和正确性证据后判断。本次用随包 libav 降低系统 HEVC 依赖，不能推导所有机器永不出现 decoder/encoder fault。保持原 GPL/专利与分发边界，既有 notices 和可复现源码/构建资料必须随当前打包路线核对；本记录不声明新增法律批准。

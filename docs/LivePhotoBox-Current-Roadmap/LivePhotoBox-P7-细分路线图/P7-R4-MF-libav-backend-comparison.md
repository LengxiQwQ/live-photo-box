# P7 R4：MF / 现有 libav backend 比较实验与决策建议

日期：2026-10-17。状态：实验完成并发现阻止直接切换的问题；R4 未通过；P4/P5 Hybrid 决策未修改。

## 结论先行

用户提出先比较现有 backend、不要先实现 Worker 的方向可行，而且本次实验确实找到了此前资源诊断之外的正确性问题。**现在既不能继续称 MF 这条路线只有资源问题，也不能直接把 HEVC 全部交给现有 sidecar 就宣布修复。**

1. MF 对原始 SDR HEVC 的真实转码重复成功，但每次保留一个 Semaphore，静置 60 秒不回落；既有真实 Core 产品输出和本次 Native 转码输出还出现明显绿画面。
2. 现有 libav 对相同 HEVC 音视频内容能完成 H.264 转码，Semaphore/Handle 不逐次积累，画面正常，时间、CICP、PCM 内容检查通过。但 Private Bytes 仍有残留趋势，项目自建 AVIO 缓冲区清理缺失是明确的源码问题；尚未用修复前后实验量化其全部影响。
3. 现有 libav 的 SDR HEVC→HEVC 会实际输出 **Main10 / yuv420p10le**，而 ABI 返回深度 8。它不是已验证的普通 8-bit HEVC 保持路线。
4. 原始 Apple MOV 有三条 mebx；当前 sidecar 的 Native-transplant 模式只接受恰好一条，直接调用会 Unsupported。Native 的完整 sidecar 集成还带有 HDR10 专用后置条件，不能只改一个 dispatch 分支接管 SDR。
5. 推荐目标是：保留 Native COPY/remux；经明确重开验证后，把所有涉及 HEVC 解码或编码的**受支持重编码路线**归现有 libav，执行前确定唯一 backend；暂不把所有 codec transcode 一次性迁移。先修复并验证 sidecar 资源/位深/集成问题，再判断是否还需要进程隔离。

本文是比较和提案，不是实施批准。未改生产源码或生产依赖，未实现 Worker，未修改系统 HEVC 扩展，未提交或推送。

## 实验身份与方法

- HEAD：`47df638cbae0c957904348cbf30f63af1e1c564f`，master；继承的未提交修改保留。
- 比较的是已存在的 `driver-v19` 实际 DLL：Native SHA256 `668D647CF94732F3144DCEB627FEC6225ECA471C00C850C76FCD91114627DDAE`，sidecar SHA256 `0C4E4DB63677CF16A8BB04734C1054C010AC9E898BBE522B71E4F017781FB7FB`。
- Core SHA256 `E87971253A909983CA158DA3370CADCAA48A737A59F99D8546F45C0E853ECDE1`；本次前后 205 个生产源码身份和 3 个运行库身份全部一致。
- 同一个新 C++ 测量程序分别加载现有 Native public transcode ABI 或现有 sidecar transcode ABI。没有实现新的 FFmpeg backend，没有调用 ffmpeg.exe。独立 ffprobe 和 PyAV 仅离线读验证。
- 每组 DLL 保持加载，同进程顺序调用；通常 3 次预热加 24 次测量，结束释放 DLL 后静置 60 秒。libav SDR HEVC→HEVC 为 3+6 次；HDR10 既有路线正对照为 3+1 次，不伪称其经过 27 次 soak。
- 记录每次函数返回和文件关闭后的 Handle、Semaphore、Private Bytes、Thread；静置期间逐秒记录。Semaphore 计数借助当前进程自建并立即关闭的探针和 Windows 内部 NtQuerySystemInformation，属于诊断手段，不是产品 API 保证。
- 多组独立进程并发执行，因此时间只作观察，不能据此宣布哪个 backend 更快；CRF23 在 MF 中映射为码率，在 x264/x265 中是 CRF，不能把同一数字当成相同画质预算。
- 结果行中的 status=0 才是转换成功；测量宿主 exit=0 只表示实验执行完毕，失败请求始终保留在 summary，不以宿主退出码冒充成功。

原始 Apple 输入 SHA256 `D47C0E8FC052393E91A6ECB0AA941AFB20758CD244220943E3C06C52BC2DF381`；HEVC Main8、全范围、P3 primaries / BT.709 transfer / SMPTE170M matrix、PCM 44.1kHz mono、含 edit list 和 VFR。61 个压缩视频包，经可见窗口解码为 55 帧。原样本未改。

为隔离三条 mebx 入口限制，制作了明确标记的实验副本：在合法 moov/trak 层级找到 track ID3/4/5，仅把其 type 四字节由 trak 改为 free。不是产品 Neutral 输出，也不是 preservation 通过证据。独立校验确认除此 12 字节外全部相同，音视频包 payload SHA、PTS、DTS、duration、flags、time base 全部相同。MF 和 libav 对该副本进行同条件 MOV 转码。

另一个 MP4Box 重封装副本改变了时间表，随后发生重复 PTS/DTS 错误。该失败保留用于工具资格记录，**不作为原样本或 libav 固有时间错误的证据**。后续主比较采用上述字节保持副本。

## 同进程资源结果

下表为“首个测量调用后 → 最后测量调用后 → 静置末尾”。首个测量调用是含预热的第4次，末个是第27次；18→41对应两者之间23次后续调用，不是把27次调用误算成23次。逐次原始记录另存。Private Bytes 是字节数；没有设置新验收阈值。

| 路线 | 成功请求 | Semaphore | Handle | Private Bytes | Thread | 判断 |
|---|---:|---|---|---|---|---|
| MF 原 Apple HEVC→H.264 MP4 | 27/27 | 18→41→41 | 431→448→254 | 54,321,152→85,352,448→23,453,696 | 39→33→23 | 每次一个 Semaphore 持续保留 |
| MF 原 Apple HEVC→HEVC MP4 | 27/27 | 18→41→41 | 396→429→254 | 52,150,272→115,453,952→14,835,712 | 31→33→22 | 同样保留 |
| MF 等价 AV 副本 HEVC→H.264 MOV | 27/27 | 18→41→41 | 429→447→254 | 54,239,232→70,459,392→20,807,680 | 36→34→22 | 去掉 metadata 不消除保留 |
| libav 等价 AV 副本 HEVC→H.264 MOV | 27/27 | 4→4→4 | 65→65→56 | 7,524,352→10,797,056→6,393,856 | 4→1→1 | 无同类 Semaphore/Handle 累积；内存不能判稳定 |
| libav 等价 AV 副本 HEVC→HEVC MOV | 9/9 | 4→4→4 | 65→65→56 | 7,716,864→11,612,160→8,409,088 | 1→1→1 | 同上，且输出位深错误 |
| libav 既有 H.264 控制→H.264 MP4 | 27/27 | 4→4→4 | 65→65→56 | 7,933,952→11,395,072→7,704,576 | 3→1→1 | 正常业务对照；仍有内存残留 |
| libav vivo H.264→H.264 MP4 | 27/27 | 4→4→4 | 65→65→56 | 6,881,280→14,041,088→10,326,016 | 3→1→1 | AAC/rotation 路线成功；仍需内存修复复验 |

补充：本次重新执行 SDK-only H.264 decode 正常对照 24 次，每次完整 EOF 成功，Semaphore 一直为 10，静置 60 秒仍为 10。它是 decode 对照，不能代替实际转换证据。

MF 对既有 H.264 控制和 vivo H.264 输入的本次转换均被 Native 的 source-frame interval 后置校验拒绝；27 次均失败，Semaphore 保持 14。失败信息为 `OutputValidation: software video encoder timing failed the Native source-frame interval rule.`。这不是正常业务 PASS，也不能据此说 H.264 产品转换已经全部正确。未放宽后置断言。

既有 H.264 控制的 MF 失败路径还出现另一项异常：Private Bytes 从宿主基线1,658,880字节升至末次195,645,440，静置末尾195,383,296，未回到基线。它必须保留为失败路径资源问题，根因未定，不能因为Semaphore不涨就忽略，也不能仅凭Private Bytes推断确定泄漏归属。

### libav 的内存问题属于哪里

项目 `video_sidecar.cpp::free_avio` 当前只调用 `avio_context_free`，而创建 input/output AVIO 时分别自行分配 buffer。FFmpeg 固定 n9.0.1 的实现只释放 context 和 whitelist/blacklist，不释放用户 buffer；官方头文件要求单独释放 buffer。因而项目 wrapper 存在缺失释放的问题，不能把这项也推给 Microsoft 扩展。

来源：[固定版本 avio 实现](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.1/libavformat/aviobuf.c)、[固定版本 avio API](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.1/libavformat/avio.h)。本次没有修改它；缺少修复前后对照，尚不能声称全部 Private Bytes 变化已由此解释。

## 实际视频正确性

| 检查 | MF Apple HEVC 重编码 | libav HEVC→H.264 | libav SDR HEVC→HEVC | libav 既有 HDR10→HEVC 正对照 |
|---|---|---|---|---|
| 真实编码及输出 codec | H264/HEVC 符合请求 | H264 | HEVC | HEVC |
| 位深 | 8-bit | 8-bit | **FAIL：输入8、ABI报8、输出Main10** | 10-bit |
| CICP / range | 标签与源一致 | 与源一致 | 与源一致 | HLG/BT.2020 保持 |
| 可见帧数 / VFR | 55；符合现有一帧容差 | 55；归一化 PTS 最大差0 | 55；最大差0 | 86；最大差0 |
| Audio | PCM→AAC；时长差约4.04ms，在旧规则内 | PCM、44.1k mono、1.9s，拼接字节完全相同 | 同左 | AAC 包哈希顺序和时长一致 |
| Rotation | Apple无旋转，保持 | Apple无旋转，保持 | 同左 | -90°保持；vivo另有旋转控制 |
| 容器 | MP4/MOV检查通过 | MOV及追加MP4请求成功 | MOV | MP4 |
| 像素画面 | **FAIL：明显偏绿** | 肉眼对照正常，RGB观察37.86dB | 图像接近源但位深策略失败，RGB观察39.18dB | RGB观察42.67dB |
| MEBX / Native StageB | 原生产路径实际执行 | **未验证**：raw sidecar StageA，原3轨输入被拒绝 | 同左 | raw StageA，不能冒充完整preservation |
| 临时文件与源身份 | 本次检测无遗留、源未变 | 同左 | 同左 | 同左 |

关键说明：

- 每个重复组只保留首个测量输出和最后输出，独立媒体正确性检查针对这些保留样本，不代表逐份检查27个输出；中间请求状态和资源均有逐次记录。追加libav MP4为3次预热加1次测量，单份保留输出通过结构/时间检查，不能称MP4经过24次soak。

- 像素观察使用 PyAV 19.0.1 自带库解码和 numpy 比较 RGB display values，无自动旋转，在输出尺寸比较。它与既有 P5 冻结的 PSNR/SSIM filter 公式不同，只作独立诊断，不套用新通过阈值，不替代 P5 正式质量验证。
- MF MOV 的两份保留输出都只有约5.77dB，首帧明显绿。进一步复核此前 **真实 Core 产品** `soak-v11/case/retained/codecs/sdr.mp4` 和本次原始样本 MF H264/HEVC 输出，首帧 RGB 均值约 `[8.66,237.37,0.82]`，同样偏绿。因此它不只是新 C++ probe 的现象。根因尚未定位到 MF 解码、像素布局/stride、编码器协商或其他环节；不得断言全部是系统扩展缺陷，也不得断言全部项目代码正确。
- 图片 `pixel-first-frame-comparison.png` 从左到右是源、MF、libav，同一首帧；`mf-product-first-frames.png` 是既有 Core 输出和本次两个 MF 输出。
- PCM 三个包的边界变化导致逐包哈希检查失败，但六份 libav 保留输出的顺序拼接 PCM 数据 SHA256 均为 `1D1D964CDDD1422FE4D187B24DFCC899716638475DB85AD1DBEF61ED41A10D5A`，167,580字节完全相同。原始失败记录保留，另有独立 PCM 证明，未把逐包断言改绿。
- 所有清理观察只覆盖当前宿主请求、文件句柄、自己创建的输出和 TEMP。raw sidecar 没有执行完整产品事务、取消、失败发布、Native MEBX transplant；这些仍需集成后的正式验证。

## 版本、发行与上游审查

当前 sidecar 已随项目分发，实测文件13,855,744字节（约13.21MiB），不是需要新增一个 ffmpeg.exe。现有 Native 为9,145,856字节。复用现有 DLL 不增加第二套运行库，但如果修复 x265 为8/10-bit多库配置，最终包大小必须重新测量，不能承诺零增量。

- FFmpeg n9.0.1，commit `bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa`。
- x264 core165 r3222，commit `b35605ace3ddf7c1a5d67a2eb553f034aef41d55`。
- x2654.3，peeled commit `e9b88125dc21393b3fd8d68e98083bdfb89778a8`。本项目 Main10 overlay 与实际 Main10 输出吻合；FFmpeg encoder 可接受8-bit frame不代表最终8-bit bitstream，必须核对实际 bitstream 和结果字段。
- n9.0.1 changelog 已含 MOV、hvcC、边界和平台修复。x2654.3 release notes 涉及线程池、race、ME、VUI timing 等修复；特定非默认 threaded-ME/ABR-ladder 问题不能未经触发条件核对就说影响本项目，也不能据版本号宣称没有 crash/leak/色彩/时间风险。
- 官方 FFmpeg security 页面列出的 LCL/RSCC/TIFF 相关条目，应与 minimal build 的实际启用组件对照；未启用组件不等于整个 DLL 获得安全证明。x264 上游网页访问受限，本次不能声称已穷尽后续全部问题。正式发行仍须固定源码/SBOM/配置和安全复核。
- 启用并静态链接 x264/x265 的构建不能按“纯 LGPL FFmpeg”分发。须延续项目 GPL 分发资料、精确对应源代码、补丁、配置、构建方式和 notices；专利问题与开源许可分开，现有发行门禁不能因换 backend 被消除。

原始资料：[FFmpeg n9.0.1 changelog](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.1/Changelog)、[FFmpeg security](https://ffmpeg.org/security.html)、[FFmpeg legal](https://ffmpeg.org/legal.html)、[x2654.3 release notes](https://raw.githubusercontent.com/Multicorewareinc/x265/4.3/doc/reST/releasenotes.rst)。

## 三种 backend 范围比较

| 选择 | 好处 | 主要缺口 / 改动 | 建议 |
|---|---|---|---|
| 只转移 SDR HEVC 输入 | 对当前 MF 解码问题最直接，范围小 | 若仅HEVC→H264，H264→HEVC仍依赖Windows HEVC encoder；HEVC→HEVC仍需8-bit修复；不能跳过metadata/timing/事务 | 适合作为第一批实验集成验证，不宜作为最终完整HEVC依赖方案 |
| 所有涉及 HEVC decode/encode 的重编码归 libav | HEVC能力由已分发固定库提供，避免假设Windows安装官方HEVC扩展；HDR10已有路线可复用 | 修复AVIO、真实位深与诊断；通用化HDR后置条件；保护MEBX/metadata；重开SDR/HDR/8/10bit/audio/container/timing矩阵 | **推荐目标，须用户确认重开P4/P5并在修复验证后实施** |
| 更多或全部codec transcode统一libav | 一致的执行和诊断，减少双实现维护 | 当前minimal只包含有限codec/音频复制能力；更大兼容矩阵、音频策略和分发风险；当前H264 MF时间失败值得另查，但不是立即全迁理由 | 当前不推荐一次性扩大 |

任何选择都保持 COPY/remux 为独立 Native 字节保持路线；“所有涉及HEVC”在此特指重新编码，不能把HEVC容器复制也转成有损编码。规划时按输入事实、目标codec/容器、位深和保留要求一次选定唯一 owner；能力不足应提前明确拒绝，不能MF失败后偷偷FFmpeg。

## 是否还需要 Worker

目前没有足够依据把 Worker 当作必须立即实施的修复。迁走 MF HEVC 解码有希望避开已观察到的 Semaphore 残留，当前 libav 的自有 AVIO 问题应先在本进程修正，不应靠退出进程掩盖项目泄漏；Worker 也不会自动修好绿画面、位深提升或 metadata 丢失。

同样不能承诺永远不需要隔离。若修复后仍出现不可回收的 Native 资源、无法可靠取消的编码、第三方崩溃，或最终产品要求恶意媒体崩溃不影响主进程，再基于真实证据评估 Worker。既有8次child-exit探针仅证明退出回收的可行性，不证明IPC、权限、事务、取消、发布已设计完成。

## 推荐下一步及需要重开的范围

1. 用户先确认是否允许**重开 P4/P5 已冻结 Hybrid 决策的受影响部分**，以及目标为“所有受支持 HEVC 重编码归现有libav”。此次实验不是这一批准。
2. 在明确范围内修复现有 sidecar：AVIO buffer 正确释放；8/10-bit encoder配置与实际bitstream一致；结果字段以真实输出为准；必要时拒绝不满足位深策略的请求，不能静默提升。
3. 在既有 Native sidecar StageA/StageB 中做 SDR 通用化，沿用已有 MEBX/authority/事务逻辑。处理多轨输入与合法preservation语义，不能简单丢弃所有metadata，也不能新造第二套backend。
4. 单独定位并修复 MF 绿画面和 H.264 时间校验失败，或以经批准的backend范围消除相关生产入口；无论如何先记录并补齐像素验证，不能用历史223/223结构/流程绿灯覆盖此次失败。
5. 重开 P4 受影响 owner/capability/依赖可用性/打包身份与GPL发行资料；P5重开明确codec reencode、8/10-bit SDR/HDR CICP、像素质量、旋转、VFR/edit list、音频及MOV/MP4、MEBX保留、取消/失败清理/事务验证。未受影响的 Native COPY路线不无理由重写；如公用组件变化则补回归。
6. 用修复后唯一候选重复本次比较和当前阶段要求的真实样本矩阵，再跑R4完整mixed soak、自然回落、批次取消/恢复及独立审查。当前实验不是30分钟R4资源门禁，通过后才决定是否另提Worker。

长期证据：`.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-3/` 保存本次实验数据、工具、输入/输出/运行库身份；旧 run-1/run-2 保持原样。完整结果以 run-3 evidence-index 为准。

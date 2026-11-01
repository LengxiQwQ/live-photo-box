# P9 R5 Samsung HEIC 实验能力方案（待用户决定）

2026-10-27。R5 LOCAL_OWNER 新主核查，当前仅提出方案，未升级产品支持。R4 HEVC决定不外推Samsung。

## 已核实事实

缓存三星JPEG/HEIC原图及哈希见 `.ai-tmp/workspace/P9/target-writers/R5/sample-baseline.json`。原HEIC为合法顶层 `ftyp/mdat/meta/free/mpvd/sefd`，sefd是mpvd的后继兄弟；0x0A30数据是mpv2+BE32绝对视频起点+BE32完整视频长度，0x0A31版本为mpv3。指针指向mpvd负载首字节。SEF每索引12字节，backward offset以SEFH为基准，size是完整tag大小；footer长度不含末尾8字节。JPEG原图没有版本tag，但含独立完整MotionPhoto_Data及普通色彩/时间/相机tag。事实文档的10B/nested/相对指针示意与这份原图不符，不能直接照抄或静默改写。

## 推荐的具体行为

允许R5补齐 Samsung HEIC 的实验结构化生命周期：严格Neutral准备 -> Native写入 -> close/fresh reopen独立Validator -> generic媒体 -> 受保护发布与独立before/after。目标显式选mpv2/mpv3，复用HEIC现有item graph/XMP重排，视频使用已支持的H264/HEVC MP4表示并保留编码字节。输出采用本次原图证实的顶层mpvd加后继sefd、绝对指针；任意假nested/重复mpvd/sefd/shadow/越界/错指针拒绝。普通SEF标签、Exif、ICC、私有数据及辅助媒体按NeutralContract保留，不能靠丢弃信息完成。

旧GUI/CLI选项和adapter本轮不切换；新链保持Experimental，设备NotRun、production preservation NotEvaluated，分别完成有限独立媒体/保留证据。R8 Samsung Gallery和适用跨平台相册实机验收后才允许正式支持/产品切换。没有新增生产依赖，也不自动转码或扩大Fusion/P10范围。若实际媒体/布局不可表示，明确拒绝并记录，不能fallback。

## 另一可选边界

本轮只做Samsung JPEG；HEIC保留明确Unsupported和未完成项。这样R5的HEIC施工目标需等待后续恢复，不能称完整R5已完成。

## 决策

待当前用户选择是否按推荐实施完整实验HEIC链。该询问来自根规则0.2实际产品功能/支持能力变化，以及正式03 R5节旧HEIC Unsupported升级须事先取得决定。普通工程内部ABI选择不再额外询问。

2026-10-27 当前用户明确选择：按推荐实施完整 HEIC 实验链。上述推荐方案已获授权；H264/HEVC保留编码字节、mpv2/mpv3显式目标、顶层mpvd及后继sefd绝对指针、完整独立生命周期；Experimental与R8真机/切换边界不变。

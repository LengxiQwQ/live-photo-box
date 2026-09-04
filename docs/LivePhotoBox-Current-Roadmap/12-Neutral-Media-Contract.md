# Neutral Media Contract — Live Photo Box 中性媒体宪法 V3.2

> **Document role:** Cross-phase architectural contract  
> **Applies to:** P1–P10, all AI agents and maintainers  
> **Authority:** 与 `00-重构总纲-唯一执行路线.md` 一起构成长期架构硬约束。  
> **Change rule:** 只有真实协议/媒体证据证明现有规则不足时才修改；不能为某个 Writer 图方便而放宽。  

# 1. NeutralMediaBundle 的定义

NeutralMediaBundle 不是“随便拆出来的一张图 + 一个视频”。

它代表：

> **已经从来源厂商 Live/Motion Photo 协议中解耦、可被后续任意 Target Writer 消费、并且媒体与保真状态可解释的一组语义资产。**

概念结构：

```text
NeutralMediaBundle
├─ PrimaryImage
├─ MotionVideo?
├─ GainMap? / Auxiliary?
├─ Timing
├─ Media properties
├─ Preservation outcomes
├─ Artifact manifest / hashes
├─ SourceProvenance        # diagnostics/history only
└─ RemovedProtocolFacts    # evidence only
```

# 2. 永久不变量

## N1 — No source live binding

Neutral artifact 不应继续携带会让 Source Inspector 判断为原来源 Live/Motion Photo 的协议绑定。

## N2 — Independent media validity

```text
PrimaryImage
MotionVideo
GainMap / Auxiliary
```

必须在各自语义下结构有效。

## N3 — SourceProvenance is not target authority

Target Writer 不得根据：

```text
bundle.SourceProvenance.Protocol
```

决定目标协议怎么写。

允许它用于：

```text
diagnostics
history
debug
evidence
```

## N4 — Target consumes semantics, not source quirks

Target Writer 消费：

```text
image
video
timing
orientation
HDR/color facts
OutputProfile
```

而不是：

```text
“这个文件以前是 Huawei，所以……”
```

## N5 — Preservation must be truthful

必须能够诚实表达：

```text
Preserved
TranscodedLossless
Reencoded
PartiallyPreserved
DegradedToSdr
DiscardedNotApplicable
Unsupported
```

具体 enum 可以演进，但不能因为“还能打开”就称为 Preserved。

## N6 — No unnecessary re-encode

如果目标要求允许 passthrough/remux，则不应无理由 decode → encode。

## N7 — Orientation has one clear semantic

无论 orientation 来自：

```text
EXIF
HEIF property
video matrix
track metadata
```

Neutral 层必须提供明确语义，避免 Target Writer 回头猜来源私有表示。

## N8 — Timing has one clear semantic

cover/key timestamp、duration、frame/timing 等 Target Writer 所需时间语义应标准化，不要求 Writer 回头解析来源私有字段。

## N9 — Unknown / non-target metadata preservation

### Same-container extraction/cleaning

在不改变媒体表示能力时：

> **未知且未证明属于来源 Live/Motion Photo 协议的数据默认保留。**

只有在：

1. 已证明属于来源 Live/Motion Photo 协议；或
2. 已证明与中性媒体结构有效性冲突

时才允许删除/重写。

### Cross-container / cross-codec conversion

如果目标容器无法表达某个来源 metadata/property：

```text
representable
→ preserve / map according to explicit contract

not representable
→ record explicit preservation loss
→ Strict policy may reject
→ BestEffort may proceed only with truthful outcome
```

禁止通过“默认保留”制造一个实际上无法成立的跨容器承诺。

## N10 — Neutral success is verifiable

任何 Neutral Success 必须有：

```text
post-clean Source Inspector result
+
artifact structural/media validity
+
manifest/evidence
```

## N11 — Auxiliary/HDR representation must not be ambiguous

GainMap/Auxiliary 是**语义资产**，不等于“Writer 应该把这个文件再 append 一次”。

Neutral layer 必须区分：

```text
Embedded representation
vs
Detached working artifact
```

并确保同一 semantic asset 的 ownership/representation 不含糊。

永久规则：

```text
Target Writer 不得因为 bundle.GainMap != null
就无条件再次 append/insert GainMap。
```

如果当前模型不能明确表达：

```text
GainMap 已嵌在 PrimaryImage
还是
GainMap 仅以 detached artifact 存在
```

则 P6 在进入 P9 前必须扩展 Neutral/Manifest contract，使这个状态可明确判断。

同一 semantic GainMap 不得因“embedded + detached working artifact”而在 Target 输出中被重复写入。

# 3. Neutral Pipeline 禁止事项

禁止：

```text
Target Writer parses original source again
Target Writer asks source vendor to choose layout
Cleaner removes unknown metadata for convenience
Converter silently degrades HDR
Extractor guesses range
Neutral pipeline keeps source protocol metadata “反正 writer 不看”
Target Writer treats detached auxiliary artifact as automatic append instruction
```

# 4. Neutral 与格式转换

Neutral 不等于固定格式。

合法 Neutral 可以是：

```text
JPEG + MP4
HEIC + MOV
HEIC + MP4
...
```

媒体格式由：

```text
MediaFormatRequirement
```

决定。

目标协议/媒体组合由：

```text
OutputProfile
= TargetProtocol + MediaFormatRequirement + target options
```

表达。

Neutral 的核心不是 extension，而是：

> **协议解耦 + 媒体有效 + 语义清楚 + preservation 可追踪。**

# 5. GainMap / HDR

GainMap 不是 Live Photo 协议本身。

Cleaner 不得因为清除 MotionPhoto 就误删 HDR 所需 GainMap。

如果目标媒体格式无法保留：

```text
must report degradation
```

不能静默输出 SDR。

GainMap 的：

```text
semantic identity
embedded/detached state
relationship to PrimaryImage
```

必须在 P6/P9 前形成无歧义 contract。

# 6. Manifest

正式 Neutral 输出至少应能够记录：

```text
role
path
byte length
hash
container
codec where applicable
preservation outcome
auxiliary representation/ownership where applicable
```

字段如何落在当前 model 由对应阶段决定，但语义必须可表达。

# 7. Architecture Tests

应防止：

```text
if (SourceProvenance.Protocol == ...)
```

重新出现在 Target Writer correctness logic 中。

也应防止：

```text
GainMap artifact present
→ unconditional append
```

等把 working artifact 误当语义指令的情况。

# 8. 修改本 Contract 的条件

只有：

```text
真实协议样本
设备行为
媒体标准事实
当前模型无法正确表达的实际 requirement
```

可以推动修改。

不能因为：

```text
某个 Writer 这样写更方便
某个 library 这样暴露 API
```

就放松 Neutral 边界。

## AI 执行硬规则

1. **先读当前 HEAD，再动代码。** Roadmap 定义目标、边界和验收，不替代当前实现事实；文档中记录的审计点必须在执行时重新核对。
2. **只执行当前阶段。** 未满足当前 Phase Gate，不提前实现后续阶段，不以“顺手整理”为理由扩大范围。
3. **验证证据必须与风险匹配。** 任何影响媒体产物、协议 correctness、兼容性或 preservation 的 Process，都必须同时具备代码证据、自动化测试和真实媒体样本证据；纯 build/platform/diagnostic infrastructure 使用与其风险匹配的 build smoke、dependency report、fault injection、platform proof 等证据。真实媒体样本不是 P7 才开始。
4. **测试失败必须可定位。** 错误应携带阶段、能力、关键输入事实、失败类别和技术原因；CLI/产品层必须能把核心错误清楚呈现。
5. **Production runtime 不恢复外部媒体 CLI 子进程依赖。** `ffmpeg.exe`、`ffprobe.exe`、`ExifTool.exe`、`jpegtran.exe`、`heif-enc.exe`、`heif-dec.exe`、`magick.exe` 等可以用于研究、测试和独立验证，但不能重新成为正式处理后端。
6. **第三方 C/C++ 媒体库是候选能力，不是预先锁定的架构承诺。** 是否采用、如何裁剪、静态或动态链接、平台 backend 组合等，由 P4 的实际证据决定；不得因为 Roadmap 举例提到某个库就机械引入，也不得为了“全部自研”重复实现成熟 codec。
7. **C# 是 Control Plane；Native 是 result-affecting Data Plane。** 产品级命名、目标目录、队列、UI/CLI orchestration 由 Core 管理；底层临时文件、原子 replace/publish 等事务 primitive 可以保留在 Native，但应置于明确的 platform/filesystem boundary。UI-only 的缩略图、预览、显示缩放和缓存不强制进入 Native。
8. **不猜协议事实，也不猜 Writer 写入位置。** 字符串命中、固定 offset、同文件名、magic number、`hit + N` 只能用于 research/candidate/evidence，不能直接成为 destructive authority 或 production Writer authoritative write location。
9. **源文件默认不原地覆盖。** Destructive/repair/write 流程优先使用新文件或安全临时输出；只有在结构与媒体验证通过后，才进入正式 publish。失败或取消不得留下“看起来成功”的半成品。
10. **所有 Done 都由证据宣布。** 代码存在、API 返回成功、文件能打开、播放器能播放，都不能单独证明阶段完成。
11. **当前 Roadmap 不主动修改用户可见文档。** README、CLI 用户手册、商店/Release 用户说明等，只有用户明确要求时才更新。

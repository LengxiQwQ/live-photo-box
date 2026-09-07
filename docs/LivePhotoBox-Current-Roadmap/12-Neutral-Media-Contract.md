# Neutral Media Contract — Live Photo Box 中性媒体宪法 V4.0

> **Document role:** Cross-phase architectural contract  
> **Applies to:** P1–P10  
> **Authority:** 与 `00-重构总纲-唯一执行路线.md` 一起构成长期架构硬约束。  
> **Change rule:** 只有真实协议/媒体/设备证据证明现有模型不足时才修改；不能为了某个 Writer、某个平台或某个第三方 library 图方便而放宽。

---

# 1. NeutralMediaBundle 的定义

NeutralMediaBundle 代表：

> **已经从来源厂商 Live/Motion Photo 协议中解耦、媒体语义明确、保真状态可追踪，并可被任意合法 Target Writer 消费的一组中性语义资产。**

概念结构：

```text
NeutralMediaBundle
├─ PrimaryImage
├─ MotionVideo?
├─ GainMap? / Auxiliary?
├─ Timing
├─ Orientation
├─ Media properties
├─ Color/HDR facts
├─ Preservation outcomes
├─ Artifact manifest / hashes
├─ SourceProvenance          # diagnostics/history/evidence only
└─ RemovedProtocolFacts      # evidence only
```

Neutral 不是：

```text
“一张图片 + 一个视频”
```

也不是：

```text
“统一转换成 JPEG + MP4”
```

---

# 2. 永久不变量

## N1 — No source live binding

Neutral artifact 不继续携带会让 Source Inspector 判定为原来源 Live/Motion Photo 的 source binding。

---

## N2 — Independent media validity

```text
PrimaryImage
MotionVideo
GainMap / Auxiliary
```

必须在各自语义下结构有效。

---

## N3 — SourceProvenance is not target authority

Target Writer 不得根据：

```text
bundle.SourceProvenance.Protocol
```

决定目标协议布局。

允许用于：

```text
diagnostics
history
debug
evidence
```

---

## N4 — Target consumes semantics, not source quirks

Target Writer 消费：

```text
image
video
timing
orientation
HDR/color facts
auxiliary semantics
OutputProfile
```

而不是：

```text
“因为来源是 Huawei 所以……”
```

---

## N5 — Preservation must be truthful

至少能诚实表达：

```text
Preserved
TranscodedLossless
Reencoded
PartiallyPreserved
DegradedToSdr
DiscardedNotApplicable
Unsupported
```

“文件还能打开”不能称为 Preserved。

---

## N6 — No unnecessary re-encode

目标允许 passthrough/remux 时，不无理由 decode → encode。

---

## N7 — Orientation has one semantic

无论来源表示为：

```text
EXIF orientation
HEIF transform/property
video matrix
track metadata
```

Neutral 都应表达统一的视觉/媒体语义。

Target Writer 不回头猜来源表示。

---

## N8 — Timing has one semantic

cover/key timestamp、duration、frame/timing 等目标 Writer 需要的时间语义统一表达。

---

## N9 — Unknown / non-target metadata preservation

### Same-container extraction/cleaning

未知且未证明属于来源 Live/Motion Photo 协议的数据默认保留。

只有：

```text
proven source protocol residue
or
proven conflict with neutral validity
```

才允许删除/重写。

### Cross-container / cross-codec

```text
representable
→ preserve/map by explicit contract

not representable
→ explicit preservation loss
→ Strict may reject
→ BestEffort may proceed only with truthful outcome
```

---

## N10 — Neutral success is verifiable

Neutral success 必须有：

```text
post-clean Source Inspector result
+
artifact structural/media validity
+
manifest/evidence
```

---

## N11 — Auxiliary/HDR representation is unambiguous

GainMap/Auxiliary 是 semantic asset，不等于“有一个文件就应该 append”。

必须区分：

```text
Embedded
Detached
Embedded + detached working representation of same asset
```

Target Writer 不得因为：

```text
bundle.GainMap != null
```

就自动再次写入。

同一 semantic GainMap 不得重复。

---

## N12 — Platform-neutral semantics

NeutralMediaBundle 的 correctness 不依赖：

```text
Windows
Linux
macOS
browser
Win32 HANDLE
COM
WIC
Media Foundation
POSIX fd
Emscripten virtual path
```

当前 Windows implementation 可以使用 path-backed artifacts，但 Neutral semantic model 不能要求：

```text
artifact must be a Windows file path
```

长期 artifact contract 应能被：

```text
Windows file
POSIX file
memory/blob
WASM/browser-backed data
```

承载。

---

## N13 — Backend-neutral semantics

NeutralMediaBundle 不因：

```text
libjpeg-turbo
libheif
Media Foundation
libav*
WIC
lcms2
```

不同而改变其语义定义。

Backend identity 可以进入：

```text
ExecutionRecord
diagnostics
evidence
```

不能成为：

```text
Writer correctness input
preservation shortcut
protocol authority
```

---

## N14 — Media backend success is not semantic success

例如：

```text
libheif encode success
≠ HEIC target protocol correct

MF transcode success
≠ timing/color/preservation automatically correct

JPEG decoder opens file
≠ metadata preserved
```

所有 semantic outcome 必须由 LivePhotoBox contract 判定。

---

# 3. Neutral Pipeline 禁止事项

禁止：

```text
Target Writer parses original source again
Target Writer branches on source vendor
Cleaner removes unknown metadata for convenience
Converter silently degrades HDR
Extractor guesses range
Neutral keeps source protocol residue “反正 writer 不看”
Target Writer treats detached auxiliary artifact as append instruction
Neutral stores Windows/backend-private objects as semantic authority
Neutral chooses protocol layout based on codec backend
```

---

# 4. Neutral 与媒体格式

合法 Neutral 可以是：

```text
JPEG + MP4
HEIC + MOV
HEIC + MP4
JPEG + MOV
...
```

实际媒体准备由：

```text
MediaFormatRequirement
```

决定。

目标组合：

```text
OutputProfile
= TargetProtocol
+ MediaFormatRequirement
+ target-specific options
```

Neutral 的核心：

> **协议解耦 + 媒体有效 + 语义明确 + preservation 可追踪。**

---

# 5. HDR / GainMap

GainMap 不是 Live Photo 协议本身。

Cleaner 不因删除 MotionPhoto binding 而误删 HDR 所需 GainMap。

目标格式无法保留 HDR 时：

```text
must report degradation
```

不得 silent SDR。

必须表达：

```text
semantic identity
embedded/detached state
relationship to PrimaryImage
metadata model/headroom facts where required
```

---

# 6. Color

Neutral 应区分：

```text
ICC/profile preserved
color transform performed
color metadata mapped
color information lost
```

不能用：

```text
“codec backend 成功”
```

替代 color correctness。

---

# 7. Manifest

正式 artifact manifest 至少能表达：

```text
role
stable artifact identity
byte length
hash
container
codec where applicable
semantic representation
preservation outcome
auxiliary ownership/relationship
```

`path` 可以存在，但不能是唯一 artifact identity/semantic representation。

---

# 8. Architecture Guards

应防止：

```text
if (SourceProvenance.Protocol == ...)
```

重新进入 Target Writer correctness。

也防止：

```text
if (backend == MediaFoundation) ...
```

改变 target protocol semantics。

也防止：

```text
GainMap artifact present
→ unconditional append
```

---

# 9. 修改本 Contract 的条件

允许推动修改：

```text
真实协议样本
真实设备行为
媒体标准事实
当前模型无法表达的真实 requirement
```

不能因为：

```text
某个 library API 比较方便
Windows 路径这样写更快
某个 Writer 想少传几个字段
```

就放松。

全局执行规则见 `00`。

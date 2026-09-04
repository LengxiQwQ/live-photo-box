# P1 — Source Inspector Reliability

> **Status:** CURRENT  
> **Goal:** 让 Source Inspector 成为可信、只读、保守的来源事实入口。  
> **Audit snapshot:** `master @ c99876956c4df59864014d942c1e81921ae289f1`；以下 known targets 只代表该快照，执行时必须重新核对当前 HEAD。  
> **Next:** P2 — Extractor Reliability  

## 1. Contract

Inspector 负责回答：

```text
What protocol?
What container?
Where is primary image?
Where is motion video?
Where are GainMap / auxiliary items?
What is pairing identity?
What protocol-owned metadata exists?
What ranges are confirmed?
What is unsupported / ambiguous / malformed?
```

Inspector 必须只读。

`NonLive` 只能表示：

> 已经积极确认不是受支持的 Live/Motion Photo。

发现协议候选证据但无法确认结构/语义时，应表达：

```text
Unknown
Unsupported
Malformed
或当前 API 能安全表达的明确失败
```

不能用 `NonLive` 吞掉不确定性。

## 2. Recognition Rule

统一思维：

```text
Candidate
→ Bounds
→ Container structure
→ Protocol semantics
→ Cross-check
→ Confirmed
```

以下单独出现都不是 authority：

```text
keyword/string hit
same filename
single metadata field
single offset
ftyp signature
offset + length <= file size
```

False Positive 风险高于 False Negative：

```text
不确定
→ 少支持一个 variant

错误 Confirmed
→ 后续 Extractor/Cleaner 可能 destructive corruption
```

## 3. Protocol Coverage

至少审计当前声称支持的：

```text
Apple
Google MicroVideo V1
Google Motion Photo V2 / Xiaomi
OPPO / OnePlus
vivo X300+
vivo legacy
Samsung JPEG SEF
Samsung HEIC
Huawei / Honor
Normal non-live media
```

## 4. Current Known Audit Targets

以下来自 `c998769...` 快照的实际代码审计。它们不是永恒实现说明，而是 P1 执行时必须重新核对的风险清单。

### Apple dual-file pairing

审计快照仍存在 `same_named_legacy_candidate` 风格 fallback。

永久方向：

```text
same basename
+
Apple-like structures
≠
confirmed pairing
```

正式 pairing authority 应来自可验证的协议 identity（例如 image/video ContentIdentifier）及双方结构有效性。

如果当前 HEAD 已经移除 fallback：

```text
用 regression tests 证明
→ 不重复修改
```

### Google MicroVideo V1

审计快照的确认仍高度依赖：

```text
MicroVideoOffset
+
valid trailing ISO-BMFF range
```

P1 应确认实际协议所需的：

```text
namespace ownership
active state
supported version
strict offset/range
```

避免 stale offset 单独把普通媒体升级成 Confirmed。

### OPPO / OnePlus

审计快照协议入口仍明显依赖：

```text
OpCamera:VideoLength
```

P1 应根据真实样本确认“最小充分 semantic gate”，而不是让单一长度字段成为 protocol authority。

### Google V2 / vivo X300+

重点核对：

```text
XMP namespace scope
Container hierarchy
Primary / MotionPhoto / GainMap semantic ownership
duplicate/conflict
exact media/GainMap range
```

GainMap 不能仅凭开头 magic 或倒推长度宣告有效。

### Unknown / NonLive

确认任何：

```text
protocol-like malformed
unsupported variant
conflicting metadata
unprovable pairing
```

不会 silent fall-through 到 `NonLive`。

### Samsung / Huawei / vivo legacy

已有较强结构 parser 的协议优先做：

```text
mutation
negative
boundary
false-positive
```

证明，而不是因为进入 P1 就无意义重写。

## 5. Shared Structural Reliability

共享 XMP/JPEG/HEIF/ISO-BMFF primitive 应足以证明当前协议真正依赖的结构。

特别是 XMP：

```text
namespace scope
prefix rebinding
parent/child hierarchy
attribute ownership
duplicate/conflict
strict numeric parse
malformed safe rejection
```

目标不是开发通用 XML 框架，而是让 protocol semantics 有真实 hierarchy 依据。

## 6. Tests — 必须在 P1 当场完成

每个协议按风险至少覆盖：

1. 正常真实设备样本；
2. 多设备/多版本样本（可获得时）；
3. synthetic fixtures；
4. truncated / malformed；
5. duplicate/conflicting metadata；
6. false-positive traps；
7. unsupported variant；
8. ordinary non-live media；
9. pairing mismatch（适用时）；
10. source SHA unchanged。

## 7. Error Contract

至少能区分并定位：

```text
protocol candidate found but structure malformed
pairing identity mismatch
media range outside file
unsupported HEIF layout
duplicate/conflicting protocol metadata
candidate evidence insufficient for confirmation
```

不得只返回 generic false。

## 8. Post-clean reuse

P3 Cleaner 完成后继续复用同一个 Source Inspector：

```text
Cleaned artifact
↓
Source Inspector
↓
Neutral / NonLive
```

这不是第二套 Checker，也不能创建第二份来源协议 truth。

## 9. Non-goals

P1 不做：

```text
Extractor redesign
Cleaner destructive expansion
media backend selection
Converter migration
Target Writer
Target Validator
Repair
UI redesign
user-facing docs
```

## 10. Exit Criteria

- 当前支持来源的确认条件有结构/语义证据；
- Unknown / NonLive 真正区分；
- known malformed 不 crash、不误报 Confirmed；
- pairing 不靠文件名 authority；
- false-positive 风险优先被控制；
- Source Inspector 保持只读；
- current real samples 通过；
- P2 可以把 Inspector 的 confirmed ranges/facts 当成可信输入。

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

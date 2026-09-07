# P5 — Converter Reliability

> **Entry:** P4 backend/build decisions frozen and usable.  
> **Goal:** 把图像/视频转换做成真实、可解释、可验证、backend-neutral 的通用媒体能力。  
> **Current acceptance platform:** Windows x64。

---

# 1. Converter Contract

Converter 只依赖：

```text
Neutral media facts
requested MediaFormatRequirement
approved Media Backend contracts
approved Platform/transaction primitives
```

禁止业务 converter 直接把：

```text
WIC
Media Foundation
libheif
libjpeg-turbo
libav*
```

当作产品 contract。

具体 library 只能出现在 backend implementation。

---

# 2. Required Semantics

必须区分：

```text
passthrough
container remux
lossless transform
lossless transcode where meaningful
lossy reencode
color transform
HDR/GainMap conversion
degraded output
unsupported
```

“输出成功”不等于 `Preserved`。

---

# 3. Image Pipeline

覆盖当前真实需要：

```text
JPEG
HEIC
orientation
EXIF/XMP
ICC/color
HDR/GainMap
primary/aux representation
```

默认 backend foundation：

```text
JPEG → libjpeg-turbo
HEIC → libheif + approved HEVC codec backend
ICC transform → lcms2 only if needed
```

## 3.1 Structural truth remains project-owned

Converter 不把：

```text
Exif relocation
XMP ownership
GainMap semantic identity
Apple auxiliary semantics
vendor metadata
```

外包给 codec library。

## 3.2 Metadata/pixel separation

明确：

```text
pixel conversion
vs
container/metadata mapping
```

codec backend 只证明像素/codec 操作成功，不能自动宣告 metadata preserved。

---

# 4. Video Pipeline

覆盖：

```text
MOV ↔ MP4
H264
HEVC
audio
rotation
duration/timing
color metadata
hardware/software backend identity
```

Container/remux 与 codec/transcode 分开。

如果 project-owned ISO-BMFF 能可靠做 passthrough/remux：

```text
不要因为引入 libav* 就强制换掉
```

视频 codec backend 使用 P4 冻结结果：

```text
Media Foundation
or
minimal libav*
or
approved hybrid
```

---

# 5. Backend Selection Rule

同一 requested conversion 如果存在多个 approved backend：

```text
capability compatibility
→ preservation
→ quality
→ stability
→ performance
```

决定。

Backend fallback：

- 必须显式；
- 必须记录原因；
- 不能改变未声明的质量/HDR/preservation contract；
- fallback 后实际结果必须重新验证。

---

# 6. ExecutionRecord

每次 result-affecting conversion 至少能记录：

```text
input container/codec
requested output
operation kind
backend name
backend/library version where useful
hardware/software
codec/profile where applicable
passthrough/remux/reencode
metadata mapping result
ICC result
HDR/GainMap result
audio result
fallback
preservation outcome
degradation
```

---

# 7. HDR / GainMap

正式 HDR/GainMap 转换逻辑进入 Native Data Plane。

禁止长期依赖：

```text
C# Magick.NET pixel math
external heif-enc/heif-dec
silent "HDR failed → plain SDR success"
```

BestEffort 如果允许 SDR fallback：

```text
必须明确 DegradedToSdr
```

Strict policy：

```text
must fail
```

GainMap 的 codec/pixel primitive 可以使用第三方库；GainMap 的语义与 container representation 由 LivePhotoBox 掌握。

---

# 8. Color

如果只是 ICC preserve：

```text
copy/map bytes according to container contract
```

如果实际执行色彩转换：

```text
approved color backend
→ lcms2 by default
```

不得：

```text
Display P3 input
→ blindly relabel sRGB without transform
```

---

# 9. Portability Rule

P5 的 converter orchestration 不包含：

```text
#ifdef _WIN32 protocol behavior
if Windows then different preservation semantics
Windows-only output facts
```

允许：

```text
same converter contract
→ Windows backend implementation
```

未来平台只替 backend。

---

# 10. Exit Criteria

- 每种公开 conversion path 有明确语义；
- backend identity 可诊断；
- passthrough/remux/reencode 区分真实；
- HDR/color/audio/preservation 不静默降级；
- approved Windows backend 覆盖产品需要；
- 无 production external CLI；
- Converter 不直接拥有平台 API；
- P6 可把 Converter 当作稳定通用能力。

全局执行规则见 `00`。

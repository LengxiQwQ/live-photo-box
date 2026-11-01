# P4-R5 — HEIF / HEIC / HDR-GainMap Native Foundation

> **性质：** P4 最大的图像基础设施轮。  
> **目标：** 建立 libheif + approved HEVC codec 的正式 HEIC backend foundation，并给 P5 提供 HDR/GainMap Native 处理所需的像素/auxiliary 基础；同时冻结 WIC 与 lcms2 的 P4 policy。

---

## 1. Ownership 边界

必须保持：

```text
LivePhotoBox project-owned:
HEIF structural truth
item / extent / reference semantics
Exif / XMP placement semantics
Apple / Huawei / Samsung / vendor rules
GainMap semantic identity
target protocol structure

libheif:
generic HEIF codec gateway
image decode / encode
generic image item codec plumbing
```

**libheif 的 parser/metadata 结果可以作为 codec evidence，但不能成为 vendor protocol authority。**

---

## 2. 默认 codec stack

P4 V4.0 已拍板的默认方向：

```text
Decode:
libheif → libde265

Encode:
libheif → x265
```

Kvazaar：

```text
benchmark / fallback candidate
```

只有真实 compatibility / correctness / distribution evidence 才允许改变默认方向。

---

## 3. 本轮要建立的能力

至少形成：

```text
HEIC primary image decode
HEIC primary image encode
primary pixel access
auxiliary image discovery/access
auxiliary image encode capability where needed
10-bit/HDR-capable pixel path where applicable
color/profile facts transport
portable GainMap processing host
```

这里的“GainMap host”是 P5 的基础设施，不代表本轮就把所有 GainMap conversion semantics 完成。

P5 仍负责：

```text
正式 HDR/GainMap conversion semantics
preservation/degradation decision
cross-format mapping policy
```

---

## 4. HDR / GainMap 不允许静默退化

本轮必须确保 architecture 能区分：

```text
HDR/GainMap capability available
unsupported
failed
degraded
```

不能形成：

```text
HEIC/HDR 处理失败
→ 偷偷输出普通 SDR/JPEG
→ 仍报告 success/preserved
```

所有会改变正式 HDR/GainMap 产物的长期逻辑应走 Native Data Plane。

C# Magick.NET pixel math、external-tool-era helper assumptions 不能成为未来正式 result-affecting foundation。

---

## 5. x265 分发 / License / Patent 记录

必须分别记录：

### Software license

```text
x265 software license compatibility
source/binary redistribution obligations
仓库 GPLv3 下的实际关系
```

### HEVC patent / distribution

```text
HEVC patent/licensing consideration
Microsoft Store / binary distribution consideration
其他正式发布渠道约束
```

这两个问题不能混为一谈。

如果出现真实 distribution blocker，再评估：

```text
Kvazaar
platform encoder
其他 approved encoder
```

不能因为理论担忧先牺牲真实兼容性。

---

## 6. Minimal libheif Build

P4 不是把完整 multimedia universe 打包进来。

应根据 Live Photo Box 当前真实需求尽量关闭：

```text
unused codecs
examples / CLI tools
unused plugins
AVIF/JPEG2000/VVC 等无产品需求能力
其他未调用 feature
```

每个最终 runtime binary / plugin 都要能说明用途。

---

## 7. lcms2 决策

本轮根据真实 image/HDR path 决定：

```text
是否真的发生 ICC A → pixel transform → ICC B
```

如果只是：

```text
ICC bytes preserve
```

则不应为了形式主义引入 lcms2。

最终给出：

```text
lcms2 required
或
lcms2 not shipped in current P4
```

并附上实际 path 证据。

---

## 8. WIC Policy 冻结

结合 R4 JPEG 与本轮 HEIC 结果，决定：

```text
还有没有 result-affecting WIC image path 值得长期保留？
```

允许保留的理由必须是：

```text
compatibility
performance
Windows system integration
特殊 Windows-only capability
```

而不是：

```text
旧代码已经写好了
```

UI thumbnail/preview 不受该限制。

---

## 9. RealSample / Compatibility 证明

至少对项目拥有的真实 HEIC/HEIF 样本覆盖：

```text
Apple
Huawei/Honor
Samsung
其他真实 HEIC 来源
带 auxiliary / HDR / GainMap 的样本（如 corpus 中存在）
普通非 live HEIC
```

关注：

```text
primary item
Exif/XMP placement
item references
auxiliary relationships
10-bit/HDR
orientation
ICC/color
vendor-private structure
malformed/edge cases
```

generic libheif 能解码，不等于 Live Photo Box vendor semantics 正确。

---

## 10. 本轮应完成的结果

- libheif/libde265/x265 进入 canonical dependency/build graph；
- HEIC backend 位于 Media Backend 边界后；
- project-owned HEIF/vendor truth 保持权威；
- HEIC primary/auxiliary codec foundation 可用；
- HDR/GainMap Native host 足以让 P5 实现正式 semantics；
- x265 software-license 与 HEVC distribution/patent 记录完成；
- libheif feature set 尽量最小化；
- lcms2 shipping decision 冻结；
- WIC result-affecting policy 冻结；
- production 不依赖 heif-enc/heif-dec 或 Magick.NET 作为 silent result-affecting fallback。

---

## 11. 验收标准

全部满足才可进入 R6：

1. libheif + default HEVC decode/encode stack clean integration；
2. 真实 HEIC primary decode 成功；
3. encode 产物经独立 decoder/structural validation 合法；
4. auxiliary image 能力有真实或可信 fixture 证明；
5. 10-bit/HDR-capable path 对适用样本不被无声降成 SDR；
6. project-owned HEIF/vendor parser 仍是 protocol authority；
7. GainMap/aux ownership 不出现 embedded/detached/materialized 重复或歧义；
8. x265 license/distribution/patent record 完成；
9. minimal feature/runtime set 有用途说明；
10. lcms2 是否需要已有明确结论；
11. WIC result-affecting policy 已明确；
12. RealSamples 没有被 skip；
13. production external HEIF CLI 不再是 fallback；
14. failure/degradation 能被上层识别，而不是统一成 success。

---

## 12. 以下情况不得通过

任一出现即 `R5 BLOCKED`：

- libheif 成为 vendor protocol authority；
- 只用 libheif “能打开”证明 Apple/Huawei/Samsung semantics；
- GainMap 被丢失却仍标记 preserved；
- 10-bit/HDR 被静默转 8-bit/SDR；
- x265 进入正式包但没有分发记录；
- 为了未来可能性把完整 libheif codec/plugin universe 打包；
- C# Magick.NET 仍是正式 HDR pixel fallback；
- heif-enc/heif-dec 回到 production subprocess；
- RealSample 缺失却用 synthetic 宣布兼容；
- WIC/lcms2 一直“暂时不决定”，把架构选择拖进 P5。

---

## 13. 验证与测试

### Codec proof

```text
primary decode
primary encode
auxiliary access/encode foundation
10-bit/HDR path
orientation/color facts
malformed input
large media behavior
```

### Structural/preservation proof

由 project-owned parser + 独立验证检查：

```text
HEIF item graph
Exif/XMP
references
auxiliary identity
vendor-private non-target data
color/HDR facts
```

### Regression

继续覆盖：

- P1–P3 涉及 HEIC 的 Inspector/Extractor/Cleaner；
- R2 platform/transaction；
- R3 canonical build；
- R4 JPEG 同容器/交叉基础不被破坏。

本轮通过的核心含义：

> P5 已有可信 HEIC/HDR Native foundation，并且 image backend 的主要技术选择不再需要重新争论。

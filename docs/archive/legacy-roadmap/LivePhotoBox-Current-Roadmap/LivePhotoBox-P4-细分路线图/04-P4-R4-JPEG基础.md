# P4-R4 — JPEG Backend Foundation / libjpeg-turbo

> **性质：** Native image backend foundation。  
> **目标：** 建立正式 JPEG codec/lossless-transform backend，同时继续由 Live Photo Box 掌握 JPEG/Exif/XMP/MakerNote/Ultra HDR/vendor structural truth。

---

## 1. Ownership 边界

必须保持：

```text
LivePhotoBox project-owned:
JPEG marker structure
APP1
EXIF
XMP
MakerNote
MPF / Ultra HDR / vendor semantics
metadata mapping/preservation truth

libjpeg-turbo:
pixel decode
pixel encode
lossless DCT transform where applicable
```

不能因为引入 codec library，就把协议/metadata authority 交给第三方库。

---

## 2. 本轮要建立的能力

JPEG backend foundation 至少能支撑：

```text
decode common production JPEG
encode device-compatible JPEG
quality control
grayscale / RGB handling
lossless rotate / flip where applicable
orientation workflow integration
large image / memory behavior
metadata detach → pixel operation → safe reattach contract
```

重点是“基础能力真实可用”，而不是在本轮把 P5 所有跨容器 Converter semantics 一次做完。

---

## 3. WIC / legacy JPEG 的迁移方向

P4 总路线默认：

```text
JPEG result-affecting codec → libjpeg-turbo
```

因此本轮应识别并收口：

```text
WIC JPEG codec dependency
jpegtran.exe-era capability
Magick.NET JPEG codec usage
```

UI preview/thumbnail 不属于本轮强制迁移范围。

如果某个 result-affecting WIC JPEG path 必须暂时保留，必须有真实兼容/性能/系统能力证据，并进入 R5/R7 的 WIC policy，而不能只是“旧代码已经能跑”。

---

## 4. Metadata / Preservation 原则

codec 成功不等于 preservation success。

JPEG pixel backend 完成操作后，仍需由 project-owned logic 判断：

```text
Exif 是否正确保留/映射
XMP 是否保留
MakerNote 是否被无意重写
orientation 是否重复应用
ICC 是否只是保留还是发生 transform
GainMap/Ultra HDR carrier 是否被破坏
unknown/non-target APP data 是否丢失
```

本轮不能因为“图能打开”就称为 preserved。

---

## 5. RealSample 证明

至少使用项目已有的代表性 JPEG 实况照片/普通媒体样本，覆盖能取得的：

```text
Apple
Google / Xiaomi
OPPO / OnePlus
vivo
Samsung
Huawei / Honor
ordinary non-live JPEG
```

并覆盖典型差异：

```text
Exif orientation
ICC/wide color where available
large images
grayscale/RGB where available
vendor metadata
GainMap/Ultra HDR carrier where available
```

若某类 Roadmap 要求的重要真实样本缺失，应明确 blocker/coverage gap，不能用合成样本冒充完成。

---

## 6. 本轮应完成的结果

- libjpeg-turbo 由 canonical CMake/vcpkg graph 管理；
- JPEG codec implementation 位于 Media Backend 边界之后；
- protocol/business converter 不把 libjpeg API 当产品 contract；
- lossless transform foundation 可用；
- metadata/pixel ownership 明确；
- result-affecting JPEG legacy path 已替换或有证据化剩余清单；
- external `jpegtran.exe` 不成为 production fallback；
- runtime capability 能至少识别 JPEG backend availability/version，为 R7 完整 capability identity 打基础。

---

## 7. 验收标准

全部满足才可进入 R5：

1. libjpeg-turbo clean build / runtime integration 可重复；
2. common real JPEG decode 通过；
3. encode 产物可由独立 decoder/reference 正确打开；
4. quality/format 基本控制正确；
5. lossless transform 对适用输入没有无理由重编码；
6. orientation workflow 不出现双旋转/错误视觉语义；
7. metadata reattachment/preservation 由 project-owned logic 控制；
8. unknown/non-target metadata 不因 codec migration 被系统性丢弃；
9. 真实样本覆盖已执行，不只 synthetic；
10. 大图/内存行为无明显退化或无界内存设计；
11. production 不依赖 `jpegtran.exe`；
12. WIC JPEG result-affecting 剩余路径有明确证据与后续决策位置。

---

## 8. 以下情况不得通过

任一出现即 `R4 BLOCKED`：

- 只证明 libjpeg-turbo demo 能 decode/encode；
- 没有走 Live Photo Box 实际 backend/data flow；
- 编码后图能打开，就直接宣告 metadata/HDR preserved；
- MakerNote/XMP/unknown APP 段被第三方 encoder 默默丢掉；
- lossless transform 实际走 decode→encode；
- production 在失败时静默回退 jpegtran/Magick/WIC 并改变 contract；
- RealSamples 被 skip 或只跑 synthetic；
- JPEG library API 泄漏到上层产品 contract；
- 为了少写代码，让 libjpeg-turbo 取代 project-owned JPEG structural parser。

---

## 9. 验证与测试

### 功能验证

```text
decode
encode
lossless transform
quality
orientation
grayscale/RGB
large input
malformed input failure
```

### Preservation 验证

对适合样本做 before/after：

```text
marker/container structure
Exif/XMP/MakerNote
ICC bytes/semantics
unknown non-target data
GainMap/Ultra HDR carrier where applicable
```

### Independent validation

至少使用一个不依赖“同一个 Live Photo Box 读回自己产物”的独立 decoder/inspection 路径证明产物合法。

外部工具可用于**离线验证**，但不能成为 production writer。

本轮通过的核心含义：

> P5 已经有一个可信的 JPEG 媒体 backend foundation，可以讨论 Converter semantics，而不需要再决定 JPEG 用什么库。

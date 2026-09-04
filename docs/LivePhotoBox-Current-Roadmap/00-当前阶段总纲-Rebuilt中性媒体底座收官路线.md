# LivePhotoBox 当前阶段总纲：Rebuilt 中性媒体底座收官路线

> **Audience:** AI Coding Agents / Codex / Claude Code / GPT Agents / maintainers  
> **Document type:** Current execution authority / engineering roadmap / acceptance specification  
> **Priority:** P0 project guidance for the current refactor stage  
> **Status:** Active  
> **Scope:** Rebuilt-only runtime、来源协议检查/提取/清洗、媒体转换、中性媒体管线、Split/Merge 前置基础  
> **Explicitly deferred:** Repair 重构、目标厂商协议 Writer 扩展
>
> 本文不是项目介绍，也不是历史总结。它定义 **LivePhotoBox 当前阶段到底要做什么、什么暂时不做、工程边界在哪里、怎样判断完成、AI 应按什么顺序推进**。
>
> 后续任何 AI 进入仓库工作前，应优先阅读本文，再读取当前 HEAD、真实代码、测试、协议分析文档与真实样本。**禁止仅根据本文或其他 AI 的交接总结推断某项能力已经完成；所有“已完成”结论都必须回到当前代码与测试证据验证。**

---

# 0. 文档优先级与冲突处理

当前 Roadmap 目录中存在早期阶段文档，例如 Legacy/Rebuilt 后端分流、来源协议清理、自动化测试等。

这些文档仍可作为：

- 历史设计依据；
- 专项实现细节；
- 协议语义参考；
- 测试路线参考。

但如果它们与本文在以下方面发生冲突：

- 是否保留 Legacy 作为正式运行时后端；
- 是否允许外部工具；
- 是否允许自动 fallback；
- 当前优先开发目标协议 Writer 还是来源中性化底座；
- Repair 是否当前推进；

**以本文为当前阶段最高优先级。**

尤其：

> 过去“Rebuilt / Legacy 可切换”的路线已经不再是目标架构。
>
> 当前目标是 **Rebuilt-only production runtime**。

---

# 1. 当前阶段一句话目标

当前 LivePhotoBox 最重要的事情不是把 Apple / Samsung / Huawei / vivo / Google / OPPO 等目标协议全部重新写出来。

当前首要工程目标是：

> **建立一个可信的来源媒体理解与中性化引擎，使 LivePhotoBox 能够安全、准确、可验证地识别、拆解、提取、清洗、检查和转换所有已支持来源 Live Photo / Motion Photo。**

必须做到：

- 知道文件里面真实有什么；
- 知道图片在哪里；
- 知道视频在哪里；
- 知道 GainMap / auxiliary image 在哪里；
- 知道哪些 metadata 属于 Live Photo 协议；
- 知道哪些 metadata 与 Live Photo 无关，绝对不能误删；
- 知道哪些 range 是结构确认的；
- 知道哪些只是 candidate；
- 知道当前解析器不支持什么；
- 在无法确认时安全失败；
- 永远不依赖猜测 offset 继续 destructive operation；
- 永远不因为“播放器还能打开”就认为提取或清洗正确。

最终形成稳定底座：

```text
Vendor Live Photo / Motion Photo Source
                ↓
         Reliable Inspector
                ↓
          Exact Extractor
                ↓
           Safe Cleaner
                ↓
       Reliable Converter
                ↓
        NeutralMediaBundle
```

在这条链路达到可信、稳定、可验证之前：

> **不要把主要开发资源投入 Target Protocol Writer。**

---

# 2. 当前阶段最重要的边界：来源协议 ≠ 目标协议 Writer

后续 AI 必须区分两个完全不同的“协议实现”概念。

## 2.1 当前必须实现和加强：来源协议理解

为了正确处理真实来源文件，当前必须继续理解并维护各厂商来源协议，例如：

- Apple Live Photo；
- Google Motion Photo V1 / V2；
- Xiaomi；
- OPPO / OnePlus；
- vivo legacy；
- vivo 新格式；
- Samsung JPEG SEF；
- Samsung HEIC；
- Huawei / Honor；
- 其他已在项目中明确支持的来源格式。

这里的“协议实现”指：

```text
Inspect
Extract
Clean / Neutralize
Validate
```

这些是当前 P0-P3 工作。

## 2.2 当前暂缓：目标协议生成

下面这些属于 **Target Writer**：

```text
NeutralMediaBundle → Apple Live Photo
NeutralMediaBundle → Samsung Motion Photo
NeutralMediaBundle → Huawei Moving Photo
NeutralMediaBundle → vivo Live Photo
NeutralMediaBundle → Google Motion Photo
NeutralMediaBundle → OPPO / OnePlus
...
```

这些全部放在当前基础阶段之后。

已有 Writer 不要求现在删除，但：

- 不以 Writer coverage 作为当前主要 KPI；
- 不新增大量 Writer variant；
- 不因为 Writer 需求污染 Inspector / Cleaner / NeutralMediaBundle 设计；
- 只修影响当前基础链路、安全性、构建或已有回归的必要问题。

---

# 3. 目标架构：Rebuilt Only

当前正式产品路线：

```text
UI / CLI
   ↓
C# Core Orchestration
   ↓
LivePhotoBox.Native (C++)
   ↓
Container / Metadata / Media Engine
```

正式运行时只允许 Rebuilt。

## 3.1 不再允许的生产设计

后续应移除或使其彻底退出 production runtime：

- Legacy backend selector；
- Legacy runtime backend；
- Legacy automatic fallback；
- Rebuilt 失败后静默调用旧实现；
- ExternalToolLocator；
- FFmpeg executable runtime dependency；
- ffprobe executable runtime dependency；
- ExifTool executable runtime dependency；
- jpegtran executable runtime dependency；
- heif-enc / heif-dec executable runtime dependency；
- `Tools/` / `Tools/bak/` 中作为产品运行依赖的第三方 exe；
- package/release 中复制这些工具的逻辑；
- UI / CLI 中允许用户切换回旧后端的正式入口。

如果 Native/Rebuilt 当前没有某能力：

```text
Unsupported / NotSupported / Explicit Failure
```

而不是：

```text
Fallback to Legacy
```

## 3.2 Legacy 历史代码的处理

Legacy 不再是产品能力来源。

若旧实现仍有参考价值，可作为：

- Git 历史；
- 测试 oracle；
- differential/reference implementation；
- 逆向研究参考。

但必须满足：

> **不会被正式 GUI / CLI / Core runtime 调用。**

若保留参考代码会持续产生维护歧义，优先移除正式源码中的 Legacy 实现，依靠 Git 历史和必要测试 fixture 保存历史知识。

---

# 4. C# / C++ 职责边界

## 4.1 C# Core 负责

- UI / CLI 请求编排；
- 参数模型；
- workspace；
- cancellation；
- progress；
- result mapping；
- error mapping；
- 文件生命周期；
- output publishing orchestration；
- 调用 Native；
- 产品级流程组合。

## 4.2 Native C++ 负责

- 二进制读取/写入；
- JPEG marker/segment；
- TIFF / EXIF / MakerNote；
- XMP；
- HEIF / HEIC；
- ISO-BMFF / MP4 / MOV；
- SEF；
- vendor structure parsing；
- source protocol inspection；
- media range validation；
- source protocol cleaning；
- exact extraction；
- metadata rewrite；
- container rebuild；
- relocation；
- image conversion backend；
- video remux/transcode backend；
- 后续 Target Writer。

原则：

> **同一协议的核心二进制解析逻辑不要在 C# 和 C++ 各实现一份。**

---

# 5. 当前阶段的安全哲学

## 5.1 Fail safely rather than corrupt silently

优先级：

```text
明确 Unsupported
>
明确 Malformed
>
明确失败
>>>>>>>>
猜 offset 后返回 Success
```

如果结构无法确认：不处理。

如果 destructive range 无法确认：不清洗。

如果 pairing 无法确认：不配对。

如果 media boundary 无法确认：不提取。

## 5.2 Inspector 允许漏报，不允许错报

Inspector 的 False Positive 风险远大于 False Negative。

```text
False Negative:
暂不支持这个文件
```

通常只是功能缺失。

```text
False Positive:
错误认为某段是 MP4 / XMP / SEF / Live metadata
```

会直接导致 Cleaner / Extractor 误操作，最终产生 silent corruption。

因此：

> **宁可少认一个合法变体，也不能把普通数据错误确认成 destructive target。**

---

# 6. Inspector：当前最高优先级核心之一

Inspector 必须只读。

Inspector 不是简单返回：

```text
Protocol = Samsung
```

而是尽可能提供结构化事实。

概念模型：

```text
SourceInspectionResult
{
    SourceContainer
    Protocol

    PrimaryImage
    {
        Offset
        Length
        Format
        ValidationState
    }

    MotionVideo?
    {
        Offset
        Length
        Format
        ValidationState
    }

    GainMap?
    AuxiliaryImages?

    MetadataRanges
    {
        Exif
        Xmp
        VendorBlocks
        LivePhotoSpecificBlocks
    }

    PairingIdentity?
    Timing?
    Orientation?
    Duration?
    Evidence[]
}
```

以上不是要求立即重构成完全相同的数据类型，而是要求语义具备这种清晰度。

---

# 7. Candidate → Confirmed 是所有 Inspector 的统一思维

任何来源协议定位都应遵循：

```text
Candidate
↓
Bounds Validation
↓
Container Validation
↓
Semantic Validation
↓
Cross-check
↓
Confirmed
```

禁止：

```text
offset + length <= file_size
→ confirmed
```

“没越界”只证明内存访问可能安全，不证明媒体语义正确。

---

# 8. MP4 / MOV / ISO-BMFF 验证要求

候选视频 range 至少要考虑：

- box header 是否完整；
- 32-bit size；
- extended 64-bit size；
- size==0 语义；
- parent boundary；
- `offset + length` overflow；
- 子 box 是否越过父 box；
- candidate range 是否能按 box 结构消费；
- `ftyp` 是否存在于协议期望位置；
- major/compatible brand 是否合理；
- `moov` / `mdat` 等结构是否合理；
- range 结尾是否是真正媒体边界；
- 是否包含厂商 trailer；
- 是否包含 `sefd` 等非视频 payload；
- 是否把随机尾部垃圾纳入视频；
- relocation 后 `stco/co64` 是否正确。

不能只做：

```text
bytes[4..8] == "ftyp"
```

就认为候选视频正确。

---

# 9. JPEG 验证要求

JPEG parser/locator 至少要正确考虑：

- SOI；
- marker；
- APP segment；
- segment length；
- SOS；
- entropy-coded scan；
- byte stuffing；
- progressive / multi-scan；
- EOI；
- appended data；
- APP1 XMP；
- APP1 EXIF。

禁止用一个简单全文件：

```text
find(FF D9)
```

作为所有情况下唯一的 JPEG 结构依据。

尤其要防止：

- 把 scan data 中类似字节误识别；
- 把 appended MP4 / SEF / vendor payload 算进主 JPEG；
- 错误 segment length 导致越界。

---

# 10. HEIF / HEIC 验证要求

必须结构化理解：

- top-level boxes；
- `meta`；
- `iinf`；
- `infe`；
- `iloc`；
- item id；
- item extent；
- offset size；
- length size；
- base offset size；
- index size；
- construction method；
- 32/64-bit width；
- Exif item；
- XMP item；
- auxiliary images；
- GainMap；
- vendor items。

如果当前 implementation 无法安全支持：

- multi-extent；
- 特定 construction method；
- idat-relative layout；
- fragmented item；
- 特殊字段宽度；

则明确：

```text
Unsupported
```

不要“先取第一个 extent 试试”。

---

# 11. 字符串搜索只能做 prefilter

例如：

- `LIVE_`；
- `MotionPhoto`；
- `MicroVideo`；
- `vivoMediaExtInfo`；
- `com.android.camera.livephoto`；
- `Lavf`；
- `<x:xmpmeta`；
- `<rdf:RDF`；
- 厂商 namespace 字符串。

这些可以是：

- candidate prefilter；
- diagnostic evidence；
- 缩小搜索范围的 hint；
- 兼容性辅助。

但原则固定：

> **String hit is evidence, not authority.**

不能仅凭字符串命中就决定：

- 截断；
- 删除；
- 媒体起点；
- 媒体长度；
- destructive metadata range。

---

# 12. 双文件 Pairing 必须验证真实 identity

文件名只允许作为候选。

## Apple

优先比较真实 ContentIdentifier：

```text
Image ContentIdentifier
==
MOV ContentIdentifier
```

## vivo legacy

优先比较协议真实 pairing id：

```text
JPEG pairing id
==
MP4 pairing id
```

若两侧 identity 均存在但不一致：必须 reject。

不能因为：

```text
文件名一致 + 两边都像某厂商
```

就直接确认 pairing。

---

# 13. Cleaner：当前最高优先级核心之二

Cleaner 的产品定义：

> **只移除来源 Live Photo / Motion Photo 协议包装，将来源媒体中性化，同时最大程度保持原始媒体 payload 和与 Live Photo 无关的 metadata。**

Cleaner 不是“厂商 metadata 清空器”。

正确思维：

```text
Vendor Metadata
├─ Live-specific metadata      REMOVE
├─ Live-specific pointer       REMOVE / REWRITE
├─ Live-specific trailer       REMOVE
├─ ordinary EXIF               PRESERVE
├─ ordinary MakerNote          PRESERVE
├─ ordinary SEF tag            PRESERVE
├─ ICC                         PRESERVE
├─ GPS                         PRESERVE
└─ unknown unrelated metadata  PRESERVE by default
```

---

# 14. Cleaner 硬规则

## 14.1 不确定就不动

Cleaner 只有在以下均成立时才能 destructive edit：

```text
Confirmed protocol
+
Confirmed structure
+
Confirmed destructive range
+
Validated ownership
```

否则明确失败。

## 14.2 Source 永远只读

禁止 source in-place mutation。

推荐模型：

```text
source read-only
↓
temp output
↓
complete rewrite
↓
structural validation
↓
flush / close
↓
atomic publish / rename
```

任何失败 / cancellation：

```text
source unchanged
temp removed
```

## 14.3 Unknown metadata 默认 preserve

除非能够证明某字段/box/tag/segment 属于 Live Photo 协议，否则默认保留。

---

# 15. Cleaner 的 ownership 要求

## TIFF / EXIF / MakerNote

不能因为：

```text
new_end <= file_size
```

就认为可以原地覆盖。

必须知道当前 tag / payload 实际拥有的 span。

如果新数据超出 ownership：

```text
rebuild / relocate / fail
```

## ISO-BMFF

删除、插入、重建 box 后必须检查：

- 当前 box size；
- parent size；
- ancestor size；
- absolute offsets；
- relative offsets；
- `stco`；
- `co64`；
- metadata references；
- chunk relocation。

## HEIF

结构变化后重新验证：

- `iloc`；
- extent；
- meta size；
- item location；
- auxiliary references。

---

# 16. Cleaner 每个协议至少必须有四类测试

## Positive

```text
真实 Live Photo
→ 协议被正确清洗
```

## Negative

```text
普通图片/视频
→ 不应被错误修改
```

若 API 设计允许，尽量验证 byte-identical。

## Idempotency

```text
Clean(x)
==
Clean(Clean(x))
```

第二次清洗不应该继续删除普通 metadata 或改变结构。

## Malformed

```text
损坏/伪造协议
→ fail safely
→ source unchanged
```

---

# 17. Extractor：必须追求 exact media boundary

Extractor 的标准不是：

```text
导出的 MP4 播放器能播放
```

而是：

> **导出的 range 必须是实际媒体 payload。**

例如来源结构：

```text
[MP4][Samsung sefd]
```

必须导出：

```text
[MP4]
```

不能因为播放器会忽略 trailing bytes 就把 `sefd` 一起导出。

对于无需转码的 payload，优先验证：

```text
SHA256(extracted)
==
SHA256(expected embedded payload)
```

Extractor 输出也采用：

```text
temp → complete → validate → publish
```

防止取消/错误留下半文件。

---

# 18. NeutralMediaBundle：当前阶段最终内部语言

当前基础阶段最终需要形成稳定的中性媒体表达。

概念上可包含：

```text
NeutralMediaBundle
{
    PrimaryImage
    MotionVideo?
    GainMap?
    AuxiliaryImages?
    Metadata
    Orientation
    Timing
    Duration
    ColorInfo
    HdrInfo
    SourceProvenance?
}
```

关键语义：

> NeutralMediaBundle 中用于后续处理的媒体必须已经脱离来源厂商 Live Photo 包装，并经过结构确认。

它不应该把以下来源协议状态当作后续 target writer 的隐式依赖：

- Samsung SEF live pointers；
- Huawei LIVE tail；
- Apple source pairing wrapper；
- vivo source wrapper；
- Google Motion Photo directory；
- OPPO live-only metadata。

可以保留 source provenance 用于 diagnostics，但不能让后续 Writer 依赖来源协议残留才能工作。

---

# 19. Split 当前阶段定义

当前 Split 的目标：

```text
Vendor Source
↓
Inspect
↓
Validate
↓
Exact Extract
↓
Clean / Neutralize
↓
Primary Image + Motion Video + optional GainMap/Auxiliary
```

验收重点：

- source 不修改；
- protocol 识别正确；
- range 正确；
- video exact；
- image exact/符合清洗 contract；
- GainMap/HDR 不误丢；
- 普通 metadata 不误删；
- 输出结构有效；
- malformed 输入安全失败。

---

# 20. Merge 当前阶段定义

当前 Merge **先不要等同于目标厂商协议 Writer**。

当前优先完成 Merge 的前置中性化阶段：

```text
Input Image
+
Input Video
↓
Inspect
↓
Validate
↓
Clean source protocol if necessary
↓
Convert / Remux / Transcode if necessary
↓
Normalize metadata / timing / orientation
↓
NeutralMediaBundle
```

可以将其理解为：

```text
PrepareMergeInput()
```

当前要确保：

- 输入图片合法；
- 输入视频合法；
- 如果输入本身来自其他 Live Photo，来源协议被安全清理；
- duration 可获取；
- orientation 明确；
- codec/container 支持状态明确；
- dimensions 合理；
- HDR / GainMap 状态明确；
- metadata preservation contract 明确；
- 最终得到可信 NeutralMediaBundle。

至于最终：

```text
NeutralMediaBundle → 某厂商目标协议
```

属于最后阶段。

---

# 21. Converter：当前阶段核心能力

Converter 不是附属工具。

在 Writer 前必须先证明中性媒体转换可靠。

## 21.1 Image Converter

至少明确并测试：

```text
JPEG → JPEG
JPEG → HEIC
HEIC → JPEG
HEIC → HEIC
```

每条路径明确：

- passthrough / rewrite / decode / encode；
- 是否 lossless；
- quality contract；
- EXIF；
- GPS；
- ICC；
- orientation；
- thumbnail；
- color profile；
- HDR；
- GainMap；
- auxiliary image。

禁止只以“图片能打开”为成功标准。

## 21.2 Video Converter

必须严格区分：

### Remux

```text
media codec 不重新编码，仅调整 container / metadata
```

### Transcode

```text
媒体重新编码
```

至少明确：

```text
MP4 → MP4
MOV → MOV
MOV → MP4
MP4 → MOV
```

并验证：

- codec；
- tracks；
- duration；
- timescale；
- rotation；
- color metadata；
- HDR metadata；
- audio；
- timed metadata；
- frame rate；
- container validity。

---

# 22. 外部工具移除：当前 P0

用户当前明确要求：

> **不再需要任何外部媒体工具作为 LivePhotoBox 正式实现。**

因此应全面清理：

```text
Tools/
Tools/bak/
ExternalToolLocator
FFmpeg process invocation
ffprobe process invocation
ExifTool process invocation
jpegtran process invocation
heif-enc/heif-dec invocation
Legacy fallback
Legacy backend runtime
```

注意：

> “把 exe 文件删掉”不等于完成。

AI 必须全仓库搜索：

- `.csproj` Content/None copy；
- publish/package include；
- release scripts；
- CI scripts；
- environment variables；
- service registration；
- settings schema；
- GUI settings；
- CLI flags；
- fallback branches；
- tool path probing；
- tests 对工具存在性的隐式依赖；
- documentation；
- resource extraction。

删除后必须验证：

```text
在机器上完全没有这些工具时
当前声称支持的 Rebuilt 功能仍可工作
```

不得为了保测试绿而重新下载或隐藏安装工具。

---

# 23. Torture / Malformed Testing：必须主动攻击底座

当前阶段不能只测试真实正常样本。

要有系统的 malformed corpus / synthetic regression。

至少覆盖：

## JPEG

- truncated marker；
- invalid APP length；
- fake EOI；
- duplicate APP1；
- malformed EXIF；
- malformed XMP；
- progressive/multi-scan edge cases；
- appended random data。

## ISO-BMFF

- truncated box；
- size < header；
- size beyond parent；
- extended size overflow；
- size==0 edge case；
- fake `ftyp`；
- missing `moov`/`mdat`；
- invalid `stco/co64`；
- random trailer。

## HEIF

- invalid `iloc` width；
- invalid item id；
- offset overflow；
- multi-extent；
- unsupported construction method；
- truncated `meta/iinf/infe/iloc`；
- bad auxiliary references。

## Protocol-specific

- stale MotionPhoto XMP；
- invalid Google V1 offset；
- invalid V2 item length；
- OPPO length conflict；
- fake Huawei `LIVE_`；
- Huawei computed start not MP4；
- corrupted Samsung SEF entry；
- corrupted SEFT total size；
- fake `mpvd`；
- invalid `sefd`；
- invalid Samsung `mpv2` pointer；
- wrong Apple ContentIdentifier pairing；
- wrong vivo pairing id；
- malformed MakerNote；
- namespace collision；
- duplicate vendor metadata。

---

# 24. Parser 的安全属性

核心 parser 应逐步满足：

- random input 不 crash；
- malformed input 不越界；
- invalid count 不无限循环；
- invalid length 不大规模异常 allocation；
- arithmetic checked；
- no signed/unsigned accidental wrap；
- no dangling pointer/string lifetime bug；
- no stale offset after mutation；
- deterministic error；
- source unchanged。

原则：

> **Crash 是 bug；silent wrong success 是更严重的 bug。**

---

# 25. Round-trip / Self-validation

当前阶段最重要的测试是行为闭环。

## 25.1 Source pipeline

```text
Source
↓
Inspect
↓
Extract
↓
Clean
↓
Neutralize
↓
Re-inspect / Structural Validate
```

## 25.2 Cleaner

```text
Cleaned output
↓
Inspect again
↓
source Live protocol should no longer be present
```

同时确认：

```text
unrelated metadata still exists
```

## 25.3 Extractor

```text
Extract
↓
Container validate
↓
Exact payload/hash compare where applicable
```

## 25.4 Converter

```text
Convert
↓
Inspect / Decode / Structure Validate
↓
Contract Compare
```

注意：

> Reader 能读自己 Writer 的结果只是一个检查层，不等于独立证明协议绝对正确。

当前基础阶段可以利用 self-validation，但关键结构应尽量加入独立断言，避免 Reader/Writer 共享同一个错误假设。

---

# 26. 真实样本是 Release Gate，不是普通 CI 的替代品

建议本地私有维护：

```text
RealSamples/
├─ Apple/
├─ Samsung/
├─ Huawei/
├─ Honor/
├─ vivo/
├─ OPPO/
├─ OnePlus/
├─ Xiaomi/
└─ Google/
```

真实样本不要求进入公共 Git。

每个样本尽量记录：

- device/model；
- firmware/OS if known；
- expected source protocol；
- primary image format；
- motion video format；
- expected GainMap；
- expected HDR；
- known special layout；
- expected extracted payload hash when available。

建立本地或私有：

```text
verify-real-samples
```

作为 Release Candidate gate。

---

# 27. Real Sample Gate 建议检查

对每个适用样本至少验证：

```text
Inspect
Extract
Clean
Re-inspect
Convert where applicable
Neutralize
```

以及：

- source SHA unchanged；
- image readable/structurally valid；
- video structurally valid；
- exact media boundaries；
- exact extracted video hash when possible；
- GainMap preservation；
- HDR preservation；
- EXIF preservation；
- ICC preservation；
- orientation；
- duration；
- timestamp contract；
- pairing behavior；
- protocol removed after clean；
- idempotency；
- malformed sibling cases safe fail。

---

# 28. Repair 页面当前冻结

Repair 是独立复杂领域，可能涉及：

- rotation fix；
- stretch/aspect repair；
- thumbnail repair；
- Key Photo；
- duration repair；
- codec repair；
- metadata repair；
- malformed container recovery；
- protocol-specific repair。

当前阶段：

> **Freeze Repair.**

只允许：

- 保持现有功能基本不崩；
- 修严重 regression；
- 修安全问题；
- 为主底座必要的共享 parser bug 修复。

暂不：

- 新增 Repair feature；
- 大规模 Repair Native migration；
- Repair architecture redesign；
- 新协议 Repair 支持。

待 Inspector/Cleaner/Extractor/Converter/Neutral Pipeline 稳定后，单独建立 Repair Roadmap。

---

# 29. Target Protocol Writer：最后阶段

只有以下基础全部达到验收要求后，才进入 Target Writer 阶段：

```text
Inspector stable
Cleaner stable
Extractor stable
Converter stable
NeutralMediaBundle stable
Malformed tests mature
Real sample gate mature
Split/Merge foundation stable
```

之后才统一推进：

```text
                ┌─ Apple Writer
                ├─ Google Writer
                ├─ Samsung Writer
NeutralBundle ──┼─ Huawei Writer
                ├─ vivo Writer
                ├─ OPPO Writer
                └─ ...
```

Writer 设计必须接受统一 NeutralMediaBundle，而不是依赖某个来源厂商残留结构。

---

# 30. 当前正式优先级

## P0 — 删除旧运行时世界

目标：

```text
Rebuilt Only
No Legacy Runtime
No External Tools
No Automatic Fallback
```

验收：

- 外部工具从 repository/package/runtime 移除；
- Legacy runtime route 移除；
- 没有隐藏 fallback；
- GUI/CLI/Core 不再读取 tool paths；
- 当前 Rebuilt 声称支持的能力在无工具环境可运行。

---

## P1 — Inspector Reliability

目标：

> 所有已支持来源协议，要么可靠确认，要么安全拒绝。

重点：

- structure-first；
- exact range；
- semantic validation；
- pairing identity；
- stale metadata；
- false positive；
- malformed input；
- no guessed destructive range。

---

## P2 — Cleaner Reliability

目标：

> 所有已支持来源协议可以安全中性化。

重点：

- remove only live-specific data；
- preserve unrelated metadata；
- source read-only；
- atomic output；
- idempotency；
- malformed safe fail；
- output revalidation。

---

## P3 — Extractor Reliability

目标：

> 图片、视频、GainMap、auxiliary 的边界准确。

重点：

- exact bytes；
- no vendor trailer；
- no partial output；
- range cross-check；
- container validity。

---

## P4 — Neutral Pipeline

目标：

```text
Inspect
→ Extract
→ Clean
→ Normalize
→ NeutralMediaBundle
```

成为所有上层产品流程的可信底座。

---

## P5 — Converter Reliability

目标：

- image conversion contract；
- video remux contract；
- video transcode contract；
- metadata preservation；
- orientation；
- HDR；
- GainMap；
- color information；
- duration/timing。

---

## P6 — Torture / Malformed / Real Samples

目标：

> 正常文件兼容，异常文件安全。

建立：

- malformed corpus；
- regression corpus；
- property/fuzz-style tests；
- private real sample gate；
- round-trip verification。

---

## P7 — Split / Merge 产品流程收口

目标：

GUI / CLI / Core 真正只依赖：

```text
Inspector
Extractor
Cleaner
Converter
NeutralMediaBundle
```

不能再依赖 Legacy 或外部工具。

Merge 在此阶段重点是可信 `PrepareMergeInput` / NeutralBundle，而不是新增 Target Writer。

---

## P8 — Repair

单独设计、单独审计、单独迁移。

---

## P9 — Target Protocol Writers

**最后执行。**

---

# 31. 当前阶段的 KPI

不要使用：

```text
支持多少厂商
支持多少 Writer
```

作为主要进度指标。

当前 KPI 应是：

- 已支持来源协议中多少能够可靠 Inspect；
- 多少能 exact Extract；
- 多少能 safe Clean；
- 多少有 malformed coverage；
- 多少有 real sample coverage；
- 多少通过 idempotency；
- 多少通过 byte-exact/hash verification；
- 多少 conversion path 有明确 contract；
- 是否仍存在 external tool runtime dependency；
- 是否仍存在 Legacy fallback；
- 是否存在 silent corruption path。

宁可：

```text
8 个来源协议全部可靠
```

不要：

```text
15 个协议中大量逻辑仍靠搜索和猜测
```

---

# 32. AI 修改前必须执行的动作

任何 AI 接到相关任务后：

1. 读取当前 HEAD / branch；
2. 检查工作区状态；
3. 阅读本文；
4. 阅读与任务相关的专项 Roadmap；
5. 搜索当前真实调用链；
6. 阅读相关 Native/C# 实现；
7. 阅读相关测试；
8. 查找可用真实样本/fixture；
9. 验证 prompt 中描述的问题是否当前仍存在；
10. 再制定修改方案。

如果用户/旧 AI 给出的 bug 已不存在：

> 不要重复修改，应报告验证证据。

如果检查过程中发现明显的同类安全 bug：

> 可以在不扩大架构范围的前提下一并解决。

---

# 33. AI 当前禁止事项

除非用户明确改变 Roadmap，否则禁止：

- 恢复 Legacy production backend；
- 恢复外部工具 runtime fallback；
- 新增 FFmpeg/ExifTool 等外部 exe 依赖；
- 为“兼容旧版”重新建立 tool locator；
- 大规模开发 Target Writer；
- 为增加协议数量牺牲结构验证；
- 用字符串搜索直接决定 destructive range；
- 在 unsupported layout 上猜 offset；
- 降低断言让测试变绿；
- 删除失败样本来让 CI 通过；
- 把 RealSamples 测试改成永远跳过来隐藏问题；
- 进行无关的大规模目录/命名/style churn；
- 再设计一套新的 binary framework，仅因为“更漂亮”；
- 在 C# 重复 Native 已有 parser；
- 为未来 Writer 过度抽象当前 Neutral Pipeline；
- 当前推进 Repair feature expansion。

---

# 34. AI 修改原则

优先：

- reuse existing parser；
- reuse bounds-aware binary helpers；
- reuse structural validators；
- centralize checked arithmetic；
- centralize confirmed-range validation；
- explicit unsupported state；
- atomic publish；
- regression test；
- real sample verification；
- small focused diff；
- preserve unknown metadata。

避免：

- duplicate parser；
- ad-hoc byte scanner；
- magic offset；
- global raw search as authority；
- shared incorrect assumption between Reader/Writer tests；
- silent fallback；
- broad refactor。

---

# 35. 每个确认 bug 的修复报告要求

AI 最终报告必须区分：

## Confirmed Issue

真实存在什么问题。

## Evidence

代码、样本、测试或结构证据是什么。

## Root Cause

为什么旧逻辑错误。

## Impact

属于：

- wrong detection；
- wrong extraction；
- wrong cleaning；
- metadata loss；
- false pairing；
- output corruption；
- source corruption；
- crash；
- partial output；
- compatibility regression。

## Fix

实际怎么修。

## Safety Reasoning

为什么新实现比旧实现更可信。

## Regression Coverage

新增/修改哪条测试阻止复发。

## Remaining Risk

哪些 variant 没有真实证据验证。

禁止把未验证推测写成“已修复”。

---

# 36. Done Definition：Inspector

Inspector 被认为稳定至少满足：

- source read-only；
- bounds checked；
- arithmetic checked；
- structure-based confirmation；
- candidate 与 confirmed 状态明确；
- media ranges 可验证；
- pairing 有真实 identity 支持时进行 identity 验证；
- stale metadata 不应轻易误报；
- malformed 输入明确失败；
- unknown variant 不猜；
- false positive 风险有 regression test。

---

# 37. Done Definition：Cleaner

Cleaner 被认为稳定至少满足：

- source unchanged；
- destructive range confirmed；
- output structurally valid；
- Live protocol data 被移除；
- unrelated metadata 被保留；
- unknown metadata 默认保留；
- ownership 明确；
- relocation 正确；
- idempotent；
- cancellation/failure 不留假成功文件；
- malformed input safe fail；
- clean 后重新 inspect/validate。

---

# 38. Done Definition：Extractor

Extractor 被认为稳定至少满足：

- confirmed source range；
- exact media boundary；
- 不携带厂商 trailing garbage；
- 不截短媒体；
- GainMap/Auxiliary 按 contract 处理；
- output structurally valid；
- 无需转码时可做 byte/hash 验证；
- failure/cancel 不留 partial output。

---

# 39. Done Definition：Converter

Converter 被认为稳定至少满足：

- 每种 conversion path 有明确 contract；
- passthrough/remux/transcode 区分清楚；
- output container structurally valid；
- image/video 核心属性符合预期；
- metadata preservation 行为明确；
- HDR/GainMap/orientation/color 行为明确；
- 不发生未声明的质量损失；
- malformed/unsupported 输入明确失败；
- 不依赖外部工具。

---

# 40. Done Definition：Neutral Foundation 阶段

只有以下全部基本成立，才认为本阶段结束：

```text
No Legacy Production Runtime
No External Tools
No Automatic Fallback
Reliable Inspector
Reliable Cleaner
Reliable Extractor
Reliable Converter
Stable NeutralMediaBundle
Malformed Regression Coverage
Real Sample Release Gate
Split/Merge Foundation Uses Rebuilt Only
```

达到后，才创建新的 Target Protocol Writer Roadmap。

---

# 41. 产品级不变量

后续任何实现都必须遵守：

> **源文件永不原地破坏。**

> **无法证明 destructive range 就不修改。**

> **无法证明 media range 就不提取。**

> **无法证明 pairing 就不配对。**

> **无法支持的 TIFF / HEIF / ISO-BMFF 结构明确 Unsupported。**

> **Unknown metadata 默认 Preserve。**

> **String match 只能产生 Candidate，不能直接产生 destructive authority。**

> **任何 Clean Success 都应能够重新结构验证。**

> **任何无需转码的 Extract 都应尽可能 byte-exact。**

> **没有外部工具时产品仍应执行所有声称已完成的 Rebuilt 能力。**

---

# 42. 当前阶段最终完成形态

完成后，LivePhotoBox 应具备如下可信数据面：

```text
              ┌─────────────────────┐
              │ Vendor Source Media │
              └──────────┬──────────┘
                         ↓
              ┌─────────────────────┐
              │ Reliable Inspector  │
              └──────────┬──────────┘
                         ↓
              ┌─────────────────────┐
              │   Exact Extractor   │
              └──────────┬──────────┘
                         ↓
              ┌─────────────────────┐
              │    Safe Cleaner     │
              └──────────┬──────────┘
                         ↓
              ┌─────────────────────┐
              │ Reliable Converter  │
              └──────────┬──────────┘
                         ↓
              ┌─────────────────────┐
              │ NeutralMediaBundle  │
              └─────────────────────┘
```

并满足：

```text
NO Legacy Runtime
NO External Tool Dependency
NO Silent Fallback
NO Guess Offset
NO Source Corruption
NO Blind Metadata Deletion
NO Unvalidated Destructive Range
```

这时项目才能进入：

```text
Target Protocol Writer Phase
```

---

# 43. 后续阶段顺序（不可随意提前）

```text
Current:
P0 Rebuilt-only / Remove External Tools
        ↓
P1 Inspector Reliability
        ↓
P2 Cleaner Reliability
        ↓
P3 Extractor Reliability
        ↓
P4 Neutral Pipeline
        ↓
P5 Converter Reliability
        ↓
P6 Torture + Real Sample Gate
        ↓
P7 Split / Merge Foundation Product Integration
        ↓
P8 Repair Roadmap
        ↓
P9 Target Protocol Writers
```

如果实际代码审计发现某一层已经高质量完成，可以验证后跳过重复实现。

但不允许因为“Writer 看起来更有成果”而越过底层可靠性验收。

---

# 44. AI 决策优先级

当 AI 发现多个可做事项时，按以下优先级判断：

```text
Source Corruption Risk
>
Silent Output Corruption
>
Wrong Media Boundary
>
Wrong Cleaner Deletion
>
False Protocol Detection
>
Metadata Loss
>
Extractor Correctness
>
Converter Correctness
>
Neutral Pipeline Consistency
>
Malformed / Real Sample Coverage
>
Split / Merge Product Integration
>
Repair
>
Target Writer
>
New Features
```

---

# 45. 最终执行原则

后续任何 AI 必须始终记住：

> **当前 LivePhotoBox 的任务不是“尽快支持更多输出协议”。**
>
> **当前真正要完成的是一个可信的来源媒体理解、拆解、清洗和中性化引擎。**

它必须可靠知道：

- 文件是什么；
- 容器是什么；
- 图片真实范围是什么；
- 视频真实范围是什么；
- GainMap / auxiliary 在哪里；
- Live Photo metadata 是什么；
- 普通 metadata 是什么；
- 哪些可以删除；
- 哪些必须保留；
- 哪些结构当前无法安全支持；
- 哪些情况必须拒绝处理。

工程角色定义：

> **Inspector 是眼睛。**  
> **Extractor 是解剖器。**  
> **Cleaner 是手术刀。**  
> **Converter 是中性化处理器。**  
> **NeutralMediaBundle 是统一内部语言。**

在这五部分没有达到可信、稳定、可验证之前：

> **不要把主要开发资源投入 Target Protocol Writer。**

Repair 也暂时不是当前主路线。

当前阶段最重要的原则只有一句：

> # **先把底座做对，再做更多。**

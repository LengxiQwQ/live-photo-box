# P1 — Source Inspector Reliability

> **Goal:** Source Inspector 成为可信、只读、保守的来源事实入口。  
> **Architecture role:** Protocol/container recognition truth 属于 Portable Native Core。  
> **Platform note:** P1 的 correctness 不要求现在做 Linux/Web；现存纯 Win32 file plumbing 若不影响识别正确性，可统一在 P4 收口。

## 1. Contract

Inspector 回答：

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

`NonLive` 只能表示已积极确认不是受支持 Live/Motion Photo。  
候选证据不足时表达：

```text
Unknown
Unsupported
Malformed
或当前 ABI 可明确表达的失败
```

不能用 `NonLive` 吞掉不确定性。

## 2. Recognition Rule

```text
Candidate
→ Bounds
→ Container structure
→ Protocol semantics
→ Cross-check
→ Confirmed
```

以下单独出现均不是 authority：

```text
keyword/string hit
same filename
single metadata field
single offset
ftyp signature
range merely inside file
```

False Positive 风险高于 False Negative。

## 3. Protocol Coverage

至少覆盖当前声明支持的：

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

具体协议事实以项目验证报告和当前真实样本为 authority。

## 4. Pairing / hierarchy / ranges

正式 pairing 不依赖 basename authority。  
XMP / HEIF / ISO-BMFF / SEF recognition 必须尊重：

```text
namespace scope
hierarchy
ownership
duplicate/conflict
strict numeric parsing
bounds
reference relationship
exact range
```

GainMap / Auxiliary 不因 magic hit 或倒推长度直接升级为 Confirmed。

## 5. Portable Core Rule

Inspector 的：

```text
protocol decision
container semantics
range semantics
pairing semantics
metadata ownership
```

不得依赖：

```text
WIC
Media Foundation
Windows registry
Windows codec installation state
UI/Core filename conventions
```

如果需要 file I/O：

```text
file access
≠
protocol truth
```

P4 将收口 Win32/stream/random-access plumbing；P1 不为此重复重写已正确的 recognition logic。

## 6. Reuse

P3 后继续复用同一个 Source Inspector：

```text
Cleaned artifact
↓
Source Inspector
↓
Neutral / NonLive
```

不建立 Cleaner-specific 第二套协议识别器。

## 7. Non-goals

P1 不负责：

```text
backend selection
CMake migration
codec integration
Converter
Target Writer
Target Validator
Repair
cross-platform product
```

## 8. Exit meaning

P1 的最终 ACCEPT/REJECT 由维护者独立审计决定。  
本 V4.0 只冻结长期架构：**Inspector correctness 属 Portable Core，平台/backend 不是协议 authority。**

全局执行规则见 `00`。

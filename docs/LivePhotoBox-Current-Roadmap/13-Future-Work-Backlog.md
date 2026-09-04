# Live Photo Box — Future Work Backlog V3.2

> **Document role:** 未来事项停车场 / Parking Lot  
> **Status:** Non-authoritative for current phase  
> **Rule:** 本文记录“主 Roadmap 完成后值得继续推进的产品/平台事项”，但绝不抢占 00 的 P0–P10 顺序。

# 1. Existing Edit Product — Core Rebuild & Feature Expansion

当前仓库已经存在：

```text
EditPage
EditViewModel
相关浏览/导出能力
```

所以 Future Work 不是“新建设计一个 Edit 页面”。

主 Roadmap 完成后，应重新审计当时已有 Edit 产品，决定如何将其 result-affecting 处理能力迁移/复用：

```text
NeutralMediaBundle
Converter
Target Writer
Target Validator
Repair primitives where appropriate
```

可能继续扩展：

```text
Key Photo / cover frame
duration / trim
rotation
other Live Photo editing operations
preview + before/after
```

Edit 不应提前污染 P1–P10 的底层 contract。

# 2. Existing PhotoClassify Placeholder — Future Implementation

当前仓库已经存在：

```text
PhotoClassifyPage
PhotoClassifyViewModel
```

但属于占位/未完整开放状态。

未来可在不复制协议 parser 的前提下，复用 Inspector / media metadata 实现：

```text
Live Photo
Normal Photo
Screenshot
Portrait
Selfie
Slow Motion
Other vendor/media categories
```

分类结果属于产品语义，不成为 Source protocol truth。

# 3. Performance / Scale

后续持续推进：

```text
1000+ file batch
very large media
lower temp-space amplification
streaming transforms
concurrency scheduler
memory ceiling
resume/retry strategy
```

# 4. Cross-platform Product Expansion

P4 已负责 Native foundation/portable-core 方向。

主 Roadmap 完成后可继续推进更高层产品：

```text
Linux Native runtime completion
Linux CLI
macOS Native/CLI
platform-specific GUI
managed Core portability
packaging/distribution per platform
```

不要求跨平台共用 UI。

# 5. Native Dependency Optimization

P4 完成后仍可按证据持续：

```text
smaller binary builds
remove unused codec/features
deduplicate media runtimes
update security/license records
replace dependency only when benefit is proven
```

不要为了体积指标牺牲兼容性、保真或维护性。

# 6. Diagnostics / Support

未来可以把结构化错误进一步产品化：

```text
diagnostic report export
protocol facts summary
sanitized technical report
operation execution record
support bundle without original media
```

# 7. Acceptance Automation

后续把 00 的 Product Acceptance Matrix 自动生成/更新：

```text
test evidence
device evidence
supported profiles
release gate
```

避免手工表格长期失真。

# 8. Target Protocol Expansion

新厂商/新版本出现时：

```text
research
→ source inspector/extractor/cleaner if it is a source
→ neutral tests
→ target writer/validator if it is a target
```

不创建 Vendor A → Vendor B 专属转换器。

# 9. Repair Expansion

只在真实用户问题出现且能定义可靠修复规则时加入：

```text
new diagnosis
→ narrow repair contract
→ real broken samples
→ validation
→ product UI
```

不把 Repair 扩成无边界“万能修复”。

# 10. Website / Online Tools

如后续推进官网或 Web/WASM：

```text
website
online inspector
online split
lossless protocol conversion where browser capability permits
WASM media/protocol layer
optional cloud backend
```

必须复用协议语义和测试 corpus，而不是重新做一套不同的协议判断。

# 11. 使用规则

任何 AI 看到本文：

```text
可以记录想法
不可以因为本文存在就提前施工
```

真正开始某一未来项前：

1. 用户/维护者明确决定进入该功能；
2. 创建新的独立 Roadmap；
3. 重新审计当时 HEAD；
4. 从真实产品需求定义范围与 Gate。

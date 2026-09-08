# Live Photo Box — Future Work Backlog V4.0

> **Document role:** 主 P0–P10 完成后的未来产品/平台停车场  
> **Status:** Non-authoritative for current phase  
> **Rule:** 不抢占 `00` 的 P0–P10 顺序。

---

# 1. Web / Online Tools — 未来第一优先平台

长期平台优先级：

```text
1. Web / WebAssembly
2. Linux
3. macOS
```

Web 目标不是复制桌面全部产品能力。

优先适合浏览器、本地处理、低部署复杂度的能力：

```text
online inspector
protocol facts
split
basic merge
lossless protocol/container transforms
metadata operations
limited media preparation where WASM codec support is practical
```

核心架构：

```text
Browser UI / TypeScript
↓
WASM bindings / C ABI adapter
↓
same LivePhotoBox Portable Core
↓
WASM-compatible media/platform backend
```

禁止重新做：

```text
Web-specific protocol parser
Web-specific vendor conversion truth
```

---

# 2. Web 产品能力允许小于 Windows

桌面 Windows 是完整 first-class 产品。

Web 可以明确 capability-based：

```text
Inspect            supported
Split              supported
Lossless merge     supported where possible
Heavy transcode    optional/unsupported
Huge batch         optional/limited
hardware encode    browser capability dependent
```

不为了“功能一致”把复杂云端服务提前塞进架构。

---

# 3. Static Website First

未来官网/Online Tools 优先：

```text
static site
+
client-side WASM
```

没有以下需求时不引入 VPS：

```text
accounts
server-side cloud processing
storage
payments/backend-only workflows
```

这样可以保持：

```text
privacy
low operating cost
simple deployment
local media processing
```

---

# 4. Linux

P4 完成后，Linux 理论工作应变成：

```text
build Portable Core
+
implement/finish POSIX Platform Backend
+
select portable media backend
+
CLI/product integration
+
package
```

而不是：

```text
rewrite Apple/Google/Huawei parsers
```

是否真正发布 Linux 由未来需求决定。

---

# 5. macOS

macOS 不是当前承诺。

如果未来做：

```text
Portable Core
+
Apple platform backend where useful
+
product UI/CLI
+
signing/notarization
+
device testing
```

协议/Writer/Validator 继续复用。

没有 Mac 测试环境时不宣称正式支持。

---

# 6. Existing Edit Product — Core Rebuild & Expansion

当前项目已有 Edit 产品。

主 Roadmap 后重新审计并复用：

```text
NeutralMediaBundle
Converter
Target Writer
Target Validator
Repair primitives
```

可能扩展：

```text
Key Photo
cover frame
duration/trim
rotation
other Live Photo edits
preview/before-after
```

Edit 不提前污染 P1–P10。

---

# 7. Existing PhotoClassify Placeholder

未来复用 Inspector/media facts：

```text
Live Photo
Normal Photo
Screenshot
Portrait
Selfie
Slow Motion
Other categories
```

分类是产品语义，不成为 protocol truth。

---

# 8. Performance / Scale

持续优化：

```text
1000+ file batch
very large media
lower temp amplification
streaming
concurrency scheduler
memory ceiling
resume/retry
```

性能优化不能突破 preservation/correctness contract。

---

# 9. Native Dependency Optimization

在 P4 已冻结 foundation 后，仍可持续：

```text
remove unused features
deduplicate codec runtimes
security upgrades
dependency version updates
smaller builds
faster backend
```

排序仍是：

```text
compatibility/correctness
before
package size/dependency count
```

---

# 10. New codecs / formats

只有出现真实产品 requirement 时增加：

```text
AV1/AVIF
new HEIF codecs
new HDR format
new video codec
new vendor format
```

不因 dependency “顺便支持”就对产品开放。

---

# 11. Diagnostics / Support

未来可产品化：

```text
diagnostic report export
protocol facts summary
sanitized support bundle
ExecutionRecord export
backend/capability report
```

不包含用户原媒体时尽量仍可用于支持。

---

# 12. Acceptance Automation

未来可以自动生成：

```text
supported source matrix
supported target matrix
backend capability matrix
device evidence
release gate
```

避免文档状态长期失真。

---

# 13. Target Protocol Expansion

新厂商/版本：

```text
research
→ Source side if needed
→ Neutral coverage
→ Target Writer/Validator if needed
```

不创建 Vendor A → Vendor B 专属转换器。

---

# 14. Repair Expansion

只有真实用户问题且可定义可靠规则：

```text
diagnosis
→ narrow RepairPlan
→ real broken sample
→ validation
→ product UI
```

不扩成万能恢复器。

---

# 15. 使用规则

看到 Future Work：

```text
可以记录
不能提前施工
```

真正启动某未来项前：

1. 用户/维护者明确决定；
2. 重新审计当时 HEAD；
3. 基于 V4.0 contracts 建独立执行 Roadmap；
4. 不重新发明 Protocol Core。

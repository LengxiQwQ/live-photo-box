# P8 — Split / Merge Product Foundation

> **Entry:** P1–P7 credible.  
> **Goal:** 把 GUI / CLI / Core 的 Split 与 Merge 产品骨架切到稳定 Rebuilt contracts，并为 P9 提供清晰 Writer boundary。  
> **Current product platform:** Windows。  
> **Non-goal:** 为了未来 Linux/Web 重写 WinUI 或把 Windows C# Core 强行跨平台化。

---

# 1. P8 定位

P8 负责：

```text
product call graph
typed selections
input discovery/preparation
Neutral preparation
queue/batch/progress/cancel
output naming/destination
error presentation
Writer orchestration boundary
```

P9 负责：

```text
target protocol structure
structured Writer
Target Validator
old Writer replacement
real-device conformance
```

---

# 2. Windows UI 可以 Windows-specific

允许：

```text
WinUI 3
Windows shell/file picker
Windows-specific product integration
Windows app packaging
```

无需为了未来平台抽象所有 UI。

真正必须避免的是：

```text
UI/C# product code
→ becomes protocol truth
```

或：

```text
Windows UI index
→ leaks into Native correctness
```

---

# 3. Typed Core Boundary

逐步消除：

```text
protocolIndex
formatIndex
splitProtocolIndex
```

长期泄漏。

统一：

```text
TargetProtocol
MediaFormatRequirement
OutputProfile
```

UI：

```text
dropdown/index
↓
Windows UI Adapter
↓
typed values
↓
Core
↓
Native
```

---

# 4. Split

Split product flow：

```text
input
↓
Inspector / Neutral foundation
↓
selected output media requirement
↓
approved media/platform backend
↓
transactional publish
```

产品层不复制：

```text
vendor parser
offset logic
external-tool logic
codec semantics
```

---

# 5. Merge

```text
input image/video
↓
typed discovery
↓
Neutral preparation
↓
OutputProfile
↓
Target Writer boundary
```

P9 前现有 production writer 可以通过 adapter 暂存。

禁止：

```text
hidden automatic Legacy fallback
P8 顺手重写 vendor writer
无 replacement 先删除当前合法 writer
```

---

# 6. Platform capability presentation

未来 platform 能力可能不同，因此产品 contract 允许查询：

```text
supported capabilities
supported output profiles
available media backend capability
```

当前 Windows UI 可以展示全部 Windows 能力。

未来 Web 若只支持：

```text
inspect
split
limited merge
```

不要求伪装成桌面完整能力。

---

# 7. Execution / history

产品 history 应记录 semantic outcome，而不是只记录：

```text
"success"
```

适合记录：

```text
operation
OutputProfile
preservation/degradation
backend summary
output artifacts
diagnostic result
```

backend 仅用于 diagnostics，不成为协议 correctness authority。

---

# 8. Exit Criteria

- Split/Merge product flow 走 Rebuilt contracts；
- GUI/CLI 使用 typed Core contract；
- Core 不长期依赖 UI index；
- Windows-specific UI 不污染 Portable Native Core；
- capability query 可支持未来平台能力差异；
- existing target writers 位于明确 boundary 后；
- P9 可逐个替换 Writer，无需再改产品主架构。

全局执行规则见 `00`。

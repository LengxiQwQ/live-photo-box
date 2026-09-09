# P7 — System Torture / Malformed / Regression Campaign

> **Entry:** P1–P6 已完成各自阶段验证。  
> **Goal:** 用系统级攻击、组合与长期运行找跨模块、backend、事务和协议边界问题。  
> **Current formal platform:** Windows x64。

---

# 1. P7 不是跨平台产品测试阶段

当前不要求：

```text
Linux product acceptance
macOS product acceptance
Web product acceptance
```

P7 应证明：

> **Windows production system 稳定，同时 V4.0 的 Portable Core / backend boundary 没有重新被破坏。**

---

# 2. Corpus

```text
real device corpus
malformed corpus
mutation corpus
collision corpus
large-file corpus
HDR/GainMap corpus
codec/backend corpus
```

真实私有样本可以仅本地运行。

---

# 3. Attack Areas

```text
integer overflow
truncation
duplicate/conflicting metadata
namespace collision
fake markers
cross-protocol collision
overlapping ranges
invalid box sizes
unsupported HEIF layouts
corrupt SEF
bad pairing
trailing garbage
cancel at arbitrary stage
disk full
locked output
temp/publish failure
batch memory growth
backend initialization failure
backend fallback
hardware encoder failure
```

---

# 4. Backend Regression

新增系统级检查：

```text
same semantic request
→ selected backend
→ truthful ExecutionRecord
→ valid result
```

如果有两个 approved backend，适合时做 differential：

```text
MF vs libav*
hardware vs software
WIC reference vs portable image backend
```

重点不是要求 byte-identical，而是：

```text
same declared semantic/preservation contract
```

---

# 5. Dependency / portability regression

自动扫描/compile checks 防止：

```text
Windows.h 重新进入 portable protocol/container targets
new external CLI production invocation
new unpinned runtime dependency
duplicate codec added without decision record
C ABI leaks C++/Windows types
```

可选：

```text
portable-core non-Windows/Emscripten compile smoke
```

不代表产品支持。

---

# 6. Independent Evidence

测试环境允许：

```text
ffprobe
ExifTool
reference implementations
system decoders
independent parsers
real devices
```

它们不进入 production runtime。

---

# 7. Exit Criteria

- 无已知 silent corruption；
- malformed 不 crash；
- ambiguous 不猜；
- transaction/failure 安全；
- batch/large file 无明显泄漏；
- backend fallback 可解释；
- Portable Core 边界未回退；
- dependency graph 无未批准扩张；
- regression corpus 可重复。

全局执行规则见 `00`。

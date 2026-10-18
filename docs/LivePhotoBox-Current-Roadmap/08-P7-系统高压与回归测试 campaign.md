> 当前有效收口状态（2026-10-16）：P7-R3 CLOSED / PASS。最终 q8 120/120、p8 16 个真实子进程场景全部按预期，零 fail/skip/notExecuted；fresh 本地只读独立审查 PASS / DONE_FOR_R3。正式证据：`.ai-tmp/evidence/P7/P7-R3-backend-truth/run-1/`；报告：`LivePhotoBox-P7-细分路线图/P7-R3-Closeout.md`。P7 仍 in_progress；active TASK 已清空；停在 R4/P8 之前，未 commit/push/tag/release。下方旧 R2/R1 停止点为历史，验收标准不变。

> 当前有效收口状态（2026-10-16）：P7-R2 CLOSED / PASS。run-3 Core 216/216、CLI 1/1（六场景），失败/跳过/未执行均为 0；fresh 只读独立复核 PASS / DONE_FOR_R2。正式证据与收口报告：`.ai-tmp/evidence/P7/P7-R2-transaction-filesystem/run-3/`。隔离 VHDX 已卸载、挂载入口已移除，镜像与记录保留。run-1 失败和 run-2 拒绝永久保留。P7 仍 in_progress，active TASK 已清空；停止在 R3/P8 之前；未 commit/push/release。下方旧通知为历史，验收标准不变。

> 当前有效执行状态（2026-10-16）：R1 保持 CLOSED；当前用户已明确授权本地 Codex 推进 R2 至真实证据及 fresh 只读独立收口，通过后停在 R3/P8 之前。R2=IN_PROGRESS，尚未验收；不联系 ChatGPT/C2C，不 commit/push/release，P7 仍 in_progress。R2 入口：P7-R2-Local-Execution-Plan.md 与 .ai/tasks/P7-R2-transaction-filesystem/TASK.md。下方“R1 后停止/R2 未激活”等通知为历史记录，由本段取代；产品验收标准不变。

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

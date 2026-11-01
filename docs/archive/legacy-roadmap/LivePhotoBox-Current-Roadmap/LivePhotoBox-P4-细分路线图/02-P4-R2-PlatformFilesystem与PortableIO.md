# P4-R2 — PlatformFilesystem / Transaction / Portable I/O Foundation

> **性质：** 高风险基础架构重构轮。  
> **目标：** 把 Windows filesystem / temp / transaction / publish plumbing 从协议与媒体真相中收口，同时保护 P1–P3 已建立的 authority、identity、transaction 和 preservation correctness。

---

## 1. 本轮核心目标

P4 不是禁止 Win32。

正确目标是：

```text
Portable Core / protocol / container / mutation semantics
                  ↓
         small platform contract
                  ↓
      Windows platform implementation
```

完成后，业务/协议模块不应各自发明：

```text
temp file
flush
atomic publish
replace
remove
Windows error mapping
path encoding
```

同一种事务能力应有一个权威低层实现路径。

---

## 2. PlatformFilesystem 应承担的语义

建立当前项目最自然、最小的 platform contract，语义至少覆盖：

```text
read-only / random-access source
streaming sink
safe temp/workspace
exact range read
write all
flush
close
exists / stat / size
safe remove
atomic publish
atomic replace（仅显式允许时）
platform capability query
platform error mapping
```

不要求为了“抽象完整”建立大型虚拟文件系统框架。

只抽象**真实存在的平台替换点**。

---

## 3. Path / ABI 边界

长期规则：

```text
Public C ABI = UTF-8
```

Windows backend 自己负责：

```text
UTF-8 ↔ native Windows path representation
```

Portable Core / public ABI 不得因此变成：

```text
wchar_t*
HANDLE
DWORD
MOVEFILE_* flag
COM object
```

---

## 4. Transaction / Publish 收口

重点审计并逐步收口：

```text
Extractor
Cleaner
Image Converter
Video Converter
MP4/container mutation
vendor cleaner
其他 result-affecting writer
```

原则：

> 同一类 atomic publish / owned-temp / rollback 能力，不允许多个模块各有一套略有不同的实现。

但 P1–P3 当前 correctness-critical 实现不能为了“统一”而被一次性重写。

正确迁移方式：

```text
现有正确 primitive
→ 进入/被包在 Windows platform backend 后
→ 保持 identity / handle ownership / flush / publish / rollback 语义
→ regression
→ 再清理重复 plumbing
```

---

## 5. Portable Byte / Stream Foundation

P4 总纲明确要求 core primitive 不能永远只能由 Windows path 驱动。

本轮建立项目最自然的：

```text
byte span
mutable byte span
random-access reader
sequential writer
artifact source / sink
```

或语义等价形式。

目标是让未来：

```text
Windows file
memory fixture
WASM ArrayBuffer/browser source
POSIX file
```

可以驱动同一 protocol/container engine。

**P4 不要求把所有 path API 一次删完。**

要求的是：新的 canonical low-level core primitive 不再天然绑定 Win32 path/handle。

---

## 6. 对 P1–P3 的保护线

本轮不得改变：

```text
Inspector protocol authority
Extractor exactness / artifact identity
Cleaner destructive authorization
stale/replayed plan handling
TOCTOU protection
post-clean inspection
preservation verdict semantics
rollback / fail-closed behavior
```

如果为了迁移 I/O 需要调整调用链，最终 externally observable correctness 必须保持。

---

## 7. 本轮应完成的结果

完成后至少应该看到：

- 一个清晰的 Windows platform implementation 边界；
- filesystem / temp / publish / replace 不再散落成多套同义实现；
- correctness-critical publish primitive 被复用而不是重新发明；
- public ABI 不泄漏 Windows types；
- portable protocol/container sources 不再无理由依赖 filesystem platform details；
- byte/stream/random-access 基础足以支撑后续 portable-core target；
- 仍无法迁移的 Win32 plumbing 有逐项说明和原因，而不是“以后再说”。

---

## 8. 验收标准

全部满足才可进入 R3：

1. PlatformFilesystem / equivalent contract 的职责边界清楚，且没有过度抽象；
2. Windows-specific file I/O/publish 进入明确 backend module；
3. P1–P3 correctness-critical transaction semantics 没被削弱；
4. canonical atomic publish/transaction primitive 已建立并被核心 result-affecting 路径复用；
5. direct Win32 filesystem/publish 残留已有明确分类，不能存在“没人知道为什么还在这里”的路径；
6. public C ABI 仍保持平台中立；
7. portable byte/stream/random-access primitive 已存在并被至少核心结构处理路径实际使用或证明可用；
8. source artifact 默认不被原地破坏；
9. failure 不留下半发布结果或虚假 success；
10. P1–P3 targeted regression 与真实 filesystem transaction 验证通过。

---

## 9. 以下情况不得通过

任一出现即 `R2 BLOCKED`：

- 为了消灭 `Windows.h` 重写 P1–P3 协议/authority 语义；
- 新 abstraction 只是“又包一层”，旧模块仍各自实现 temp/publish；
- public ABI 出现 `HANDLE` / COM / Windows path type；
- atomic publish 变成 path-only rename，而丢失当前 exact-object/identity 保护；
- rollback、flush、close、post-operation verification 被简化；
- path alias / source overwrite 风险增加；
- tests 只用 mock filesystem，却没有真实 filesystem failure/transaction 证据；
- existing RealSample / preservation regression 出现无法解释的变化；
- 为未来 Linux/Web 实现一整套当前完全用不到的 backend。

---

## 10. 验证与测试

本轮验证重点不是 UI，而是底层语义。

### 必须覆盖

```text
P1 Inspector impacted regression
P2 exact extraction / transaction / rollback
P3 cleaner authority / transaction / preservation
real filesystem publish behavior
UTF-8 / unusual path handling
source and destination alias protection
temp cleanup
failure before publish
failure during publish
post-publish identity/verification
large/random-access read behavior
```

### 真实失败

Roadmap/AI rules 要求真实 filesystem 行为时，应使用真实：

```text
share lock
ACL/non-writable
destination collision/replacement
filesystem failure where practical
```

Mock/fault injection 只能补充状态机覆盖，不能替代要求的真实证据。

### 回归判断

同一真实样本在 R2 前后：

- Inspector facts 不应无理由变化；
- Extracted artifact semantics 不应无理由变化；
- Cleaner 只能删除原授权结构；
- preservation evidence 不应退化；
- transaction failure 不应留下额外产物。

本轮通过的核心含义：

> 平台 plumbing 被收口了，但 P1–P3 的真相没有被改写。

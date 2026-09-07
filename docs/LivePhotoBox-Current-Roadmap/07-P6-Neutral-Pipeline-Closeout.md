# P6 — Neutral Pipeline Closeout

> **Entry:** P1–P5 reliable.  
> **Goal:** 把 Inspect → Extract → Clean → Convert 串成稳定、platform-neutral、backend-neutral 的 NeutralMediaBundle 生产线。

---

# 1. Pipeline

```text
Source
→ Inspect
→ Extract
→ Clean
→ Convert only when required
→ post-clean inspection
→ NeutralMediaBundle
→ manifest / preservation outcomes
```

---

# 2. Neutral 必须与 platform/backend 解耦

NeutralMediaBundle 不得把以下作为 correctness state：

```text
Windows HANDLE
COM object
WIC decoder identity
Media Foundation object
backend-private pointer
Win32 error code
source Windows path semantics
```

可以记录 backend identity 作为：

```text
diagnostics / ExecutionRecord
```

但不能成为 Target Writer 的语义输入。

---

# 3. Artifact abstraction

当前 Windows 产品可以继续使用 path-backed artifacts。

长期 contract 应能表达：

```text
artifact role
container
codec
length
hash
semantic identity
representation
preservation
```

而不要求 artifact 永远必须是：

```text
C:\some\path\file.heic
```

这样未来：

```text
WASM memory/blob
POSIX file
Windows file
```

都可承载同一 Neutral semantics。

---

# 4. Auxiliary representation gate

必须无歧义区分：

```text
GainMap/Auxiliary embedded in PrimaryImage
vs
detached working artifact
vs
both representations of same semantic asset
```

Target Writer 不能通过：

```text
GainMap != null
```

推断“需要再次 append”。

同一 semantic GainMap 不得重复写入。

---

# 5. Neutral Gate

服从：

```text
12-Neutral-Media-Contract.md
```

尤其：

```text
no source live binding
independent media validity
normalized timing/orientation
truthful preservation
platform/backend neutrality
```

---

# 6. No unnecessary conversion

Neutral 不等于固定：

```text
JPEG + MP4
```

如果目标允许原媒体：

```text
passthrough preferred
```

Media conversion 由：

```text
MediaFormatRequirement
```

驱动，而不是为了统一 Neutral 形式强制重编码。

---

# 7. Product boundary

P6 输出给产品层的是稳定 semantic contract。

C# 不应需要：

```text
if Apple source then...
if WIC backend then...
if Windows file layout then...
```

来修补 Neutral pipeline correctness。

---

# 8. Exit Criteria

- Neutral contract 无 source vendor hidden dependency；
- 无 platform/backend-specific correctness state；
- auxiliary representation 无歧义；
- preservation/manifest 真实；
- supported source → Neutral 或明确 Unsupported；
- P8/P9 无需重新设计 Native 主流程。

全局执行规则见 `00`。

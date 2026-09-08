# P3 — Cleaner Reliability

> **Entry:** P1/P2 facts 可信。  
> **Goal:** 精确移除来源 Live/Motion Photo 协议绑定，同时保留非目标数据。  
> **Architecture role:** Destructive semantics 属 Portable Core；transaction/publish 属 Platform Backend。

## 1. Core Rule

> **Preserve unknown/non-target data unless proven to belong to source live protocol or proven to conflict with neutral correctness.**

Cleaner 只能修改 SourceFacts 明确授权的协议结构。

禁止：

```text
keyword hit → delete
unknown MakerNote → wipe
unknown XMP → rewrite whole packet without preservation proof
```

## 2. Post-clean Gate

```text
cleaned artifact
→ flush/close
→ Source Inspector reopen
→ Neutral / NonLive
```

## 3. Portable boundary

Cleaner 的以下部分应平台无关：

```text
destructive authority
authorized residue identity
mutation plan
container/metadata rewrite semantics
preservation comparison semantics
post-clean expected facts
```

以下不应散落在 vendor cleaner 中：

```text
MoveFileExW
GetTempFileNameW
DeleteFileW
Windows error codes
platform-specific lock/publish logic
```

它们在 P4 收口到 PlatformFilesystem / transaction primitives。

## 4. Existing Win32 plumbing

发现 protocol cleaner 中存在 Win32 plumbing：

```text
≠ P3 protocol design automatically invalid
```

若 correctness 已成立，P4 负责迁移 plumbing。  
只有当平台调用本身导致 destructive authority、事务安全或 preservation 出错时，才回到 P3 correctness 修复。

## 5. Long-term requirement

未来平台应复用同一：

```text
CleanupPlan
destructive authorization
container mutation engine
post-clean validation semantics
```

只替换 I/O/publish backend。

全局执行规则见 `00`。

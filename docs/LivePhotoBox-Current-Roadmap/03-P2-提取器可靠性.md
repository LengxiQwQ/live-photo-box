# P2 — Extractor Reliability

> **Entry:** P1 facts 可作为 authority。  
> **Goal:** 只从 Inspector 已确认事实中精确提取媒体资产。  
> **Architecture role:** Extraction semantics portable；file I/O / transaction 为 backend concern。

## 1. Invariants

Extractor 不重新猜协议，也不建立第二套识别。

只消费：

```text
SourceFacts
confirmed ranges
confirmed relationships
```

输出：

```text
PrimaryImage
MotionVideo?
GainMap?
Auxiliary?
ExtractedProtocolFacts
```

## 2. Exactness

无需转换时优先：

```text
byte-exact
hash-verifiable
exact boundary
no trailing vendor garbage
no truncation
streaming for large media
```

## 3. Portable boundary

以下属于 extraction semantics，应留在 Portable Core：

```text
which confirmed range to extract
range validation rules
artifact role
source identity/fingerprint expectations
exactness requirements
```

以下属于 platform/filesystem backend：

```text
CreateFileW / HANDLE
random-access file open
temp creation
write/flush
atomic publish
delete
platform error code mapping
```

当前 Windows 可继续用高质量 Win32 implementation；P4 统一收口，不要求为跨平台牺牲 Windows I/O 能力。

## 4. Failure semantics

应明确：

```text
confirmed source range became unreadable
source changed during operation
output write failed
cancelled
disk full
unsupported extraction layout
publish failed
```

失败不修改源文件，不留下伪成功 partial output。

## 5. Long-term requirement

未来 Web/Linux/macOS 实现时：

```text
same extraction semantics
+
different source/sink backend
```

而不是复制一份 vendor extractor。

全局执行规则见 `00`。

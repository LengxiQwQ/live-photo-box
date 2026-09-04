# Roadmap V3.2 迁移说明

> **Status:** Final migration guide  
> **Purpose:** 只说明如何把旧 Roadmap 整理成不会误导 AI 的结构；不拥有阶段调度权。

## 1. 目标目录

```text
docs/LivePhotoBox-Current-Roadmap/
├─ 00-重构总纲-唯一执行路线.md
├─ 01-P0-Rebuilt-only-Runtime-Closeout.md
├─ 02-P1-Source-Inspector-Reliability.md
├─ 03-P2-Extractor-Reliability.md
├─ 04-P3-Cleaner-Reliability.md
├─ 05-P4-Native-Media-Toolchain-Evaluation-and-Foundation.md
├─ 06-P5-Converter-Reliability.md
├─ 07-P6-Neutral-Pipeline-Closeout.md
├─ 08-P7-System-Torture-and-Regression-Campaign.md
├─ 09-P8-Split-Merge-Product-Foundation.md
├─ 10-P9-Target-Protocol-Writers-and-Validators.md
├─ 11-P10-Repair.md
├─ 12-Neutral-Media-Contract.md
├─ 13-Future-Work-Backlog.md
└─ Historical/
```

## 2. 旧 Roadmap

旧：

```text
01-协议后端分流...
02-媒体格式转换...
02A...
02B...
02C...
03-自动化测试...
```

若仍有知识价值，移入 `Historical/`，顶部标记：

```text
STATUS: SUPERSEDED / HISTORICAL
CURRENT AUTHORITY: 00-重构总纲-唯一执行路线.md
```

旧文档不再拥有阶段调度权。

## 3. V3.2 的关键收口

### 测试路线

不是：

```text
P7 才开始真实样本
```

而是：

```text
P1-P6 所有 result-affecting / protocol-correctness process
= automated + real media + malformed/negative + regression

pure build/platform/diagnostic infrastructure
= 与风险匹配的 build/platform/dependency/fault evidence

P7
= system torture / mutation / cross-module / stress campaign
```

### P4

P4 同时包含：

```text
toolchain candidate evaluation
+
platform/filesystem boundary
+
reproducible Native build/dependency foundation
+
cross-platform/portable-core proof
```

P4 不预设：

```text
FFmpeg
libheif
libjpeg-turbo
CMake
或任何其他方案
```

为最终答案。

### P8 / P9

P8：

```text
Split product cutover
Merge preparation/orchestration
typed OutputProfile
Writer boundary
```

P9：

```text
structured target writers
target protocol validators
real-device target conformance
old writer replacement/removal
```

P8 不提前重写/删除仍有合法调用者的 Target Writer。

### Validator

“Independent Validator”不等于复制一整套低层 parser。

允许共享：

```text
trusted read-only structural parser primitives
```

必须独立：

```text
fresh disk reopen
observed facts
target conformance rules
PASS/FAIL decision
```

### Neutral Contract

V3.2 增加：

```text
same-container vs cross-container metadata preservation distinction
GainMap/Auxiliary embedded vs detached canonical representation
```

避免 Writer 重复写 GainMap 或对无法跨容器表达的 metadata 做虚假 preservation 承诺。

### Vocabulary

统一：

```text
TargetProtocol
MediaFormatRequirement
OutputProfile
```

不再并列创造 `TargetProfile`。

## 4. Future Work 事实修正

当前项目已有：

```text
EditPage / EditViewModel
PhotoClassifyPage / PhotoClassifyViewModel placeholder
```

所以 Future Work 描述为：

```text
Edit product/core rebuild & expansion
PhotoClassify placeholder future implementation
```

而不是“未来新建页面”。

## 5. Completion Matrix

00 的 Phase Completion 表只记录**已经取得的证据**。

未来要求不使用 ✅ 表示。

## 6. 不修改用户文档

本次 Roadmap 更新不要求修改：

```text
README.md
README.zh-CN.md
CLI-User-Guide.md
CLI-User-Guide.zh-CN.md
Release 用户说明
商店文案
```

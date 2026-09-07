# P9 — Target Protocol Writers + Target Protocol Validators

> **Entry:** P8 DONE.  
> **Goal:** 从 NeutralMediaBundle + OutputProfile 结构化生成各目标协议，并通过重新解析、独立 conformance rules 与真实设备证明正确。  
> **Architecture role:** Writer / Validator correctness 尽量属于 Portable Core。

---

# 1. Writer 永久规则

Production Writer 必须理解：

```text
container hierarchy
parent/child ownership
metadata namespace/item/track
required/optional fields
protocol version
size
offset
extent
reference
index/table
relocation
pairing identity
target timing semantics
media placement
```

正式位置来自：

```text
parsed structure
+
protocol model
+
ownership/reference relationship
```

禁止 authority：

```text
全文关键词搜索
固定绝对 offset
hit + N
单一样本 magic patch
"一般在第几个 box"
```

---

# 2. Writer 与 Media Backend 分离

Writer 可以请求：

```text
prepare JPEG
prepare HEIC
prepare H264/HEVC video
remux if required
```

但不能让：

```text
libheif
Media Foundation
libav*
WIC
```

决定目标 Live/Motion Photo protocol structure。

正确关系：

```text
Writer
↓
requires media representation
↓
Media Backend
↓
returns prepared generic media
↓
Writer performs project-owned target structure
```

---

# 3. Platform-neutral correctness

Writer 的：

```text
target protocol semantics
identity
timing
metadata
container mutation
references
validation rules
```

不得依赖：

```text
Windows API
Windows codec installation state
WinUI
Windows filename convention
```

Platform-specific file creation/publish 通过 P4 primitives。

因此未来 WASM/Linux：

```text
same Writer/Validator core
+
different host/backend
```

---

# 4. Input vocabulary

```text
NeutralMediaBundle
+
OutputProfile
```

禁止：

```text
if SourceProvenance.Protocol == Huawei ...
```

来决定目标布局。

SourceProvenance 只能用于：

```text
diagnostics
history
evidence
```

---

# 5. Shared Structural Core

尽量复用：

```text
JPEG structure
TIFF/EXIF
XMP structured model
HEIF structure
ISO-BMFF
SEF
```

Vendor Writer 只实现 vendor semantics，不复制底层 binary engine。

---

# 6. Target Validator

闭环：

```text
Writer
↓
flush/close
↓
reopen final output
↓
trusted low-level parse
↓
fresh observed target facts
↓
independent conformance rules
↓
generic media validation
↓
reference/device evidence
```

Validator 证明：

> **最终文件符合目标协议，而不是“Writer 刚才返回了 success”。**

允许共享：

```text
trusted read-only structural parser primitives
```

必须独立：

```text
validation orchestration
observed target facts
target correctness rules
PASS/FAIL
```

---

# 7. Backend neutrality in Validator

Generic media validity 可以通过 approved backend/system/reference tool 辅助证明。

但：

```text
backend decode success
≠
target protocol conformance
```

Validator 不因为：

```text
Windows Photos can open it
```

就判 Apple/Huawei/Samsung protocol 合格。

---

# 8. Real-device compatibility first

每个 target 升级为 production support 时，优先级：

```text
correct target structure
real-device import/view/play
preservation
stability
quality
performance
package size
```

不能为了缩小 dependency 或统一 backend，接受更差设备兼容性。

---

# 9. Writer lifecycle

```text
Research
→ Structural Model Ready
→ Writer Prototype
→ Validator Ready
→ Synthetic/Mutation Verified
→ RealSamples Verified
→ RealDevice Verified
→ Production Ready
```

具体厂商顺序由届时样本、需求、设备验证条件决定。

---

# 10. Existing Writer replacement

```text
new structured writer
↓
target validator
↓
automated + real samples
↓
external/reference evidence
↓
real device acceptance
↓
product cutover
↓
remove obsolete writer
```

不保留自动双轨 fallback。

---

# 11. Exit Criteria

某 target 只有在：

```text
structural model understood
+
structured Writer
+
fresh reopen Validator
+
generic media valid
+
real-device acceptance
```

后才正式支持。

未验证 variant：

```text
Experimental / Unsupported
```

全局执行规则见 `00`。

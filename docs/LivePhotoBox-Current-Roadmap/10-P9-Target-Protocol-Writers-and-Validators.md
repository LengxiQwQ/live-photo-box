# P9 — Target Protocol Writers + Target Protocol Validators

> **Entry:** P8 DONE  
> **Goal:** 从 NeutralMediaBundle + OutputProfile 结构化生成各目标协议，并通过重新解析、独立 conformance rules、外部证据和真实设备证明正确。  
> **Importance:** 主 Merge 能力的最终协议层。  

# 1. Writer Rule — 结构化写入是永久硬规则

Production Writer 必须理解：

```text
container hierarchy
parent/child ownership
metadata namespace/item/track
required vs optional fields
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

正式写入位置必须来自：

```text
parsed structure
+
protocol model
+
ownership/reference relationship
```

禁止把以下作为 authoritative write logic：

```text
全文关键词搜索
固定绝对 offset
hit + N
单一样本 magic patch
“这个 box 一般在第几个”
```

这些方式只能用于：

```text
reverse engineering
candidate research
fixture construction
diagnostics
```

# 2. Container mutation 规则

Writer 改变结构后必须正确处理所有受影响的：

```text
parent size
child size
offset
extent
reference
item/property association
chunk/sample table where applicable
relocation
length/count fields
```

“文件能打开/视频能播放”不能证明 Writer 正确。

# 3. Input vocabulary

Writer 输入：

```text
NeutralMediaBundle
+
OutputProfile
```

其中：

```text
OutputProfile
= TargetProtocol
  + MediaFormatRequirement
  + target-specific version/options
```

Writer 不得要求：

```text
source vendor == X
```

不得读取 `SourceProvenance.Protocol` 决定目标结构。

# 4. 每个目标协议必须先真正理解

Production Ready 前必须有证据理解：

```text
container hierarchy
metadata hierarchy
identity/pairing
media placement
timing
references
offset/length semantics
version differences
required/optional fields
device compatibility differences
```

只有几个 sample offset 或字符串位置：

```text
= Research
≠ Production Writer Ready
```

# 5. Shared Structural Core

Writer 应尽量复用项目自己的可信结构 primitive：

```text
JPEG structure writer/parser
TIFF / EXIF
XMP structured model
HEIF structure
ISO-BMFF
SEF
```

Vendor Writer 负责：

```text
target protocol semantics
target field/value rules
target relationships
target identity/timing
```

不要为每个厂商复制一套低层 binary engine。

# 6. Target Protocol Validator

每个正式 Writer 必须配套 Validator。

正确闭环：

```text
Writer
↓
flush / close
↓
reopen final output from disk
↓
trusted low-level structural parse
↓
fresh observed target facts
↓
independent target conformance rules
↓
generic media validation
↓
external/reference evidence where useful
↓
real device gate
```

Validator 的目标：

> **证明最终文件符合目标协议，而不是证明 Writer 执行过。**

# 7. Validator 独立性的准确边界

允许共享：

```text
已经被独立测试证明可靠的
JPEG / TIFF / XMP / HEIF / ISO-BMFF / SEF
低层 read-only parser/primitives
```

必须独立：

```text
validation orchestration
observed target facts
target correctness rules
PASS/FAIL decision
```

禁止：

```text
读取 Writer 的内存对象作为最终事实
读取 Writer 的“写过某字段”布尔值
Writer success return code → PASS
复用 Writer 的目标 correctness 判断
```

这样避免为了“独立”而维护两套容易漂移的底层 parser，同时避免 Writer 和 Validator 共享同一逻辑错误。

# 8. Validator 至少检查什么

按目标协议适用性检查：

```text
container hierarchy valid
required metadata in correct structural location
required fields present
optional/version rules valid
pairing identity valid
references valid
offset/extent valid
media ranges valid
image decodable
video probe/playback valid
timing valid
target-specific relationships valid
no unintended source protocol residue
```

# 9. Source Inspector 与 Target Validator 的关系

它们不是同一个概念：

```text
Source Inspector
= 输入来源事实 / post-clean neutral check

Target Protocol Validator
= 我们刚生成的目标协议 conformance
```

可以共享底层 parser，但结果模型和判断规则必须分开。

适用时可以做：

```text
Target Writer
↓
Target Validator
↓
Source Inspector round-trip
```

Source Inspector 能重新识别目标协议是有价值的补充证据，但不能替代 Target Validator。

# 10. Protocol implementation lifecycle

具体厂商顺序不永久写死。

进入 P9 时根据：

```text
现有协议研究成熟度
真实样本
用户需求
真实设备可验证性
风险
```

逐个推进：

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

# 11. Existing Writer replacement

如果 P8 仍保留当前 production writer：

```text
不得先删再重写
```

每个 target 的切换顺序：

```text
new structured writer
↓
new target validator
↓
automated/real samples
↓
external/reference evidence
↓
real device acceptance
↓
product cutover
↓
remove corresponding obsolete writer
```

不保留双轨 automatic fallback。

# 12. Tests

每个 Writer/Validator 至少考虑：

- Neutral inputs from multiple source vendors；
- supported image/video container variants；
- HDR/SDR/GainMap where applicable；
- codec combinations；
- malformed Neutral rejection；
- wrong offset/reference/size mutations；
- missing/duplicate/conflicting target metadata；
- validator negative mutations；
- ffprobe/ExifTool/system decoder/reference checks where useful；
- target device import/playback/view；
- Source Inspector round-trip where useful。

# 13. Exit Criteria

某 target 只有在：

```text
protocol structural model is understood
+
Writer uses structured ownership/location
+
Target Validator reopens and reparses final file
+
target conformance rules pass
+
media is independently valid
+
real-device acceptance passes
```

后，才能从 Experimental 升为正式支持。

未理解/未验证的 variant 必须明确 Unsupported/Experimental，不能靠 magic patch 扩大兼容性。

## AI 执行硬规则

1. **先读当前 HEAD，再动代码。** Roadmap 定义目标、边界和验收，不替代当前实现事实；文档中记录的审计点必须在执行时重新核对。
2. **只执行当前阶段。** 未满足当前 Phase Gate，不提前实现后续阶段，不以“顺手整理”为理由扩大范围。
3. **验证证据必须与风险匹配。** 任何影响媒体产物、协议 correctness、兼容性或 preservation 的 Process，都必须同时具备代码证据、自动化测试和真实媒体样本证据；纯 build/platform/diagnostic infrastructure 使用与其风险匹配的 build smoke、dependency report、fault injection、platform proof 等证据。真实媒体样本不是 P7 才开始。
4. **测试失败必须可定位。** 错误应携带阶段、能力、关键输入事实、失败类别和技术原因；CLI/产品层必须能把核心错误清楚呈现。
5. **Production runtime 不恢复外部媒体 CLI 子进程依赖。** `ffmpeg.exe`、`ffprobe.exe`、`ExifTool.exe`、`jpegtran.exe`、`heif-enc.exe`、`heif-dec.exe`、`magick.exe` 等可以用于研究、测试和独立验证，但不能重新成为正式处理后端。
6. **第三方 C/C++ 媒体库是候选能力，不是预先锁定的架构承诺。** 是否采用、如何裁剪、静态或动态链接、平台 backend 组合等，由 P4 的实际证据决定；不得因为 Roadmap 举例提到某个库就机械引入，也不得为了“全部自研”重复实现成熟 codec。
7. **C# 是 Control Plane；Native 是 result-affecting Data Plane。** 产品级命名、目标目录、队列、UI/CLI orchestration 由 Core 管理；底层临时文件、原子 replace/publish 等事务 primitive 可以保留在 Native，但应置于明确的 platform/filesystem boundary。UI-only 的缩略图、预览、显示缩放和缓存不强制进入 Native。
8. **不猜协议事实，也不猜 Writer 写入位置。** 字符串命中、固定 offset、同文件名、magic number、`hit + N` 只能用于 research/candidate/evidence，不能直接成为 destructive authority 或 production Writer authoritative write location。
9. **源文件默认不原地覆盖。** Destructive/repair/write 流程优先使用新文件或安全临时输出；只有在结构与媒体验证通过后，才进入正式 publish。失败或取消不得留下“看起来成功”的半成品。
10. **所有 Done 都由证据宣布。** 代码存在、API 返回成功、文件能打开、播放器能播放，都不能单独证明阶段完成。
11. **当前 Roadmap 不主动修改用户可见文档。** README、CLI 用户手册、商店/Release 用户说明等，只有用户明确要求时才更新。

# P8 — Split / Merge Product Foundation

> **Entry:** P1–P7 credible  
> **Goal:** 把 GUI / CLI / Core 的 Split 与 Merge 产品骨架切到稳定 Rebuilt contracts，同时为 P9 提供清晰的 Target Writer orchestration boundary。  
> **Non-goal:** 在 P8 重写正式 Target Protocol Writers。  

# 1. P8 的准确定位

P8 负责的是：

```text
产品调用链
typed selections
input discovery/preparation
Neutral preparation
queue/batch/progress/cancel
output naming/destination
error presentation
Writer orchestration boundary
```

P9 才负责：

```text
目标厂商协议结构研究
structured Target Writer
Target Protocol Validator
old Writer replacement/cutover
real-device target conformance
```

因此 P8 与 P9 不重复施工。

# 2. Flow-by-flow Cutover

禁止“大爆炸迁移”。

每一个**不涉及重写目标协议结构**的产品 Flow：

```text
identify current behavior
→ route through new Rebuilt contracts
→ automated tests
→ real user/media samples where result-affecting
→ CLI integration
→ GUI integration
→ compare outputs/behavior
→ prove obsolete product plumbing unused
→ remove obsolete plumbing
→ next flow
```

# 3. P8 要迁移的产品能力

至少包括：

```text
single split
batch split

single merge input discovery/preparation
batch merge orchestration
neutral/media preparation
output profile selection
target writer invocation boundary

output naming
destination/collision policy
queue/progress/cancel
error presentation
history/execution record integration where applicable
```

具体以当前 HEAD 的真实产品功能为准。

# 4. Existing Writer 的过渡规则

如果进入 P8 时已有 production Target Writer：

```text
可以暂时通过明确 adapter/boundary 调用
```

但 P8 不得：

```text
把旧 Writer 当作新 Writer 架构已完成
为“迁移 Flow”顺手大规模改写 Apple/Google/Samsung/... protocol structure
在没有 P9 replacement + validator 证据前删除仍有合法调用者的 target writer
```

P8 可以删除的是：

```text
obsolete product plumbing
legacy routing shell
duplicated UI/Core branching
dead adapters
hidden fallback
```

不是“所有旧 Target Writer 实现”。

每个 Writer 的最终替换/删除发生在 P9：

```text
new structured writer
→ validator
→ external/device evidence
→ product cutover
→ remove corresponding obsolete writer
```

# 5. Strongly Typed Core Boundary

P8 清理：

```text
protocolIndex
formatIndex
splitProtocolIndex
```

等 UI 下标向 Core 的长期泄漏。

统一概念：

```text
TargetProtocol
MediaFormatRequirement
OutputProfile
```

其中：

```text
TargetProtocol
= 目标协议 identity

MediaFormatRequirement
= 通用媒体格式/codec requirement

OutputProfile
= TargetProtocol + MediaFormatRequirement + target-specific options
```

UI：

```text
dropdown index
↓
UI Adapter
↓
typed values
↓
Core
```

不要因为本文机械新建重复 profile model；先复用/演进当前已有 typed model。

# 6. Split

Split 在 P8 应真正完成产品级 Rebuilt cutover：

```text
input
↓
Source/Neutral foundation
↓
selected output media requirement
↓
publish
```

Split 产品层不能再自己复制 protocol parser、offset 或 external-tool logic。

# 7. Merge

P8 的 Merge 目标：

```text
input image/video
↓
typed discovery
↓
media preparation / NeutralMediaBundle
↓
OutputProfile
↓
explicit Target Writer boundary
```

在 P9 Writer 尚未替换时，产品可以明确调用当前受支持 writer。

未支持 target/variant 必须：

```text
Unsupported / Experimental
```

不能 silent fallback 到旧 hidden path。

# 8. Tests

P8 至少覆盖：

```text
Split GUI/CLI
Split batch
Merge preparation GUI/CLI
Merge batch orchestration
typed selection mapping
cancellation
invalid input
output collision
diagnostic mapping
no hidden Legacy/external fallback
no UI index leakage beyond adapter boundary
```

真实媒体样本用于验证结果相关 Flow；纯 UI mapping 可以使用 contract/integration tests。

# 9. Exit Criteria

- Split product path 全走 Rebuilt contracts；
- Merge input/neutral preparation 全走 Rebuilt contracts；
- GUI/CLI 共享 typed Core contracts；
- `OutputProfile` boundary 明确；
- Core 不长期依赖 UI index；
- obsolete product plumbing 已删除；
- existing target writers 被隔离在明确 writer boundary 后，不被 P8 偷偷重写；
- 未支持 target 明确失败；
- P9 可以逐个替换 Target Writer，而无需重新设计 GUI/CLI/Core 主流程。

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

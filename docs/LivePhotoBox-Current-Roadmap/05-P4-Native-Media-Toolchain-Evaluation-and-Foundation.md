# P4 — Native Media Toolchain Evaluation & Foundation

> **Status:** Planned / HARD GATE BEFORE P5  
> **Goal:** 在真正收口 Converter 前，建立可复现、可裁剪、可维护、跨平台友好的 Native media foundation，并通过证据选择实际 backend。  
> **Critical rule:** 本阶段不预先锁死视频编码、HEIC、JPEG、color 或 build-system 方案。  
> **Audit snapshot:** `master @ c99876956c4df59864014d942c1e81921ae289f1`；执行时必须重新扫描当前 HEAD。  
> **Next:** P5 — Converter Reliability  

# 1. P4 的正确含义

P4 不是：

```text
Roadmap 写了 FFmpeg
→ 必须用 FFmpeg

Roadmap 写了 libheif
→ 必须用 libheif

为了跨平台
→ 先删除 WIC / Media Foundation

为了“纯 Native”
→ 自己重写 JPEG / HEVC / HEIF codec
```

P4 是：

```text
inventory actual processing needs
↓
freeze current baseline
↓
建立 platform/build/dependency foundation
↓
列 candidate
↓
minimal POC
↓
real-media differential
↓
security/license/size/performance/portability review
↓
Accepted / Rejected / Deferred decision
↓
为 P5 提供明确 backend contract
```

候选被拒绝也是有效结果。

# 2. Current Baseline 不是最终方案

在 `c998769...` 快照：

```text
LivePhotoBox.Native
= Windows x64 MSVC DLL / .vcxproj

cross-container image conversion
= Windows Imaging Component

cross-codec video transcode
= Windows Media Foundation

project-owned ISO-BMFF/container/protocol logic
= 已存在并继续保留
```

这只是当前 reference baseline。

执行 P4 时必须重新确认真实 HEAD，不能根据本文假设这些事实永远不变。

# 3. Capability Inventory

先回答 Live Photo Box **真正需要什么**，再选库。

至少区分：

```text
JPEG structure
JPEG pixel decode/encode
JPEG lossless transform
HEIF/HEIC structure
HEIC pixel decode/encode
ISO-BMFF structure
MOV/MP4 remux
H264/HEVC transcode
audio handling
color / ICC
HDR / GainMap pixel processing
UI thumbnail/preview
filesystem/path/atomic publish
```

每项记录：

```text
current backend
result-affecting?
platform
runtime dependencies
known preservation behavior
test coverage
candidate replacement needed?
```

# 4. Protocol truth 与 media primitive 的边界

Live Photo Box 自己掌握：

```text
Live/Motion Photo protocol semantics
source/target ownership
exact ranges
pairing identity
vendor metadata meaning
container mutation rules
Target Writer placement
Neutral contract
```

第三方库可以承担：

```text
JPEG encode/decode
HEIC encode/decode
H264/HEVC encode/decode
generic media primitives
color transform
resample/scale where actually required
```

引入 generic media framework 不意味着删除已经可靠的 project-owned protocol/container truth。

# 5. Platform Foundation

当前 Native 中存在仅因：

```text
UTF-8 / UTF-16 path
atomic file replace
filesystem publish
platform capability
```

而直接依赖 Windows API 的代码。

P4 应建立清晰的 platform/filesystem boundary，使协议、container、extract/clean 等 portable core 不因无关 plumbing 锁死某个平台。

目标语义可以包括：

```text
path conversion
safe remove
atomic replace/publish
filesystem capability
platform error mapping
```

具体 API、目录、类名由执行时当前代码决定。

不要求为了这个步骤顺手重写 Cleaner/Extractor/Converter。

# 6. Build & Dependency Foundation

P4 必须建立**可复现**的 Native build/dependency strategy。

至少能明确表达：

```text
dependency name
exact version/tag/commit
source
hash/checksum where applicable
patches
enabled features
disabled features
static/dynamic
platform
runtime binary list
license/security record
```

Build graph 必须适合未来：

```text
Windows
+
portable/non-Windows Native core
+
third-party native dependencies
+
feature options
```

Roadmap **不规定必须 CMake、Meson、vcpkg 或其他方案**。

选择 build/dependency 方案本身也必须在 P4 基于：

```text
可维护性
CI
依赖集成
跨平台
可复现性
开发体验
```

做决定。

当前 `.vcxproj` 不应在 replacement 尚未证明 parity 时被机械删除。

# 7. Cross-platform Proof

P4 的跨平台目标是：

> **证明 result-affecting Native core 的架构和 build foundation 不被无必要的 Windows-only plumbing 锁死。**

不要求：

```text
Linux GUI
macOS GUI
完整跨平台 C# Core
立即发布 Linux CLI
```

根据执行时条件，至少建立：

```text
portable core build proof
或
明确记录仍阻塞 non-Windows build 的依赖/模块
```

不能为了“跨平台”牺牲当前 Windows correctness。

# 8. Candidate Pool 不是 Architecture Commitment

可以研究但不限于：

```text
JPEG:
  libjpeg-turbo
  focused JPEG codec/lossless-transform alternatives
  platform backend

HEIC:
  libheif-based stack
  platform codec
  other maintained HEIC stacks

Video:
  current Media Foundation
  minimal FFmpeg libav* library build
  focused codec/container libraries
  platform-specific backend set behind one contract

Color:
  lcms2
  backend-provided color management
  other evidence-supported option
```

Roadmap 中出现名字只表示“值得评估”。

# 9. Candidate Evaluation

每个 result-affecting candidate 至少评估：

```text
Capability need
→ minimal POC
→ automated tests
→ real-device/media samples
→ current-backend differential
→ quality/preservation
→ malformed/security behavior
→ performance/memory
→ binary size/runtime dependencies
→ portability
→ maintenance
→ license/distribution
→ decision
```

最终状态：

```text
Proposed
Trial
Accepted
Rejected
Deferred
Reference-only
```

# 10. Minimal Build / Packaging Rule

若候选库能力很大，应由产品真实需求反推最小 feature set。

例如大型 media framework 可能需要关闭：

```text
CLI frontend
network
unused protocols
unused demuxers/muxers
unused decoders/encoders
unused filters
unrelated devices/features
```

具体关闭项必须来自真实 dependency graph，而不是照抄模板。

目标：

> **发布包里每个 native binary 和启用 feature 都有明确用途。**

# 11. UI-only Dependencies

只服务：

```text
thumbnail
preview
display resize
UI cache
```

的库不要求迁移到 Native。

同一 library 同时用于 UI 和正式 processing 时，按调用点拆分职责，不做“全删/全留”的粗暴决定。

# 12. Video 特别规则

必须区分：

```text
container/remux
vs
codec/transcode
```

项目已有可靠 ISO-BMFF remux 时，默认保留并继续验证，不因为选中大型 media library 就无条件替换。

跨 codec transcode backend 才单独做候选评估。

允许统一 Converter contract 下不同平台使用不同已批准 backend，但：

```text
capability
fallback policy
execution record
preservation semantics
```

必须一致且可诊断。

这类 backend fallback 仍属于 Rebuilt，不等于恢复 Legacy product fallback。

# 13. Audio / Color / HDR Foundation

P4 要让 P5 能诚实表达：

```text
audio bitstream copied
audio decoded/reencoded
audio dropped
no audio
unsupported
```

以及：

```text
ICC preserved/converted
HDR/GainMap preserved
HDR degraded
orientation normalized
```

GainMap 语义仍由项目自己掌握；codec/pixel backend 只处理通用媒体 primitive。

# 14. Dependency Decision Record

每个正式 candidate/decision 至少记录：

```text
Capability:
Current implementation:
Why evaluate/change:
Candidate:
Exact version/source:
Enabled features:
Disabled features:
Static or dynamic:
Platforms:
Runtime binaries:
Binary/package delta:
License/distribution:
Security status:
Real-media tests:
Differential result:
Known gaps:
Approved fallback inside Rebuilt:
Removal plan for obsolete dependency:
Decision:
```

# 15. P4 Exit Gate

P4 完成不要求“所有旧 backend 全部删除”。

必须满足：

1. result-affecting capability inventory 完整；
2. current baseline 与 differential samples 已建立；
3. platform/filesystem boundary 足够清晰；
4. build/dependency strategy 可复现；
5. dependency version/features/runtime binary 可追踪；
6. P5 必需的 JPEG/HEIC/video/audio/color/HDR 能力已有 Accepted backend，或明确 Unsupported/Deferred 且不会被产品误宣称支持；
7. result-affecting candidate 有真实媒体证据；
8. binary size/runtime dependency report 明确；
9. license/security 状态有记录；
10. cross-platform/portable-core 状态有证据或明确 blocker；
11. 未恢复 external media CLI production backend；
12. 不继续为了“潜在未来需求”无限扩张 P4。

> **P4 的成功是“有证据的 foundation + decision”，不是“必须选中某一个预设库”。**

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

# P4 — Native Platform / Media Backend / Build Foundation

> **Status:** Planned / HARD GATE BEFORE P5  
> **Goal:** 在 Converter 收口前，收紧 Windows production 所需的 Native/media/build 边界，并只保留未来可选接入所需的最小接口缝。
> **Current production target:** Windows x64  
> **Future platform stance:** WebAssembly/Linux/macOS 仅保留可能性，当前不实现、不排期、不预先选定其 backend。
> **Important:** P4 不为假设的跨平台需求增加抽象层。只有当 Windows-specific 依赖确实会污染协议/容器真相时，才放到一个小而稳定的 interface 后面。未来真要接入新平台时另立任务。

---

# 1. P4 的核心任务

P4 必须建立：

```text
Windows production Native Core
+
small PlatformFilesystem seam where Windows APIs are unavoidable
+
small Media Backend seam where codec/backend substitution is real
+
reproducible Windows build/dependency identity
+
capability/runtime identity
```

不要在 P4 预先实现 Web/WASM/Linux/macOS backend。“未来可接”只意味着当前边界不被无理写死，不意味着必须接入。

P4 完成后，P5–P10 不应再直接在业务模块中随意引入：

```text
Windows.h
WIC
Media Foundation
new codec library
new filesystem publish implementation
```

任何新增平台/媒体能力都通过既有 backend contract 进入。

---

# 2. 已拍板的默认技术栈

这些是默认决策，不是平级候选池：

| Capability | V4.0 default |
|---|---|
| Canonical Native build | **CMake** |
| Windows dependency declaration | **vcpkg manifest** as primary strategy |
| JPEG decode/encode | **libjpeg-turbo** |
| JPEG lossless transform | **libjpeg-turbo transform API** |
| HEIF/HEIC generic codec gateway | **libheif** |
| HEIC HEVC decode | **libde265 via libheif** |
| HEIC HEVC encode | **x265 via libheif** |
| Alternative HEIC encoder | **Kvazaar** as benchmark/fallback candidate |
| ICC transform | **lcms2 only when actual transform is required** |
| ISO-BMFF/MOV/MP4 protocol/container truth | **LivePhotoBox project-owned engine** |
| Windows file I/O / atomic publish | **Win32 backend behind PlatformFilesystem** |
| Production external media CLI | **Forbidden** |

默认方案只有在：

```text
real compatibility evidence
correctness failure
preservation failure
security/license/distribution blocker
measurable severe performance issue
```

出现时才允许替换。

“另一个库看起来也可以”不是换技术栈的理由。

---

# 3. 决策优先级

所有 P4 benchmark 与 dependency decision 按：

```text
1 correctness
2 real-device compatibility
3 preservation / HDR / metadata / color
4 stability / malformed / failure safety
5 output quality
6 performance / hardware acceleration
7 maintainability
8 portability
9 license / distribution / patent handling
10 binary/package size
11 dependency count
```

排序。

这意味着：

```text
更大但兼容性明显更好
→ 选更兼容的

多个专用库明显更稳
→ 可以多个

一个库可可靠覆盖多个能力
→ 优先复用，减少重复 runtime
```

---

# 4. Capability Inventory

执行 P4 时先扫描当前 HEAD，至少建立以下表：

```text
JPEG structure
JPEG decode/encode
JPEG lossless transform
TIFF/EXIF/XMP/MakerNote
HEIF structure
HEIC decode/encode
HEIF auxiliary image handling
ISO-BMFF/MOV/MP4 structure
video structural probe
MOV/MP4 generic remux
H264/HEVC transcode
audio
orientation
ICC/color
HDR/GainMap pixel processing
SHA/hash
filesystem/random access
temp/transaction/atomic publish
UI-only thumbnail/preview
```

每项记录：

```text
current owner
current backend
result-affecting?
platform coupling
runtime dependency
duplicate capability
preservation risk
P4 action
```

---

# 5. Windows-only Contamination Audit

P4 必须系统扫描 Native：

```text
Windows.h
HANDLE
CreateFileW
GetFileAttributesW
DeleteFileW
MoveFileExW
GetTempFileNameW
GetLastError
MAX_PATH
IWIC*
IMF*
MF*
strncpy_s / _TRUNCATE
其他 MSVC-only API
```

分类：

## A — Legitimate Windows backend

例如：

```text
Windows atomic publish
Windows file open flags
Media Foundation backend
possible WIC backend
```

允许保留，但移动到明确 platform/backend module。

## B — Accidental platform coupling

例如：

```text
protocol parser 为了复制字符串用 strncpy_s
cleaner 为了发布文件直接 MoveFileExW
SHA algorithm 接受 HANDLE
Inspector 因 video probe include converter/MF
```

应消除或通过 adapter/backend 收口。

## C — Current Windows correctness-critical code

如果移动会在 P4 造成高风险：

```text
先建立 contract
→ 保留旧 implementation behind adapter
→ regression
→ 再替换
```

禁止为“跨平台看起来干净”进行无证据大爆炸重写。

---

# 6. PlatformFilesystem Contract

P4 建立统一低层 platform contract，语义至少覆盖：

```text
open read-only/random-access source
open streaming sink
safe temp file/workspace
read exact range
write all
flush
close
exists/stat/size
safe remove
atomic publish
atomic replace when explicitly allowed
platform capability query
platform error mapping
```

当前实现：

```text
WindowsPlatformFilesystem
→ Win32 / std::filesystem where appropriate
```

Portable Core 不直接知道：

```text
HANDLE
DWORD
Windows path encoding
MOVEFILE_* flags
```

### 6.1 Path policy

Public C ABI：

```text
UTF-8
```

内部 platform adapter 负责：

```text
UTF-8 ↔ native path representation
```

不让 `wchar_t*` / Windows path 成为核心协议 API。

### 6.2 Transaction rule

不要在：

```text
image_converter.cpp
video_converter.cpp
media_cleaner.cpp
media_extractor.cpp
mp4_strip.cpp
vendor cleaner
```

分别实现自己的 atomic publish。

应收敛成共享：

```text
transaction / publish primitive
```

---

# 7. Portable Byte / Stream Foundation

为了未来 WASM 与大文件，不应只有 path API。

应逐步形成：

```text
ByteSpan / MutableByteSpan
RandomAccessReader
SequentialWriter
ArtifactSource/Sink
```

或当前代码最自然的等价 contract。

目标：

```text
Windows file
WASM ArrayBuffer / browser-backed source
POSIX file
memory fixture
```

可复用同一 protocol/container engine。

不要求 P4 一次把所有 path API 删除；要求 canonical core primitive 不再只能由 Win32 file path 驱动。

---

# 8. CMake Migration

## 8.1 Canonical build

P4 后：

```text
CMakeLists.txt
= Native build authority
```

至少支持当前正式 target：

```text
Windows x64 / MSVC
```

并让 build graph 能表达：

```text
portable core
windows backend
media dependencies
feature flags
tests
```

## 8.2 `.vcxproj` 迁移规则

当前 `.vcxproj`：

```text
保留
→ CMake Windows parity
→ tests/artifact/debug workflow parity
→ 再删除或降级为 generated/compatibility entry
```

不允许先删再修。

## 8.3 Future build readiness

本阶段不要求发布 Linux/Web，但 CMake graph 应允许未来：

```text
Emscripten
GCC/Clang
AppleClang
```

接入，而不复制 protocol sources。

---

# 9. Dependency Management

Windows Native 默认：

```text
vcpkg manifest
```

P4 必须记录：

```text
dependency
exact version/baseline
source
features
disabled features
static/dynamic
license
runtime binaries
patches
hash/checksum where appropriate
```

如果某依赖用 vcpkg 无法可靠满足：

```text
允许 CMake FetchContent / pinned source / vendored patch
```

但必须解释原因，不能形成第二套无版本控制的依赖管理。

---

# 10. JPEG Foundation — libjpeg-turbo

## 10.1 Ownership

```text
LivePhotoBox:
JPEG marker structure
APP1
EXIF
XMP
MakerNote
MPF/Ultra HDR/vendor semantics

libjpeg-turbo:
JPEG pixel decode
JPEG pixel encode
lossless DCT transform where applicable
```

## 10.2 Migration goal

逐步替代 result-affecting path 中：

```text
WIC JPEG codec dependency
jpegtran.exe legacy capability
Magick.NET JPEG codec usage
```

但 UI-only preview 不要求迁移。

## 10.3 Required capability proof

```text
decode common real JPEG
encode device-compatible JPEG
quality controls
grayscale/RGB handling
EXIF orientation workflow
lossless rotate/flip transform
metadata reattachment/preservation contract
large-file/memory behavior
```

---

# 11. HEIF/HEIC Foundation — libheif

## 11.1 Ownership

```text
LivePhotoBox:
HEIF structural truth
item/extent/reference semantics
Exif/XMP placement semantics
Apple/Huawei/Samsung/vendor rules
GainMap semantic identity
target protocol structure

libheif:
generic HEIF image codec access
decode/encode
generic image item codec plumbing
```

libheif 的 parser 结果可以作为通用 codec input/evidence，但不是 vendor protocol authority。

## 11.2 Default codec stack

```text
Decode:
libheif → libde265

Encode:
libheif → x265
```

Kvazaar：

```text
benchmark / fallback candidate
```

不默认为了“少 GPL 依赖”替换 x265；compatibility first。

## 11.3 x265 distribution gate

仓库当前为 GPLv3。  
x265 使用 GPL v2-or-later / commercial dual licensing；P4 必须把：

```text
software license compatibility
source/binary redistribution obligations
HEVC patent/licensing considerations
store/distribution requirements
```

作为正式 dependency record。

> License compatibility 与 codec patent rights 是两个不同问题，均需记录。

如果未来 distribution channel 对 x265 形成实际 blocker：

```text
再评估 Kvazaar / platform encoder / other approved encoder
```

不能提前因为理论担忧牺牲实际兼容性。

## 11.4 Minimal HEIF build

禁用 Live Photo Box 不需要的：

```text
unused codecs
examples
CLI tools
plugins when not needed
AVIF/JPEG2000/VVC etc. if no product requirement
```

是否 static/plugin 由 package/security/update 证据决定。

---

# 12. HDR / GainMap Native Migration

当前正式目标是：

> **所有会改变 HDR/GainMap 正式产物的逻辑收口到 Native。**

P4/P5 边界：

P4 建 foundation：

```text
pixel access
HEIC primary/aux decode/encode capability
JPEG codec
color primitive
portable GainMap processing host
```

P5 完成可靠转换 semantics。

需要逐步退出 result-affecting：

```text
C# Magick.NET pixel math
C# HEIC external-tool-era helper assumptions
result-affecting fallback that silently drops GainMap
```

但：

```text
GainMap math
Apple/ISO metadata semantics
Ultra HDR structure
auxiliary ownership
```

仍由 LivePhotoBox 自己掌握。

---

# 13. Color Management — lcms2 Optional

lcms2 是 **approved optional dependency**。

只有发生真实：

```text
ICC A → pixel color transform → ICC B
Display P3 → sRGB
other explicit profile conversion
```

时引入。

如果只是：

```text
ICC bytes preserve
```

禁止为了形式主义调用 color engine。

P4 应先证明实际 Converter path 是否需要 lcms2 runtime；若不需要：

```text
do not ship it
```

---

# 14. Video Backend — 主要剩余决策

视频是 P4 唯一需要认真“比赛”的大项。

候选：

```text
A. Windows Media Foundation
B. minimal FFmpeg libav* library build
C. hybrid per-capability backend
```

禁止：

```text
ffmpeg.exe production subprocess
```

## 14.1 为什么不能现在强行统一

Media Foundation 可能在 Windows 赢在：

```text
inbox runtime
hardware MFT
GPU acceleration
small package footprint
Windows integration
```

minimal libav* 可能赢在：

```text
codec/control consistency
diagnostics
portability
format coverage
future Linux/Web reuse
```

因此用真实 Live Photo 视频做 differential。

## 14.2 Benchmark corpus

至少覆盖：

```text
Apple HEVC/H264 MOV
Google/Xiaomi MP4
OPPO/OnePlus
vivo
Samsung
Huawei/Honor
audio/no-audio
rotation
10-bit where applicable
HDR/color metadata where applicable
odd dimensions
VFR/timing variants
```

## 14.3 Metrics

```text
device compatibility
output structural validity
quality
codec profile/level
audio
rotation/timing
color/HDR metadata
CPU
GPU
elapsed time
memory
failure/cancel behavior
binary/package delta
maintenance
```

## 14.4 Decision rule

```text
MF clearly better on Windows
→ WindowsVideoBackend = MF

libav* equal or better overall
→ portable backend may become Windows default

different capabilities have different winners
→ hybrid backend allowed
```

只要 backend identity、fallback、preservation semantics 可诊断即可。

---

# 15. WIC Policy

WIC 不再是默认跨平台 image foundation。

允许保留的情况：

```text
某个 Windows-specific image capability
在 compatibility/performance/system integration 上
有可证明优势
```

否则：

```text
JPEG → libjpeg-turbo
HEIC → libheif stack
```

优先。

P4 需要决定是否还有任何 **result-affecting WIC path** 值得长期保留。

UI preview/thumbnail 不受此限制。

---

# 16. Dependency Consolidation Gate

对最终 Windows package 建 capability graph：

```text
dependency
↓
which features use it?
↓
which codecs does it duplicate?
↓
can an existing dependency cover the same need?
↓
does removal hurt compatibility?
```

原则：

> **先兼容、正确、稳定；再考虑总体体积；最后才考虑“用了几个库”。**

典型禁止：

```text
libde265 + second HEVC decoder
only because both happened to be easy to add

ImageMagick + libjpeg-turbo
both serving the same production JPEG conversion with no evidence

two atomic publish implementations
```

典型允许：

```text
libde265 for HEIC
+
Media Foundation HEVC for video

if each is demonstrably the best/most compatible in its domain
```

---

# 17. Runtime Capability & ExecutionRecord

P4 建立可诊断 capability identity。

至少能够回答：

```text
which backend is available?
which codec path was chosen?
hardware/software?
library/backend version?
fallback happened?
why?
```

P5 的 ExecutionRecord 应能记录：

```text
operation class
backend
codec
transform kind
preservation outcome
fallback
quality/degradation
```

不得：

```text
fallback happened
→ output still called "preserved"
```

---

# 18. Portable-core Proof

P4 不要求完整 Linux/Web 产品。

但至少完成一种结构证明：

### Preferred

建立一个：

```text
livephotobox_portable_core
```

目标，不链接 Win32/WIC/MF，包含：

```text
binary
metadata
protocol/container
neutral structural primitives
```

并在 Windows compiler 上证明它可独立构建。

### Stronger optional proof

如果成本合理：

```text
Emscripten compile smoke
or
non-Windows Clang compile smoke
```

仅用于发现污染，不构成 Linux/Web 产品支持承诺。

---

# 19. P4 Exit Gate

P4 只有全部满足以下条件才能进入 P5：

1. Platform / Media backend contracts 已存在且职责清楚；
2. scattered Win32 filesystem/publish logic 已收口或有明确剩余清单；
3. CMake Windows build 成为 canonical 且与当前生产能力 parity；
4. dependency manifest 可复现；
5. libjpeg-turbo foundation 可用；
6. libheif foundation 可用；
7. libde265/x265 HEIC default path 有真实样本与分发记录；
8. HDR/GainMap Native foundation 已能支撑 P5；
9. video backend comparison 已完成，Windows P5 backend 已冻结；
10. lcms2 是否需要已由实际 path 决定；
11. WIC 是否保留 result-affecting path 已明确；
12. duplicate codec/runtime report 完成；
13. runtime binaries/features 有用途说明；
14. portable-core build proof 通过或 blocker 被明确记录；
15. production runtime 无 external media CLI；
16. package footprint 有 baseline，但未以体积牺牲 compatibility；
17. P5 不再需要重新发明 build/backend architecture。

> **P4 成功的定义：长期骨架已经定型，P5 只需要把 Converter 正确接上它。**

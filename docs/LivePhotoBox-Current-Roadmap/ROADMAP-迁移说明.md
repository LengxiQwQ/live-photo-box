# Roadmap V4.0 迁移说明

> **Status:** Final migration guide  
> **Purpose:** 说明 V3.2 → V4.0 的架构变化；不拥有阶段调度权。  
> **Authority:** `00-重构总纲-唯一执行路线.md`

---

# 1. 文件结构

```text
docs/LivePhotoBox-Current-Roadmap/
├─ 00-重构总纲-唯一执行路线.md
├─ 01-P0-仅Rebuilt运行时收尾.md
├─ 02-P1-来源检测器可靠性.md
├─ 03-P2-提取器可靠性.md
├─ 04-P3-清理器可靠性.md
├─ 05-P4-Native平台与媒体后端及构建基础.md
├─ 06-P5-转换器可靠性.md
├─ 07-P6-中性流水线收尾.md
├─ 08-P7-系统高压与回归测试 campaign.md
├─ 09-P8-拆分与合成产品骨架.md
├─ 10-P9-目标协议写入器与验证器.md
├─ 11-P10-修复功能.md
├─ 12-中性媒体契约.md
├─ 13-未来工作规划.md
└─ Historical/
```

文件编号保持不变，避免无意义 rename churn。

---

# 2. V4.0 为什么要改

V3.2 已经正确建立：

```text
Source → Neutral → Target
C# Control Plane / Native Data Plane
P0–P10
Target Writer + Validator
```

V4.0 进一步解决：

```text
Native 仍有较多 Win32/MSVC plumbing 混入 result-affecting modules
P4 技术候选过于开放，后续 AI 可能重复选型
跨平台目标写得太抽象
依赖数量/包体/重复 codec 缺少硬规则
Web/Linux/macOS 的边界没有按真实优先级冻结
后续 Phase 没有统一 backend-neutral contract
```

---

# 3. V4.0 最关键的新决策

## 3.1 当前仍只交付 Windows

```text
Windows x64
= current formal production target
```

不要求现在实现 Linux/macOS/Web。

## 3.2 未来优先级

```text
Web/WASM first
Linux later
macOS optional/deferred
```

## 3.3 平台规则

不是：

```text
禁止 Windows API
```

而是：

```text
generic/portable solution clearly better
→ portable

roughly equal
→ portable preferred

Windows native clearly better
→ Windows backend
```

Protocol truth 不分平台。

---

# 4. P4 从“开放选型”变成“默认技术栈 + 少数 benchmark”

V3.2：

```text
libjpeg-turbo / libheif / FFmpeg / CMake
只是候选
```

V4.0：

```text
CMake                         DEFAULT
vcpkg manifest                DEFAULT Windows dependency strategy
libjpeg-turbo                 DEFAULT JPEG
libheif                       DEFAULT HEIF gateway
libde265                      DEFAULT HEIC decode
x265                          DEFAULT HEIC encode
Kvazaar                       benchmark/fallback candidate
lcms2                         optional only if real ICC transform required
project-owned ISO-BMFF        permanent protocol/container truth
Win32 PlatformFilesystem      current platform backend
```

真正主要待决：

```text
Windows video:
Media Foundation
vs
minimal libav*
vs
hybrid
```

以及 exact linking/packaging details。

---

# 5. x265 决策

V4.0 不因“依赖少/许可证看起来简单”优先 Kvazaar。

默认：

```text
libheif → x265
```

原因排序：

```text
compatibility
maturity
quality
stability
performance
then package size/dependency count
```

但 P4 必须记录：

```text
GPL v2-or-later / commercial licensing
GPLv3 project distribution compatibility
source/binary obligations
HEVC patent/licensing considerations
store/distribution constraints
```

如果真实 distribution blocker 出现，再换 approved encoder。

---

# 6. Dependency Consolidation Rule

新增硬规则：

> 不追求“库数量最少”，追求“正确、兼容、稳定前提下，总 runtime 不重复、总 feature footprint 合理”。

禁止：

```text
一个小功能引一个大型 framework
同一种 codec 重复带两套却无证据
因为 library 顺便支持就开启无用 feature
```

允许：

```text
多个专用库
```

只要它们各自确实是最佳能力。

---

# 7. Win32 处理方式

V4.0 不要求删除：

```text
MoveFileExW
CreateFileW
Media Foundation
```

而要求：

```text
platform concern
→ Platform Backend

media backend concern
→ Media Backend

protocol/container truth
→ Portable Core
```

P1–P3 若现存 Win32 只是 plumbing：

```text
P4 收口
```

不为形式主义回头重写正确协议逻辑。

---

# 8. CMake

CMake 现在是 canonical Native build direction。

`.vcxproj`：

```text
过渡保留
→ CMake Windows parity
→ tests/debug/artifacts parity
→ 再降级/删除
```

不先删。

---

# 9. C# 不强行跨平台

当前：

```text
WinUI
Windows Core
Windows product integration
```

可以继续 Windows-specific。

真正要求 portable：

```text
result-affecting Native core
```

未来 Web 直接用 WASM，不需要复用 C# Core。

---

# 10. Neutral Contract 新增

V4.0 新增：

```text
platform-neutral semantics
backend-neutral semantics
backend success != semantic success
artifact 不要求永远是 Windows path
```

Writer/Validator 不因 backend 改变 correctness rules。

---

# 11. Future Work 修正

Future Work 明确：

```text
Web/WASM first
Linux second
macOS optional
```

Web 优先静态网站 + 本地 WASM，能力可小于 Windows desktop。

---

# 12. 全局硬规则去重

V3.2 每个 Phase 复制相同 11 条规则。

V4.0 改为：

```text
00
= global execution hard rules

P0–P10
= phase-specific rules only
```

减少未来修改漂移。

---

# 13. 不修改用户文档

本次 Roadmap 更新不自动要求修改：

```text
README.md
README.zh-CN.md
CLI user guides
Release notes
Store listing
```

只有用户明确要求时更新。

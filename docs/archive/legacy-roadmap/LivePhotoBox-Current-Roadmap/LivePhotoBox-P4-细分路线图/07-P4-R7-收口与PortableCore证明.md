# P4-R7 — Capability Identity / Dependency Consolidation / Portable-Core Proof / P4 Closure

> **性质：** P4 最终施工收口轮。  
> **目标：** 把 R1–R6 的基础设施变成一个完整、可诊断、可打包、可进入 P5 的稳定骨架，并逐项关闭 P4 Exit Gate。

---

## 1. 本轮不再做什么

R7 不是：

- 再选一次 JPEG/HEIC/video backend；
- 大规模重写 P1–P3；
- 开始 P5 Converter semantics；
- 开始 Linux/Web/macOS 产品；
- 为追求“零 Windows API”继续抽象。

如果 R1–R6 的核心技术决策仍未完成，应回到相应轮次，不要在 R7 模糊收尾。

---

## 2. Runtime Capability Identity

P4 必须能回答：

```text
which backend is available?
which codec path is active?
backend/library version?
hardware or software?
fallback happened?
why?
```

为 P5 的 ExecutionRecord 奠定基础。

至少让正式 runtime/diagnostic layer 能表达：

```text
operation/capability class
backend
codec
version where meaningful
hardware/software
fallback
fallback reason
```

P5 再扩展完整：

```text
transform kind
preservation outcome
quality/degradation
```

---

## 3. Dependency Consolidation

重新画最终 Windows production capability graph：

```text
dependency
   ↓
which feature uses it?
   ↓
which codec/runtime does it provide?
   ↓
does another dependency duplicate it?
   ↓
is duplication justified by evidence?
```

原则：

```text
correctness / compatibility / stability first
package size later
library count last
```

允许：

```text
libde265 for HEIC
+
Media Foundation HEVC for video
```

前提是它们在各自领域有明确价值。

不允许：

```text
两个 HEVC decoder 都只是“顺手加了”
两个 atomic publish implementation
ImageMagick + libjpeg-turbo 同时承担同一 production JPEG conversion 且无证据
```

---

## 4. Runtime Binary / Feature Purpose Table

最终 package 中每一个 native runtime binary/plugin/codec feature 都要能说明：

```text
谁使用？
用于什么 capability？
是否 production required？
是否 duplicate？
license/distribution status？
能否安全移除？
```

未被调用的 feature 不应因为依赖默认配置而进入正式 package。

---

## 5. Production External CLI Audit

最终再次代码级扫描并追调用链，确认：

```text
ffmpeg.exe
ffprobe.exe
ExifTool.exe
jpegtran.exe
heif-enc
heif-dec
magick.exe
```

没有作为 production result-affecting runtime dependency。

注意：

- 注释、资源文案、旧兼容 wrapper 名字不等于实际 production usage；
- 反过来，也不能因为“About 页面没写”就认为 production 没调用。

结论必须来自真实 call path / package/runtime evidence。

---

## 6. Portable-Core Proof

P4 不做 Linux/Web 产品，但必须证明核心没有被 Windows 锁死。

首选建立：

```text
livephotobox_portable_core
```

或语义等价 target。

它应包含：

```text
binary primitives
metadata
protocol/container truth
neutral structural primitives
portable mutation semantics
```

并且不直接链接：

```text
Win32 filesystem backend
WIC
Media Foundation
```

至少在 Windows compiler 下独立构建成功。

如成本合理，可追加：

```text
non-Windows Clang smoke
或 Emscripten compile smoke
```

这只是污染探针，不是发布 Linux/Web 的承诺。

---

## 7. Windows Contamination Final Pass

对 R1 contamination map 逐项重新核对。

最终允许存在：

### 合法 backend dependency

位于明确：

```text
windows platform backend
windows media backend
UI-only Windows layer
```

### 明确暂留项

如果某处 correctness 风险使迁移应推迟，必须说明：

```text
为什么不能在 P4 安全迁移
是否污染 portable truth
P5 是否会被它阻塞
未来处理位置
```

不能留下“散落但没关系”的未知项。

---

## 8. Package Footprint Baseline

记录 R7 最终 Windows package：

```text
native runtime binaries
codec dependencies
size contribution
optional/required features
```

并与 R1 baseline 比较。

大小只用于优化判断。

**不允许为了把包缩小而移除真实兼容性所需 backend。**

---

## 9. CMake / Dependency / Backend 最终一致性

确认：

```text
CMake = canonical
vcpkg/approved pinned dependency path = reproducible
JPEG backend = frozen
HEIC backend = frozen
video backend = frozen
WIC policy = frozen
lcms2 policy = frozen
PlatformFilesystem = canonical
runtime capability identity = present
portable core = proven
```

P5 不能再需要回答“我们到底用哪个 backend”。

---

## 10. R7 验收标准

全部满足才可以进入 Fresh Audit：

1. R1 inventory 中所有 P4 action 已关闭或明确进入非阻塞剩余清单；
2. Platform/Media backend contract 职责清楚；
3. scattered filesystem/publish implementation 已收口，无第二套无授权的 transaction path；
4. CMake canonical Windows build 仍通过；
5. dependency restore 可重复；
6. JPEG / HEIC / video backend 均已冻结；
7. lcms2 / WIC policy 已冻结；
8. runtime capability/backend/version/fallback identity 可诊断；
9. duplicate codec/runtime report 完成；
10. package 每个 runtime binary/feature 有用途；
11. production external media CLI audit 为 clear；
12. package footprint baseline 完成；
13. portable-core target 独立 build proof 通过；
14. public C ABI 未泄漏 platform/C++ implementation type；
15. P1–P3 targeted regressions 通过；
16. R4–R6 RealSample evidence 仍可复现；
17. P5 不需要重新设计 build/backend architecture。

---

## 11. 以下情况不得通过

任一出现即 `R7 BLOCKED`：

- 仍存在两个不一致的 atomic publish 实现；
- CMake 只是“能用”，正式 workflow 仍以 `.vcxproj` 为真实 authority；
- JPEG/HEIC/video backend 仍然“运行时随便试”；
- fallback 不记录原因；
- package 带着大量无人使用 codec/plugin/runtime；
- external media CLI 仍参与 production；
- portable_core 只是名字，实际仍链接 Win32/WIC/MF；
- 为了通过 portable proof 复制 protocol source；
- P1–P3 regression 被跳过；
- RealSample tests 被 filter 掉；
- 当前仍有 blocker，却开始写 P5；
- 用“未来 Linux 可能需要”为理由继续增加没有现实调用者的抽象。

---

## 12. 验证与测试

### 构建

```text
clean CMake configure/build
Windows x64 Debug/Release as required
portable_core independent build
production Native artifact
```

### Regression

只运行与 P4 及其前置受影响区域相关的：

```text
P1 Inspector
P2 Extractor
P3 Cleaner
R2 platform/transaction
R4 JPEG
R5 HEIC/HDR foundation
R6 video backend
runtime/C ABI
```

P4 仍不运行 P10 后才能做的全产品最终总验收。

### Package / runtime

实际检查：

```text
packaged binaries
dynamic dependencies
unused/duplicate runtime
external CLI absence
version/capability identity
```

### RealSamples

R4–R6 要求的真实样本验证必须实际进入 test/evidence 范围，不得仅引用旧日志。

---

## 13. P4 Exit Gate 映射

R7 结束时应给出一张明确的 17 项 P4 Exit Gate matrix：

```text
Gate
Implementation evidence
Test/RealSample evidence
Status
Residual risk
```

任何一项为 blocker：

```text
→ 不进入 Audit 的“清洁审计”
→ 回对应 R2–R6 修复
```

R7 的成功状态只能是：

```text
IMPLEMENTATION READY FOR FRESH INDEPENDENT AUDIT
```

不能写：

```text
P4 COMPLETED
```

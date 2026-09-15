# P4-R3 — CMake / vcpkg / Reproducible Native Build Foundation

> **性质：** 构建系统迁移轮。  
> **目标：** 让 CMake 成为 Native canonical build authority，并建立可复现 dependency identity；不借构建迁移改变媒体/协议行为。

---

## 1. 本轮目标

当前 Native 仍以 `.vcxproj` 为主要构建描述。

P4 要达到：

```text
CMake
= canonical Native build graph

vcpkg manifest
= Windows Native dependency declaration primary strategy
```

CMake graph 至少要能自然表达：

```text
portable core
Windows platform backend
media backend/dependencies
feature flags
Native DLL / public C ABI
tests / test harness
```

---

## 2. `.vcxproj` 迁移原则

禁止：

```text
先删 .vcxproj
→ 再慢慢把 CMake 修到能用
```

正确顺序：

```text
保留现有 .vcxproj baseline
        ↓
建立 CMake Windows x64 build
        ↓
build / tests / artifact / ABI / debug workflow parity
        ↓
CMake 才成为 canonical
        ↓
旧 .vcxproj 再决定降级、生成化或保留 compatibility role
```

本轮不要求为了形式主义立即删除旧项目文件。

---

## 3. CMake 必须保留的当前生产事实

至少保持：

```text
C++20
MSVC Windows x64
Debug / Release
warning/error policy
required security/compiler options
public include boundary
Native DLL name/identity
product version synchronization
test-only definitions/hooks separation
runtime/library linkage policy
artifact output used by C# tests/product
```

特别需要确认：

> C# / tests 实际加载的是新 CMake 构建出的 Native DLL，而不是某个旧目录里的 stale artifact。

---

## 4. vcpkg / Dependency Identity

建立 manifest-based 依赖策略。

每一个进入正式 Native build 的 dependency 至少记录：

```text
name
version / baseline
source
enabled features
disabled features
static/dynamic policy
license
runtime binaries
patches
hash/checksum where appropriate
why it exists
```

如果某个依赖确实不能可靠由 vcpkg 满足，可使用：

```text
FetchContent
pinned source
vendored patch
```

但必须有理由，并且仍然可复现。

不能产生“vcpkg 一套 + 手工下载最新版一套”的双轨失控状态。

---

## 5. Future-ready 但不提前做 future product

CMake graph 应允许未来接入：

```text
Emscripten
GCC / Clang
AppleClang
```

但本轮正式验收仍只针对：

```text
Windows x64 / MSVC
```

不为了 future-ready：

- 写 Linux GUI；
- 实现 POSIX product backend；
- 建 Web 产品；
- 添加无调用者的 portability layer。

---

## 6. 本轮应完成的结果

完成后：

- Native 有清晰 canonical CMake entry；
- Windows x64 clean build 可重复；
- 当前 production DLL / C ABI 可以由 CMake 构建；
- test harness 与 production build 的区别明确；
- vcpkg manifest/baseline 可恢复当前所需 dependencies；
- R4/R5 新 codec dependency 有明确加入位置，而不是继续手工塞进项目；
- 旧 `.vcxproj` 未在 parity 前被删除；
- 开发者不需要靠“某台机器以前装过什么”才能构建。

---

## 7. 验收标准

全部满足才可进入 R4：

1. CMake 能 clean configure + build Windows x64 Native；
2. Debug/Release 至少与当前正式开发/发布需求一致；
3. public C ABI/export surface 无无意丢失；
4. version identity 与当前产品版本机制保持正确；
5. C# Core / targeted tests 确认加载 CMake artifact；
6. P1–P3 受影响测试在 CMake artifact 上通过；
7. vcpkg manifest 与 baseline 已固定，依赖恢复可重复；
8. 构建没有依赖未记录的机器级手工库；
9. test-only hooks 不污染 production artifact；
10. `.vcxproj` 只有在 parity 已证明后才允许改变地位；
11. CMake source grouping 清楚地区分 portable core、Windows backend、media backend；
12. 没有借 build migration 改写 protocol/container semantics。

---

## 8. 以下情况不得通过

任一出现即 `R3 BLOCKED`：

- “CMake 能编译”但测试/应用仍加载旧 DLL；
- CMake build 与 `.vcxproj` export/behavior 不一致且未解释；
- 依赖使用 floating latest/unpinned 下载；
- 关键编译选项、runtime linkage、test definitions 丢失；
- Native version 与 App/package version 失去同步；
- `.vcxproj` 在 parity 前被删；
- 为跨平台 readiness 复制一套 protocol source；
- CMake 只是 wrapper，真实 build authority 仍必须人工编辑 `.vcxproj`；
- 依赖恢复只在原开发机缓存中有效。

---

## 9. 验证与测试

### Build parity

至少比较：

```text
old baseline build
vs
CMake build

artifact identity
public exports
C ABI smoke
version/runtime info
Debug/Release behavior
```

### Clean/reproducible proof

应在尽可能干净的 dependency/build 状态下证明：

```text
manifest → restore
CMake configure
CMake build
Native tests / C# targeted tests
```

### Regression

继续只跑：

- P1–P3 与 Native boundary 相关 targeted tests；
- R2 platform/transaction regression；
- Native runtime/C ABI smoke。

本轮不需要跑 P5/P9 等未到阶段的最终功能测试。

本轮通过的核心含义：

> 从现在开始，Native build/dependency graph 有唯一、可复现的权威入口，后续 codec/backend 不再靠临时工程配置堆进去。

# P4-R3 — CMake / vcpkg 实施与本地审查

状态：**本地技术验收就绪；P4 仍在进行中**。本轮只迁移 Native 构建与依赖入口，没有修改 public C ABI、媒体/协议语义或进入 R4。执行计划保存在 `.ai/tasks/P4-R3-cmake-vcpkg/TASK.md`；验证命令与产物身份保存在其 `EVIDENCE.md`。

## 实际交付与构建路径

根 `CMakeLists.txt` 是 26 个生产 `.cpp` 的显式源图（磁盘 26、列入 26、差异 0）。它区分已确认无 Windows 头的 portable core、仍受 Windows 约束的协议/元数据源码、Windows filesystem/foundation backend 和 media backend；这只是当前分组，R7 才负责 portable-core 构建证明。CMake 生产 target 输出 `LivePhotoBox.Native.dll`；独立 `LPB_BUILD_TEST_HARNESS` target 输出 `LivePhotoBox.Native.TestHarness.dll`。测试定义仅进入 harness，Debug harness 用 `/Z7` 避开本机 MSVC 19.51 的类型服务器崩溃，仍保留调试符号。

构建保留 C++20、Windows x64/MSVC、`/W4 /WX /sdl /permissive- /utf-8 /EHsc /guard:cf`、Debug `/MTd`、Release `/MT /O2 /Oi /Gy /GL /LTCG /OPT:REF /OPT:ICF` 和 PDB；Windows SDK/system import libraries 在 CMake 明示链接。`Package.appxmanifest` 的 `2.2.2.0` 通过 CMake 生成版本头；模板只在内容变化时更新，连续第二次默认 Debug build 没有重新编译源码。

`scripts/native/build-native.ps1` 现在直接发现 VS 自带 CMake/vcpkg，执行 manifest-mode configure/build，并写入产品既有 `artifacts/native/<Configuration>/win-x64` 路径。`build-native-test-harness.ps1` 使用同一图的独立 harness target。C# Core 的默认 Native target 调用该脚本，实际 runtime/test DLL 来自 CMake；`.vcxproj` 保留为 solution/IDE 兼容工程，输出改到 `artifacts/native/compat/`，不能覆盖 canonical DLL。旧项目源码/filters 同步脚本只维护兼容工程，不再修改 C# 的 Native build inputs。

## 依赖身份

根 `vcpkg.json` 固定 Microsoft built-in registry baseline `9e44ec0e9f247d77c230ced0ee66c76296837807`。当前 Native 没有第三方 C++ package，所以依赖列表为空；Windows SDK、MSVC runtime、功能、许可与运行库身份见 [Native 依赖身份](P4-R3-Native依赖身份.md)。空 manifest 在独立安装目录执行 `vcpkg install` 成功；另一全新 build 目录通过 vcpkg toolchain configure 成功；默认目录完成 clean Debug/Release build。R4/R5 新 codec 必须在该 manifest 添加固定 ports/features 后再证明真实包恢复。无 FetchContent、未锁版本的下载或手工机器库。

## R3 原始验收矩阵

| # | 原始要求 | 证据与状态 |
|---:|---|---|
| 1 | CMake clean configure/build Windows x64 | 独立 parity 与 vcpkg toolchain 目录全新 configure；默认目录 Debug/Release production build 成功。**PASS** |
| 2 | Debug/Release 与开发/发布需求一致 | MSVC 生成项目确认 C++20、警告/SDL/CFG、`/MTd`/`/MT`；两配置 DLL/PDB 与定向测试。**PASS** |
| 3 | public C ABI/export 不丢失 | `.vcxproj` 旧基线与 CMake parity 均 59 个 public `lpb_*`，集合差异 0；最终 production 两配置各 59，0 个 `lpb_test_*`。公开头未改。**PASS** |
| 4 | version 与产品版本机制同步 | 从 `Package.appxmanifest` 生成 `2.2.2.0`；C# `NativeRuntimeTests.Probe_LoadsMatchingNativeRuntime` 实际比较 managed/native version。**PASS** |
| 5 | C# Core/targeted tests 实际加载 CMake artifact | parity Debug probe 1/1；默认 C# 构建自动运行 CMake，runtime 15/15；最终测试目录 DLL SHA-256 与 canonical DLL 相等。**PASS** |
| 6 | 受影响 P1–P3 测试通过 | Inspector、Extractor/RealSample/authority/transaction、Cleaner/real filesystem、R2 platform、Converter、GainMap、ABI 的同一 scope Debug/Release 各 274/274，0 skip。**PASS** |
| 7 | vcpkg manifest/baseline 固定、恢复可重复 | 固定 registry commit；当前确无第三方 Native port，空 manifest 独立 restore 与全新 toolchain configure/build 成功。真实 codec 包恢复留待 R4/R5 新依赖出现。**PASS（当前依赖集）** |
| 8 | 无未记录机器级手工库 | CMake 只链接文档列出的 Windows SDK/system import libs；`dumpbin /dependents` Release parity 与默认产物 7 个系统 DLL，集合差异 0。**PASS** |
| 9 | test-only hooks 不污染 production | 两配置 production 59 导出、0 测试钩子；独立 harness 76 导出，其中 17 个仅测试使用。**PASS** |
| 10 | parity 前不删除/降级 `.vcxproj` | 旧工程一直保留到 CMake build、导出、version、C# 加载和 274 项行为回归通过；随后只改为 `compat` 输出。**PASS** |
| 11 | portable/Windows/media source grouping 清楚 | 26/26 显式 `.cpp`，四组如上；Windows-bound remaining 明示，没有把整批协议源码冒称 portable。**PASS** |
| 12 | 不借迁移改变 protocol/container semantics | R3 没有修改 production protocol/container/native ABI；Release smoke、真实样本和 P1–P3 回归保持原断言。**PASS** |

## 附加验证与限制

| 最终 CMake 产物 | 字节数 | SHA-256 | C# 测试目录复制件 |
|---|---:|---|---|
| Debug production DLL | 5,398,016 | `9EFCFC63C1A0B661C007A92FF5CCA7B94B83DC64992CBC73337F17E7BEFD9324` | 相同 |
| Release production DLL | 927,232 | `EE0C9C660E4E390D0188F2720BA405E253435C095E5B1BD6F1290164514DB160` | 相同 |
| Debug test harness DLL | 独立输出 | `D00E657BEA1A2E444831AF61D77832FD7C09F42AA91D63FCC278BD16C1029958` | 相同 |
| Release test harness DLL | 独立输出 | `CE8F9B948DF9A95456583A8F04F48DA6D21676B26A525755CD17562B0BA54805` | 相同 |

R2 `.vcxproj` baseline 的 Debug/Release SHA-256 分别是 `2FEBD3BACB880F31C2C16E0E7ADDB024B6B36A149221903633DBCB0474754603` / `5D6EC28827E6D24685219375EDBDE0D912DEB721E9041D720F2D6E1FCB04AF0F`。二进制哈希不同是构建路径、生成图和链接顺序差异，不将逐字节相同当作 parity；实际验的是 59 个导出集合、版本、系统依赖、C ABI smoke、真实样本及定向行为。

`portable_io_smoke` 在默认 CMake 图 Debug/Release 各 1/1。测试在 Release 下也强制保持断言；CTest 传入确定性的 fixture 路径。Release 的真实 NTFS sparse 4.5 GB 逻辑偏移测试 1/1、0 skip。17 个只读原始机型样本与 R1 baseline SHA-256 比较，缺失或变化 0。RealSample/产品回归和 portable smoke 是不同类别的证据，未将产品自身验证冒充独立媒体正确性审计。本轮没有跑 P0–P10 全项目最终 suite。

`doctor.ps1 -ActiveOnly` 的唯一本地报错是未参与 R3 的可选生成 adapter `.agents/mcp_config.json` 缺失；MSVC/CMake/vcpkg/.NET/Windows SDK 与本轮独立工具均可用，构建测试通过。`.ai/project-facts.md` 的恢复文件仍不可信，本报告以实际源码、构建日志、DLL 哈希及测试结果为依据。P4 需要后续 R4–R7、fresh Audit/Verify 和外部 Chief Gate；本地 R3 就绪并不等于 P4 ACCEPT，R4 未自动启动。

# P7-R5 — Portable Core / 依赖 / C ABI 边界扫描与验证收口

2026-10-19：**CLOSED / PASS / DONE_FOR_R5**。

当前用户授权本地 Codex 采用 `LOCAL_OWNER` 模式负责 P7-R5 的范围审计、脚本编制、本地 Windows 与非 Windows 验证及独立审查组织，不联系 ChatGPT。
最终候选完成全部 7 项核心检查与 fresh 只读独立审查，已停止在 R6/P8 之前。
P7 仍为 `in_progress`；本报告不是整个 P7 或产品发布验收。未 commit/push/tag/release。

## 1. 验证结果

| 检查项 | 可复核证据 | 状态 | 严重性 | Blocker | 说明与边界 |
|---|---|:---:|:---:|:---:|---|
| **Windows.h / Win32 类型泄漏扫描** | `run-1/windows_leakage_report.json`：全量扫描 15 个 `LPB_PORTABLE_SOURCES` 源码、17 个内部头文件及公开 `livephotobox_native.h`（共 33 文件），0 违规 | **PASS** | 无 | 否 | Portable Core 与公共 ABI 严格零 `<windows.h>` 及 Win32 类型依赖 |
| **生产 External CLI 调用审计** | `run-1/external_cli_audit_report.json`：全量扫描 `Core`、`Native`、`CLI`、`WinUI`。12 处进程启动均为系统/UI 行为（Explorer 定位/打开、更新器、浏览器 URL）；Core/Native 零进程启动；媒体 CLI 生产调用为 0 | **PASS** | 无 | 否 | 媒体处理 100% 运行于 Native/Core 进程内，无外部 CLI 生产运行时依赖 |
| **运行时依赖与 PE 导入表** | `run-1/runtime_dependencies_report.json`：`dumpbin /dependents` 检查 `LivePhotoBox.Native.dll`（6 个批准系统 DLL）与 `LivePhotoBox.Video.Libav.dll`（3 个系统 DLL）；vcpkg 与 NuGet 完全固定 | **PASS** | 无 | 否 | 零未批准运行时依赖；编解码库静态链接 |
| **编解码器职责与去重** | `run-1/codec_duplication_report.json`：8 类编解码职责唯一固定（JPEG、HEIC、视频 remux、MF SDR 转码、minimal libav HDR/10-bit 转码、UI 缩略图） | **PASS** | 无 | 否 | 零未经批准的重复编解码器引入 |
| **C ABI 边界与符号导出** | `run-1/c_abi_boundary_report.json`：`dumpbin /exports` 检查 Native 67 导出符号、Video.Libav 2 导出符号，全部为 clean C 符号（无 C++ `?` 修饰）；公共头文件纯 C ABI，POD 布局对齐 | **PASS** | 无 | 否 | 零 C++ 类型/异常泄漏跨界 |
| **Portable Core 编译 Smoke (Windows)** | `run-1/portable_compile_smoke_report.json`：`lpb_portable_io_smoke.exe` 链接 `livephotobox_portable_core.lib` 运行，断言全部通过，退出码 0 | **PASS** | 无 | 否 | Windows MSVC 静态库构建与调用证明 |
| **Portable Core 编译 Smoke (Linux GCC)** | `run-1/portable_compile_smoke_report.json`：WSL Ubuntu 24.04 GCC 13.3.0 C++20 编译全部 15 个 portable 源码并链接运行，断言全部通过，退出码 0 | **PASS** | 无 | 否 | 仅作为跨平台纯净度/无 Windows 污染证明，不代表 Linux/Web 产品支持 |
| **托管 NativeRuntimeTests 回归** | `run-1/NativeRuntimeTests.trx`：17/17 passed，0 failed，0 skipped，耗时 2s | **PASS** | 无 | 否 | ABI 版本、POD 尺寸/偏移量、capability identities 全部吻合 |
| **Fresh 只读独立收口复核** | `run-1/independent-review.md`：独立只读审查子代理，未参与实现与候选修改，结论 PASS / DONE_FOR_R5 | **PASS** | 无 | 否 | 独立审查通过，满足收口门槛 |

正式证据目录：`.ai-tmp/evidence/P7/P7-R5-portable-core-dependencies-abi/run-1/`。
候选索引：`evidence-index.json`（SHA256: `B8B38ED83B4747955E0FA70544E4D687A495FFD5194A378397A54B4728EC256C`）。

---

## 2. 实际修改及影响

为确保 Portable Core 在非 Windows (Linux GCC C++20) 下完全纯净且无 MSVC 专属 CRT 锁死，本次实施了 4 处纯标准 C++ 移植修正：
1. `LivePhotoBox.Native/src/foundation/portable_internal.h`：
   添加基于标准 C++20 `<algorithm>`、`<cstring>` 的数组安全复制模板 `copy_fixed_string` 与长度检查 `safe_strnlen`。
2. `LivePhotoBox.Native/src/containers/heif.cpp`：
   将 2 处 MSVC 特有 `strncpy_s(..., _TRUNCATE)` 替换为标准 `copy_fixed_string`。
3. `LivePhotoBox.Native/src/protocols/apple_mebx.cpp`：
   将 1 处 MSVC 特有 `strnlen_s` 替换为标准 `safe_strnlen`。
4. `LivePhotoBox.Native/src/containers/isobmff_video_profile.cpp`：
   为变量 `high_profile` 补充 `[[maybe_unused]]` 属性，消除 Linux GCC 严苛编译告警。

**影响评估**：
- 属于 Agent 在 R5 范围内的自主工程权限（规则第 13 条）。
- 不改变公开 ABI（`livephotobox_native.h` 未修改任何签名）。
- 不改变任何业务逻辑、媒体处理行为或 Contract。
- 完全向后兼容，MSVC 与 GCC 均编译通过。

---

## 3. P7 Exit Criteria 对应情况

- **Exit Criterion 7（Portable Core 边界未回退）**：**PASS**
  - 33 个 portable 相关文件零 Windows 污染。
  - Windows 与 Linux GCC 13.3.0 C++20 双平台编译并执行 portable smoke 成功。
- **Exit Criterion 8（dependency graph 无未批准扩张）**：**PASS**
  - Native PE 导入表无未授权动态链接库。
  - 零外部媒体 CLI 生产依赖。
  - 8 类编解码器职责清晰，无未决重复。

---

## 4. 边界与停止条件

- **P7-R5 正式关闭（CLOSED）**。
- **停止在 R6/P8 之前**。
- **严禁未经授权自动进入 R6 或 P8**。
- **不执行 Git push / publish**。

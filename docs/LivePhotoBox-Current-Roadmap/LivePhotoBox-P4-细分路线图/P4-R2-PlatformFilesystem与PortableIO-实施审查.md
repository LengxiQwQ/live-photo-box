# P4-R2 PlatformFilesystem / Portable I/O 实施与本地审查

本轮按 `LivePhotoBox-P4-细分路线图/02-P4-R2-PlatformFilesystem与PortableIO.md` 原始范围实施。R1 已按当前用户要求完成自我审查，当前用户随后授权 R2 施工。这里的结论仅是本地技术就绪；P4 仍在进行中，没有外部 Chief Gate 对整个 P4 的 ACCEPT，也不启动 R3。

## 实施结果

- `src/platform/windows_filesystem.*` 接管 UTF-8/Windows 路径、文件身份、路径别名、目录 pin、CREATE_NEW owned temp、通过 owned handle 的 no-replace publish、owned-handle 删除和 Win32 错误分类。Cleaner 的原有事务注册与测试 hook 迁入后端，Extractor 的 exact-slice 状态机没有改写。原有 `FileDispositionInfo` 删除调用收口为一个 backend primitive。
- 新的 `windows_owned_output` 给图片复制/WIC 转换、视频 remux/MF 转码、GainMap 重组及旧 JPEG 辅助写入提供独占 staging、flush、staging identity 检查、handle-first 发布和发布后 exact-object 检查。目标文件已存在时失败，保留 foreign object；失效的 owned staging 只通过持有的 handle 删除。MF 和 WIC 的平台 codec 对已拥有的 staging 对象写入。
- `src/binary/portable_io.h` 提供 byte span、mutable span、random-access reader、sequential writer 和 memory source。`containers/isobmff.cpp` 顶层 box 扫描由 reader 驱动；视频 probe/remux 用同一结构扫描处理 Windows 文件与 owned-handle staging，容器核心不再引入 `foundation/internal.h`。
- 所有公开 ABI 仍是 UTF-8、POD/C 结果码。没有复活 Legacy runtime，没有把 ffprobe/ExifTool 等验证工具变成生产写入依赖，也没有更改厂商协议事实或 Cleaner authority/ABI。

当前没有携带“明确允许替换某个已验证目标对象”的生产 authority/ABI。Backend capability query 因而将 authorized atomic replace 报为不可用；所有当前写入路径只允许 no-replace，不能以 path-only replace 绕过目标身份门禁。这是显式 fail-closed 的替换策略，不是声称已支持未来的 authorized replace。

## R2 §8 验收矩阵

| 项目 | 源码/实测证据 | 状态 | 严重性 / blocker | 建议 |
|---|---|---|---|---|
| 1. Platform contract 清晰且最小 | `portable_io.h` 两个 callback source/sink；`windows_filesystem.h` 一个 backend 与 owned output；没有虚拟文件系统/factory | PASS | 无 | 保持当前边界 |
| 2. Windows file I/O/publish 收口 | 路径/身份/temp/pin/publish/dispose 在 Windows backend；旧媒体模块 Win32 残留见下表 | PASS | 无 | 后续只按真实替换点迁移 |
| 3. P1–P3 correctness 不退化 | Inspector/Extractor/Cleaner 原 authority、exact slice、snapshot、rollback、preservation 流程保留；R2 Debug/Release scoped RealSamples 与事务回归 | PASS | 无 | 外部 Auditor 可重新取证 |
| 4. 核心结果路径复用 canonical publish | Cleaner、Extractor、图片/视频、GainMap、旧 JPEG writer 全部调用 owned-handle no-replace backend | PASS | 无 | 不新增 pathname rename |
| 5. Win32 残留逐项分类 | 见下表；当前没有生产 `MoveFileExW`/pathname `DeleteFileW` publish/remove | PASS | 无 | R3–R6 按数据面/codec 范围再迁移 |
| 6. C ABI 平台中立 | public header 未变；Release `dumpbin /exports` 仍为 59 个 `lpb_*` 导出 | PASS | 无 | 保持 UTF-8/POD |
| 7. Portable byte/stream/random access 真使用 | ISOBMFF 顶层扫描接入 video probe/remux；独立 C++ smoke 同时驱动 memory/file/大于 4 GiB virtual source、部分写 sink | PASS | 无 | R7 再扩大 portable core |
| 8. source artifact 默认不原地破坏 | 原始 17 样本 SHA-256 与 R1 基线完全一致；真实同名 alias/锁/Unicode/冲突测试检验 source 未变 | PASS | 无 | 保留只读样本规则 |
| 9. failure 不半发布/虚假 success | owned-handle no-replace 与发布后身份检查；Extractor/Cleaner rollback；converter locked/collision fail-closed；remux 结构验证在发布前 | PASS | 无 | 持续留意真实 cleanup failure |
| 10. P1–P3 与真实 filesystem regression | scoped Debug/Release、真实 share lock、ReadOnly 目标、collision/race、rollback/post-publish 测试；0 skip | PASS | 无 | Chief Gate 可复跑同一过滤器 |

## 真实 filesystem 与结构证据

| 行为 | 实际测试/证据 | 状态 |
|---|---|---|
| 独占 share lock | `PlatformFilesystemTests.ImageCopy_RealExclusiveShareLock_FailsBeforePublish`；Cleaner `Clean_RealFileSystem_DestinationLockedWithNoShare_FailsClosedAndRollsBack` | PASS，真实 FileShare.None |
| ACL / 非可写目标 | Cleaner `Clean_RealFileSystem_ReadOnlyExistingDestinationFile_FailsClosedAndRollsBack` | PASS，真实 Windows ReadOnly 属性 |
| 目标冲突与 race | 图片 foreign sentinel 保留、Extractor destination collision/after-preflight race、Cleaner owner lease | PASS，owned/no-replace |
| alias / UTF-8 | 图片 source=destination fail closed；中文源/目标字节相同；原 Apple/厂商 RealSamples 使用 UTF-8 名称 | PASS |
| failure 前/中/后发布 | Extractor fault seam 只作状态机补充；真实 lock/collision/ReadOnly 提供文件系统失败；postpublish exact-object/rollback classes | PASS，类别区分 |
| 大文件随机访问 | Release NTFS sparse-file `ExtractorScaleTests` 实际 4.5 GB logical offset；独立 portable smoke 虚拟 >4 GiB box | PASS，0 skip |
| 实况协议/保护 | 已读取 `docs/实况照片协议分析文档/` 的全部厂商文档；当前 RealSamples Inspector/Extractor/Cleaner/preservation 回归和 17 个 source hash | PASS；产品测试不是独立媒体格式 oracle |

## Win32 / pathname 残留分类

| 位置 | 仍保留的操作 | 原因与边界 |
|---|---|---|
| `media_inspector.cpp`, `preservation_observation.cpp`, `foundation/sha256.cpp` | 只读对象打开、结构/哈希观察 | P1/P3 的 source snapshot 和 before/after evidence；R2 不重写协议/保护真相，结果不得成为 writer 绕道 |
| `media_extractor.cpp` | source handle、exact-range ReadFile/WriteFile/Flush、postpublish身份验证 | P2 correctness-critical slice、fault/rollback 状态机；temp/pin/publish/dispose 已复用后端 |
| `media_cleaner.cpp`, `containers/mp4_strip.cpp`, `protocols/clean/heif_cleaner.cpp`, `samsung_sef_cleaner.cpp` | 在已拥有的 staging handle 上 WriteFile/Flush | P3 权威/指纹/厂商删除逻辑保留；CREATE_NEW、publish、owned dispose 已共用后端；无独立 pathname replace |
| `media_api.cpp` | GainMap 两个 input handle 只读读取；测试 harness 对 plan artifact 只读打开哈希 | GainMap 输出走 `windows_owned_output`；测试专用 authority issuance 不形成生产写入入口 |
| `image_converter.cpp`, `video_converter.cpp` | WIC/MF codec COM、路径 decoder、标准库 file reader | Windows codec 实现本来属于平台媒体后端；output 对象由 filesystem backend 所有，ISO-BMFF 数据扫描已可移植 |
| `protocols/clean/jpeg_structure_cleaner.cpp` | 旧辅助协议解析的 `std::ifstream` | 当前无 product caller；其 writer 已路由 owned output，保留解析逻辑供后续清理评估 |

## 构建、测试与身份

- 基线 HEAD：`47c6374412497af0f326a3eb76d1342017840393`；本轮保持未提交工作区改动，用户先前的 `.gitignore` 未改动。生成的 `GEMINI.md` 按项目规则由 `.ai/sync.ps1` 同步。
- Native Debug/Release：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/build-native.ps1 -Configuration Debug|Release`，两者通过。Debug DLL 为 5,529,600 B，SHA-256 `2FEBD3BACB880F31C2C16E0E7ADDB024B6B36A149221903633DBCB0474754603`；Release DLL 为 910,336 B，SHA-256 `5D6EC28827E6D24685219375EDBDE0D912DEB721E9041D720F2D6E1FCB04AF0F`；测试目录加载的 DLL 分别与这些 hash 相同。
- 定向测试过滤器包含 `SourceInspectorTests|ExtractorTransactionTests|ExtractorRealSampleTests|ExtractorAuthorityTests|CleanerTrustChainTests|CleanerRealFilesystemTransactionTests|CleanerEighthRoundOwnershipTests|ImageConverterTests|VideoConverterTests|NativeGainMapReassemblyTests|PlatformFilesystemTests`。Debug/Release 各 259/259、0 skip；Release `NativeRuntimeTests` 15/15、0 skip，`ExtractorScaleTests` 1/1、0 skip。未运行 P0–P10 全量 suite。
- MSVC standalone `tests/native/portable_io_smoke.cpp` + `containers/isobmff.cpp` 在 `.ai-tmp/workspace/P4-R2/` 编译执行通过；内存/文件的相同 box 结果、越界 read fail、部分写 all、>4 GiB 虚拟 source 都由 assert 检查。它是独立 portable primitive smoke，不冒充媒体格式第三方验证。
- R1 corpus 17/17 文件 hash 相同；Release `lpb_*` 导出 59 个；Debug/Release 实际加载的 Native DLL 与对应构建产物 hash 相同。
- `doctor.ps1 -ActiveOnly` 只有未参与 R2 的可选 `.agents/mcp_config.json` adapter 缺失，必需的 dotnet/MSVC/ffprobe/核心校验器可用；Doctor 是工具预检，不是产品 PASS 证据。
- 原 `ExtractorTransactionTests.Extract_NativeDirectAbi_MissingPrimarySnapshot_FailsClosed` 在跨 DLL 错用生产 context 时挂起；依据同仓库 `ExtractorAuthorityTests` 已记录的 harness context 规则，测试改用 `TestNativeContext.Create()`，原 AuthorityViolation 和错误前缀断言保留，修复后该项通过。

本地结论：**R2 ready for external technical audit**。这是同一执行会话的自我审查，不冒充 fresh/独立 Auditor，也不将 P4 标记 completed。

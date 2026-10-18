# Live Photo Box：P7 R4 资源残留问题完整分析交接

交接整理日期：2026-10-17，Asia/Kuala_Lumpur；下述实验主要发生于 2026-10-16。本文是供另一 AI 独立分析的事实交接，**不是 R4 验收、架构变更批准或系统更新授权**。

## 1. 用户目标与目前状态

用户授权本地主代理独立规划、审查、修复、验证，推进 P7 R4 到完成，不默认联系 ChatGPT。R3 已完成；R4 当前仍未完成，原因是重复负载下的资源门槛尚未通过。

用户随后明确提出：

- 必须分清项目代码有问题，还是验证工具有问题。
- 如果项目代码有问题，要修项目；如果只是验证工具失效，可以改用其他独立/开源验证工具。
- 产品要适配大众用户，不能为了当前一台电脑加入特殊适配。
- 目前没有批准更新系统 HEVC 扩展，也没有批准把新的生产工作进程加入产品安装包。
- 本次要求是完整说明情况，供用户自行转发给另一个 AI 分析。请先分析，不把此交接当成新架构的实施授权。

仓库：`D:/Projects/live-photo-box`；分支 `master`；HEAD：

```text
47df638cbae0c957904348cbf30f63af1e1c564f
```

工作区包含之前多个阶段已存在的改动，因此 HEAD 本身不能代表当前源码。当前候选必须以证据中的 source/runtime 哈希为准。没有执行 commit、push、tag、release，没有进入 R5/P8。

## 2. 先解释“产品”和“验证器”各是什么

实际产品的一条视频转换路径是：

```text
C# 业务编排 / VideoConverter
    → 项目 Native C++ 媒体转换
    → Windows Media Foundation（MF）
    → 系统提供的 HEVC 解码组件
    → 软件视频编码 / mux / 输出校验
```

MF 和 HEVC 解码组件在这里是实际产品使用的运行路径的一部分，不只是测试工具。

测试工具则调用产品真实公开入口，并记录输出、源文件哈希、工作区清理、取消、内存、句柄等。离线 FFmpeg/ffprobe 用于独立检查输出；它们没有被加入生产写入路径。

此次最初观察到的是：实际转换成功后，运行转换的进程持续多留 Semaphore（Windows 内核信号量，一类系统句柄）。因此这不是“验证程序不能运行了”。后来又编写了完全不引用项目代码的独立程序，仍能复现类似增长。

**两个问题必须分开：**

1. 输出的媒体是否正确、可解码、源是否保持不变。
2. 连续处理后资源是否正常退役，是否发生长期增长。

输出通过检查，不能自动证明资源也正常；资源增长也不能直接等同于输出媒体损坏。

## 3. 已核实环境

| 项目 | 当前观察 |
|---|---|
| 操作系统 | Microsoft Windows 11 Pro，10.0.26300，x64 |
| 系统 HEVC 包 | Microsoft.HEVCVideoExtension_2.5.33.0_x64__8wekyb3d8bbwe |
| 实际加载模块 | HEVCDECODER_STORE.dll |
| DLL 文件版本 | 10.0.29645.1000 |
| DLL 字节数 | 2,061,104 |
| DLL SHA256 | `6A3B7E0E3BE03474BE8098CC816915C558B1ACB73CF10A741819396341A48CB3` |
| 开发构建 | Visual Studio 18 2026 / MSVC、Windows SDK、.NET 项目 |

实际 DLL 路径：

```text
C:/Program Files/WindowsApps/Microsoft.HEVCVideoExtension_2.5.33.0_x64__8wekyb3d8bbwe/x64/HEVCDECODER_STORE.dll
```

没有更新、卸载、替换或磁盘上修改该组件。一次只读 `winget show --id 9N4WGH0Z6VHQ --source msstore --disable-interactivity` 返回没有匹配包；这不能证明所有官方渠道都没有可用更新。

截至本记录，没有在另一台机器、另一 Windows 构建或另一 HEVC 扩展版本上完成同一复现。因此不能推断所有 Windows 用户都会遇到相同行为。

## 4. R4 要求与真实负载

当前路线图 R4 覆盖 large-file correctness、repeated mixed workloads、memory/handle growth、cleanup、重复负载后的 cancellation、source identity；目标是发现 leak、runaway growth、stale handle、cleanup failure、long-run corruption，不新增性能 SLA。P7 最终 exit criteria 包含 batch/large file no obvious leak。

本地执行计划采用 3 轮预热，之后至少 30 分钟且至少 12 个完整测量轮次。没有用 sleep 填满长跑时间。自然资源采样与诊断性强制 GC 分开记录。

混合轮次包含 14 个厂商/来源路线、真实公开合并入口、并发拆分、JPEG/HEIC 转换、MF SDR 视频转换及既有 HDR 路径。17 个冻结输入反复校验 SHA256。真实样本源目录只读，操作使用缓存。

已完成两次完整混合长跑：

| 运行 | 完整测量轮次 / 时间 | 功能和清理 | 资源 |
|---|---|---|---|
| soak-v6 | 76 轮 / 1811.0618183 秒，另有 3 轮预热 | 业务断言、源身份、清理、负载后取消通过 | FAIL |
| soak-v11 | 83 轮 / 1813.4899148 秒，另有 3 轮预热 | 上述断言通过 | FAIL |

每个时间四分位中，自然样本的 private bytes 中位数和 handles 中位数：

| 运行 | 第 1 段 | 第 2 段 | 第 3 段 | 第 4 段 |
|---|---|---|---|---|
| v6 private bytes | 329,293,824 | 479,506,432 | 542,289,920 | 570,466,304 |
| v6 handles | 573 | 609 | 635 | 640 |
| v11 private bytes | 314,626,048 | 446,918,656 | 488,656,896 | 546,279,424 |
| v11 handles | 557.5 | 615 | 646 | 656 |

这些是观察数据，不是新设置的性能阈值。**目前没有证据证明混合长跑中的全部 private-memory 增长都由后面定位的 HEVC Semaphore 滞留造成。** 若解决该 Semaphore 问题，仍需新的完整负载确认其余资源趋势。

历史 v11 输出做过独立 ffprobe/FFmpeg 检查：31 个保留媒体及 3 个大文件相关检查通过。它只证明对应历史候选的对应结果，不替代后来候选的最终验收。

大文件方面，128 MiB 合法容器经真实公开 Neutral 流程完成；4.5 GB 级 fixture 用真实 Windows Job 512 MiB 进程内存限制验证分配失败/拒绝路径，并做多次 primary/secondary 交替重开及源哈希检查。**这不是宣称产品已经支持任意超过 4 GiB 的成功转换，也没有把已有约 2 GiB snapshot 限制改成 streaming 能力。**

## 5. 本轮确实发现并修改过哪些项目代码

不能把全部问题都归给系统组件。以下属于本轮项目自身的修复候选，已经保留在正常源码中。

### 5.1 暂停后取消仍占用工作线程

文件：`LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs`，`WaitPauseAsync`。

旧路径用后台工作线程执行阻塞的 `pauseEvent.Wait()`；取消代理 Task 后，真实阻塞等待仍可占着线程。改为 `ThreadPool.RegisterWaitForSingleObject`，配合 TaskCompletionSource、取消注册和 finally Unregister。

实际公开 runner 对照中，12 个暂停任务取消后，在尚未 Set 这些事件前：旧实现仍有 12 个活动等待工作线程，修复后为 0。这个问题独立于 HEVC 滞留。

### 5.2 大文件分配异常时的文件句柄与错误边界

文件：`LivePhotoBox.Native/src/media/media_inspector.cpp`。

`read_file_bytes` 在打开文件 HANDLE 后进行 vector 分配；异常路径需先关闭 HANDLE 再抛出。上层 primary/secondary 及相关 metadata 读取对 bad_alloc/length_error 进行明确映射，避免异常错误处理导致进程异常终止或错误成功状态。真实受限 Job 的超大文件读取用于验证，不用 mock exception 冒充真实资源失败。

这没有放宽授权或成功条件，没有增加超大文件成功支持。某些 GainMap/length_error 分支主要经过源码审查，不能声称全部都被真实触发。

### 5.3 MF encoder activation 的 ShutdownObject

文件：`LivePhotoBox.Native/src/media/video_converter.cpp`，`mf_activation_list`。

增加在释放 `IMFActivate` 列表元素前执行 `ShutdownObject()`，完成 activation 对所创建对象的生命周期协议。此修复没有消除每次 MF SDR 转换多留一个 Semaphore 的现象，不能把它写成已解决当前 blocker。

### 5.4 MF media sink 的显式 Shutdown 生命周期

同一文件，增加 `mf_media_sink_owner`。writer 接口退役后，对底层 sink 执行 Shutdown，并按顺序释放/关闭相关对象；失败路径有 RAII。

最初在 `BeginWriting` 前获取底层 sink 的版本在真实运行中失败，记录已保留。后来改为 `BeginWriting` 后通过 `GetServiceForStream(MF_SINK_WRITER_MEDIASINK, GUID_NULL, IID_IMFMediaSink)` 获取，转换可完成。

**显式 sink Shutdown 仍没有消除 Semaphore 增长。** 该修改不是资源 blocker 已修复的证据。

### 5.5 测试和工具修复，不能混称产品缺陷

合成 fixture 的部分测试错误地使用了严格的生产 MediaWorkspace ownership 约束，改为专用 TestFixtureWorkspace；一个源码路由回归检查仍定位旧 ViewModel 方法，改为检查实际迁移后的 EditExportService 方法。相关媒体/ownership 断言没有因追求通过而删除或放宽。

早期还出现过测试部署、工具源码运行中变化、编译/API 名称及归档控制台编码等工程问题，均保留了失败记录，后续结果不能覆盖早期记录。

## 6. 为什么现在认为问题不只是验证器制造的

下面是逐步缩小范围的实际证据，重要的是保留曾被推翻的判断。

| 步骤 | 观察 | 可得结论 / 不能越过的边界 |
|---|---|---|
| 分离混合工作负载 | MF SDR 转换逐次多留 1 个 Semaphore，其他阶段没有同样的逐次特征 | 先集中 MF；不等于其他内存问题全部排除 |
| 带音频/去音频对照 | 两者都逐次增长 | 音频不是必要条件 |
| Native 子阶段计数 | 创建 SourceReader 时数量 +1 | 当时一度怀疑 constructor，但计数本身不能证明对象身份 |
| 只创建、立即释放 reader 24 次 | 每次回到基线 | constructor-only 不足以复现 |
| 跟踪具体 handle | constructor handle 关闭；首次解码出现的另一个 handle 留下 | 修正早期 constructor 归因 |
| 不创建/喂 encoder，只读取 HEVC 解码 sample | 仍逐次多留 1 个 | 编码器不是必要条件 |
| 去掉额外 XVP 视频处理 | 仍增长 | 不依赖该视频处理链 |
| reader Flush / RemoveAllTransformsForStream | 调用成功但残留不变 | 不是有效修复 |
| encoder drain 后 Flush | 成功但残留不变 | 不是有效修复 |
| decoder worker-count 属性设为 1 | 写入/读回成功但残留不变 | 未形成有效修复，已撤销 |
| decoder END_STREAMING、FLUSH、切回 native media type | 成功但增长仍在；IMFShutdown QI 为 E_NOINTERFACE | 常规附加清理没有解决 |
| 保留 decoder 接口，释放 reader 后再 Release decoder | 24 次中 21 次返回 0，3 次返回 4；稍后 DllCanUnloadNow 为 S_OK | 不能写成“全部 24 次立即完全析构”；Release 返回值只是诊断观察 |
| owned-child 临时 IAT 探针 | CreateSemaphore 调用栈经过 HEVCDECODER_STORE.dll | 定位创建路径，不证明闭源 DLL 的唯一内部缺陷 |
| 两次 CoFreeUnusedLibrariesEx(0) 诊断 | HEVC 模块仍加载、Semaphore 不减 | 无效；也不能把全进程零延迟卸载塞进产品 |
| 所有产品 Native 调用返回后的安静观察 | 61 个自然样本，约 65.217 秒，Semaphore 恒为 28；同期线程和其他 handles 下降 | 所观察窗口内，额外 24 个没有随普通异步线程退役而释放 |

临时 IAT hook 只改了自有诊断子进程内该模块的 import slot，没有修改系统 DLL 磁盘文件。更重要的是，后面的独立 SDK 复现**没有使用 IAT hook**，同样观察到增长。

## 7. 关键独立实验：不调用任何项目代码

永久源码位置：

```text
tools/p7-r4-soak-validation/sdk-repro/main.cpp
tools/p7-r4-soak-validation/sdk-repro/CMakeLists.txt
tools/p7-r4-soak-validation/sdk_repro.py
```

程序不引用 Live Photo Box 的头文件、C#、Native DLL、parser、encoder、workspace 或 Libav sidecar。CMake 和 PE 依赖检查均支持这一点。

解码主流程为：

```text
CoInitializeEx(COINIT_MULTITHREADED)
MFStartup(MF_VERSION, MFSTARTUP_FULL)
重复 24 次：
  创建局部 attributes / SourceReader / media type
  hardware transforms = FALSE
  advanced video processing = TRUE
  disable converters = FALSE
  只选择第一个 video stream
  请求 NV12
  ReadSample 直到整段视频 EOS
  局部 ComPtr 释放 sample；Flush reader
  函数退出释放 reader / media type / attributes
  记录资源
MFShutdown()
CoUninitialize()
观察 60 个间隔约 1 秒的安静样本
```

每轮 sample 都使用局部 WRL ComPtr；初始化/关闭配对。独立只读审查未发现解释该逐次增长的显式 COM/handle 不平衡。

计数器用自有临时 Semaphore 确定当前系统的对象类型编号，查询当前进程的 handles，排除自己的 probe，然后关闭 probe；同时记录 GetProcessHandleCount。计数辅助使用内部 `NtQuerySystemInformation`，因此不应声称“整个程序全部只用公开文档化 API”。**解码部分使用正式 Windows SDK/MF API。** 同一个计数器的 H264 对照不逐次增长，也支持计数器不是累积来源。

| 同一个 EXE 的对照 | HEVC 原始冻结样本 | H264 对照 |
|---|---|---|
| 输入 | 缓存中的 `苹果双文件.MOV` | 既有产品输出 `soak-v11/case/retained/codecs/sdr.mp4` |
| 分类 | 原始 RealSample 缓存 | 产品输出派生的诊断对照，不冒充原始样本 |
| 完整解码 | 24 次，每次 61 个 samples | 24 次，每次 55 个 samples |
| 直接退出码 | 0 | 0 |
| 各轮释放后 Semaphore | 3,4,5,...,26 | 每轮都是 10 |
| MF/COM 关闭后的 60 个 quiet 记录 | 都是 26 | 都是 10 |

同一 EXE SHA256：

```text
FEF1F1DF74DD1DABE88E6CB65C3AD01109D285E70048B38A950DE1D61A278D0C
```

另一个独立构建变体把 CoInitializeEx/CoUninitialize 替换为 RoInitialize/RoUninitialize，其余解码流程不变；HEVC 仍是 3..26，quiet 恒 26。此变体没有接入产品。

先前两个离线 FFmpeg 派生 H264 文件在项目严格 profile 检查处被拒绝，尚未进入 MF，所以没有把它们算作 MF 对照。之后改用已有、可被生产路径接受的产品 H264 输出，才得到上表对照。其拒绝原因没有在本轮另行修改媒体 parser，避免扩大 R4 范围。

可复现的独立 EXE 调用结构（仅说明命令，分析者无需默认执行）：

```powershell
& 'D:/Projects/live-photo-box/.ai-tmp/workspace/P7/R4-large-batch-soak/sdk-repro/build/Release/MfSdkLifecycleRepro.exe' '<输入文件绝对路径>' 24 full 60
```

**已经成立的结论：Live Photo Box 项目代码不是当前这个资源累积现象发生的必要条件。**

**尚未成立的结论：**

- 闭源 HEVC DLL 内部某个函数是唯一根因。
- 所有 Windows 版本、HEVC 包版本或输入都会如此。
- 已经证明整个项目没有任何 bug。
- 只要换验证器，实际产品的资源问题就会消失。
- 进程隔离已被证明是必要、最小或最佳产品方案。

## 8. 当前项目的回归结果与源码恢复

正常候选已补跑 `targeted-v35`：发现/执行 223 项，223 Passed，0 fail、skip、notExecuted，discovery 与 TRX 名称一致，执行期间源码未变化，隔离 TEMP 无文件残留。

覆盖 SourceInspector、源码路由、ExtractorScale、MediaWorkspace、R2 合并/ownership/handoff、P6 Neutral、VideoConverter/ConverterContract 以及 Native runtime identity 的有界集合。不是全软件全部功能测试，也不是 R4 资源成功。

测试运行使用与正常 `driver-v19` 字节相同的 Core/Native/sidecar。旧 tests-v8 的编译测试源码适用性已经逐项核对；后来仅增加诊断工具模式及归档工具，没有更改生产和编译测试源码。独立审查核验了相关源码/runtime/TRX。

所有临时 Native trace、IAT hook、decode-only 提前返回、额外 Flush/Shutdown/worker-count 和 CoFree 尝试均已撤下。正常 Native 源码和 6 个规范目录产物恢复为 `native-v3-repair3.json` 绑定身份。

当前 `LivePhotoBox.Native/src/media/video_converter.cpp` SHA256：

```text
0821580420B2BC1D3FD3A4D16107D0763EE149CCE3683A6C77FE66F78818F739
```

保留了第 5 节正常修复候选；“恢复正常源码”不是回退这些修复，也不是宣称候选已通过最终资源门槛。

## 9. 讨论过的处理方向与授权状态

### 9.1 更新当前机器的官方 HEVC 包

曾提出检查并在有恢复条件时更新/修复该组件。用户随后强调不能仅解决一台开发机，也要求先分清代码和验证器。这项系统变更**没有获得授权，也没有执行**。即便本机更新后通过，也不能单凭这一点推出大众环境正确。包名/哈希不是可重装回退包。

### 9.2 更换独立验证器

对于输出正确性，可以使用不同独立实现交叉检查，现有离线 FFmpeg/ffprobe 已在使用。它不能自动解决真实运行路径的资源滞留。SDK-only 实验已经把原项目验证框架和项目媒体代码从解码链中拿掉，现象仍然存在。

### 9.3 通用工作进程隔离

这是当前主代理提出、只读审查认为值得继续设计的备选方案，**不是已经选定必须实施的唯一方案**。

思路：由产品自己的短生命周期工作进程完成 MF decode/encode，任务结束时进程退役，约束组件保留资源对主应用的累积影响。面向所有目标用户，不检查机器名、HEVC 版本或文件名做特判；保留原 MF 后端和真实诊断，不引入外部媒体 CLI 生产依赖。

先做了工具层可行性实验：8 个独立测试 harness 进程，每个实际调用一次 public Core/Native MF 转换；8 个直接退出码为 0，全部已退役，workspace removed，隔离 TEMP 无文件，17 个输入哈希不变。父进程 handles 基线 144，首轮后 146，其后每轮和最终都是 146。

不能写成“零增长”：它是首轮 +2 后平稳。不能把它当作生产 IPC、Windows Job 取消、跨进程 authority/ownership、保留输出独立正确性或最终发布事务验证。

生产方案尚未实现，关键未完成设计包括：

- 新 worker 的固定安装路径、build/hash/version 与 Native/sidecar 匹配。
- 私有 IPC 的端点归属、访问限制、消息限额、严格解析、请求身份与防重放。
- opaque pointer / Native plan handle 不能直接跨进程传递。
- process-local workspace registration 不能假定在另一个进程仍有效。
- artifact role、fingerprint、root identity、ownership receipt、authority 在实际 mutation site 的完整验证。
- child staging 与 parent publication 的安全交接、post-inspection、reconciliation。
- 真实取消、崩溃、超时、Job 生命周期、无半发布结果与无孤儿进程。
- 增加产品可执行文件后的构建、打包和部署，及适用 R2/R3/R4 回归。

已有提案 `P7-R4-MF-isolation-proposal.md`。用户 AGENTS/Bootstrap 0.2 要求影响运行/打包依赖、执行/安全边界的改动先取得决定。因此只做了工具实验和提案；没有把 worker 加入产品，也没有更换生产后端或改公开 Contract/C ABI。

## 10. 正式证据、独立审查与异常记录

正式证据根目录：

```text
D:/Projects/live-photo-box/.ai-tmp/evidence/P7/P7-R4-large-batch-soak/
```

| 记录 | 状态 / 范围 | 索引 SHA256 |
|---|---|---|
| run-1 | FAIL_RESOURCE_RETENTION_RECOVERY_SNAPSHOT；2354 个 payload 全量复制/哈希核对；独立审查接受失败恢复记录，不是 R4 PASS | `9C1A97A7F01F9C1FA1ED963CE2AF2A2F8A135C5D5F6B909AB399EBB943BF12D7` |
| run-2 | SDK_DIAGNOSTIC_CONFIRMED_RESOURCE_BLOCKER_R4_OPEN；595 个 payload 全量复制/哈希核对；包含 SDK 对照、正常候选回归、工具隔离试验及源码/runtime | `3FD50A90DAEDF8B3FA158D2E8604CB8324C0C0C300657B81B8AE46D1EE4F8522` |

run-1 归档在全部 payload 和 index 已写入后，仅最后打印含中文路径时发生 CP1252 控制台编码异常。随后单独全量复核 durable index 和 2354 文件一致，没有覆盖/重跑 run-1。这个工具异常与媒体资源问题不同。

独立上下文 `/root/r4_closure_review` 未参与被审候选编辑。对 run-1 抽查 16 个关键 payload 并核对恢复源码/Native 产物；对 run-2 抽查 30 个关键 payload，并核对当前源码、测试编译适用性、runtime 和 TRX。全量 payload 哈希由主代理完成；没有冒称独立审查者重算了全部文件。

run-2 下可直接查阅：

```text
evidence-index.json
copy-verification.json
independent-sdk-review.json
independent-proposal-review.json
independent-diagnostic-review.json
followup-index.json
raw/sdk-repro/hevc-full/summary.json
raw/sdk-repro/h264-full/summary.json
raw/sdk-repro-ro-init/hevc-full/summary.json
raw/worker-isolation-feasibility-v36/summary.json
raw/targeted-v35/summary.json
raw/targeted-v35/results/r4.trx
bindings/functional-candidate-v35.json
source/tools/p7-r4-soak-validation/sdk-repro/main.cpp
```

原始 scratch/cache 路径并不是唯一长期证据，关键结果已复制到上述正式目录。R0-R3 sealed evidence 保留。`.ai/tasks/.../TASK.md` 中有追加式历史 checkpoint，前面的“某诊断仍在跑”已经过时，恢复时应以最后 source-verified checkpoint 和正式证据为准。

## 11. 希望另一 AI 独立分析的问题

请不要默认接受主代理的归因或进程隔离建议。重点分析：

1. SDK-only 最小程序是否仍有 COM/MF 生命周期、sample/buffer、线程模型或 EOS/Flush 使用错误？若有，请指明具体符号、调用顺序及正式依据。
2. 当前计数与 H264 对照是否充分排除测量工具自身累积？还需要哪些最小、有限、有辨识力的对照？
3. 在直接 Release、MFShutdown 和 60 秒 quiet 后仍残留，哪些证据还缺失，才能区分缓存、延迟回收、闭源组件泄漏或系统集成行为？
4. 是否存在更小、通用、文档化的正确关闭方式，能在不手动关闭外部组件内部 handles、不强制卸载 DLL、不改 backend/ABI 的情况下解决？
5. 第 5 节四项项目修改各自是否合理？哪些是真正缺陷修复，哪些需要进一步论证？不能因为没有修复当前 Semaphore 问题，就自动断言它们都应保留或都应删除。
6. 混合长跑 private-memory 增长是否还需要独立归因？请勿把所有资源增长都归因于已复现的一个 Semaphore/次。
7. 如果是当前平台组件路径问题，面向普通用户最合理的处理是什么？通用进程隔离是否必要、过重，或者有更小的替代方案？请比较正确性、复杂度、取消、事务/authority、打包、性能和维护成本。
8. 若建议隔离，如何保证不绕过已有 artifact identity、owner protocol、root identity、mutation gate 和 publish/rollback Contract？
9. 哪些结论需要第二台机器/不同系统组件版本才能支持？在没有这些证据时，应如何准确表达兼容性风险及 R4 状态？

请把“已证实事实”“合理推测”“待补证据”“建议修改”分开。不要通过放宽 no-obvious-leak 标准、删除测试、增加隐式 fallback 或把故障写成通过来结束 R4。

## 12. 当前停止边界与继续条件

当前状态：**R4 = IN_PROGRESS / RESOURCE BLOCKER OPEN**。

已确认的项目修复及相关 223 项回归通过；独立 SDK 重现证明项目代码不是此特定现象的必要条件；尚无被验证的生产级资源修复。系统未变更，生产 worker 未实现，公开 Contract/ABI 未改。

下一步先分析并决定合理方案。如果实际需要变更生产进程/打包/依赖/安全边界，取得用户明确决定后再实施。真正修复后，先验证短重复实验不再持续增长，再对新实际生产候选完成适用回归、完整混合长跑、独立媒体与资源验证、正式新 run 及 fresh read-only closure review。停止在 R4，不自动进入 R5/P8。


## 附录 A：独立 SDK 复现程序完整源码

以下源码逐字读取当前已验证文件，便于只拿到这份 MD 的 AI 审查。这里的程序是诊断工具，不是产品新增运行组件。

### A.1 main.cpp

```cpp
// Standalone Windows SDK diagnostic. No LivePhotoBox headers, libraries, parsers,
// encoder, workspace, sidecar or managed runtime are linked or called.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <psapi.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <vector>
#include <cstdint>

using Microsoft::WRL::ComPtr;

struct handle_entry {
    uintptr_t object, process, handle;
    ULONG access;
    USHORT creator, type;
    ULONG attributes, reserved;
};
static_assert(sizeof(handle_entry) == 40);

static int semaphore_count() {
    HANDLE probe = CreateSemaphoreW(nullptr, 0, 1, nullptr);
    if (!probe) return -1;
    using query_fn = LONG (NTAPI*)(ULONG, void*, ULONG, ULONG*);
    auto query = reinterpret_cast<query_fn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    int result = -1;
    if (query) {
        std::vector<uint8_t> bytes(1024U * 1024U);
        ULONG required = 0;
        LONG status = query(64U, bytes.data(), static_cast<ULONG>(bytes.size()), &required);
        if (static_cast<ULONG>(status) == 0xC0000004U && required < 64U * 1024U * 1024U) {
            bytes.resize(static_cast<size_t>(required) + 65536U);
            status = query(64U, bytes.data(), static_cast<ULONG>(bytes.size()), &required);
        }
        if (status == 0 && bytes.size() >= 16U) {
            uintptr_t count = 0;
            std::memcpy(&count, bytes.data(), sizeof(count));
            if (count <= (bytes.size() - 16U) / sizeof(handle_entry)) {
                USHORT semaphore_type = 0;
                for (uintptr_t i = 0; i < count; ++i) {
                    handle_entry row{};
                    std::memcpy(&row, bytes.data() + 16U + i * sizeof(row), sizeof(row));
                    if (row.process == GetCurrentProcessId() && row.handle == reinterpret_cast<uintptr_t>(probe)) semaphore_type = row.type;
                }
                if (semaphore_type) {
                    result = -1; // Exclude our own probe.
                    for (uintptr_t i = 0; i < count; ++i) {
                        handle_entry row{};
                        std::memcpy(&row, bytes.data() + 16U + i * sizeof(row), sizeof(row));
                        if (row.process == GetCurrentProcessId() && row.type == semaphore_type) ++result;
                    }
                }
            }
        }
    }
    CloseHandle(probe);
    return result;
}

static void snapshot(const char* kind, int iteration, HRESULT hr, unsigned samples) {
    DWORD handles = 0;
    GetProcessHandleCount(GetCurrentProcess(), &handles);
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
    std::printf("{\"kind\":\"%s\",\"iteration\":%d,\"hr\":%ld,\"decodedSamples\":%u,\"handles\":%lu,\"semaphores\":%d,\"privateBytes\":%llu,\"tickMilliseconds\":%llu}\n",
        kind, iteration, static_cast<long>(hr), samples, static_cast<unsigned long>(handles), semaphore_count(),
        static_cast<unsigned long long>(memory.PrivateUsage), static_cast<unsigned long long>(GetTickCount64()));
    std::fflush(stdout);
}

static HRESULT decode(const wchar_t* path, bool full, unsigned& samples) {
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 3);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, FALSE);
    ComPtr<IMFSourceReader> reader;
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromURL(path, attributes.Get(), &reader);
    if (SUCCEEDED(hr)) hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (SUCCEEDED(hr)) hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE);
    ComPtr<IMFMediaType> type;
    if (SUCCEEDED(hr)) hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.Get());
    if (FAILED(hr)) return hr;
    for (unsigned attempt = 0; attempt < 1000000U; ++attempt) {
        DWORD stream = 0, flags = 0;
        LONGLONG timestamp = 0;
        ComPtr<IMFSample> sample;
        hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream, &flags, &timestamp, &sample);
        if (FAILED(hr)) return hr;
        if (flags & MF_SOURCE_READERF_ERROR) return E_FAIL;
        if (sample) ++samples;
        if ((!full && samples) || (flags & MF_SOURCE_READERF_ENDOFSTREAM)) {
            sample.Reset();
            const HRESULT flush_hr = reader->Flush(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS));
            return samples ? flush_hr : E_FAIL;
        }
    }
    return E_FAIL;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 2; // input, repetitions, first|full, quiet seconds
    const int repetitions = _wtoi(argv[2]);
    const int quiet = _wtoi(argv[4]);
    if (repetitions < 1 || repetitions > 1000 || quiet < 0 || quiet > 120) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) { CoUninitialize(); return 4; }
    snapshot("baseline", -1, hr, 0);
    bool passed = true;
    for (int iteration = 0; iteration < repetitions; ++iteration) {
        unsigned samples = 0;
        hr = decode(argv[1], std::wcscmp(argv[3], L"full") == 0, samples);
        snapshot("after-release", iteration, hr, samples);
        if (FAILED(hr)) { passed = false; break; }
    }
    hr = MFShutdown();
    CoUninitialize();
    snapshot("after-runtime", -1, hr, 0);
    for (int second = 0; second < quiet; ++second) {
        Sleep(1000);
        snapshot("quiet", second + 1, S_OK, 0);
    }
    return passed && SUCCEEDED(hr) ? 0 : 5;
}

```

### A.2 CMakeLists.txt

```cmake
cmake_minimum_required(VERSION 3.24)
project(MfSdkLifecycleRepro LANGUAGES CXX)
add_executable(MfSdkLifecycleRepro main.cpp)
target_compile_features(MfSdkLifecycleRepro PRIVATE cxx_std_20)
target_compile_definitions(MfSdkLifecycleRepro PRIVATE UNICODE _UNICODE NOMINMAX)
target_compile_options(MfSdkLifecycleRepro PRIVATE /W4 /WX /EHsc)
target_link_libraries(MfSdkLifecycleRepro PRIVATE mfplat mfreadwrite mfuuid ole32 psapi)

```

## 附录 B：本轮三个产品文件相对 R4 开始时的实际差异

以下 diff 的 before 来自 sealed R3 source，并与 R4 baseline.json 中的 before SHA256 核对一致；after 为当前正常产品源码。不是工作区所有历史改动的总 diff。临时诊断代码不在当前源码中。请逐项分析这些修改，而不是默认接受先前 AI 的判断。

### LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs

```diff
--- before-R4/LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs
+++ current/LivePhotoBox.Core/Services/LivePhotoMergeRunnerService.cs
@@ -261,16 +261,24 @@
         // a parked OS thread, so cancellation and Set() both propagate cleanly.
         private static async Task WaitPauseAsync(ManualResetEventSlim pauseEvent, CancellationToken token)
         {
+            token.ThrowIfCancellationRequested();
+            if (pauseEvent.IsSet) return;
+
             var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
-            var reg = token.Register(() => tcs.TrySetCanceled(token));
+            // Registered waits observe the signal without parking a worker.
+            // Cancellation must retire the actual wait, not merely its proxy task.
+            RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(
+                pauseEvent.WaitHandle,
+                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
+                tcs, Timeout.Infinite, executeOnlyOnce: true);
             try
             {
-                var waitTask = Task.Run(() => { pauseEvent.Wait(); tcs.TrySetResult(true); }, token);
+                using var reg = token.Register(() => tcs.TrySetCanceled(token));
                 await tcs.Task.ConfigureAwait(false);
             }
             finally
             {
-                reg.Dispose();
+                wait.Unregister(null);
             }
         }


```

### LivePhotoBox.Native/src/media/media_inspector.cpp

```diff
--- before-R4/LivePhotoBox.Native/src/media/media_inspector.cpp
+++ current/LivePhotoBox.Native/src/media/media_inspector.cpp
@@ -21,6 +21,8 @@
 #include <cstdlib>
 #include <charconv>
 #include <cmath>
+#include <new>
+#include <stdexcept>

 namespace fs = std::filesystem;

@@ -781,7 +783,14 @@
         ? max_bytes
         : static_cast<size_t>(size);

-    std::vector<uint8_t> buffer(to_read);
+    std::vector<uint8_t> buffer;
+    try {
+        buffer.resize(to_read);
+    } catch (...) {
+        // The opened source handle must not leak if vector allocation fails.
+        CloseHandle(handle);
+        throw;
+    }
     size_t offset = 0;
     while (offset < to_read) {
         const DWORD requested = static_cast<DWORD>(std::min<size_t>(to_read - offset, 1024 * 1024));
@@ -3242,7 +3251,18 @@
         return LPB_RESULT_INVALID_ARGUMENT;
     }

-    auto primary_data = read_file_bytes(primary_path, 0, share_existing_writer);
+    std::vector<uint8_t> primary_data;
+    try {
+        primary_data = read_file_bytes(primary_path, 0, share_existing_writer);
+    } catch (const std::bad_alloc&) {
+        set_error(context, "Primary inspection snapshot allocation is unavailable; no extraction plan was issued.");
+        set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_READ);
+        return LPB_RESULT_INVALID_ARGUMENT;
+    } catch (const std::length_error&) {
+        set_error(context, "Primary inspection snapshot exceeds the allocator's supported length; no extraction plan was issued.");
+        set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_READ);
+        return LPB_RESULT_INVALID_ARGUMENT;
+    }
     if (primary_data.empty()) {
         set_error(context, "Failed to read primary file.");
         set_inspection_status(context, LPB_INSPECTION_FAILURE_IO, LPB_INSPECTION_STAGE_READ);
@@ -3275,7 +3295,18 @@

     // Dual file check
     if (secondary_path && std::strlen(secondary_path) > 0) {
-        auto sec_data = read_file_bytes(secondary_path, 0, share_existing_writer);
+        std::vector<uint8_t> sec_data;
+        try {
+            sec_data = read_file_bytes(secondary_path, 0, share_existing_writer);
+        } catch (const std::bad_alloc&) {
+            set_error(context, "Secondary inspection snapshot allocation is unavailable; no extraction plan was issued.");
+            set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_READ);
+            return LPB_RESULT_INVALID_ARGUMENT;
+        } catch (const std::length_error&) {
+            set_error(context, "Secondary inspection snapshot exceeds the allocator's supported length; no extraction plan was issued.");
+            set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_READ);
+            return LPB_RESULT_INVALID_ARGUMENT;
+        }
         uint64_t secondary_size = sec_data.size();
         if (secondary_size == 0) {
             set_error(context, "Secondary file is empty or does not exist.");
@@ -5230,7 +5261,7 @@
     if (!context || !primary_path || primary_path[0] == '\0') return LPB_RESULT_INVALID_ARGUMENT;
     try {
         const auto load_bounded = [share_existing_writer](const char* path,
-            std::vector<uint8_t>& bytes) noexcept {
+            std::vector<uint8_t>& bytes) {
             if (!path || path[0] == '\0') return false;
             std::error_code ec;
             const uintmax_t size = fs::file_size(utf8_to_path(path), ec);
@@ -5427,6 +5458,16 @@
         set_error(context, "GainMap metadata lacks a complete supported ISO or Apple mapping.");
         out_metadata = {};
         return LPB_RESULT_INVALID_ARGUMENT;
+    } catch (const std::bad_alloc&) {
+        set_error(context, "GainMap inspection snapshot allocation is unavailable.");
+        set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_METADATA);
+        out_metadata = {};
+        return LPB_RESULT_INVALID_ARGUMENT;
+    } catch (const std::length_error&) {
+        set_error(context, "GainMap inspection snapshot exceeds the allocator's supported length.");
+        set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_METADATA);
+        out_metadata = {};
+        return LPB_RESULT_INVALID_ARGUMENT;
     } catch (const std::exception& ex) {
         set_error(context, ex.what());
         out_metadata = {};

```

### LivePhotoBox.Native/src/media/video_converter.cpp

```diff
--- before-R4/LivePhotoBox.Native/src/media/video_converter.cpp
+++ current/LivePhotoBox.Native/src/media/video_converter.cpp
@@ -794,7 +794,14 @@
     ~mf_activation_list() {
         if (values != nullptr) {
             for (UINT32 index = 0U; index < count; ++index) {
-                if (values[index] != nullptr) values[index]->Release();
+                if (values[index] != nullptr) {
+                    // ActivateObject retains the created encoder. Release of
+                    // IMFActivate alone does not complete its shutdown protocol.
+                    // Retire every activation before releasing the list, also
+                    // on capability probes and failed negotiation exits.
+                    static_cast<void>(values[index]->ShutdownObject());
+                    values[index]->Release();
+                }
             }
             CoTaskMemFree(values);
         }
@@ -1748,6 +1755,22 @@
     return true;
 }

+struct mf_media_sink_owner final {
+    IMFMediaSink* value{};
+    mf_media_sink_owner() noexcept = default;
+    mf_media_sink_owner(const mf_media_sink_owner&) = delete;
+    mf_media_sink_owner& operator=(const mf_media_sink_owner&) = delete;
+    ~mf_media_sink_owner() { static_cast<void>(close()); }
+    HRESULT close() noexcept {
+        IMFMediaSink* sink = value;
+        value = nullptr;
+        if (sink == nullptr) return S_OK;
+        const HRESULT hr = sink->Shutdown();
+        sink->Release();
+        return hr;
+    }
+};
+
 static lpb_result mux_retained_software_samples(
     lpb_context* context,
     const fs::path& output_path,
@@ -1782,6 +1805,10 @@
         set_error(context, "Failed to configure a software-only Media Foundation SinkWriter.");
         return LPB_RESULT_INTERNAL_ERROR;
     }
+    // Finalize writes the MP4 indexes; Shutdown breaks the media sink's
+    // circular references/event queue. Keep this owner alive past all writer
+    // interfaces, and retire it on every early-return path as well.
+    mf_media_sink_owner sink;
     mf_owned_ptr<IMFSinkWriter> writer;
     hr = MFCreateSinkWriterFromURL(output_path.c_str(), owned_stream.get(),
         writer_attributes.get(), writer.put());
@@ -1817,6 +1844,14 @@
         set_error(context, "Media Foundation SinkWriter could not begin compressed sample muxing.");
         return LPB_RESULT_INTERNAL_ERROR;
     }
+    hr = writer->GetServiceForStream(static_cast<DWORD>(MF_SINK_WRITER_MEDIASINK), GUID_NULL,
+        IID_PPV_ARGS(&sink.value));
+    if (FAILED(hr) || sink.value == nullptr) {
+        const std::string reason = "Media Foundation MP4 media sink lifetime ownership could not be queried after BeginWriting; HRESULT=" +
+            std::to_string(static_cast<uint32_t>(hr)) + ".";
+        set_error(context, reason.c_str());
+        return LPB_RESULT_INTERNAL_ERROR;
+    }
     mf_owned_ptr<IMFSinkWriterEx> writer_ex;
     hr = writer->QueryInterface(IID_PPV_ARGS(writer_ex.put()));
     if (FAILED(hr) || !writer_ex) {
@@ -1882,6 +1917,13 @@
     hr = writer->Finalize();
     if (FAILED(hr)) {
         set_error(context, "Media Foundation SinkWriter failed to finalize the completed compressed-sample mux.");
+        return LPB_RESULT_INTERNAL_ERROR;
+    }
+    writer_ex.reset();
+    writer.reset();
+    hr = sink.close();
+    if (FAILED(hr) && hr != MF_E_SHUTDOWN) {
+        set_error(context, "Media Foundation MP4 media sink shutdown failed after finalization.");
         return LPB_RESULT_INTERNAL_ERROR;
     }
     return LPB_RESULT_OK;

```

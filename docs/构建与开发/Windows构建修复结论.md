# Windows / Visual Studio 构建修复结论

日期：2026-10-30。范围：本次获准的构建集成修复，`AD_HOC / LOCAL_OWNER`。

本次收口解决 Visual Studio 编译、程序启动、运行库复制和构建路径问题。用户最后明确将 GUI 操作及不同照片转换留给自己测试；这些不纳入本报告的通过结论。P9-R8 协议施工仍暂停。

## 问题与处理结果

原来确实有两套不同的 Native 构建路径：解决方案中的旧 C++ 工程自己编译，Core 又调用 CMake。旧工程没有跟上当前源码和依赖；正式脚本还引用历史阶段的 AI 工作目录。此前局部 Native/Core 测试通过，不能证明用户打开解决方案就能编译。这是工程集成缺口。

| 项目 | 现在的结果 | 验证情况 |
|---|---|---|
| VS 里的旧 C++ 工程 | 改成调用同一套 CMake 构建，保留 IDE 源码浏览，不再自己维护另一份编译和链接规则 | 解决方案实际生成、重新生成通过 |
| Core、CLI、GUI 的 Native 入口 | 都走正式 `scripts/native/build-native.ps1`，Core 独立构建也会自动准备运行库 | 公共解决方案和独立 Core 复制检查通过 |
| Debug 视频运行库遗漏 | Debug 和 Release 都生成并携带已有的视频运行库和许可证，测试专用 DLL 单独放置 | 两种配置的 DLL/PDB 收据、复制结果和视频库加载通过 |
| 正式构建引用 AI 临时文件 | 固定源码包、补丁、说明和规则放在 `build/native/video/` 与 `scripts/native/`；依赖和缓存放 `artifacts/` | 不含 AI 目录的源码副本，两种配置编译通过 |
| 目录搬迁后留下旧 CMake 路径 | 在清理或构建前检查缓存位置，不匹配的生成目录保留改名，再重新配置 | 复制真实旧缓存的定向检查通过，原缓存和标记文件保留 |
| 工程路径带空格 | FFmpeg 在自身源码目录构建，安装和 codec 参数使用相对路径，避免路径被拆成多个参数 | `artifacts/build-validation/clean checkout` 实际编译通过 |
| 输出目录留有旧 DLL | 强制复制当前 Native 和视频运行库；跳过编译仍要核对完整文件和哈希 | 未来日期的旧测试文件也被当前 DLL 替换 |
| 不同 PowerShell 误判缓存 | 文件列表改为固定的 ordinal 排序，避免 PS5/PS7 产生不同构建指纹 | 两宿主固定/真实工具链指纹一致；四配置 PS7 构建后 PS5 校验通过 |
| GUI 本地启动和调试配置 | 默认列出 Unpackaged，启用 Native 调试；修正资源名称中点与 PRI 路径中斜线的对应 | VS F5 曾实际启动；最后成品 Debug/Release 均新建进程、出现主窗口并正常退出 |
| 发布入口漏报失败 | Native、publish、安装器和 ZIP 失败明确向上传递；ZIP 必需文件包含视频运行库 | 源码审查通过；本次没有执行完整发布打包、上传或远程 CI |

没有改变媒体协议、保留策略或公开 ABI，没有新增生产依赖或安装新的开发工具。视频 DLL 单独使用与既有固定 codec archives 一致的静态运行库，Debug 主 Native 保持调试运行库。没有恢复旧 runtime 或自动 fallback。

## 实际验证

| 检查 | 结果及边界 |
|---|---|
| 主工作区 x64 Debug / Release 重新生成 | 两种配置均直接退出 0 |
| 主工作区 x64 Debug / Release 后续增量生成 | 两种配置均直接退出 0 |
| 不含 AI 目录的源码副本 | 含当前未提交源码，不含 `.ai`、`.ai-tmp`、`.git` 或从主工作区复制的构建输出；正常 vcpkg 工具、下载和二进制缓存允许共享。副本 Debug/Release 均退出 0 |
| 最后一次缓存修复 | 只修改排序 helper。主工作区与副本的双配置共四次 Native 正常增量构建使用 PS7，随后正式 PS5 入口校验均通过；所有相关 DLL/PDB 的字节保持不变，因此复用已有解决方案和启动证据 |
| GUI 启动 | 最后 Debug/Release 的准确项目 EXE 均出现主窗口并正常退出 0；只检查启动和退出，没有点击产品功能或转换照片 |
| CLI 启动 | 最后 Debug/Release 的 `lpb --info` 均退出 0，Native 显示 Available，实际加载模块身份匹配 |
| 视频库 | 最后双配置 DLL 实际加载、只读 ABI 查询成功 |
| 运行库与许可证复制 | 主工作区及副本的 GUI/CLI 输出与各自 `artifacts/native` DLL、三份正式许可证逐项一致 |
| 构建收据保护 | 原有 10 项和新增 Debug 3 项通过。这些使用合成非 PE 字节，只证明文件集及哈希保护，不冒充运行测试 |
| 施工中的定向回归 | 50/50、无跳过；保留原候选身份，不重新标成最终成品或全产品测试 |
| 独立检查 | 源码审查与结果复核由两个未参与所审源码修改的独立上下文完成；跨宿主最后补验另有补充结论 |

实际使用本机 Visual Studio 2026 / MSBuild 18、项目指定 .NET SDK 和 Windows x64 工具链。VS2022 仅保留脚本兼容选择，没有据此宣称本次已实测。仍存在分析器和 PRI 等编译警告；没有通过屏蔽失败或放宽断言制造通过。

## 正式使用位置

在 VS 打开根目录 `Live Photo Box.sln`，选择 `x64`、`Debug` 或 `Release`，将 `LivePhotoBox` 设为启动项目，选择 `Live Photo Box (Unpackaged)`。已有的本地 `.csproj.user` 可能记住 Package，需要改选一次；本机验证时已改选，原设置留有备份。不要选择 Native DLL 工程作为程序启动项目。

- GUI：`LivePhotoBox/bin/x64/<Debug或Release>/net9.0-windows10.0.19041.0/win-x64/Live Photo Box.exe`。
- CLI：`LivePhotoBox.CLI/bin/<Debug或Release>/net9.0-windows10.0.19041.0/lpb.exe`。
- Native：`artifacts/native/<Debug或Release>/win-x64/`。
- 构建方法和目录规则：[Windows构建.md](Windows构建.md)。

历史备用 `bin` 路径可能仍有旧程序，不属于本次成品，未为整理而清空。运行程序占用 DLL 时必须先关闭程序再重新生成；本次保留了实际文件占用失败，并在用户关闭后完成重新生成。

## 证据和停止边界

详细命令、直接退出码、哈希、日志、binlog、失败尝试和独立报告都在同一个稳定任务目录：`.ai-tmp/workspace/maintenance/build-integration/`。其中最后源码身份清单为 `cold-checkout/source-hashes-shell-final.jsonl`，执行及二进制不变证明为 `shell-cache-final/results.json`；独立结论见 `independent-verify/final-verdict.md` 与最后的 `final-verdict-shell.md`。本结论文档在编译结束后写入，不属于源码副本的编译输入清单。

最初失败、施工期间输入变更被拒、文件占用和启动检查 helper 的失败记录均保留，没有清空工作区或改写旧通过记录。后续记录明确区分了产品问题与检查 helper 的过早窗口观测、退出码读取问题。

机械清点、固定收据检查和部分构建脚本工作按用户要求交给 Luna；其额度耗尽后由主代理继续执行，没有静默更换机械代理模型。源码审查和结果复核分别使用独立上下文。

本次不签认完整 GUI 操作、不同照片的转换结果、保留效果、手机相册识别、协议正式支持、ZIP/安装器实际发布、远程 CI 或 P9-R8 全部完成。修改保留在本地工作区，未 push、tag 或 release。工程收口后停止，等待用户自行测试的反馈，不自动恢复协议施工或进入后续阶段。

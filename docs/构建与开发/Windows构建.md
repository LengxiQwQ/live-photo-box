# Windows x64 构建与调试

正式构建入口是根目录的 Visual Studio 解决方案。Native 工程是 CMake 转调入口，Core、GUI、CLI 使用同一套 Native 构建；不要手工添加第三方库或维护另一份 C++ 编译清单。

## 开发环境

- Visual Studio 2022/2026 的 .NET 桌面、Windows/WinUI 与 C++ x64 工作负载，以及项目声明的 Windows SDK。实际已验证的安装版本见本次构建报告，不能将工具选择兼容性当成每个 VS 版本均已实测。
- `global.json` 指定的 .NET SDK；目标运行时仍以各项目声明为准。
- Visual Studio 自带 CMake、vcpkg、MSVC；Release 视频依赖编译还使用 Python 3.9+、Git，以及由 vcpkg 恢复的 MSYS2/NASM。Python 仅参与构建，不随产品打包。
- 首次恢复第三方依赖需要联网并耗时较长；固定版本和源码哈希不随本机缓存改变。

## 使用

在 Visual Studio 打开 `Live Photo Box.sln`，选择 x64、Debug 或 Release，生成解决方案，以 GUI 为启动项目，选择 `Unpackaged` 配置按 F5 运行。该配置已启用 C#/Native 混合调试，不需要为普通本地运行部署 MSIX。已有 `.csproj.user` 可能记住旧的 Package 选择，需要在 VS 中改选一次。Native 调试加载当前配置的 PDB。

Debug 与 Release 的产品构建均带完整的现有视频运行库和许可证。视频依赖按固定的静态 Release 配方恢复，两种配置共用依赖缓存；视频 DLL 自身使用与该依赖一致的静态运行库，Debug 主 Native 仍保留自己的调试运行库和符号。测试专用 Native 与产品输出隔离。

命令行的等价入口是 Visual Studio 的 MSBuild：`MSBuild.exe "Live Photo Box.sln" /restore /t:Build /m /p:Configuration=Debug /p:Platform=x64`。改为 Release 构建发布配置；使用 `/t:Rebuild` 重新生成。

单独 `dotnet build` GUI 或 CLI 会自动准备 Native。`scripts/verify.ps1` 默认 `Build`，只恢复并构建公共解决方案；日常完整单元验证优先只构建受影响项目，再调用正式 CLI 检查真实成品。`Fast`/`Full` 当前执行同一套较宽检查，不是小修改默认命令，也不代表全产品验收；`Release` 额外运行的现有 CLI 集成仅为 split，不证明 P9 全来源转换。测试 scope 必须显式选择，已有 CI 显式选择不受默认值调整影响。

`SkipNativeBuild=true` 仅用于调用者已完成 Native 的流程，并核验本次源码、工具链、配置及产物身份；不是缺少运行库或跳过错误的开关。不要将它作为 IDE 构建必须手工设置的条件。

## 文件位置

- 正式规则和固定输入：根 CMake、`build/native/`、`scripts/native/`；源码包、补丁和许可证是版本化输入。
- 可恢复生成物：`artifacts/build/`（中间文件）、`artifacts/deps/`（依赖）、`artifacts/cache/`（缓存）。CMake 生成工程不跨 checkout 缓存。
- 产品 Native：`artifacts/native/<配置>/win-x64/`；测试 DLL 位于单独的 TestHarness 目录。
- AI 测试材料仍在 `.ai-tmp`；正常构建不读取它。不要把历史阶段目录、旧绝对路径或目录链接作为依赖修复方法。

编译失败即失败，不能用输出目录中已有 exe 判断成功。未提交或未推送的本地结果不代表远程 CI 已通过；编译通过也不代表协议、手机或整个产品验收完成。

# Live Photo Box — 项目总览

> **生成日期**: 2026-06-26  
> **项目版本**: 1.14.10.0  
> **作者**: LengxiQwQ  
> **仓库**: https://github.com/lengxiqwq/live-photo-box  
> **许可**: GPL 3.0

---

## 1. 项目概述

**Live Photo Box（实况照片工具箱）**是一款专为 Windows 打造的 Apple 实况照片 (Live Photos) 管理与修复桌面应用。基于 **WinUI 3 (Windows App SDK 1.8)** 构建，原生适配 Windows 11 Fluent Design 设计规范，支持 Mica / Acrylic 材质、深色/浅色主题自动切换。

### 核心功能

| 功能 | 说明 |
|------|------|
| **🔗 实况照片合成 (Combo)** | 将任意静态图片 + 视频素材组合为标准实况照片。支持 `Micro Video V1` / `Motion Photo V2` / `O-Live Photo`（OPPO）等协议，自动写入完整 `EXIF` + `QuickTime` 元数据 |
| **📸 实况照片拆分 (Split)** | 一键拆分为独立静态图片（`JPG` / `HEIC`）和视频（`MOV` / `MP4`）。智能剥离 Google `XMP` 元数据，按 `JPEG` 段结构逐段重建 |
| **🛠️ 实况照片修复 (Repair)** | 深度修复 iPhone 实况照片导出到 Windows 后的显示异常（缩略图拉伸、前置旋转、HEIC 方向、UUID 丢失） |
| **🖼️ 封面修改 (Key Photo)** | 自由更换实况照片封面帧（功能开发中） |
| **📂 自动整理相册** | 按拍摄设备、日期、实况照片类型自动扫描分类归档（功能开发中） |

---

## 2. 技术栈详情

### 语言与框架

| 层级 | 技术 | 版本 |
|------|------|------|
| 语言 | C# | 13.0 |
| 运行时 | .NET | 9.0 |
| UI 框架 | Windows App SDK（WinUI 3） | 1.8 |
| 架构 | MVVM（CommunityToolkit.Mvvm） | 8.4.2 |
| 最低系统 | Windows 10 (1809+) | 10.0.19041.0 |

### 关键 NuGet 依赖

| 包名 | 版本 | 用途 |
|------|------|------|
| `CommunityToolkit.Mvvm` | 8.4.2 | MVVM 架构 (ObservableObject, RelayCommand, 源生成器) |
| `CommunityToolkit.WinUI.Controls.Primitives` | 8.2.251219 | WinUI 扩展控件 |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | 8.2.251219 | 设置页卡片控件 |
| `FluentIcons.WinUI` | 2.0.320 | Fluent 图标库 |
| `Magick.NET-Q16-x64` | 14.14.0 | ImageMagick 图像处理 (HEIC 解码) |
| `Microsoft.Graphics.Win2D` | 1.3.2 | GPU 加速 2D 图形渲染 |
| `Microsoft.Xaml.Behaviors.WinUI.Managed` | 3.0.0 | XAML 行为 (EventTriggerBehavior 等) |
| `System.Management` | 8.0.0 | WMI 硬件信息查询 |

### 外部工具依赖 (自动探测 PATH 或 Tools/ 目录)

| 工具 | 用途 |
|------|------|
| **ExifTool** (v13.x) | 图像/视频元数据读写（常驻进程模式，`-stay_open` 复用） |
| **FFmpeg** | 视频编解码，支持 NVENC / QSV / AMF 硬件加速 |
| **jpegtran** | JPEG 无损旋转、缩略图剥离 |

---

## 3. 支持的实况照片协议

| 协议 | 来源 | 说明 |
|------|------|------|
| `Micro Video V1` | Google（已弃用，但老设备兼容性高） | `MP4` 视频附加在 `JPEG` 末尾，`GCamera:MicroVideoOffset` 记录偏移。旧版小米 MIUI / 旧版 Pixel 使用 |
| `Motion Photo V2` | Google | 现代标准，`Container:Directory` `XMP` 结构。Google Pixel / Xiaomi HyperOS 3+ 使用 |
| `O-Live Photo` | OPPO / OnePlus | 扩展 `Motion Photo V2`，增加 `OpCamera` 命名空间 + `EXIF` `UserComment`。OPPO ColorOS / OnePlus OxygenOS 使用 |

> ⚡ 目前任何协议合成的实况照片，均可在 Windows 11 上直接查看动态效果。

---

## 4. 项目目录树

```
live-photo-box/
├── .github/
│   ├── copilot-instructions.md        # GitHub Copilot AI 指令
│   └── workflows/
│       └── build.yml                  # GitHub Actions 自动构建与发布
│
├── .vscode/
│   ├── launch.json                    # VS Code 调试配置
│   └── tasks.json                     # VS Code 构建任务
│
├── changelogs/                        # 📁 版本更新日志
│   └── release-v1.14.10.md            # v1.14.10 发布说明
│
├── docs/                              # 📁 项目文档目录
│   ├── LivePhotoBox-ProjectOverview.md      # 👈 本文件
│   ├── LivePhotoBox-历史记录标识规范.md       # XMP 历史标记规范
│   ├── 小米实况照片协议分析报告.md            # 小米协议逆向分析
│   ├── 小米实况照片协议开发记录.md            # 小米协议开发全过程
│   ├── 苹果的矩阵幻象.md                    # Display Matrix 镜头判定
│   ├── GitHub-Release发布指南.md             # GitHub 发布指南
│   └── 发布流程.md                          # 自动发布流程文档
│
├── screenshots/                       # 📁 README 应用截图
│   ├── 主页.png
│   ├── 拆分页.png
│   ├── 合成页.png
│   ├── 修复页.png
│   ├── 设置.png
│   ├── acrylic_thin.png
│   ├── Microsoft.svg
│   └── GitHub.svg
│
├── scripts/                           # 📁 构建脚本
│   ├── build-release.ps1              # 本地发布打包脚本
│   ├── build-dev.ps1                  # 开发构建脚本
│   └── setup.iss                      # Inno Setup 安装包配置
│
├── publish/                           # 📁 构建产物输出目录
│   ├── Live-Photo-Box-v1.14.10-x64-portable.zip
│   └── Live-Photo-Box-v1.14.10-x64-setup.exe
│
├── Live Photo Box/                    # 📦 主项目 (WinUI 3 MSIX 应用)
│   ├── LivePhotoBox.csproj            # 项目文件
│   ├── Package.appxmanifest           # MSIX 包清单
│   ├── App.xaml / App.xaml.cs         # 应用入口
│   ├── MainWindow.xaml / MainWindow.xaml.cs  # 主窗口
│   │
│   ├── Assets/                        # 静态资源
│   │   ├── Icons/                     # 应用图标 (多分辨率)
│   │   ├── Banners/                   # 首页 Banner 图片
│   │   └── Fonts/                     # 字体资源
│   │
│   ├── Collections/                   # 自定义集合类
│   │   └── BulkObservableCollection.cs
│   │
│   ├── Controls/                      # 自定义控件
│   │   ├── LightboxPreview.xaml/.cs   # 全屏灯箱预览
│   │   └── PageStatusBar.xaml/.cs     # 底部状态栏
│   │
│   ├── Converters/                    # XAML 值转换器
│   │   ├── BackdropToAcrylicVisibilityConverter.cs
│   │   ├── BoolToDiagnosisErrorBrushConverter.cs
│   │   ├── CommonConverters.cs        # Bool↔Visibility 通用转换
│   │   ├── DoubleToPercentConverter.cs
│   │   ├── ProgressBarForegroundConverter.cs
│   │   ├── ProgressBarIndeterminateConverter.cs
│   │   └── StatusToColorConverter.cs
│   │
│   ├── Helpers/                       # 工具类
│   │   ├── ComboBoxHelper.cs
│   │   ├── FileNameFormatter.cs
│   │   ├── FileSizeFormatter.cs
│   │   ├── TaskListAutoScroller.cs
│   │   ├── TaskListScrollHelper.cs
│   │   └── VisualTreeHelperExtensions.cs
│   │
│   ├── Models/                        # 数据模型
│   │   ├── AppLogEntry.cs
│   │   ├── BannerPreset.cs
│   │   ├── FileHistoryInfo.cs
│   │   ├── LivePhotoConstants.cs
│   │   ├── LivePhotoSplitResult.cs
│   │   ├── MergeTask.cs
│   │   ├── ProcessStatus.cs
│   │   ├── ProgressBarState.cs
│   │   ├── RepairAnalysisResult.cs
│   │   ├── RepairFileEntry.cs
│   │   ├── RepairTask.cs
│   │   ├── SplitTask.cs
│   │   └── WorkProgressSnapshot.cs
│   │
│   ├── Services/                      # 服务层 (核心业务逻辑)
│   │   ├── AppSettingsService.cs
│   │   ├── CrashHandler.cs
│   │   ├── EncoderHelper.cs
│   │   ├── ExternalToolLocator.cs
│   │   ├── FeedbackService.cs
│   │   ├── FilePickerService.cs
│   │   ├── HardwareService.cs
│   │   ├── HeicConverterService.cs
│   │   ├── ImagePreviewService.cs
│   │   ├── LanguageService.cs
│   │   ├── LivePhotoBatchRunnerService.cs
│   │   ├── LivePhotoCompositionService.cs
│   │   ├── LivePhotoMergeRunnerService.cs
│   │   ├── LivePhotoMergeScanService.cs
│   │   ├── LivePhotoMergeService.cs
│   │   ├── LivePhotoMetadataMatcher.cs
│   │   ├── LivePhotoRepairService.cs
│   │   ├── LivePhotoSplitScanService.cs
│   │   ├── LivePhotoSplitService.cs
│   │   ├── LogService.cs
│   │   ├── PathHelper.cs
│   │   ├── PersistentExifTool.cs
│   │   ├── ResourceService.cs
│   │   ├── ThumbnailService.cs
│   │   ├── VideoTranscodeService.cs
│   │   └── WindowsAppSdkBootstrap.cs
│   │
│   │   └── Protocols/                 # 实况照片协议实现
│   │       ├── LivePhotoProtocol.cs          # 抽象基类
│   │       ├── MicroVideoV1Protocol.cs       # Google MicroVideo V1
│   │       ├── MotionPhotoV2Protocol.cs      # Google Motion Photo V2
│   │       └── OppoLivePhotoProtocol.cs      # OPPO/OnePlus O-Live
│   │
│   ├── Strings/                       # 多语言资源
│   │   ├── en-US/Resources.resw       # 英文
│   │   └── zh-Hans/Resources.resw     # 中文(简体)
│   │
│   ├── Tools/                         # 外部工具二进制
│   │   ├── exiftool.exe
│   │   ├── exiftool_files/            # exiftool Perl 运行环境
│   │   ├── jpegtran.exe
│   │   └── ffmpeg.exe (可选)
│   │
│   ├── ViewModels/                    # MVVM ViewModel 层
│   │   ├── ViewModelBase.cs
│   │   ├── WorkViewModelBase.cs
│   │   ├── WorkViewModelBase.ScanStateHook.cs
│   │   ├── AboutViewModel.cs
│   │   ├── AppViewModel.cs
│   │   ├── HistoryViewModel.cs
│   │   ├── HomeViewModel.cs
│   │   ├── KeyPhotoViewModel.cs
│   │   ├── MergeViewModel.cs
│   │   ├── PhotoClassifyViewModel.cs
│   │   ├── RepairViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   └── SplitViewModel.cs
│   │
│   └── Views/                         # XAML 页面
│       ├── AboutPage.xaml/.cs
│       ├── HistoryPage.xaml/.cs
│       ├── HomePage.xaml/.cs
│       ├── KeyPhotoPage.xaml/.cs
│       ├── MergePage.xaml/.cs
│       ├── PhotoClassifyPage.xaml/.cs
│       ├── RepairPage.xaml/.cs
│       ├── SettingsPage.xaml/.cs
│       ├── SettingsPageOld.xaml/.cs
│       └── SplitPage.xaml/.cs
│
├── README.md                          # 项目说明 (GitHub 首页)
├── LICENSE                            # GPL 3.0 许可
└── Generate-AppIcons.ps1              # 图标生成脚本
```

> 📖 完整目录说明见 [README](../README.md)

---

## 5. 解决方案与项目文件

### 解决方案 (`LivePhotoBox.sln`)
位于项目根目录（`Live Photo Box.sln` 为 VS 显示名）。

### 项目文件 (`LivePhotoBox.csproj`)

| 属性 | 值 |
|------|-----|
| 输出类型 | WinExe (Windows 可执行文件) |
| 目标框架 | `net9.0-windows10.0.19041.0` |
| 最低平台版本 | `10.0.17763.0` (Windows 10 1809) |
| C# 语言版本 | 13.0 |
| 可空引用类型 | 启用 |
| 目标平台 | x86, x64, ARM64 |
| 运行时标识符 | win-x86, win-x64, win-arm64 |
| WinUI | 启用 |
| MSIX 打包 | 启用 |
| 自包含发布 | 启用（无需用户安装 .NET 运行时） |
| Windows App SDK 自包含 | 启用 |
| 默认语言 | zh-Hans |
| 附属资源语言 | zh-Hans, en-US（构建后自动剥离其他语言） |
| 应用包名 | `LengxiQwQ.58702A0DB398F` |
| 应用版本 | 1.14.10.0 |

---

## 6. 核心入口与主窗口

### `App.xaml.cs` — 应用入口
- 继承 `Microsoft.UI.Xaml.Application`
- 职责: 全局初始化（抑制子进程崩溃对话框、语言设置、日志初始化、硬件检测、崩溃处理注册、构造激活 MainWindow）
- 持有全局单例 `AppViewModel`

### `MainWindow.xaml.cs` — 主窗口
- 继承 `Microsoft.UI.Xaml.Window`
- 职责: 窗口初始化与 DPI 适配、NavigationView 页面导航、背景材质 (Mica/Acrylic) 管理与切换、窗口透明控制、主题切换与标题栏按钮颜色、状态栏/历史导航可见性控制

### `WindowsAppSdkBootstrap.cs` — 启动引导
- 使用 `[ModuleInitializer]` 在 `Main()` 之前自动执行
- 解决非打包模式 (unpackaged) 下 WinRT 激活失败 (0xc000027b) 的问题
- 打包模式 (MSIX) 自动跳过；非打包模式动态注册 WinAppSDK 框架包

---

## 7. Models 层（数据模型）

| 文件 | 说明 |
|------|------|
| `AppLogEntry.cs` | 日志条目，包含 `LogLevel` 和 `LogSource` 枚举 |
| `BannerPreset.cs` | 首页 Banner 图片预设 |
| `FileHistoryInfo.cs` | 单次照片操作历史记录 |
| `LivePhotoConstants.cs` | 实况照片检测/拆分的共享常量，含 `MetadataMatchingMode` 枚举 |
| `LivePhotoSplitResult.cs` | 拆分结果：输出照片和视频文件路径 |
| `MergeTask.cs` | 合并任务：图片+视频配对，支持 MVVM 属性变更通知和缩略图懒加载 |
| `ProcessStatus.cs` | 任务处理状态枚举 (Idle/Processing/Success/Failed/Cancelled 等) |
| `ProgressBarState.cs` | 进度条运行状态枚举 (Idle/Scanning/Processing/Success/Pausing/Paused/Cancelled) |
| `RepairAnalysisResult.cs` | 照片/视频诊断问题类型和单文件诊断分析结果 |
| `RepairFileEntry.cs` | 修复队列中的单个文件条目（照片或视频） |
| `RepairTask.cs` | 修复队列任务单元（可含 1 个单文件或 2 个配对文件），属性展平供 XAML x:Bind |
| `SplitTask.cs` | 拆分任务：待拆分的实况照片文件，支持 MVVM 通知和缩略图懒加载 |
| `WorkProgressSnapshot.cs` | 扫描或批量任务进度快照 |
| `BulkObservableCollection.cs` | 自定义批量可观察集合 |

---

## 8. ViewModels 层（视图模型）

### 基类

| 文件 | 说明 |
|------|------|
| `ViewModelBase.cs` | 所有 ViewModel 的抽象基类，继承 `ObservableObject`，提供 `PageStatusTag` 和 `Status` 属性 |
| `WorkViewModelBase.cs` | 工作流页面基类，封装扫描/处理/暂停/取消/进度上报的通用生命周期 |
| `WorkViewModelBase.ScanStateHook.cs` | 分部类：扫描状态变更时的进度条状态和按钮样式更新 |

### 页面 ViewModel

| 文件 | 对应页面 | 说明 |
|------|----------|------|
| `AppViewModel.cs` | MainWindow | 全局应用 VM，管理子 VM 生命周期、底部状态栏进度聚合、页面导航事件转发 |
| `HomeViewModel.cs` | HomePage | 首页/教程页，负责功能入口导航 |
| `MergeViewModel.cs` | MergePage | 合成页：扫描图片视频配对、选择协议、执行合并 |
| `SplitViewModel.cs` | SplitPage | 拆分页：扫描实况照片、拆分为独立图片视频、输出格式选择 |
| `RepairViewModel.cs` | RepairPage | 修复页：扫描实况照片、分析元数据完整性、执行修复 |
| `HistoryViewModel.cs` | HistoryPage | 历史页：扫描文件夹中图片文件、解析 XMP 检测实况照片、展示操作历史列表 |
| `SettingsViewModel.cs` | SettingsPage | 设置页：语言、主题、背景、Banner、合成/拆分/修复参数、硬件编码等 |
| `AboutViewModel.cs` | AboutPage | 关于页：崩溃日志查看、导出、清除、反馈导航 |
| `KeyPhotoViewModel.cs` | KeyPhotoPage | 封面修改页（占位，功能待扩展） |
| `PhotoClassifyViewModel.cs` | PhotoClassifyPage | 照片分类页（占位，计划支持自动扫描分类） |

---

## 9. Views 层（页面）

| 文件 | 说明 |
|------|------|
| `HomePage.xaml/.cs` | **主页/教程页** — 展示欢迎信息和实况照片合成/拆分/修复的图文教程。支持 Banner 轮播、功能卡片悬停预览、滚动到功能按钮、导航参数支持 |
| `MergePage.xaml/.cs` | **合成页** — 将普通图片+视频合成为实况照片。含任务列表自动滚动、文件夹选择、全屏预览、错误详情提示 |
| `SplitPage.xaml/.cs` | **拆分页** — 将实况照片拆分为独立的照片和视频文件。含任务列表自动滚动、文件夹选择、全屏预览、错误详情提示 |
| `RepairPage.xaml/.cs` | **修复页** — 修复损坏/不完整的实况照片。含缩略图懒加载、文件夹浏览、全屏预览、错误详情提示、筛选菜单 |
| `SettingsPage.xaml/.cs` | **设置页(新版)** — 外观/转码/合成/拆分/修复配置及调试工具。支持导航参数滚动到指定区域 |
| `SettingsPageOld.xaml/.cs` | **设置页(旧版)** — 经典布局，功能等价于新版，通过 `AppSettingsService('UseClassicSettingsPage')` 控制 |
| `AboutPage.xaml/.cs` | **关于页** — 应用信息、开发动机、项目信息、开源致谢、隐私条款 |
| `HistoryPage.xaml/.cs` | **历史页** — 文件历史分析，扫描文件夹展示实况照片操作历史 |
| `KeyPhotoPage.xaml/.cs` | **封面修改页(占位)** — 功能开发中 |
| `PhotoClassifyPage.xaml/.cs` | **照片分类页(占位)** — 功能开发中 |

---

## 10. Controls 层（自定义控件）

| 文件 | 说明 |
|------|------|
| `LightboxPreview.xaml/.cs` | **全屏灯箱控件** — 半透明遮罩中预览图片/视频，支持双播放器无缝切换、键盘/鼠标导航、视频进度条和时间显示、图片预加载 |
| `PageStatusBar.xaml/.cs` | **底部状态栏** — 左侧状态文本 + 右侧进度百分比 + 顶部细进度条。全局单例，数据绑定到 AppViewModel |

---

## 11. Services 层（服务层）

### 核心业务服务

| 文件 | 说明 |
|------|------|
| `LivePhotoSplitService.cs` | **核心拆分服务** — 按 JPEG 段结构逐段复制，丢弃含实况特征 XMP 的 APP 段，防止假阳性循环 |
| `LivePhotoMergeService.cs` | **核心合并服务** — 构建协议特定 XMP 元数据写入 APP1 段，底层结构: SOI + APP1(XMP) + 剩余JPEG + 视频 |
| `LivePhotoCompositionService.cs` | **合成兼容层** — 输出文件名生成和实况照片写入，内部委托给 `LivePhotoMergeService` |
| `LivePhotoRepairService.cs` | **修复服务** — 诊断(方向/缩略图/ContentIdentifier) → 修复(jpegtran 无损旋转 + exiftool 重置方向 + FFmpeg 视频重编码) → 标记(XMP 写入操作记录) |
| `LivePhotoMergeScanService.cs` | **合并扫描** — 定义 `LivePhotoFilePairInfo` 和 `LivePhotoScanResult` |
| `LivePhotoSplitScanService.cs` | **拆分扫描** — 定义 `LivePhotoSplitFileInfo` 和 `LivePhotoSplitScanResult` |

### 批量运行器

| 文件 | 说明 |
|------|------|
| `LivePhotoMergeRunnerService.cs` | 合并批量执行器 — 并行批次执行 MergeTask 列表，支持暂停/取消/进度回调，自动清理临时文件 |
| `LivePhotoBatchRunnerService.cs` | 批量测试运行器 — 类似 MergeRunnerService 但更简化，用于开发/测试场景 |

### 工具服务

| 文件 | 说明 |
|------|------|
| `LogService.cs` | **统一日志系统** — 每会话一个 `.log` 文件，ConcurrentQueue + 异步批量刷新，15 日志 + 5 dump 保留策略 |
| `PersistentExifTool.cs` | **常驻 exiftool 封装** — `-stay_open` 模式，进程复用省 200-400ms/次，SemaphoreSlim 线程安全，崩溃自动恢复+重试 |
| `EncoderHelper.cs` | **编码器助手** — 硬件加速编码器检测/选择/参数/线程数，VideoTranscodeService 和 LivePhotoRepairService 共用 |
| `VideoTranscodeService.cs` | **视频转码** — FFmpeg 封装，支持 NVENC/QSV/AMF 硬件加速 |
| `HeicConverterService.cs` | **HEIC 转换** — 支持 Magick.NET(默认) 和 Windows BitmapDecoder 双解码器，ExifTool 拷贝元数据 |
| `ThumbnailService.cs` | **缩略图服务** — 三种来源(Shell API/BitmapDecoder/FFmpeg)，两级缓存 + SemaphoreSlim 并发控制 |
| `ImagePreviewService.cs` | **图片预览** — LRU 内存缓存 + DecodePixelWidth 解码限制 + 相邻预加载 |

### 系统服务

| 文件 | 说明 |
|------|------|
| `AppSettingsService.cs` | 基于 `ApplicationData.LocalSettings` 的轻量键值存储，泛型类型安全读写 |
| `CrashHandler.cs` | 异常处理注册 + WER 本地 dump 注册 + 崩溃对话框 UI |
| `ExternalToolLocator.cs` | 定位 exiftool / jpegtran / ffmpeg（仅 3 个工具），线程安全 `Lazy<T>` 缓存 |
| `FeedbackService.cs` | 导航到 GitHub Issues 页面 |
| `FilePickerService.cs` | 封装 WinRT 文件/文件夹选择器和 Windows Explorer 操作 |
| `HardwareService.cs` | 检测 CPU/GPU 等硬件信息 (依赖 System.Management) |
| `LanguageService.cs` | UI 语言索引映射、语言覆盖切换、重启提示 |
| `PathHelper.cs` | 文件路径工具 (配对键生成、唯一路径、原子路径预留) |
| `ResourceService.cs` | ResourceLoader 封装，多语言字符串获取和格式化 |

---

## 12. Protocols 层（实况照片协议）

```
LivePhotoProtocol (抽象基类)
├── MicroVideoV1Protocol  (Id=0) — Google 已弃用格式
│     MP4 视频直接附加在 JPEG 图片末尾
│     XMP 中通过 GCamera:MicroVideoOffset 记录视频字节偏移
│     使用者: 旧版小米 (MIUI)、旧版 Google Pixel
│
├── MotionPhotoV2Protocol (Id=1) — 现代跨平台标准
│     使用 Container:Directory XMP 结构 + Item:Semantic="MotionPhoto"
│     使用者: Google Pixel、Samsung Galaxy、Xiaomi HyperOS 3+
│
└── OppoLivePhotoProtocol (Id=2) — OPPO/OnePlus 专有
      扩展 Motion Photo V2，增加 OpCamera 命名空间 + EXIF UserComment 标记
      二进制布局: [JPEG + GainMap] + [MP4 视频] + [可选 OnePlus 尾部]
      使用者: OPPO ColorOS、OnePlus OxygenOS
```

---

## 13. Converters 层（值转换器）

| 文件 | 转换逻辑 |
|------|----------|
| `BackdropToAcrylicVisibilityConverter.cs` | BackdropIndex → Visibility (仅 Acrylic=2 时 Visible) |
| `BoolToDiagnosisErrorBrushConverter.cs` | bool (诊断错误) → 红色/正常色画刷 |
| `CommonConverters.cs` | 包含 BoolToVisibilityConverter (true→Visible) 和 InverseBoolToVisibilityConverter (true→Collapsed) |
| `DoubleToPercentConverter.cs` | 0.0~1.0 → "50%" 格式化字符串 |
| `ProgressBarForegroundConverter.cs` | ProgressBarState → 前景画刷颜色 |
| `ProgressBarIndeterminateConverter.cs` | ProgressBarState → 是否不明确模式 (仅 Scanning 为 true) |
| `StatusToColorConverter.cs` | ProcessStatus → 状态颜色画刷 |

---

## 14. Helpers 层（工具类）

| 文件 | 说明 |
|------|------|
| `ComboBoxHelper.cs` | WinUI 3 ComboBox 自适应宽度：测量最宽选项文本并调整宽度 |
| `FileNameFormatter.cs` | 文件名截断：在列宽范围内显示，保留扩展名 |
| `FileSizeFormatter.cs` | 文件大小格式化：字节 → KB/MB 人类可读 |
| `TaskListAutoScroller.cs` | 任务列表自动滚动控制器：扫描/处理时自动跟随 (120ms 防抖)，用户上滚时暂停 (2s 后恢复) |
| `TaskListScrollHelper.cs` | 任务列表自动滚动辅助，封装 MergePage/SplitPage/RepairPage 的通用滚动逻辑 |
| `VisualTreeHelperExtensions.cs` | WinUI 3/UWP 可视化树扩展方法，按类型查找后代元素 |

---

## 15. 资源与本地化

| 文件/目录 | 说明 |
|-----------|------|
| `Strings/zh-Hans/Resources.resw` | 中文(简体) UI 字符串资源 |
| `Strings/en-US/Resources.resw` | 英文 UI 字符串资源 |
| `Assets/Icons/` | 应用图标 (多分辨率) |
| `Assets/Banners/` | 首页 Banner 图片预设 |

### 图片引用规则
- Assets 图片使用 `ms-appx:///Assets/...` URI 引用
- 修改 Assets 后必须重新编译（`CopyToOutputDirectory=PreserveNewest` 只在编译时复制）
- MSIX 打包版需重新生成并部署才能更新资产文件

### 语言支持
| 语言 | 状态 |
|------|:----:|
| 中文（简体） | ✅ 完整 |
| English | ✅ 完整 |
- 支持系统语言自动跟随，也可在设置中手动切换
- 语言变更通过 `PrimaryLanguageOverride` 持久化，切换后提示重启

---

## 16. 打包与部署

| 发布渠道 | 说明 |
|----------|------|
| **GitHub Releases** | 每次 `git push --tags` 自动触发 GitHub Actions 构建 + 打包 + 创建 Release |
| **Microsoft Store** | 即将上线 |

### 发布产物

| 文件 | 说明 |
|------|------|
| `Live-Photo-Box-v{version}-x64-portable.zip` | 便携版，解压即用，不写注册表 |
| `Live-Photo-Box-v{version}-x64-setup.exe` | 安装版，Inno Setup 打包，开始菜单快捷方式 |

### 自动发布流程

```bash
git add .
git commit -m "v1.14.11"
git tag v1.14.11
git push --tags
# → GitHub Actions 自动编译、打包、创建 Release（草稿）
# → 去 Releases 页面检查 → 点 Publish 发布
```

详细流程见 [`docs/发布流程.md`](docs/发布流程.md)。

---

## 17. 架构总览图

```mermaid
graph TB
    subgraph MainWindow["MainWindow"]
        NavView["NavigationView"]
        ContentFrame["Content Frame"]
        StatusBar["PageStatusBar（底部状态栏）"]

        subgraph Page["Page (XAML View)"]
            Home["HomePage - 主页"]
            Merge["MergePage - 合成"]
            Split["SplitPage - 拆分"]
            Repair["RepairPage - 修复"]
            History["HistoryPage - 历史"]
            Settings["SettingsPage - 设置"]
            About["AboutPage - 关于"]
        end

        subgraph VM["ViewModel 层"]
            AppVM["AppViewModel（全局单例）"]
            HomeVM["HomeViewModel"]
            MergeVM["MergeViewModel"]
            SplitVM["SplitViewModel"]
            RepairVM["RepairViewModel"]
            HistoryVM["HistoryViewModel"]
            SettingsVM["SettingsViewModel"]
            AboutVM["AboutViewModel"]
        end

        subgraph Service["Service 层"]
            MergeSvc["LivePhotoMergeService"]
            SplitSvc["LivePhotoSplitService"]
            RepairSvc["LivePhotoRepairService"]
            TranscodeSvc["VideoTranscodeService"]
            ExifToolSvc["PersistentExifTool"]
            LogSvc["LogService"]
            ThumbSvc["ThumbnailService"]
        end

        subgraph External["外部工具"]
            ExifTool["exiftool（元数据）"]
            FFmpeg["FFmpeg（视频转码）"]
            JpegTran["jpegtran（无损旋转）"]
        end

        subgraph Protocols["实况照片协议"]
            MV1["MicroVideo V1"]
            MP2["MotionPhoto V2"]
            OPPO["O-Live Photo (OPPO)"]
        end
    end

    %% 导航关系
    NavView --> ContentFrame
    ContentFrame --> Home & Merge & Split & Repair & History & Settings & About

    %% MVVM 双向绑定
    Home <-- 双向绑定 --> HomeVM
    Merge <-- 双向绑定 --> MergeVM
    Split <-- 双向绑定 --> SplitVM
    Repair <-- 双向绑定 --> RepairVM
    History <-- 双向绑定 --> HistoryVM
    Settings <-- 双向绑定 --> SettingsVM
    About <-- 双向绑定 --> AboutVM

    %% AppVM 全局状态
    AppVM -.-> HomeVM & MergeVM & SplitVM & RepairVM & HistoryVM & SettingsVM & AboutVM
    AppVM --- StatusBar

    %% ViewModel 调用 Service
    MergeVM --> MergeSvc
    SplitVM --> SplitSvc
    RepairVM --> RepairSvc & TranscodeSvc

    %% Service 依赖外部工具
    MergeSvc --> ExifToolSvc
    SplitSvc --> ExifToolSvc
    RepairSvc --> ExifToolSvc & TranscodeSvc
    TranscodeSvc --> FFmpeg
    ExifToolSvc --> ExifTool
    RepairSvc --> JpegTran

    %% 合并服务调用协议
    MergeSvc --> MV1 & MP2 & OPPO

    %% 样式
    classDef view fill:#e3f2fd,stroke:#1565c0
    classDef vm fill:#f3e5f5,stroke:#7b1fa2
    classDef svc fill:#e8f5e9,stroke:#2e7d32
    classDef tool fill:#fff3e0,stroke:#e65100
    classDef proto fill:#fce4ec,stroke:#c62828
    classDef app fill:#f5f5f5,stroke:#616161

    class Home,Merge,Split,Repair,History,Settings,About view
    class HomeVM,MergeVM,SplitVM,RepairVM,HistoryVM,SettingsVM,AboutVM,AppVM vm
    class MergeSvc,SplitSvc,RepairSvc,TranscodeSvc,ExifToolSvc,LogSvc,ThumbSvc svc
    class ExifTool,FFmpeg,JpegTran tool
    class MV1,MP2,OPPO proto
    class MainWindow,NavView,ContentFrame,StatusBar,Page,VM,Service,External,Protocols app
```

### MVVM 数据流

```mermaid
graph LR
    View["XAML View（页面）"] -->|"x:Bind / Binding（双向数据绑定）"| VM["ViewModel（业务逻辑）"]
    VM -->|"命令/方法调用"| Service["Service（核心服务）"]
    Service --> External["外部工具 / 协议"]
    VM -.->|"属性变更通知"| View
```

### ViewModel 继承层次

```mermaid
graph TB
    ObservableObject["ObservableObject\n(CommunityToolkit.Mvvm)"]
    ViewModelBase["ViewModelBase（抽象）"]
    AppVM["AppViewModel（全局单例）"]
    HomeVM["HomeViewModel"]
    SettingsVM["SettingsViewModel"]
    AboutVM["AboutViewModel"]
    KeyPhotoVM["KeyPhotoViewModel（占位）"]
    ClassifyVM["PhotoClassifyViewModel（占位）"]
    WorkBase["WorkViewModelBase（抽象）"]
    MergeVM["MergeViewModel"]
    SplitVM["SplitViewModel"]
    RepairVM["RepairViewModel"]
    HistoryVM["HistoryViewModel"]

    ObservableObject --> ViewModelBase
    ViewModelBase --> HomeVM & SettingsVM & AboutVM & KeyPhotoVM & ClassifyVM & WorkBase
    WorkBase --> MergeVM & SplitVM & RepairVM & HistoryVM
    ObservableObject -.-> AppVM
```

### UI 容器嵌套结构

```mermaid
flowchart TB
    classDef mainWin fill:#f5f5f5,stroke:#333,stroke-width:3px
    classDef navView fill:#e8e8e8,stroke:#666,stroke-width:2px
    classDef navMenu fill:#d0d8e8,stroke:#334,stroke-width:1px
    classDef content fill:#e3f2fd,stroke:#1565c0,stroke-width:2px
    classDef stack fill:#fff,stroke:#999,stroke-width:1px
    classDef status fill:#e8e8e8,stroke:#666,stroke-width:2px

    subgraph MainWindow["MainWindow"]
        subgraph NavView["NavigationView"]
            direction LR
            NavMenu["Nav Menu\n🏠 Home\n🔗 Merge\n✂️ Split\n🛠️ Repair\n📋 History\n⚙️ Settings\nℹ️ About"]

            subgraph Content["Content Frame"]
                direction TB
                Page["📄 Page (XAML View)"]
                DB["↕ x:Bind / Binding"]
                VM["🧠 ViewModel"]
                SVC["↕ Service Layer"]
                TOOL["🔧 Protocols / ExifTool\n🎬 FFmpeg / jpegtran"]
            end
        end

        StatusBar["PageStatusBar（底部状态栏）"]
    end

    AppVM["AppViewModel（全局单例）"] --- StatusBar

    class MainWindow mainWin
    class NavView navView
    class NavMenu navMenu
    class Content content
    class Page,DB,VM,SVC,TOOL stack
    class StatusBar status
    class AppVM navMenu
```

## 18. 构建配置要点

1. **自包含发布**: 所有平台均为 `SelfContained=true`，无需用户安装 .NET 运行时
2. **WinAppSDK 自包含**: `WindowsAppSDKSelfContained=true`，无需单独安装 WinAppSDK 运行时
3. **语言裁剪**: 构建后自动删除非 zh-Hans/en-US 的附属资源文件夹
4. **平台**: 独立构建 x86/x64/ARM64，不做 Bundle
5. **ReadyToRun**: 所有配置均为 `false`（禁用预编译）
6. **剪裁**: `PublishTrimmed=false`（不剪裁）
7. **条件签名**: 仅当 `.pfx` 证书文件存在时才启用签名

---

## 19. 项目统计

| 指标 | 数量 |
|------|------|
| C# 源文件 (.cs) | ~65 |
| XAML 页面/控件 (.xaml) | 12 |
| Models | 13 |
| ViewModels | 11 |
| Views (页面) | 10 |
| Controls (自定义控件) | 2 |
| Services | 24 |
| Protocols | 4 (1 抽象 + 3 实现) |
| Converters | 7 |
| Helpers | 6 |
| RESW 资源文件 | 2 (zh-Hans + en-US) |
| 外部工具 | 3 (exiftool/jpegtran/ffmpeg) |
| 目标平台 | x86-x64 |
| 支持的实况协议 | 3 (MicroVideo V1 / MotionPhoto V2 / OPPO O-Live) |

---

> 最后更新: 2026-06-28

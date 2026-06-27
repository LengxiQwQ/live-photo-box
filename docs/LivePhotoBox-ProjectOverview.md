# Live Photo Box — 项目总览

> **生成日期**: 2026-06-27  
> **项目版本**: 1.14.10.0  
> **作者**: LengxiQwQ  
> **仓库**: https://github.com/lengxiqwq/live-photo-box  
> **许可**: 开源 (LICENSE)

---

## 1. 项目概述

**Live Photo Box（实况照片工具箱）**是一款专为 Windows 打造的现代化 Apple 实况照片 (Live Photos) 管理与修复桌面应用。基于 **WinUI 3 (Windows App SDK 1.8)** 构建，拥有契合 Windows 11 Fluent Design 的现代化界面。

### 核心功能

| 功能 | 说明 |
|------|------|
| **📸 实况分离 (Split)** | 将实况照片一键拆分为独立的静态图片 (JPG/HEIC) 和动态视频 (MOV/MP4)，智能剥离谷歌 XMP 元数据防止假阳性循环 |
| **🔗 实况合成 (Combo)** | 将图片+视频组合为标准实况照片格式，自动写入 EXIF + QuickTime 元数据 |
| **🖼️ 封面修改 (Key Photo)** | 自由更换实况照片封面图，支持从视频提取帧或自定义图片 |
| **🛠️ 元数据修复 (Repair)** | 深度修复 UUID 匹配问题，解决跨平台传输后无法识别的痛点 |
| **⚡ 批量引擎** | 多线程批量处理，支持拖拽导入 |

---

## 2. 技术栈详情

### 语言与框架

| 层级 | 技术 | 版本 |
|------|------|------|
| 语言 | C# | 13.0 (C# 13) |
| 运行时 | .NET | 9.0 |
| UI 框架 | WinUI 3 / Windows App SDK | 1.8.260317003 |
| 最低系统 | Windows 10 (1809+) | Target 10.0.19041.0 |
| 架构模式 | MVVM | CommunityToolkit.Mvvm 8.4.2 |

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
| **exiftool** (13.x) | EXIF/XMP/QuickTime 元数据读写（含视频元数据探测，无需 ffprobe） |
| **jpegtran** | JPEG 无损旋转（修复时用） |
| **ffmpeg** (可选) | 视频转码、缩略图提取 |

---

## 3. 项目目录树

```
live-photo-box/
├── .github/
│   ├── copilot-instructions.md        # GitHub Copilot AI 指令
│   └── workflows/                     # CI/CD 工作流 (GitHub Actions)
│
├── .vscode/
│   ├── launch.json                    # VS Code 调试配置
│   └── tasks.json                     # VS Code 构建任务
│
├── docs/                              # 📁 项目文档目录
│   ├── LivePhotoBox-历史记录标识规范.md   # XMP 历史标记规范
│   ├── LivePhotoBox-ProjectOverview.md      # 👈 本文件
│   ├── 小米实况照片协议分析报告.md        # 小米协议逆向分析
│   ├── 小米实况照片协议开发记录.md        # 小米协议开发全过程
│   └── 苹果的矩阵幻象.md                # Display Matrix 镜头判定
│
├── LivePhotoBox/                      # 📦 主项目 (WinUI 3 MSIX 应用)
│   ├── LivePhotoBox.csproj            # 项目文件
│   ├── LivePhotoBox.sln               # 解决方案 (在上级目录)
│   ├── CLAUDE.md                      # AI 助手上下文文件
│   ├── Package.appxmanifest           # MSIX 包清单
│   ├── App.xaml / App.xaml.cs         # 应用入口
│   ├── MainWindow.xaml / MainWindow.xaml.cs  # 主窗口
│   │
│   ├── Assets/                        # 静态资源
│   │   ├── Icons/                     # 应用图标 (多分辨率)
│   │   └── Tutorials/                 # 教程截图 (combo/split/repair)
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
│   │   ├── ComboBoxHelper.cs          # ComboBox 自适应宽度
│   │   ├── FileNameFormatter.cs       # 文件名截断
│   │   ├── FileSizeFormatter.cs       # 文件大小格式化
│   │   ├── ImageHoverService.cs       # 悬停大图预览
│   │   ├── TaskListAutoScroller.cs    # 任务列表自动滚动
│   │   ├── TaskListScrollHelper.cs    # 滚动逻辑封装
│   │   └── VisualTreeHelperExtensions.cs  # 可视化树扩展
│   │
│   ├── Models/                        # 数据模型
│   │   ├── AppLogEntry.cs             # 日志条目
│   │   ├── BannerPreset.cs            # 首页 Banner
│   │   ├── FileHistoryInfo.cs         # 文件操作历史
│   │   ├── LivePhotoConstants.cs      # 实况照片常量
│   │   ├── LivePhotoSplitResult.cs    # 拆分结果
│   │   ├── MergeTask.cs               # 合并任务
│   │   ├── ProcessStatus.cs           # 处理状态枚举
│   │   ├── ProgressBarState.cs        # 进度条状态枚举
│   │   ├── RepairAnalysisResult.cs    # 修复诊断结果
│   │   ├── RepairFileEntry.cs         # 修复文件条目
│   │   ├── RepairTask.cs              # 修复任务
│   │   ├── SplitTask.cs               # 拆分任务
│   │   └── WorkProgressSnapshot.cs    # 进度快照
│   │
│   ├── Services/                      # 服务层 (核心业务逻辑)
│   │   ├── AppSettingsService.cs      # 应用设置持久化
│   │   ├── CrashHandler.cs            # 崩溃处理与上报
│   │   ├── EncoderHelper.cs           # 硬件编码器检测与选择
│   │   ├── ExternalToolLocator.cs     # 外部工具路径定位
│   │   ├── FeedbackService.cs         # GitHub Issues 反馈跳转
│   │   ├── FilePickerService.cs       # 文件/文件夹选择器
│   │   ├── HardwareService.cs         # CPU/GPU 硬件信息检测
│   │   ├── HeicConverterService.cs    # HEIC→JPEG 转码
│   │   ├── ImagePreviewService.cs     # 图片预览 (LRU 缓存)
│   │   ├── LanguageService.cs         # 多语言切换管理
│   │   ├── LivePhotoBatchRunnerService.cs    # 批量测试运行器
│   │   ├── LivePhotoCompositionService.cs    # 实况照片合成(兼容层)
│   │   ├── LivePhotoMergeRunnerService.cs    # 合并批量执行器
│   │   ├── LivePhotoMergeScanService.cs      # 合并扫描服务
│   │   ├── LivePhotoMergeService.cs          # 核心合并(合成)服务
│   │   ├── LivePhotoMetadataMatcher.cs       # 元数据配对匹配器
│   │   ├── LivePhotoRepairService.cs         # 修复服务
│   │   ├── LivePhotoSplitScanService.cs      # 拆分扫描服务
│   │   ├── LivePhotoSplitService.cs          # 核心拆分服务
│   │   ├── LogService.cs                     # 统一日志系统
│   │   ├── PathHelper.cs                     # 文件路径工具
│   │   ├── PersistentExifTool.cs             # exiftool 常驻进程封装
│   │   ├── ResourceService.cs                # 多语言字符串获取
│   │   ├── ThumbnailService.cs               # 缩略图加载与缓存
│   │   ├── VideoTranscodeService.cs          # FFmpeg 视频转码
│   │   ├── WindowsAppSdkBootstrap.cs         # 非打包模式启动引导
│   │   │
│   │   └── Protocols/                 # 实况照片协议实现
│   │       ├── LivePhotoProtocol.cs          # 抽象基类
│   │       ├── MicroVideoV1Protocol.cs       # Google MicroVideo V1 (旧)
│   │       ├── MotionPhotoV2Protocol.cs      # Google Motion Photo V2 (新)
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
│   │   ├── ViewModelBase.cs           # 抽象基类
│   │   ├── WorkViewModelBase.cs       # 工作流页面基类
│   │   ├── WorkViewModelBase.ScanStateHook.cs  # 扫描状态钩子
│   │   ├── AboutViewModel.cs          # 关于页
│   │   ├── AppViewModel.cs            # 全局应用 VM
│   │   ├── HistoryViewModel.cs        # 文件历史
│   │   ├── HomeViewModel.cs           # 主页/教程
│   │   ├── KeyPhotoViewModel.cs       # 封面修改(占位)
│   │   ├── MergeViewModel.cs          # 合成页
│   │   ├── PhotoClassifyViewModel.cs  # 照片分类(占位)
│   │   ├── RepairViewModel.cs         # 修复页
│   │   ├── SettingsViewModel.cs       # 设置页
│   │   └── SplitViewModel.cs          # 拆分页
│   │
│   └── Views/                         # XAML 页面
│       ├── AboutPage.xaml/.cs         # 关于页
│       ├── HistoryPage.xaml/.cs       # 历史记录页
│       ├── HomePage.xaml/.cs          # 主页/教程页
│       ├── KeyPhotoPage.xaml/.cs      # 封面修改页(占位)
│       ├── MergePage.xaml/.cs         # 合成页
│       ├── PhotoClassifyPage.xaml/.cs # 照片分类页(占位)
│       ├── RepairPage.xaml/.cs        # 修复页
│       ├── SettingsPage.xaml/.cs      # 设置页(新版)
│       ├── SettingsPageOld.xaml/.cs   # 设置页(旧版)
│       └── SplitPage.xaml/.cs         # 拆分页
│
└── README.md                          # 项目说明 (GitHub 首页)
```

---

## 4. 解决方案与项目文件

### 解决方案 (`LivePhotoBox.sln`)
- Visual Studio 2022 格式
- 单一项目: `LivePhotoBox\LivePhotoBox.csproj`
- 配置: Debug / Release × x86 / x64 / ARM64

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
| 自包含发布 | 启用 |
| Windows App SDK 自包含 | 启用 |
| 默认语言 | zh-Hans |
| 附属资源语言 | zh-Hans, en-US (构建后自动剥离其他语言) |
| 应用包名 | `LengxiQwQ.58702A0DB398F` |
| 应用版本 | 1.14.10.0 |

---

## 5. 核心入口与主窗口

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

## 6. Models 层（数据模型）

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

## 7. ViewModels 层（视图模型）

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

## 8. Views 层（页面）

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

## 9. Controls 层（自定义控件）

| 文件 | 说明 |
|------|------|
| `LightboxPreview.xaml/.cs` | **全屏灯箱控件** — 半透明遮罩中预览图片/视频，支持双播放器无缝切换、键盘/鼠标导航、视频进度条和时间显示、图片预加载 |
| `PageStatusBar.xaml/.cs` | **底部状态栏** — 左侧状态文本 + 右侧进度百分比 + 顶部细进度条。全局单例，数据绑定到 AppViewModel |

---

## 10. Services 层（服务层）

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
| `ImageHoverService.cs` | **悬停预览** — 鼠标悬停列表图片时等比例放大显示在 Canvas 叠加层 |

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

## 11. Protocols 层（实况照片协议）

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

## 12. Converters 层（值转换器）

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

## 13. Helpers 层（工具类）

| 文件 | 说明 |
|------|------|
| `ComboBoxHelper.cs` | WinUI 3 ComboBox 自适应宽度：测量最宽选项文本并调整宽度 |
| `FileNameFormatter.cs` | 文件名截断：在列宽范围内显示，保留扩展名 |
| `FileSizeFormatter.cs` | 文件大小格式化：字节 → KB/MB 人类可读 |
| `ImageHoverService.cs` | 鼠标悬停大图预览服务 |
| `TaskListAutoScroller.cs` | 任务列表自动滚动控制器：扫描/处理时自动跟随 (120ms 防抖)，用户上滚时暂停 (2s 后恢复) |
| `TaskListScrollHelper.cs` | 任务列表自动滚动辅助，封装 MergePage/SplitPage/RepairPage 的通用滚动逻辑 |
| `VisualTreeHelperExtensions.cs` | WinUI 3/UWP 可视化树扩展方法，按类型查找后代元素 |

---

## 14. 资源与本地化

| 文件/目录 | 说明 |
|-----------|------|
| `Strings/zh-Hans/Resources.resw` | 中文(简体) UI 字符串资源 |
| `Strings/en-US/Resources.resw` | 英文 UI 字符串资源 |
| `Assets/Icons/` | 应用图标 (多分辨率: 16~400 scale, 目标尺寸, 不同底板) |
| `Assets/Tutorials/` | 教程截图 (combo-01~04, split-01~03, repair-01~03) |
| `Assets/AppIcon.ico` | 应用 ICO 图标 |

### 图片引用规则
- Assets 图片使用 `ms-appx:///Assets/...` URI 引用
- 修改 Assets 后必须重新编译（`CopyToOutputDirectory=PreserveNewest` 只在编译时复制）
- MSIX 打包版需重新生成并部署才能更新资产文件

### 语言支持
- 支持系统跟随模式（自动检测 Windows 显示语言）
- 支持手动切换：中文(简体) / English
- 语言变更通过 `PrimaryLanguageOverride` 持久化，切换后提示重启

---

## 15. 打包与部署

| 项 | 配置 |
|-----|------|
| 包类型 | MSIX |
| 包标识名 | `LengxiQwQ.58702A0DB398F` |
| 包版本 | 1.14.10.0 |
| 包族名 | `LengxiQwQ.58702A0DB398F_...` |
| 最低系统 | Windows 10 1809+ |
| 能力 | `runFullTrust` |
| 资源语言 | en-US, zh-Hans |
| 证书 | 临时证书自动生成 (`.pfx`) |
| 签名算法 | SHA256 |
| 更新检查间隔 | 0 (禁用自动检查) |
| Bundle 平台 | 按平台独立生成 (Never) |
| 安装程序 URI | https://github.com/LengxiQwQ/live-photo-box |

### 启动配置 (`Properties/launchSettings.json`)
- **LivePhotoBox (Package)** — MSIX 打包部署模式
- **LivePhotoBox (Unpackaged)** — 非打包直接运行模式 (通过 `WindowsAppSdkBootstrap.cs` 自动引导 WinAppSDK)

### VS Code 配置 (`.vscode/`)
- **launch.json**: 两个配置 — 启动 (控制台) 和 附加到进程
- **tasks.json**: 三个任务 — build、publish、watch (dotnet watch run 热重载)

---

## 16. 文档（`docs/` 目录）

| 文件 | 说明 |
|------|------|
| `LivePhotoBox-ProjectOverview.md` | 👈 **本文件** — 项目完整总结 |
| `LivePhotoBox-历史记录标识规范.md` | XMP 元数据追踪标记规范：Combo/Split/Repair 三种操作如何注入 XMP 标记 |
| `小米实况照片协议分析报告.md` | 小米实况照片协议逆向分析：HyperOS 3+ 已切换到 MotionPhoto V2；旧版 12S Ultra 使用 MicroVideo V1 |
| `小米实况照片协议开发记录.md` | 新增小米协议的全过程开发记录，含失败尝试和根因分析 |
| `苹果的矩阵幻象.md` | 通过解析视频 Display Matrix 行列式判断 iPhone 前置/后置摄像头（det<0→前置，det>0→后置） |

---

## 17. 架构总览图

```
┌─────────────────────────────────────────────────────────┐
│                     MainWindow                           │
│  ┌─────────────────────────────────────────────────┐   │
│  │              NavigationView                      │   │
│  │  ┌──────────┐  ┌────────────────────────────┐   │   │
│  │  │   Nav    │  │       Content Frame          │   │   │
│  │  │  Menu    │  │  ┌────────────────────────┐  │   │   │
│  │  │          │  │  │    Page (XAML View)     │  │   │   │
│  │  │ • Home   │  │  │    ↕ Data Binding       │  │   │   │
│  │  │ • Merge  │  │  │    ViewModel             │  │   │   │
│  │  │ • Split  │  │  │    ↕                     │  │   │   │
│  │  │ • Repair │  │  │    Service Layer          │  │   │   │
│  │  │ • History│  │  │    ↕                     │  │   │   │
│  │  │ • Settings│ │  │    Protocols / ExifTool   │  │   │   │
│  │  │ • About  │  │  │    ↕                     │  │   │   │
│  │  └──────────┘  │  │    FFmpeg / jpegtran     │  │   │   │
│  │                 │  └────────────────────────┘  │   │   │
│  │                 └────────────────────────────┘  │   │   │
│  └─────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────┐   │
│  │              PageStatusBar (底部)                │   │
│  └─────────────────────────────────────────────────┘   │
│         ↕ AppViewModel (全局单例)                       │
└─────────────────────────────────────────────────────────┘
```

### MVVM 数据流

```
XAML View  ←── x:Bind / Binding ──→  ViewModel  ←── 调用 ──→  Service
  (页面)        (双向数据绑定)         (业务逻辑)    (命令/方法)   (核心服务)
```

### ViewModel 继承层次

```
ObservableObject (CommunityToolkit.Mvvm)
├── ViewModelBase (抽象)
│   ├── HomeViewModel
│   ├── SettingsViewModel
│   ├── AboutViewModel
│   ├── KeyPhotoViewModel (占位)
│   ├── PhotoClassifyViewModel (占位)
│   └── WorkViewModelBase (抽象 — 工作流页面)
│       ├── MergeViewModel
│       ├── SplitViewModel
│       ├── RepairViewModel
│       └── HistoryViewModel
└── AppViewModel (全局单例)
```

---

## 18. 构建配置要点

1. **自包含发布**: 所有平台均为 `SelfContained=true`，无需用户安装 .NET 运行时
2. **WinAppSDK 自包含**: `WindowsAppSDKSelfContained=true`，无需单独安装 WinAppSDK 运行时
3. **语言裁剪**: 构建后 PowerShell 脚本自动删除非 zh-Hans/en-US 的附属资源文件夹
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
| Helpers | 7 |
| RESW 资源文件 | 2 (zh-Hans + en-US) |
| 外部工具 | 3 (exiftool/jpegtran/ffmpeg) |
| 目标平台 | 3 (x86/x64/ARM64) |
| 支持的实况协议 | 3 (MicroVideo V1 / MotionPhoto V2 / OPPO O-Live) |

---

> 📝 本文档基于项目文件头部注释、CLAUDE.md、.csproj 以及各源代码文件自动汇总生成。  
> 最后更新: 2026-06-27

# Live Photo Box 版本历史

> 当前版本：**v2.0.2**
> 项目开始：2026-03-27
> 技术栈：WinUI 3 + .NET 9 + C# 13

---

## v2.0.x — LivePhotoEdit 页面与工具链重构

> **v2.0.2** (2026-07-23) **[新增]** 多格式导出（JPEG/PNG/WebP）、视频/GIF 导出；导出进度焕新 — 每步操作有清晰提示、完成后绿勾常驻、一键打开输出文件夹；LIVE 徽标颜色固定为蓝色；**[优化]** 界面视觉统一刷新、导出所有帧不再弹窗；**[修复]** Alt+Tab 窗口名称异常、拖拽提示重叠、导出菜单灰显逻辑
>
> **v2.0.1** (2026-07-21) **[性能]** 编辑页面性能大幅提升 — 点击文件即时响应、快速切换不再闪退、HEIC 缩略图加载提速、时间线显存骤降；刷新/清空按钮自动切换
>
> **v2.0.0** (2026-07-18) **[重大发布]** 新增实况照片编辑页面 — 时间线、导出帧、封面替换、属性查看；全部页面支持文件夹拖拽；时间线模式切换；日志查看增强；修复安装包工具缺失及语言切换无效

## v1.15.x — 自动更新与配对增强

> **v1.15.3** (2026-07-03) **[修复]** 修复页空队列提示不消失、设置页跳转定位、启动动画移除、空队列引导优化
>
> **v1.15.2** (2026-07-03) **[新增]** 修复选项对话框、灯箱全面升级、安装包体积骤降80%、修复页"已跳过"细分、队列回到顶部按钮
>
> **v1.15.1** (2026-07-02) **[修复]** 非打包模式大量 bug 修复、自动更新体验优化、启动时间从4-5秒缩短至1-2秒
>
> **v1.15.0** (2026-07-01) **[发布]** 新增自动更新、元数据组合匹配、仅修复 Apple 照片筛选、外部工具检测器

## v1.14.x — 发布修复

此版本为发布候选，遇到了 MSIX 打包故障、外部工具调用异常、安装包过大、设置背景控件不显示、Unpackaged 无法运行等问题，经 10 轮迭代修复后正式发布。

> **v1.14.10** (2026-06-29) **[发布]** 正式发布版 — 所有问题修复完毕，版本定稿
>
> v1.14.9 (2026-06-28) **[修复]** MSIX 打包问题修复、Release Notes 定稿、构建工具更新
>
> v1.14.8 (2026-06-28) **[工具]** CI/CD 自动构建发版 — GitHub Actions + Inno Setup 安装脚本
>
> v1.14.7 (2026-06-28) **[文件]** 中英双语 README 、GitHub SVG 徽章、项目截屏
>
> v1.14.6 (2026-06-27) **[新增]** Unpackaged 模式支持 — 引导脚本 + sideload 旁加载打包
>
> v1.14.5 (2026-06-27) **[稳定]** CrashHandler + ViewModels 更新、应用稳定性加固
>
> v1.14.4 (2026-06-27) **[优化]** **安装包大幅减少** — ffmpeg full→essentials（308→156 MB）、移除 jhead
>
> v1.14.3 (2026-06-27) **[修复]** ExternalToolLocator 重写、工具探测路径修复
>
> v1.14.2 (2026-06-27) **[修复]** 设置页背景切换控件 Acrylic 主题显示 BUG 修复
>
> v1.14.1 (2026-06-26) **[修复]** 外部工具调用路径修复、LivePhotoSplitService 路径统一
>
> v1.14.0 (2026-06-26) **[发布]** 发布候选：修复样本、拆分/合并服务最终定型

## v1.13.x — 功能完成

v1.13 为功能完成里程碑，三大实况照片协议、智能配对引擎、Lightbox 灯箱、Acrylic 设置页全部就位。

> **v1.13.5** (2026-06-26) **[重构]** Combo→Merge 全库重命名（52 文件）+ 统一转换器 + 开发文档体系
>
> v1.13.4 (2026-06-25) **[新增]** LightboxPreview 灯箱控件（全屏图片查看）、预览服务集成
>
> **v1.13.3** (2026-06-24) **[新增]** 智能元数据配对引擎 — LivePhotoMetadataMatcher（415 行）
>
> v1.13.2 (2026-06-24) **[UI]** 现代设置页 UI（Backdrop/AcrylicVisibility 转换器）、App.xaml 主题统一
>
> v1.13.1 (2026-06-23) **[新增]** ImagePreviewService 大图预览 + Acrylic 亚克力设置页
>
> **v1.13.0** (2026-06-23) **[重构]** **三大实况照片协议** — MicroVideo V1 / MotionPhoto V2 / OPPO Live Photo 识别与解析（+694 行）

## v1.12.x — 服务重构与工具优化

> v1.12.7 (2026-06-22) **[优化]** RepairAnalysisResult 深度修复分析、LivePhotoConstants 常量体系、缩略图/文件名全面优化
>
> **v1.12.6** (2026-06-21) **[新增]** EncoderHelper 编码器（270 行统一 ffmpeg 参数）+ LivePhotoBatchRunnerService 批处理引擎
>
> v1.12.5 (2026-06-20) **[重构]** HEIC 解码器重写、Combo 队列优化、TaskListScrollHelper 滚动辅助、工具类模块化
>
> **v1.12.4** (2026-06-20) **[性能]** ExifTool 常驻模式 — PersistentExifTool 守护进程，批量修复性能飞跃
>
> v1.12.3 (2026-06-19) **[新增]** BannerPreset 横幅预设模型、全页面进度条状态统一集成
>
> v1.12.2 (2026-06-18) **[重构]** 日志系统翻新 — CrashHandler + 日志三件套 + ComboBoxHelper + SessionStateManager
>
> v1.12.1 (2026-06-17) **[UI]** 硬件加速设置页 UI（QSV/NVENC/AMF 开关）、字符串全量更新
>
> v1.12.0 (2026-06-15) **[重构]** **服务层全面重构** — AppLogService/CrashLogWriter/CrashLogService 拆分（+1299 行）

## v1.11.x — 核心服务

> **v1.11.1** (2026-06-13) **[性能]** 硬件加速检测 — HardwareService（Intel QSV / NVIDIA NVENC / AMD AMF）
>
> **v1.11.0** (2026-06-12) **[新增]** 为拆分服务增加 FFmpeg 视频转码引擎 — VideoTranscodeService（486 行）

## v1.10.x — HEIC 转码

> v1.10.1 (2026-06-11) **[文档]** 合并/拆分教程图片、悬浮预览、示例素材入库
>
> **v1.10.0** (2026-06-10) **[新增]** HEIC 转码服务 — HeicConverterService HEIC→JPEG 解码

## v1.9.x — 模型重构

> v1.9.2 (2026-06-08) **[UI]** ViewModel 本地化字符串全面梳理、语言/布局跨页面适配
>
> v1.9.1 (2026-06-07) **[新增]** LivePhotoBatchRunnerService 批处理雏形、MergeTask 批量处理
>
> v1.9.0 (2026-06-05) **[重构]** **模型层重构** — RepairAnalysisResult、MergeTask/SplitTask 模型统一、AppLogEntry 体系

## v1.8.x — 状态栏控件

> v1.8.1 (2026-05-25) **[优化]** BulkObservableCollection 扫描性能优化、ScanDirectoryButton 废弃移除
>
> **v1.8.0** (2026-05-23) **[新增]** PageStatusBar 状态栏控件 + ScanDirectoryButton 目录选择器

## v1.7.x — UI 精细化

> v1.7.2 (2026-05-22) **[新增]** 合并/拆分示例样本文件、合成/修复扫描进度模型
>
> v1.7.1 (2026-05-20) **[UI]** 全页面 XAML 布局/代码隐藏/字符串逐页精细化打磨
>
> v1.7.0 (2026-05-13) **[文档]** 多轮 README 重写（Logo、徽章、截屏）、解决方案重组

## v1.6.x — 修复引擎

> v1.6.1 (2026-04-10) **[UI]** Home 页/About 页布局与字符串终稿、README 品牌重塑
>
> **v1.6.0** (2026-04-10) **[新增]** 修复引擎 — LivePhotoRepairService 完整修复分析逻辑

## v1.5.x — 首个内测版

> **v1.5.0** (2026-04-10) **[发布]** **首个 Microsoft Store 内测版** — exiftool/jpegtran 工具集成、Home 首页定稿

## v1.4.x — 拆分服务

> v1.4.2 (2026-04-09) **[优化]** RepairService 迭代、ComboPage UI 布局翻新、AppViewModel 字符串接入
>
> **v1.4.1** (2026-04-09) **[新增]** LivePhotoRepairTask 修复任务模型、LivePhotoRepairService（+531 行）
>
> **v1.4.0** (2026-04-08) **[新增]** 拆分服务 — LivePhotoSplitService（201 行）完整实现、SplitPage 布局重构

## v1.3.x — 结构标准化

> v1.3.1 (2026-04-07) **[UI]** ConsolePage → **KeyPhotoPage（封面更换）**、导航栏更新
>
> **v1.3.0** (2026-04-07) **[重构]** 结构标准化 — 多语言文件夹 en-US→en / zh-CN→zh-Hans

## v1.2.x — 架构重构

> v1.2.5 (2026-04-06) **[UI]** 应用图标全套（所有尺寸+缩放）、3D 图标、About 图标更新
>
> v1.2.4 (2026-04-05) **[新增]** 崩溃日志 CrashLogService、版本检测 About 页、FeedbackService 反馈
>
> v1.2.3 (2026-04-05) **[新增]** LivePhotoSplitTask 拆分模型、BulkObservableCollection 集合性能优化、扫描+缩略图预览
>
> **v1.2.2** (2026-04-03) **[重构]** **项目重命名** — LivePhotoStudio → **LivePhotoBox**，45 文件全库重命名
>
> v1.2.1 (2026-04-03) **[重构]** 架构深化：FilePicker/Thumbnail/Scan 服务全面接入，旧 SharedViewModel 移除
>
> **v1.2.0** (2026-04-03) **[重构]** **重大架构重构** — 引入 Services 层（AppSettings/Language/Composition）+ AppViewModel MVVM

## v1.1.x — 多语言与设置

> v1.1.1 (2026-03-30) **[新增]** 完整设置页全部实现、SharedViewModel 配置管理、设置持久化
>
> **v1.1.0** (2026-03-29) **[新增]** 多语言资源文件，替换以前的硬编码，使用 LanguageService 代码架构

## v1.0.x — 首个完整版

> **v1.0.0** (2026-03-29) **[发布]** **首个完整版** — 合成+拆分+修复三功能首次完整可用，MainWindow 后台逻辑完备

## v0.x — 初始构建

> v0.4.0 (2026-03-29) **[新增]** Combo/Repair/Split 三页面全部打通，合成任务模型重构
>
> v0.3.0 (2026-03-28) **[新增]** ComboPage 合成页核心逻辑、SharedViewModel 406 行、LivePhotoTask 任务模型
>
> v0.2.0 (2026-03-27) **[新增]** MainWindow 布局、About 页、Home 首页、Console 调试日志页、GitHub CI 工作流
>
> **v0.1.0** (2026-03-27) **[新建]** WinUI 3 + .NET 9 项目脚手架搭建，Split/Combo/Repair 三页面骨架，30 个初始文件

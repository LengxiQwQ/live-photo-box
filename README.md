<div align="center">

<h1>
  <img src="Live Photo Box/Assets/Icons/AppIcon-full.png" alt="icon" width="120" align="middle" hspace="16" />
  Live Photo Box
</h1>

<p><em>实况照片工具箱 — 专为 Windows 打造的 Apple 实况照片管理与修复工具</em></p>

</div>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=for-the-badge&color=0078D7" alt="Release"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/actions"><img src="https://img.shields.io/github/actions/workflow/status/lengxiqwq/live-photo-box/build.yml?style=for-the-badge&logo=githubactions" alt="Build"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=for-the-badge&logo=windows11" alt="Platform">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL%203.0-blue?style=for-the-badge" alt="GPL 3.0"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=for-the-badge&color=yellow" alt="Stars"></a>
</p>

---

## 💡 这是什么？

iPhone 拍摄的实况照片（Live Photos）本质上是一张照片 + 一段视频。问题就是苹果用的格式和 Windows 不兼容：

- 在 Windows 上**无法直接预览**实况照片的动态效果
- 从 iPhone 导出到电脑后，文件管理器里看到的只是一个普通的静态图片
- 跨平台传输（比如从 iOS 传到 Android 再传回来）经常导致**元数据丢失、配对失效**，实况照片变成一张普通 JPEG
- 前置摄像头拍的视频方向会**旋转、被异常拉伸**，Windows 完全识别不了

**Live Photo Box** 就是来解决这些问题的——让你在 Windows 上也能像 iPhone 一样查看、管理、修复实况照片。

基于 **WinUI 3（Windows App SDK 1.8）** 构建，原生适配 Windows 11 Fluent Design 设计规范，支持 Mica / Acrylic 材质、深色/浅色主题自动切换。

---

## 🚀 下载

<div align="center">
  <a href="#"><img src="./screenshots/Microsoft.svg" alt="Get it from Microsoft" height="52" hspace="24" /></a><a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="./screenshots/GitHub.svg" alt="GitHub Releases" height="52" hspace="24" /></a>
</div>

<p align="center">
  <sub>
    支持架构：<b>x86_64</b>
    &nbsp;|&nbsp;
    系统要求：<b>Windows 10 (1809+)</b> 或 <b>Windows 11</b>
    &nbsp;|&nbsp;
    无需额外安装运行时（应用自包含 .NET 9 + WinAppSDK 1.8）
  </sub>
</p>

---

## ✨ 核心功能

### 🔗 实况照片合成

将任意静态图片 + 视频素材组合为标准实况照片。

- 支持 **Google `Micro Video V1` / `Motion Photo V2` / `OPPO` / `小米`** 等多种协议
- 自动写入完整 `EXIF` + `QuickTime` 元数据（`ContentIdentifier` `UUID`）
- **黑科技配对引擎**：即使照片和视频文件名完全不同，也能通过 Apple 元数据中的 `ContentIdentifier`（`UUID`）精确匹配；无法匹配 `UUID` 时自动降级为拍摄日期 ±2 秒容差匹配，时区感知
- **目前任何协议合成的实况照片均可在 Windows 上直接查看**

### 📸 实况照片拆分

将实况照片拆分为独立的静态图片（`JPG` / `HEIC`）和视频（`MOV` / `MP4`）。

- 智能剥离 Google `XMP` 元数据，防止拆分后的图片被再次误识别为实况照片
- 按 `JPEG` 段结构逐段重建，不丢失 `EXIF` / `ICC` / `GPS` / 拍摄参数

### 🛠️ 实况照片修复

深度修复 iPhone 实况照片导出到 Windows 后的显示异常。

- **多余缩略图及横向拉伸**（iOS 17.3 之前）：Apple 曾嵌入低分辨率缩略图但带有方向标签，Windows 误将其当横图处理导致拉宽或压扁 → `jpegtran` 无损旋转 + 剥离残留缩略图
- **前置摄像头视频旋转**：iPhone 前置镜头纵向像素横向存储，依赖方向标签指示角度，Windows 不识别 → `FFmpeg` 重编码消除旋转矩阵
- **HEIC 方向错误**：修正错误的 `Orientation` 标签
- **ContentIdentifier 丢失**：自动修复照片-视频的 `UUID` 配对关系
- 扫描后可查看每张照片的**诊断详情**，可以按文件类型或修复状态快速筛选

### 🖼️ 封面修改（功能开发中）

自由更换实况照片的封面帧。

- 从视频中提取任意帧作为新封面
- 支持上传自定义图片
- 无损替换，保持实况属性完整

### 📂 自动整理相册（功能开发中）

按拍摄设备、日期、实况照片类型自动扫描分类归档。

---

## 📋 支持的实况照片协议

| 协议 | 来源 | 说明 |
|------|------|------|
| Micro Video V1 | Google（已弃用，但老设备等兼容性高） | `MP4` 视频附加在 `JPEG` 末尾，`GCamera:MicroVideoOffset` 记录偏移。旧版小米 MIUI / 旧版 Pixel 使用 |
| Motion Photo V2 | Google | 现代标准，`Container:Directory` `XMP` 结构。Google Pixel / Xiaomi HyperOS 3+ 使用 |
| O-Live Photo | OPPO / OnePlus | 扩展 `Motion Photo V2`，增加 `OpCamera` 命名空间 + `EXIF` `UserComment`。OPPO ColorOS / OnePlus OxygenOS 使用 |

> ⚡ **目前任何协议合成的实况照片，均可在 Windows 11 上直接查看动态效果。**

---

## 📸 应用截图

<table align="center">
<tr>
  <td align="center"><b>🏠 主页与图文教程</b><br><img src="screenshots/主页.png" alt="主页" width="100%" /></td>
  <td align="center"><b>📸 实况照片拆分</b><br><img src="screenshots/拆分页.png" alt="拆分" width="100%" /></td>
</tr>
<tr>
  <td align="center"><b>🔗 实况照片合成</b><br><img src="screenshots/合成页.png" alt="合成" width="100%" /></td>
  <td align="center"><b>🛠️ 实况照片修复</b><br><img src="screenshots/修复页.png" alt="修复" width="100%" /></td>
</tr>
<tr>
  <td align="center"><b>⚙️ 设置面板</b><br><img src="screenshots/设置.png" alt="设置" width="100%" /></td>
  <td align="center"><b>✨ Acrylic 半透明材质效果</b><br><img src="screenshots/acrylic_thin.png" alt="Acrylic效果" width="100%" /></td>
</tr>
</table>

---

## 🛠️ 技术栈

| 层级 | 技术 | 版本 |
|------|------|------|
| 语言 | C# | 13.0 |
| 运行时 | .NET | 9.0 |
| UI 框架 | Windows App SDK（WinUI 3） | 1.8 |
| 架构 | MVVM（CommunityToolkit.Mvvm） | 8.4.2 |
| 图像处理 | Magick.NET（ImageMagick）+ Win2D | 14.14.0 / 1.3.2 |
| 元数据引擎 | `ExifTool`（常驻进程模式，v13.x） | — |
| 视频处理 | `FFmpeg`（NVENC / QSV / AMF 硬件加速） | — |
| JPEG 操作 | `jpegtran`（无损旋转、缩略图剥离） | — |
| UI 扩展 | CommunityToolkit.WinUI + FluentIcons | — |
| 打包 | MSIX 自包含（无需安装运行时） | — |

---

## 💻 编译与开发

### 环境

- [Visual Studio 2022](https://visualstudio.microsoft.com/) 及以上
- 在 VS Installer 中勾选：**.NET 桌面开发** + **通用 Windows 平台开发**（含 Windows App SDK 组件）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### 构建

```bash
# 克隆仓库到本地
git clone https://github.com/lengxiqwq/live-photo-box.git
cd live-photo-box

# 还原 NuGet 依赖包
dotnet restore

# 编译项目
dotnet build Live Photo Box/LivePhotoBox.csproj

# 启动运行
dotnet run --project Live Photo Box/LivePhotoBox.csproj
```

---

## 📁 项目结构

```
live-photo-box/
├── Live Photo Box/              # 主项目（WinUI 3 MSIX 应用）
│   ├── Assets/                # 图标、教程截图等静态资源
│   ├── Controls/              # 自定义控件（全屏灯箱、底部状态栏）
│   ├── Converters/            # XAML 值转换器
│   ├── Helpers/               # 工具类（滚动、格式化、悬停预览等）
│   ├── Models/                # 数据模型
│   ├── Services/              # 业务逻辑层
│   │   └── Protocols/         # 实况照片协议实现（3 种）
│   ├── Strings/               # 多语言资源（中文 / 英文）
│   ├── ViewModels/            # MVVM ViewModel 层
│   └── Views/                 # XAML 页面
├── docs/                      # 项目文档
└── README.md
```

> 📖 完整目录说明见 [`docs/LivePhotoBox-ProjectOverview.md`](docs/LivePhotoBox-ProjectOverview.md)

---

## 🌍 本地化

| 语言 | 状态 |
|------|:----:|
| 中文（简体） | ✅ 完整 |
| English | ✅ 完整 |

支持系统语言自动跟随，也可在设置中手动切换。

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

- 🐛 **Bug 报告** and 💡 **功能建议** → [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- 🔧 **代码贡献** → Fork → Feature Branch → Pull Request

### 规范

- UI 文本使用 RESW 多语言资源文件，不硬编码字符串
- 遵循项目 MVVM 分层惯例，保持代码整洁
- 文件顶部添加多行注释描述文件用途

---

## 📄 许可证

本项目基于 **GNU General Public License v3.0 (GPL 3.0)** 开源。详见 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢

| 工具/库 | 用途 | 许可 |
|---------|------|------|
| [ExifTool](https://exiftool.org/) | 图像/视频元数据读写 | Perl |
| [FFmpeg](https://ffmpeg.org/) | 视频编解码 | LGPL/GPL |
| [jpegtran](https://jpegclub.org/) | JPEG 无损变换 | 自由软件 |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | HEIC 解码 | Apache 2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM 框架 | MIT |
| [Win2D](https://github.com/microsoft/Win2D) | GPU 加速图形 | MIT |
| [FluentIcons](https://github.com/davidxuang/FluentIcons) | Fluent 图标集 | MIT |

---

<p align="center">
  <sub>Made with ❤️ by <a href="https://github.com/lengxiqwq">LengxiQwQ</a></sub>
</p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=social" alt="Stars"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/network/members"><img src="https://img.shields.io/github/forks/lengxiqwq/live-photo-box?style=social" alt="Forks"></a>
</p>

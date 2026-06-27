<div align="center">

<img src="LivePhotoBox/Assets/Square150x150Logo.png" alt="Live Photo Box Logo" width="150" />

# Live Photo Box（实况照片工具箱）

*一款专为 Windows 打造的现代化 Apple 实况照片管理与修复利器*

</div>

<p align="center">
  <!-- 版本 -->
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=for-the-badge&color=0078D7&logo=github" alt="Release"></a>
  <!-- 微软商店 -->
  <a href="#下载与安装"><img src="https://img.shields.io/badge/Microsoft%20Store-审核中-0078D7?style=for-the-badge&logo=microsoftstore" alt="Microsoft Store"></a>
  <!-- 构建状态 -->
  <a href="https://github.com/lengxiqwq/live-photo-box/actions"><img src="https://img.shields.io/github/actions/workflow/status/lengxiqwq/live-photo-box/build.yml?style=for-the-badge&logo=githubactions" alt="Build"></a>
  <!-- 许可 -->
  <a href="LICENSE"><img src="https://img.shields.io/github/license/lengxiqwq/live-photo-box?style=for-the-badge&color=blue" alt="License"></a>
</p>

<p align="center">
  <!-- 平台 -->
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <!-- 语言 -->
  <img src="https://img.shields.io/badge/Language-C%23%2013-239120?style=flat-square&logo=csharp&logoColor=white" alt="C#">
  <!-- 框架 -->
  <img src="https://img.shields.io/badge/Framework-.NET%209%20%2B%20WinUI%203-512BD4?style=flat-square&logo=dotnet" alt=".NET">
  <!-- 架构 -->
  <img src="https://img.shields.io/badge/Arch-MVVM-FF6C2C?style=flat-square" alt="MVVM">
  <!-- 最低系统 -->
  <img src="https://img.shields.io/badge/Min%20OS-Windows%2010%201809-0078D7?style=flat-square" alt="Windows 10 1809">
  <!-- 多语言 -->
  <img src="https://img.shields.io/badge/语言-中文%20%7C%20English-FF6C2C?style=flat-square" alt="Languages">
  <!-- Stars -->
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=flat-square&color=yellow" alt="Stars"></a>
</p>

---

## 💡 这是什么？

在 Windows 上处理 iPhone 实况照片（Live Photos）一直是一件麻烦事——拆分不行、合成不了、跨平台传输后配对丢失、第三方相册无法识别……

**Live Photo Box** 正是为此而生。无论你需要拆分、合成、修改封面，还是修复因跨平台传输导致的元数据损毁，它都能一站式搞定。

基于 **WinUI 3（Windows App SDK 1.8）** 构建，拥有原生的 Fluent Design 现代化界面、流畅的动画效果，以及深色/浅色模式自动适配。

---

## ✨ 核心功能

<table>
<tr>
<td width="50%">

### 📸 实况分离（Split）

一键将实况照片拆分为独立的静态图片（JPG/HEIC）和动态视频（MOV/MP4）。

- 智能剥离 Google XMP 元数据，防止"假阳性"循环
- 按 JPEG 段结构逐段重建，不丢失 EXIF / ICC / 拍摄参数
- 支持 MicroVideo V1、MotionPhoto V2、OPPO O-Live 三种协议

</td>
<td width="50%">

### 🔗 实况合成（Combo）

将普通的静态图片与视频素材组合为标准实况照片。

- 支持 Google / OPPO 两种合成协议可选
- 自动写入 EXIF + QuickTime 元数据（ContentIdentifier UUID）
- 文件名智能匹配，自动发现图片-视频配对

</td>
</tr>
<tr>
<td width="50%">

### 🖼️ 封面修改（Key Photo）

自由更换实况照片的封面帧。

- 从视频中提取任意帧作为新封面
- 支持上传自定义图片
- 无损替换，保持实况属性完整

</td>
<td width="50%">

### 🛠️ 元数据修复（Repair）

深度修复因跨平台传输导致的元数据损毁。

- 自动诊断：方向、缩略图、ContentIdentifier、视频时长等
- 智能修复：jpegtran 无损旋转 + exiftool 元数据纠错
- 视频修复：FFmpeg 重编码（支持硬件加速自动回退）
- 修复后自动写入操作历史标记（XMP `dc:subject`）

</td>
</tr>
<tr>
<td colspan="2" align="center">

### ⚡ 多线程批量引擎

所有功能均支持**多选文件 / 文件夹批量拖拽导入**，内置并行处理引擎，支持**暂停 / 恢复 / 取消**操作。

</td>
</tr>
</table>

---

## 📋 支持的实况照片协议

| 协议 | 来源 | 状态 | 说明 |
|------|------|:----:|------|
| **MotionPhoto V2** | Google | ✅ | 现代跨平台标准，Google Pixel / Samsung Galaxy / Xiaomi HyperOS 3+ |
| **OPPO O-Live** | OPPO / OnePlus | ✅ | 扩展 MotionPhoto V2，增加 OpCamera 命名空间和 EXIF 标记 |
| **MicroVideo V1** | Google（旧） | ✅ | 已弃用格式，兼容旧版小米 MIUI / 旧版 Pixel |

> 📖 协议细节详见 [`docs/`](docs/) 目录下的分析报告

---

## 📸 应用截图

<p align="center">
  <em>（截图存放于 <code>screenshots/</code> 目录）</em>
</p>

<details open>
<summary><b>🏠 主页</b></summary>
<br>
<p align="center">
  <img src="screenshots/home.png" alt="主页" width="80%" />
</p>
</details>

<details>
<summary><b>📸 实况分离</b></summary>
<br>
<p align="center">
  <img src="screenshots/split.png" alt="拆分" width="80%" />
</p>
</details>

<details>
<summary><b>🔗 实况合成</b></summary>
<br>
<p align="center">
  <img src="screenshots/merge.png" alt="合成" width="80%" />
</p>
</details>

<details>
<summary><b>🛠️ 元数据修复</b></summary>
<br>
<p align="center">
  <img src="screenshots/repair.png" alt="修复" width="80%" />
</p>
</details>

<details>
<summary><b>⚙️ 设置面板</b></summary>
<br>
<p align="center">
  <img src="screenshots/settings.png" alt="设置" width="80%" />
</p>
</details>

<details>
<summary><b>🖼️ 全屏灯箱预览</b></summary>
<br>
<p align="center">
  <img src="screenshots/lightbox.png" alt="灯箱" width="80%" />
</p>
</details>

<details>
<summary><b>🌙 深色模式</b></summary>
<br>
<p align="center">
  <img src="screenshots/dark-mode.png" alt="深色模式" width="80%" />
</p>
</details>

---

## 🚀 下载与安装

### 📦 方式一：微软商店（推荐）

<a href="#"><img src="https://img.shields.io/badge/Microsoft%20Store-审核中-0078D7?style=for-the-badge&logo=microsoftstore" alt="Microsoft Store"></a>

> 应用正在微软商店审核中，审核通过后将提供商店链接。商店版自动更新，无需手动升级。

### 📥 方式二：GitHub Releases

前往 [Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 下载最新版本：

| 包类型 | 说明 |
|--------|------|
| `.msix` | MSIX 安装包，双击安装，自动注册 WinAppSDK 运行时 |
| `.zip` | 绿色便携版，解压即用（需系统已安装 WinAppSDK 运行时） |

支持架构：**x64** · **ARM64** · **x86**

### 📋 运行环境要求

| 要求 | 最低配置 |
|------|----------|
| 操作系统 | Windows 10 (版本 1809+) 或 Windows 11 |
| 架构 | x64 / ARM64 / x86 |
| 运行时 | 无需额外安装（应用自包含 .NET 9 + WinAppSDK 1.8） |

---

## 🛠️ 技术栈

<table>
<tr><th>层级</th><th>技术</th><th>版本</th></tr>
<tr><td>语言</td><td><img src="https://img.shields.io/badge/C%23-13.0-239120?logo=csharp" alt="C#"></td><td>13.0</td></tr>
<tr><td>运行时</td><td><img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET"></td><td>9.0</td></tr>
<tr><td>UI 框架</td><td>Windows App SDK（WinUI 3）</td><td>1.8</td></tr>
<tr><td>架构模式</td><td>MVVM（CommunityToolkit.Mvvm）</td><td>8.4.2</td></tr>
<tr><td>图像处理</td><td>Magick.NET（ImageMagick）+ Win2D</td><td>14.14.0 / 1.3.2</td></tr>
<tr><td>元数据处理</td><td>ExifTool（常驻进程模式，13.x）</td><td>—</td></tr>
<tr><td>视频转码</td><td>FFmpeg（可选，NVENC/QSV/AMF 硬件加速）</td><td>—</td></tr>
<tr><td>UI 扩展</td><td>CommunityToolkit.WinUI + FluentIcons</td><td>—</td></tr>
<tr><td>包管理</td><td>MSIX 打包（自包含，无需运行时）</td><td>—</td></tr>
</table>

---

## 💻 编译与开发

### 环境准备

1. 安装 [Visual Studio 2022](https://visualstudio.microsoft.com/)
2. 在 VS Installer 中勾选：
   - ✅ **.NET 桌面开发**
   - ✅ **通用 Windows 平台开发**（含 Windows App SDK 组件）
3. 安装 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### 克隆与构建

```bash
# 克隆仓库
git clone https://github.com/lengxiqwq/live-photo-box.git
cd live-photo-box

# 还原依赖
dotnet restore

# 构建（非打包模式，可直接运行）
dotnet build LivePhotoBox/LivePhotoBox.csproj

# 运行
dotnet run --project LivePhotoBox/LivePhotoBox.csproj
```

### VS Code

```bash
# VS Code 配置位于 .vscode/ 目录，包含：
#   launch.json — 启动 & 附加调试
#   tasks.json  — build / publish / watch（热重载）
code .
# 按 F5 启动调试
```

---

## 📁 项目结构

```
live-photo-box/
├── LivePhotoBox/              # 主项目（WinUI 3 MSIX 应用）
│   ├── Assets/                # 图标、教程截图等静态资源
│   ├── Controls/              # 自定义控件（灯箱、状态栏）
│   ├── Converters/            # XAML 值转换器（7 个）
│   ├── Helpers/               # 工具类（滚动、格式化、悬停预览）
│   ├── Models/                # 数据模型（13 个）
│   ├── Services/              # 业务逻辑层（24 个服务）
│   │   └── Protocols/         # 实况照片协议实现（3 种）
│   ├── Strings/               # 多语言资源（中/英）
│   ├── ViewModels/            # MVVM ViewModel 层（11 个）
│   └── Views/                 # XAML 页面（10 个）
├── docs/                      # 项目文档
│   ├── LivePhotoBox-项目总览.md
│   ├── LivePhotoBox-历史记录标识规范.md
│   ├── 小米实况照片协议分析报告.md
│   ├── 小米实况照片协议开发记录.md
│   └── 苹果的矩阵幻象.md
└── README.md
```

> 📖 完整目录树及每个文件的功能说明见 [`docs/LivePhotoBox-项目总览.md`](docs/LivePhotoBox-项目总览.md)

---

## 🌍 本地化

| 语言 | 状态 |
|------|:----:|
| 中文（简体） | ✅ 完整 |
| English | ✅ 完整 |

支持系统语言自动跟随，也可在设置中手动切换。切换后提示重启应用生效。

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

- 🐛 **Bug 报告** → [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- 💡 **功能建议** → [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- 🔧 **代码贡献** → Fork → Feature Branch → Pull Request

### 贡献前须知

- 所有 UI 文本请使用 RESW 多语言资源文件，不要硬编码字符串
- 保持代码整洁、清晰，遵循项目现有的 MVVM 分层惯例
- 在文件顶部添加多行注释简要描述该文件的用途

---

## 📄 许可证

本项目基于开源许可证发布。详见 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢

本项目依赖以下优秀的开源工具和库：

| 工具/库 | 用途 | 许可 |
|---------|------|------|
| [ExifTool](https://exiftool.org/) | 图像/视频元数据读写 | Perl 许可证 |
| [FFmpeg](https://ffmpeg.org/) | 视频编解码 | LGPL/GPL |
| [jpegtran](https://jpegclub.org/) | JPEG 无损变换 | 自由软件 |
| [ImageMagick (Magick.NET)](https://github.com/dlemstra/Magick.NET) | HEIC 图像解码 | Apache 2.0 |
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

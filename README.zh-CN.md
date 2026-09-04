<div align="center">
<h1>
  <img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/LivePhotoBox/Assets/Icons/AppIcon-full.png" width="130" align="left" hspace="16" />
  Live Photo Box（实况照片工具箱）
</h1>
<p><em>统一各类实况照片协议，实现跨设备无缝查看与迁移</em></p>
<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release" alt="Latest release"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/actions"><img src="https://img.shields.io/github/actions/workflow/status/lengxiqwq/live-photo-box/build.yml?style=flat-square&logo=githubactions" alt="Build"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <img src="https://img.shields.io/badge/9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" />
  <img src="https://img.shields.io/badge/WinUI%203-1.8-0078D7?style=flat-square&logo=windows" alt="WinUI 3" />



</p>
</div>

---

<p align="center">
  📖 README Language：<strong>简体中文</strong> &nbsp;·&nbsp; <a href="README.md">English</a>
</p>

## 🚀 下载

<div align="center">
  <a href="https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full" target="_blank" rel="noopener noreferrer"><img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft" height="52" width="190" hspace="35" /></a><a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/GitHub.svg" alt="GitHub Releases" height="52" width="190" hspace="35" /></a>
</div>
<p align="center">
</p>

<p align="center">
  或通过 <b>winget</b> 安装 <b>纯命令行版本</b>： <code>winget install LengxiQwQ.LivePhotoBox</code>
</p>

---

> 🎨 **想给软件换个新图标**
>
> Live Photo Box 的主要功能已经逐渐稳定，接下来作者想给软件换一个更合适的图标，也顺便更新一下主页 Banner 横幅。
>
> 如果你有喜欢的设计方向，或者愿意动手尝试一下，欢迎把作品分享到 [GitHub Discussions](https://github.com/LengxiQwQ/live-photo-box/discussions/new?category=ideas)。PNG、PSD、SVG、Figma 文件以及其他设计网站链接都可以，简单的草图或想法也没问题。谢谢大家！

---

## 💡 这是什么？

不同品牌的实况照片虽然都由图片与短视频组成，却采用了不同的文件结构和数据方式。跨设备或跨平台迁移后，常常会遇到无法播放、方向异常、动态丢失或元数据缺失等问题。

**实况照片工具箱** 提供统一的实况照片处理能力，支持合成、拆分、格式转换、修复与编辑，并可在不同品牌与设备之间迁移实况照片。

同时最大程度保留原始照片的细节与信息，包括 HDR、EXIF、相机参数、拍摄信息以及原始媒体内容，让每一次转换，都尽可能接近照片最初的样子。

---

## 📸 应用截图

<p align="center"><b>🖼️ 实况照片编辑</b><br><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/%E7%BC%96%E8%BE%91%E9%A1%B5.png" alt="编辑" width="80%" /></p>

<p align="center"><b>🔗 实况照片合成</b><br><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/%E5%90%88%E6%88%90%E9%A1%B5.png" alt="合成" width="80%" /></p>

---

> ℹ️ **历史版本与当前架构说明**
>
> Live Photo Box 的部分历史发布版本曾使用 FFmpeg、ExifTool、jpegtran 和 libheif 命令行工具完成部分媒体与元数据处理。当前开发主线已经迁移至 Rebuilt / Native-only 架构，这些外部可执行工具不再属于当前产品运行时设计。因此，旧发布版本与当前 master 在目录结构、依赖和部分功能可用性上可能存在差异。

---

## ✨ 核心功能与开发状态

Live Photo Box 严格区分**来源协议识别与清理**、**中性媒体提取**与**目标协议组装**：

```text
GUI / CLI → Core 编排调度 → LivePhotoBox.Native (C++20)
（仅保留 Rebuilt 引擎 · 无 Legacy 运行时 · 无随包外部工具二进制）
```

### 🔗 实况照片合成

将**双文件实况照片**（或任意图片 + 视频）转换为**单文件实况照片**，在 Windows 及 Android 设备上均可查看。

- **任意素材，一键合成**：拖拽或选择图片（`JPG` / `HEIC`）+ 视频（`MP4` / `MOV`）直接合成；也可以扫描整个文件夹，自动识别配对、批量入队
- **多种智能配对**：按文件名、Apple `ContentIdentifier` UUID（由 Native 直接解析，无需外部工具）、vivo 相机 ID 自动匹配图片与视频
- **目标协议输出**：JPEG 组合完整支持 Google Micro Video (v1)、Google Motion Photo (v2)、OPPO O-Live、vivo、Samsung Motion Photo（SEF 尾标）与 HUAWEI Moving Photo；Samsung 与 HUAWEI 亦支持 HEIC 尾标合成
- **批量文件命名**：命名模板快速编排，支持拖拽排序、预设模板、分隔符选择与实时预览
- **收尾处理**：合成完成后可选择移动到指定目录，或移入回收站
- **并行批量合成**：任务队列支持搜索、多维度排序与状态筛选，多任务并行处理，实时显示进度与耗时

| 目标合成协议 | 支持格式组合 | 当前 master 状态 |
|---|---|---|
| Google - Micro Video (V1) | JPEG + MP4/MOV | ✅ 可用 |
| Google - Motion Photo (V2) | JPEG + MP4/MOV | ✅ 可用 |
| OPPO - O-Live Photo | JPEG + MP4/MOV | ✅ 可用 |
| HUAWEI - Moving Photo | JPEG + MP4/MOV, HEIC + MP4 | ✅ 可用 |
| Samsung - Motion Photo | JPEG + MP4/MOV, HEIC + MP4 | ✅ 可用（SEF 尾标） |
| vivo - Live Photo | JPEG + MP4/MOV | ✅ 可用 |
| Google / OPPO / vivo (HEIC 目标) | HEIC + MP4/MOV | 🟡 安全拒绝（待 Native HEIC XMP 写入能力） |
| Apple - Live Photo（双文件目标） | HEIC/JPEG + MOV | ⏳ 暂未开放（规划于协议 Writer 阶段 P9） |

### 📸 实况照片拆分

将**实况照片**（单文件形式）拆分为协议无关的独立中性静态图片（`JPG` / `HEIC`）和视频（`MP4` / `MOV`）。

- **批量拆分**：扫描整个文件夹自动识别实况照片、批量入队；支持 Google、OPPO、vivo、Samsung、华为等协议来源
- **中性提取与协议清理**：剥离实况照片专有元数据（XMP 命名空间、SEF MotionPhoto 标签、华为尾标等），防止拆分出的图片被再次误识别为实况照片；同时完整保留原始 `EXIF` / `ICC` / `GPS` / 拍摄参数与 HDR 增益图
- **命名模板**：与合成页一致的片段式命名，支持拖拽排序与实时预览

| 能力阶段 | 范围 | 当前 master 状态 |
|---|---|---|
| 来源识别 (Inspect) | Google、OPPO、vivo、Samsung、HUAWEI、Apple | ✅ 可用（Native） |
| 中性拆分与清理 (Split & Clean) | 提取协议无关中性图片与视频 | ✅ 可用（Native） |
| 目标协议重封装 (Target Packaging) | 重新组装为 Apple/vivo 双文件实况照片 | ⏳ 暂未开放（规划于协议 Writer 阶段 P9） |

### 🖼️ 实况照片编辑（封面 / Key Photo）

更换实况照片的封面帧，从视频时间轴中选取精彩瞬间。

> ⏳ **当前 master 状态**：界面与 CLI 命令已保留，但底层中性媒体管线尚未就绪（抛出 `RebuiltPipelineNotReady`）。当前冻结，待 Native 重构完成。

### 🛠️ 实况照片修复

修复针对 Apple 实况照片导出后出现的显示异常（多余缩略图横向拉伸、前置镜头方向异常等）。

> ⏳ **当前 master 状态**：界面与 CLI 命令已保留，但底层中性媒体管线尚未就绪（抛出 `RebuiltPipelineNotReady`）。当前冻结，规划于 Roadmap P8。

### 📂 自动整理相册（功能开发中）

通过识别照片元数据，自动按拍摄设备、日期、实况照片类型自动扫描分类归档。首批从 iPhone 起步，逐步覆盖更多品牌。

---

## 💻 命令行工具

Live Photo Box 提供**命令行工具** —— `livephotobox`，与 GUI 共享 100% 核心逻辑，适合脚本和 AI Agent 调用。

- **命令**：`convert`（独立媒体格式转换）、`protocols`（协议 × 格式兼容矩阵查询）、`merge`（单对或批量合成）、`split`（拆分提取中性图片 + 视频）、`cover` / `keyphoto`（修改封面帧；待 Native 重构）、`repair`（元数据修复；待 Native 重构）、`update` / `update-check`（检查并安装更新）
- **四个可执行别名**：`livephotobox` / `livephoto` / `livebox` / `lpb`
- **批量配对和命名方式**：按文件名、Apple `ContentIdentifier` UUID、vivo 相机 ID 自动配对；`-n custom:{name}_{date}` 等命名模板批量重命名输出；`--after` 支持完成后移动到文件夹 / 回收站
- **脚本友好**：使用 `--json` 输出结构化结果，供脚本与 AI Agent 直接消费；`--dry-run` 可预览操作而不实际处理文件
- **分发**：随安装包 / 便携版内置（可选"添加到 PATH"），或独立 `-x64-cli.zip`（单文件免安装，包内附 `add-to-path.cmd` / `remove-from-path.cmd` 辅助脚本，双击即可一键加入 / 移除 PATH）；也可用 `winget install LengxiQwQ.LivePhotoBox` 一键安装纯 CLI 版

📖 **CLI 使用指南**：[English](docs/CLI-User-Guide.md) · [简体中文](docs/CLI-User-Guide.zh-CN.md)

---

## 🛠️ 技术栈

| 层级 | 技术 | 版本 |
|------|------|------|
| 语言 | C# | 13.0 |
| Native 后端 | Visual C++ / C++20 DLL（稳定 C ABI） | MSVC x64（v143/v145） |
| 运行时 | .NET | 9.0 |
| UI 框架 | Windows App SDK（WinUI 3） | 1.8 |
| 架构 | MVVM（CommunityToolkit.Mvvm） | 8.4.2 |
| 媒体与协议引擎 | `LivePhotoBox.Native`（进程内 C++20 ISO-BMFF / JPEG / SEF / Apple MakerNote 解析、清洗、提取与合成） | — |
| 图像缩放 | PhotoSauce.MagicScaler | 0.15.0 |
| 图像处理 (GUI) | Windows Imaging Component (WIC) + Win2D | — / 1.3.2 |
| Markdown 渲染 | Markdig | 1.3.2 |
| UI 扩展 | CommunityToolkit.WinUI + FluentIcons | — |
| 命令行 | System.CommandLine | 2.0.11 |
| 打包 | MSIX 自包含（GUI）/ 单文件 zip（CLI） | — |

> 当前产品运行时完全基于 **Rebuilt / Native** 引擎（`LivePhotoBox.Native`）通过稳定 C ABI 运行。旧运行时代码与随包外部工具已从产品运行时中彻底移除。

---

## 💻 编译与开发

### 环境

- [Visual Studio 2022](https://visualstudio.microsoft.com/) 及以上
- 在 VS Installer 中勾选：**.NET 桌面开发** + **通用 Windows 平台开发** + **使用 C++ 的桌面开发**（MSVC x64 工具链）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### 构建

```bash
# 克隆仓库到本地
git clone https://github.com/lengxiqwq/live-photo-box.git
cd live-photo-box
```

仓库提供现成的 PowerShell 构建脚本（`scripts/`），GUI 与 CLI 均可一键构建，无需手敲 dotnet 命令：

| 脚本 | 产物 |
|------|------|
| `scripts/build-dev.ps1` | 未打包开发版（GUI + CLI），输出到 `publish/` |
| `scripts/build-cli-release.ps1` | 独立 CLI 单文件包（`publish/Live-Photo-Box-v{版本}-x64-cli.zip`） |
| `scripts/build-release.ps1` | 完整发布三件套：便携版 zip + CLI zip + 安装包 |

> 脚本支持 `-CI` 参数，供 GitHub Actions 等 CI 环境使用（不弹 `pause` 等待）。

本地验证请在仓库根目录依次运行 Native smoke test、解决方案测试和 CLI 全流程测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/build-native.ps1 -Configuration Release -Architecture x64 -RunTests
dotnet test
python scripts/testing/run-cli-integration-test.py
```

创建发布标签前，以上三项必须全部通过。`scripts/build-release.ps1` 也会自动构建 Native，并检查 GUI 与 CLI 产物中存在 `LivePhotoBox.Native.dll`。

---

## 📁 项目结构

```
live-photo-box/
├── LivePhotoBox.Core/        # 共享核心库（协议、合成/拆分/修复服务、本地化）
├── LivePhotoBox.Native/      # C++20 Native 后端（稳定 C ABI，x64 DLL）
├── LivePhotoBox/             # 主项目（WinUI 3 MSIX 应用）
│   ├── Assets/               # 图标、教程截图等静态资源
│   ├── Controls/             # 自定义控件（全屏灯箱、底部状态栏）
│   ├── Converters/           # XAML 值转换器
│   ├── Helpers/              # 工具类（滚动、格式化、悬停预览等）
│   ├── Models/               # 数据模型
│   ├── Services/             # GUI 业务逻辑层（委托给 LivePhotoBox.Core）
│   ├── Strings/              # 多语言资源（中文 / 英文）
│   ├── ViewModels/           # MVVM ViewModel 层
│   └── Views/                # XAML 页面
├── LivePhotoBox.CLI/         # 命令行工具（livephotobox）
├── tests/                    # 测试项目（Core / CLI / UI / 基准测试）
├── docs/                     # 项目文档
├── changelogs/               # 更新日志
├── scripts/                  # 构建与打包脚本
│   └── native/               # MSVC/Native 构建与 smoke test 脚本
├── artifacts/                # Native 构建产物（gitignore）
├── screenshots/              # 截图资源
├── lpb.cmd                   # 源码版 CLI 开发别名（直接运行当前源码，等价 dotnet run）
└── README.md
```

📖 完整目录说明见 <strong><a href="docs/项目总览.md">项目总览</a></strong>

---

## 📋 更新日志

📋 CHANGELOG：<strong><a href="changelogs/CHANGELOG.zh-CN.md">简体中文</a> &nbsp;·&nbsp; <a href="changelogs/CHANGELOG.md">English</a></strong>

---

## 🌍 本地化

| 语言 | 状态 |
|------|:----:|
| 中文（简体）(zh-Hans) | ✅ 完整 |
| English (en) | ✅ 完整 |

支持系统语言自动跟随，也可在设置中手动切换。

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

- 🐛 **Bug 报告**、💡 **功能建议** → [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- 🔧 **代码贡献** → Fork → Feature Branch → Pull Request

---

## 📄 许可证

本项目基于 **GNU General Public License v3.0 (GPL 3.0)** 开源。详见 [LICENSE](LICENSE) 文件。

---

## 🙏 致谢

| 工具/库 | 用途 | 许可 |
|---------|------|------|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM 框架 | MIT |
| [PhotoSauce.MagicScaler](https://github.com/saucecontrol/PhotoSauce) | 高性能图片缩放 | MIT |
| [Microsoft.Graphics.Win2D](https://github.com/microsoft/Win2D) | GPU 加速 2D 图形渲染 | MIT |
| [Markdig](https://github.com/xoofx/markdig) | Markdown 渲染 | BSD-2-Clause |
| [FluentIcons](https://github.com/davidxuang/FluentIcons) | Fluent 图标集 | MIT |
| [ExifTool](https://exiftool.org/) / [FFmpeg](https://ffmpeg.org/) | 历史发布版本与离线测试独立验证夹具 (`run-cli-integration-test.py`) | Perl / LGPL |

---

## ⭐ Star 历史

<a href="https://www.star-history.com/?repos=lengxiqwq%2Flive-photo-box&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&theme=dark&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
 </picture>
</a>

<!-- INSIGHTS:START -->
**📊 仓库流量**

访问次数：**2,390** ｜ 不重复访客：**183**（近 14 天） ｜ 仓库克隆：**2,068** ｜ 不重复克隆：**161**（近 14 天）

**热门来源（近 14 天）：** github.com · Bing · Google · chatgpt.com · t.co · doubao.com  
**热门内容（近 14 天）：** releases · releases/tag/v2.2.1 · README.zh-CN.md · releases/tag/v2.2.0

> 数据开始：2026-08-02 · 最后更新：2026-09-04 (UTC+8)
<!-- INSIGHTS:END -->

---

<p align="center">
  <sub>Made with ❤️ by <a href="https://github.com/lengxiqwq">LengxiQwQ</a></sub>
</p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=social" alt="Stars"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/network/members"><img src="https://img.shields.io/github/forks/lengxiqwq/live-photo-box?style=social" alt="Forks"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/downloads/lengxiqwq/live-photo-box/total?style=social" alt="Downloads"></a>
</p>

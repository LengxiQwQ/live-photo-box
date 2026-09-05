<div align="center">
<h1>
  <img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/LivePhotoBox/Assets/Icons/AppIcon-full.png" width="130" align="left" hspace="16" />
  Live Photo Box（实况照片工具箱）
</h1>
<p><em>统一各类实况照片协议，实现跨设备无缝查看与迁移</em></p>
<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" />
  <img src="https://img.shields.io/badge/C%2B%2B-20-00599C?style=flat-square&logo=c%2B%2B" alt="C++20" />
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 9" />
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

## ✨ 核心功能

### 🖼️ 实况照片编辑

自由更换实况照片的封面帧，从视频时间轴中选取最完美的一刻。

- 视频帧时间轴胶片条，逐帧预览
- 一键替换封面、导出单帧或全部视频帧，或者导出为视频以及 GIF 动图
- 快速实况照片协议转换
- 文件基本属性查看，实况照片协议查看

### 🔗 实况照片合成

将**双文件实况照片**（或任意图片 + 视频）转换为**单文件实况照片**，在 Windows 及 Android 设备上均可查看。

- **任意素材，一键合成**：拖拽或选择图片（`JPG` / `HEIC`）+ 视频（`MP4` / `MOV`）直接合成；也可以扫描整个文件夹，自动识别配对、批量入队
- **多种智能配对**：按文件名、Apple `ContentIdentifier` UUID、vivo 相机 ID 自动匹配图片与视频；也可直接合并现成的双文件实况照片
- **多品牌协议自由切换**：主流品牌目标协议一键切换，配合 `JPEG+MP4` / `JPEG+MOV` / `HEIC+MP4` / `HEIC+MOV` / `HEIC+MP4(H.265)` 输出格式，随协议自动筛选可用项
- **批量文件命名**：命名模板快速编排（原名 / 协议 / 日期 / 时间 / EXIF 日期时间 / 计数器 / 自定义文本），支持拖拽排序、预设模板、分隔符选择与实时预览——无需编写正则表达式即可批量重命名
- **收尾处理**：合成完成后可选择移动到指定目录，或移入回收站
- **并行批量合成**：任务队列支持搜索、多维度排序与状态筛选，多任务并行处理，实时显示进度、成功/失败统计与耗时

| 合成协议 | 支持设备 | 状态 |
|---|---|---|
| Google - Micro Video (v1) | Windows / 小米 (旧版 MIUI) / Pixel | ✅ 可用 |
| Google - Motion Photo (v2) | Windows / 小米 / Pixel | ✅ 可用 |
| OPPO - O-Live Photo | Windows / 小米 / OPPO | ✅ 可用 |
| HUAWEI - Moving Photo | 华为 / 荣耀 | ✅ 可用 |
| Samsung - Motion Photo | Windows / Samsung | ✅ 可用 |
| vivo - Live Photo | Windows / vivo（≥ x300） | 🟡 测试中 |

### 📸 实况照片拆分

将**实况照片**（单文件形式）拆分为**双文件实况照片**形式，或独立的静态图片（`JPG` / `HEIC`）和视频（`MP4` / `MOV`）。

- **批量拆分**：扫描整个文件夹自动识别实况照片、批量入队；可按协议筛选（Google / OPPO / vivo / Samsung / 华为），只拆分目标品牌
- **协议输出**：可转为 Apple / vivo 双文件实况照片（写入配对元数据），也可重新封装为 `HEIC+MOV` / `JPG+MOV` / `JPG+MP4` 格式
- **剥离实况照片元数据**：防止拆分后的图片被再次误识别为实况照片；保留照片其他元数据，不丢失 `EXIF` / `ICC` / `GPS` / 拍摄参数
- **命名模板**：与合成页一致的片段式命名，支持拖拽排序与实时预览

> ⚠️ **关于 iPhone / iPad**：受 iOS 系统限制，实况照片无法直接导入 iOS 设备。本软件只负责生成实况照片数据，导入需通过爱思助手（i4Tools）等第三方软件完成。

| 拆分协议 | 支持机型 | 状态 |
|---|---|---|
| Apple - Live Photo | iPhone / iPad | ✅ 可用 |
| vivo - Live Photo | vivo（≤ x200） | 🟡 测试中 |

### 🛠️ 实况照片修复

修复针对 Apple 实况照片导出后出现的显示异常。扫描后可查看每张照片的**诊断详情**，可以按文件类型或修复状态快速筛选。

- **多余缩略图及横向拉伸**（iOS 17.3 之前）：Apple 曾嵌入低分辨率缩略图但带有方向标签，Windows 误将其当作横向图片处理，导致拉宽或压缩。消除旋转异常并剥离多余缩略图
- **前置摄像头视频旋转**：iPhone 前置镜头纵向像素横向存储，依赖方向标签指示角度，消除旋转矩阵让播放方向恢复正常
- **HEIC 方向错误**：修正错误的 `Orientation` 标签（如果存在）

### 📂 自动整理相册（功能开发中）

通过识别照片元数据，自动按拍摄设备、日期、实况照片类型自动扫描分类归档。首批从 iPhone 起步，逐步覆盖更多品牌。

---

## 💻 命令行工具

Live Photo Box 提供**命令行工具** —— `livephotobox`，与 GUI 共享 100% 核心逻辑，适合脚本和 AI Agent 调用。

- **命令**：`merge`（单对或批量合成）、`split`（拆回图片 + 视频）、`repair`（修复 Apple 实况照片显示问题）、`cover`（修改封面帧）、`protocols`（协议 × 格式兼容矩阵查询）、`update` / `update-check`（检查并安装更新）
- **四个可执行别名**：`livephotobox` / `livephoto` / `livebox` / `lpb`
- **批量配对和命名方式**：按文件名、Apple `ContentIdentifier` UUID、vivo 相机 ID 自动配对；`-n custom:{name}_{date}` 等命名模板批量重命名输出；`--after` 支持完成后移动到文件夹 / 回收站
- **脚本友好**：使用 `--json` 输出结构化结果，供脚本与 AI Agent 直接消费；`--dry-run` 可预览操作而不实际处理文件
- **分发**：随安装包 / 便携版内置（可选"添加到 PATH"），或独立 `-x64-cli.zip`（单文件免安装，包内附 `add-to-path.cmd` / `remove-from-path.cmd` 辅助脚本，双击即可一键加入 / 移除 PATH）；也可用 `winget install LengxiQwQ.LivePhotoBox` 一键安装纯 CLI 版

📖 **CLI 使用指南**：[English](docs/CLI-User-Guide.md) · [简体中文](docs/CLI-User-Guide.zh-CN.md)

---

## 🛠️ 技术栈

| 技术                        | 版本           | 职责                      |
| ------------------------- | ------------ | ----------------------- |
| C#                        | 13.0         | GUI、CLI、业务流程与任务编排       |
| C++                       | C++20        | 底层媒体容器、元数据与实况照片协议处理     |
| .NET                      | 9.0          | 应用运行时                   |
| WinUI 3 / Windows App SDK | 1.8          | Windows 桌面界面            |
| CommunityToolkit.Mvvm     | 8.4.2        | MVVM 与状态管理              |
| LivePhotoBox.Native       | Stable C ABI | C# 与 Native C++ 之间的调用接口 |
| WIC                       | Windows      | 图片解码、编码与格式转换            |
| Windows Media Foundation  | Windows      | 视频探测、Remux 与转码          |
| PhotoSauce.MagicScaler    | 0.15.0       | 图片缩放与部分图像处理             |
| System.CommandLine        | 2.0.11       | CLI 命令行框架               |
| MSIX / Self-contained     | —            | GUI 与 CLI 打包分发          |


> 说明：当前已发布的部分版本仍使用上一代外部工具媒体处理实现。开发主线正在将这些能力迁移到 LivePhotoBox.Native，新的 Native 架构将在后续正式版本中替代旧实现。

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
| [FFmpeg](https://ffmpeg.org/) | 历史发布版本与独立验证参考 | LGPL/GPL |
| [ExifTool](https://exiftool.org/) | 历史发布版本与独立验证参考 | Perl |

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

访问次数：**2,483** ｜ 不重复访客：**209**（近 14 天） ｜ 仓库克隆：**2,110** ｜ 不重复克隆：**171**（近 14 天）

**热门来源（近 14 天）：** github.com · Bing · Google · chatgpt.com · t.co · doubao.com  
**热门内容（近 14 天）：** releases · releases/tag/v2.2.1 · README.zh-CN.md · issues

> 数据开始：2026-08-02 · 最后更新：2026-09-05 (UTC+8)
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

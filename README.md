![Live Photo Box Logo](LivePhotoBox/Assets/StoreLogo.png)

# 📦 Live Photo Box (实况照片工具箱)

*一款专为 Windows 打造的现代化 Apple 实况照片 (Live Photos) 管理与修复利器。*

[![Build Status](https://github.com/lengxiqwq/live-photo-box/actions/workflows/build.yml/badge.svg)](https://github.com/lengxiqwq/live-photo-box/actions)
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?color=0078D7&logo=github)](https://github.com/lengxiqwq/live-photo-box/releases)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078d7?logo=windows)
![Language C#](https://img.shields.io/badge/Language-C%23-239120?logo=csharp)
![Framework WinUI 3](https://img.shields.io/badge/Framework-WinUI%203-blue?logo=windows)
[![License](https://img.shields.io/github/license/lengxiqwq/live-photo-box)](LICENSE)

---

## 💡 项目简介

无论您是需要在 Windows 上对实况照片进行拆分与合成，还是为了解决实况照片在 Android 设备（如各类第三方相册、定制 ROM）上的兼容性和识别问题，**Live Photo Box** 都能为您提供一站式、高效率的批量处理解决方案。

本项目基于最新的 **WinUI 3 (Windows App SDK)** 构建，拥有契合 Windows 11 Fluent Design 的现代化界面与流畅的操作体验。

## ✨ 核心功能

* **📸 实况分离 (Split)**
  * 将实况照片一键拆分为独立的静态图片 (JPG/HEIC) 和动态视频 (MOV/MP4) 文件。
  * 方便在不支持实况格式的平台上分享或进行二次视频剪辑。
* **🔗 实况合成 (Combo)**
  * 将普通的静态图片与视频素材进行完美组合。
  * 底层自动写入对应的 EXIF 数据和 QuickTime 元数据（Asset Identifier），生成标准的实况照片格式。
* **🖼️ 封面修改 (Key Photo)**
  * 自由更改实况照片的封面图（关键帧）。
  * 支持从视频中提取特定帧，或上传自定义图片作为新封面，无损替换并保持实况属性。
* **🛠️ 元数据修复 (Repair)**
  * 深度修复实况照片损坏的元数据。
  * 自动校准图片和视频之间的 UUID 匹配问题，解决跨平台传输后无法正常识别、播放的痛点。
* **⚡ 强大的批量引擎 (Batch Processing)**
  * 内置多线程批量处理服务。
  * 所有功能均支持多选文件/文件夹的批量拖拽导入，极大提升处理效率。

## 📸 应用截图

*(提示：您可以在下面填入您应用的实际运行截图路径)*

![主页演示](路径/到/您的/截图1.png)
![分离合成功能演示](路径/到/您的/截图2.png)

## 🚀 下载与安装

### 发行版下载 (推荐)
1. 前往 [Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases)。
2. 下载最新版本的安装包 (`.msix` 或 `.zip`)。
3. 双击安装并运行。

### 运行环境要求
* **操作系统**: Windows 10 (版本 1809 及以上) 或 Windows 11。
* **运行时**: 需要系统安装有 [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) 运行时（通常内置于最新版 Windows 或在安装应用时自动部署）。

## 💻 编译与开发指南

如果您希望参与本项目的开发或自行编译代码：

1. **环境准备**: 
   * 安装 [Visual Studio 2022](https://visualstudio.microsoft.com/)。
   * 确保在 VS Installer 中勾选了 **.NET 桌面开发** 和 **通用 Windows 平台开发 (UWP)** 工作负载，并包含 Windows App SDK 组件。
2. **克隆仓库**:
   ```bash
   git clone [https://github.com/lengxiqwq/live-photo-box.git](https://github.com/lengxiqwq/live-photo-box.git)

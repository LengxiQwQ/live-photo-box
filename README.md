# 🍏 Live Photo Converter (WinUI 3)

![Platform](https://img.shields.io/badge/Platform-Windows%2011-blue?style=flat-square&logo=windows)
![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%7C%20Windows%20App%20SDK-blueviolet?style=flat-square)
![Language](https://img.shields.io/badge/Language-C%23-239120?style=flat-square&logo=c-sharp)

一个基于 **Windows 11 Fluent Design (WinUI 3)** 的现代化桌面工具。旨在将 Apple 设备拍摄的 Live Photo (JPG + MOV) 无损转换为 Google 相册原生支持的实况照片格式。

## ✨ 核心特性

- **🎨 现代 UI 设计**：完美契合 Windows 11 风格，支持 Mica (云母) 沉浸式背景、圆角卡片与深色模式。
- **🔄 智能镜像修复**：自动解析视频 `Display Matrix`，精准识别前置摄像头拍摄的镜像视频并进行物理重构。
- **💎 极致无损转换**：调用 FFmpeg 进行 1:1 像素级重构，锁定原始码率，保持原生动态帧率，注入 `hvc1` 标签以完美支持 Windows 硬件加速。
- **📦 双协议支持**：
  - **V2 现代模式** (`.MP.jpg`)：Google Motion Photo 协议（推荐）。
  - **V1 传统模式** (`MVIMG_`)：Google Micro Video 协议。
- **⚡ 异步批量处理**：支持一键批量扫描、配对与转换，实时流式输出处理日志。

## 🚀 快速开始 (开发者指南)

### 1. 环境要求
- Windows 10 (1809及以上) 或 Windows 11。
- [Visual Studio 2022](https://visualstudio.microsoft.com/)，并安装以下工作负载：
  - `.NET 桌面开发`
  - `Windows 应用程序开发` (需勾选 Windows App SDK C# 模板)

### 2. 准备依赖工具 (重要)
为了保持代码仓库的纯净，本项目没有包含庞大的第三方二进制文件。在编译运行前，请先准备以下工具：
1. 下载 [FFmpeg (包含 ffprobe)](https://ffmpeg.org/download.html) Windows 版。
2. 下载 [ExifTool](https://exiftool.org/) Windows 版（请将下载的 `exiftool(-k).exe` 重命名为 `exiftool.exe`）。
3. 在项目的 `LivePhotoConverter` 目录下新建一个名为 `Tools` 的文件夹。
4. 将 `ffmpeg.exe`、`ffprobe.exe` 和 `exiftool.exe` 放入 `Tools` 文件夹中。
5. 在 Visual Studio 中选中这三个文件，将它们的属性设置为：
   - **生成操作**: `内容 (Content)`
   - **复制到输出目录**: `如果较新则复制 (Copy if newer)`

### 3. 编译运行
在 Visual Studio 中打开 `.sln` 解决方案文件，按 `F5` 即可编译并运行。

## 📝 许可证
本项目基于 [MIT License](LICENSE) 开源。

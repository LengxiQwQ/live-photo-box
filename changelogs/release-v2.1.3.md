## ⚡ 优化 / Optimizations

- **📤 编辑页导出流程改进** — 三星 HEIC 重设封面、OPPO 原厂文件视频提取、单文件实况封面帧导出等流程优化（真机验证中）
  > Improved the edit-page export & cover pipeline — Samsung HEIC cover replacement, OPPO pure-video extraction, and still-frame export from single-file containers (real-device testing pending).

- **📱 vivo X300 合成调整** — 依据真实相机直出文件逆向调整合成结构，改善 vivo 相册兼容性（真机兼容性仍在测试中）
  > vivo X300 merging adjusted to match reverse-engineered camera output, aiming for better vivo Gallery compatibility (real-device testing still in progress).

## 🐛 修复 / Bug Fixes

- **📤 华为实况导出视频修复** — 修复华为实况导出视频损坏（此前定位偏移 60 字节导致视频损坏），已端到端验证通过
  > Fixed HUAWEI live photo video export being corrupted — previously the video was located 60 bytes off and the output was damaged; verified end-to-end.

- **💻 CLI 输出默认值与残留目录修复** — 单文件合成默认输出到照片所在目录（协议后缀命名，避免覆盖源图），批量默认输出到输入目录下 `{目录名}_{协议}` 子文件夹，不再落到终端当前目录；`-w` 可直接覆盖已存在文件；`--dry-run` 不再创建任何目录，批量不再遗留空 `Temp` 文件夹
  > CLI output-default & leftover-folder fixes — single-pair merges write next to the photo (protocol-suffixed name), batch merges into a `{folder}_<protocol>` subfolder, never the terminal's current directory; `-w` overwrites in place, `--dry-run` creates no folders, and batch no longer leaves an empty `Temp` folder.

> 📖 **CLI 使用指南 / CLI User Guide** → [English](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.3/docs/CLI-User-Guide.md) · [中文](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.3/docs/CLI-User-Guide.zh-CN.md)

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）⭐ 推荐
[⬇️ **Live-Photo-Box-v2.1.3-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.3/Live-Photo-Box-v2.1.3-x64-setup.exe)
> 一键安装，包含完整桌面应用 + 命令行工具 / Full app + CLI in one step.

#### 其他版本 / Other Packages

**📦 便携版 / Portable（GUI + CLI）** — 免安装  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.3/Live-Photo-Box-v2.1.3-x64-portable.zip"><small>⬇️ Live-Photo-Box-v2.1.3-x64-portable.zip</small></a>

**💻 命令行版 / CLI-only** — 仅脚本 / AI 调用  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.3/Live-Photo-Box-v2.1.3-x64-cli.zip"><small>⬇️ Live-Photo-Box-v2.1.3-x64-cli.zip</small></a>

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

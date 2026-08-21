## ✨ 新增功能 / New Features

- **🔔 系统通知** — 适配 Windows 系统通知，批量处理完成后弹出，点击可跳转到对应页面；通知频率与声音可在设置页自定义。
  
  > System toast notifications — adapted to Windows notifications, popping up after batch processing with one-click navigation to the page; frequency and sound customizable in Settings.
  
- **💻 CLI `cover` 命令** — 支持查看和自定义当前封面帧时间位置与信息；与 GUI 编辑页完全同步。
  
  > New `cover` command — view and customize the current key photo timestamp and information; fully in sync with the GUI editor page.
  
- **📊 任务栏全局进度条** — 适配系统任务栏图标实时显示，在扫描和队列任务生效，后台任务处理更直观。
  
  > Taskbar progress indicator — the system taskbar icon shows real-time progress, making background jobs easier to follow.
  
- **💡 交互式引导教程** — 合成页与拆分页新增操作引导气泡，可跟随提示逐步操作。
  
  > Interactive guided tutorial — new step-by-step tooltips on Merge and Split pages guide users.

## ⚡ 优化 / Optimizations

- **🖼️ 窗口布局记忆优化** — 重启软件后窗口宽度与高度恢复上次状态，不再重置；可在设置中调整。
  
  > Window layout memory improved — restarting the app restores the previous width and height instead of resetting; adjustable in Settings.
  
- **🎨 修复页状态栏统一风格** — 修复页底部状态栏重新设计，对齐合成/拆分页的配色与布局，三页风格一致。
  > Repair page status bar unified — the footer status bar redesigned to match the color scheme and layout of the Merge and Split pages, achieving a consistent look across all three.

- **🖱️ 合成/拆分中间拖拽** — 左右面板分隔线拖拽按比例分配空间，适应不同窗口尺寸。
  
  > Center splitter now uses proportional resizing — dragging the divider between left and right panels allocates space proportionally, adapting to different window sizes.
  
- **📝 其他细节优化** — 多余设置项清理；软件标识写入历史记录；GUI 与 CLI 日志补全。
  
  > Other refinements — cleaned up unused settings; Live Photo Box marker written to history records; log coverage improved for both GUI and CLI.

## 🐛 修复 / Bug Fixes

- **🛠️ 队列问题修复** — 修复任务识别跳过、暂停不生效、添加文件不自动设置输出目录等问题。
  
  > Queue issues fixed — task identification skipping, pause not working, output directory not auto-set when adding files, and other problems resolved.
  
- **🖼️ 灯箱播放修复** — 修复灯箱播放问题，各个协议实况照片预览更稳定。
  
  > Lightbox playback fixed — playback issues resolved for more reliable live photo previews across protocols.

---

## 📥 下载 / Download

> **x64** 架构（Windows 11 / 10 64 位）

### ⭐ 推荐 / Recommended

| 版本 / Version | 下载 / Download |
| --- | --- |
| **⚙️ 安装版 / Installer（GUI + CLI）** | ⬇️ [**Live-Photo-Box-v2.2.1-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.1/Live-Photo-Box-v2.2.1-x64-setup.exe) |
| **🪟 WinGet（CLI-only）** | `winget install LengxiQwQ.LivePhotoBox` |

> 📖 CLI 使用指南 / CLI User Guide: [English](https://github.com/LengxiQwQ/live-photo-box/blob/v2.2.1/docs/CLI-User-Guide.md) · [中文](https://github.com/LengxiQwQ/live-photo-box/blob/v2.2.1/docs/CLI-User-Guide.zh-CN.md)

#### 📁 其他版本 / Other Versions

| 版本 / Version | 下载 / Download |
| --- | --- |
| **📦 便携版 / Portable（GUI + CLI）** | ⬇️ [Live-Photo-Box-v2.2.1-x64-portable.zip](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.1/Live-Photo-Box-v2.2.1-x64-portable.zip) |
| **💻 命令行便携版 / CLI Portable** | ⬇️ [Live-Photo-Box-v2.2.1-x64-cli.zip](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.1/Live-Photo-Box-v2.2.1-x64-cli.zip) |

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)
> ⭐ 如果喜欢这个项目，欢迎点个 Star！
## ✨ 新增功能 / New Features

- **💻 命令行增强** — `merge` 命令可直接传入两个文件，按扩展名自动识别图片与视频，不再需要 `--image`/`--video`；新增 `--all-variants` 一键生成全部协议 × 格式组合
  
  > CLI improvements — `merge` now accepts two bare file arguments with image/video auto-detection, and the new `--all-variants` flag generates every protocol × format combo in one go.

> 📖 **CLI 使用指南 / CLI User Guide** → [English](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.2/docs/CLI-User-Guide.md) · [中文](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.2/docs/CLI-User-Guide.zh-CN.md)

## ⚡ 重构与优化 / Redesign & Optimizations

- **🖼️ 关于页内容重构** — 新增商店页面、更新日志、检查更新等快捷入口按钮，更新窗口改版（先出弹窗再检查，商店版可一键前往商店更新）
  > About page restructured — new Store page, changelog, and check-update shortcut buttons, plus a reworked update window (appears instantly, Store build can jump straight to the Store).

- **📦 安装包体积更小** — 示例照片不再随安装包内置，设置页新增「示例内容」（在线下载即将上线）
  
  > Smaller installer — sample photos are no longer bundled; Settings now has a Sample Content entry (online download coming soon).
  
- **🧹 协议设置精简** — 移除设置无用开关；华为协议统一为 v6_f 格式，移除鸿蒙 4.0 特例
  
  > Protocol settings simplified — a useless settings toggle was removed; HUAWEI is unified on the v6_f format with the HarmonyOS 4.0 special case removed.
  
- **其他细节优化** — 更正合成页输出格式选项标注，更新各格式兼容性提示文案
  > Minor tweaks — corrected the Merge page's output format option label and refreshed the format compatibility hints.

## 🐛 修复 / Bug Fixes

- **🌐 语言切换修复** — 应用内切换语言后，部分界面与功能不再错误地跟随系统语言（关于页链接、编辑页地理标签、合成页英文布局、资源加载）
  > Fixed UI and features still following the system language after switching in-app — About links, edit-page geolocation, Merge page English layout, and resource loading now respect the chosen UI language.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）
[⬇️ **Live-Photo-Box-v2.1.2-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.2/Live-Photo-Box-v2.1.2-x64-setup.exe)

### 📦 便携版 / Portable（GUI + CLI）
[⬇️ **Live-Photo-Box-v2.1.2-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.2/Live-Photo-Box-v2.1.2-x64-portable.zip)

### 💻 命令行版 / CLI-only
[⬇️ **Live-Photo-Box-v2.1.2-x64-cli.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.2/Live-Photo-Box-v2.1.2-x64-cli.zip)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

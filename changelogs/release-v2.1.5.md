## ✨ 新增功能 / New Features

- **📖 关于页新增「CLI 手册」** — 关于页新增「CLI 手册」按钮，应用内直接弹出命令行使用手册（
  
  > New "CLI Manual" button on the About page — opens the command-line user guide in-app (instant from the local copy, fetched from GitHub when missing).
  
- **📦 CLI 一键加入 PATH** — 便携版与命令行包内置 `add-to-path.cmd` / `remove-from-path.cmd`：双击即可把命令加入或移出用户 PATH，无需管理员权限
  > One-click CLI PATH setup — portable and CLI packages now ship `add-to-path.cmd` / `remove-from-path.cmd` to add or remove the commands from your user PATH with a double-click, no admin rights needed.

## ⚡ 优化 / Optimizations

- **⬆️ CLI 更新可靠性大幅提升** — 版本检查与下载失败自动重试 3 次，下载支持断点续传、实时进度条（百分比 + 速度）与 SHA256 完整性校验，自动重试耗尽后可按 R 交互重试
  > CLI update reliability — version checks and downloads auto-retry up to 3 times; downloads support HTTP range resume, a live progress bar with percent and speed, and SHA256 integrity verification; press R to retry interactively once automatic retries are exhausted.

- **🖼️ GUI 关于页安装渠道展示增强** — 安装方式按卸载器身份识别（商店 / Inno Setup / Scoop / 便携），并标注「含 CLI / 仅 GUI」；设置页更新提示区分「已是最新」与「正在运行预览版」且更醒目
  
  > Enhanced install-channel display on the About page — identity-based detection (Store / Inno Setup / Scoop / Portable) plus a "GUI + CLI / GUI only" marker; the Settings update message now distinguishes "up to date" from "running a preview" and is more prominent.

- **💻 CLI 命令别名精简至 4 个** — 移除冗余的 `lipbox` / `lpbx`，保留 `livephotobox` / `livebox` / `lpb` / `livephoto`；旧脚本用到被移除别名的，请改用 `lpb` 或 `livephotobox`
  > CLI command aliases trimmed to 4 — redundant `lipbox` and `lpbx` are gone, leaving `livephotobox` / `livebox` / `lpb` / `livephoto`; switch scripts to `lpb` or `livephotobox` if they used the removed ones.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）⭐ 推荐
[⬇️ **Live-Photo-Box-v2.1.5-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.5/Live-Photo-Box-v2.1.5-x64-setup.exe)

> 一键安装，包含完整桌面应用 + 命令行工具 / Full app + CLI in one step.

#### 其他版本 / Other Packages

**📦 便携版 / Portable（GUI + CLI）** — 免安装  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.5/Live-Photo-Box-v2.1.5-x64-portable.zip"><small>⬇️ Live-Photo-Box-v2.1.5-x64-portable.zip</small></a>

**💻 命令行版 / CLI-only** — 仅脚本 / AI 调用  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.5/Live-Photo-Box-v2.1.5-x64-cli.zip"><small>⬇️ Live-Photo-Box-v2.1.5-x64-cli.zip</small></a>

> 📖 **CLI 使用指南 / CLI User Guide: **  <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.5/docs/CLI-User-Guide.md">English</a> · <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.5/docs/CLI-User-Guide.zh-CN.md"> 中文</a>

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

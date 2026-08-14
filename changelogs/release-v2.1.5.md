## ⚠️ 重要提示 / Important

- **💻 纯 CLI 版改为单文件，旧版升级请手动重装一次** — 命令行工具从 200+ 散文件精简为单文件。纯 CLI / WinGet 升级不清除旧文件，请卸载重装（WinGet 先 `winget uninstall`）；便携版与安装版不受影响。
  
  > The standalone CLI is now a single file (down from 200+ loose files). Pure CLI / WinGet upgrades don't remove old files — reinstall once (WinGet: `winget uninstall` first). Portable & installer are unaffected.

---

## ✨ 新增功能 / New Features

- **📖 GUI 关于页新增「CLI 手册」** — 关于页新增「CLI 手册」按钮，点一下就能在应用里直接查看命令行工具的使用说明，不用再自己翻文档
  
  > New "CLI Manual" button on the About page — open the command-line user guide right inside the app.
  
- **📦 命令行工具一键加入系统 PATH** — 便携版和命令行包新增「加入 / 移除」脚本，双击一下就能在任何文件夹直接使用命令行工具，不用手动改系统设置，也不需要管理员权限
  
  > One-click CLI setup — portable and CLI packages now include add/remove scripts: double-click to use the commands from any folder, no manual setup or admin rights.
  
- **🔍 命令行查看支持范围更清楚** — `lpb protocols` 现在会标注每种格式支持哪些手机品牌、哪些还在测试中，一眼就能看明白
  
  > `lpb protocols` now marks which phone brands each format supports and what is still in testing, at a glance.
  
- **🏷️ WinGet 自动同步新版本** — 用 WinGet 安装的用户，每次发版后都会自动同步到 WinGet，一条命令就能装好或升级（安装命令见下方下载区）
  
  > WinGet auto-sync — releases now sync to WinGet automatically; one command installs or upgrades (see the Download section).

## ⚡ 优化 / Optimizations

- **⬆️ 更新更加稳定** — 检查更新和下载失败都会自动重试；下载中断能接着上次的进度继续，不用从头再来；下载完会自动检查文件是否完整，防止装到坏文件
  
  > More reliable updates — checks and downloads auto-retry, interrupted downloads resume where they left off, and every file is verified before installing.
  
- **🖼️ 安装方式识别更准确** — 关于页自动识别应用的安装方式（商店 / 安装包 / Scoop / WinGet / 便携版），并标注是否附带命令行工具；设置页的更新提示能区分「已是最新」和「正在使用预览版」
  
  > Smarter About & Settings — the About page detects how the app was installed (Store / installer / Scoop / portable) and marks whether the CLI is included; Settings now tells "up to date" from "running a preview".
  
- **💻 命令行多余别名减少** — 命令快捷名称精简到常用的 4 个（移除了不常用的 `lipbox` / `lpbx`）；`lpb info` 调整为更规范的 `lpb --info`，并新增显示安装位置与日志位置，遇到问题好排查
  
  > Smoother CLI — command names trimmed to the 4 common ones (removed `lipbox` / `lpbx`); `lpb info` becomes the cleaner `lpb --info`, which also shows your install and log locations for easier troubleshooting.

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

**🏷️ WinGet 安装 (CLI-only)**
⬇️ `winget install LengxiQwQ.LivePhotoBox`

> WinGet 更新略有延迟 / The WinGet update has a slight delay.
> 📖 CLI 使用指南 / CLI User Guide:  <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.5/docs/CLI-User-Guide.md">English</a> · <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.5/docs/CLI-User-Guide.zh-CN.md"> 中文</a>

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

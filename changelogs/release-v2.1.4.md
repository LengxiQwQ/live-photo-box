## ✨ CLI 新增功能 / CLI New Features

- **💻 `info` 命令与 `--version`** — 新增 `lpb info`：一条命令查看版本、构建日期、运行时、系统、作者联系方式，以及内置外部工具（exiftool、ffmpeg、jpegtran、heif-dec、heif-enc）的版本，末尾顺带检查更新；`lpb --version` 纯本地秒出，不联网、不启动子进程
  > New `lpb info` command and `--version` flag — version, build date, runtime, platform, author contact, and bundled tool versions (exiftool, ffmpeg, jpegtran, heif-dec, heif-enc) at a glance, plus a built-in update check; `--version` is fully local and instant.

- **⏱️ `--key-timestamp` 自定义封面时间** — merge 新增 `--key-timestamp`：指定封面在视频时间轴上的位置，支持 秒（1.5）、分:秒（1:30）、时:分:秒（0:01:30）写法；默认跟随源视频自带时间轴，自动适配各协议存储方式，可与 `--all-variants` 组合
  > New `--key-timestamp` merge option — set the key photo position on the video timeline in seconds, mm:ss or hh:mm:ss; defaults to the source video's own timeline, adapts to each protocol automatically, and works with `--all-variants`.

- **⬆️ 手动更新模式** — 此前 CLI 无法更新，现在 `lpb update-check` 可以检查新版本，并按安装方式提示如何更新（便携版下载解压覆盖、安装版重新安装、WinGet 安装运行 `winget upgrade`，WinGet 包审核中、上架后可用）
  > Manual update mode — the CLI previously couldn't update; now `lpb update-check` checks for new versions and tells you how to update based on install method (portable zip, setup.exe, or `winget upgrade` for WinGet-managed copies — the WinGet package is still pending review and becomes available once live).

## ⚡ CLI 优化 / CLI Optimizations

- **🎨 输出全面改版** — 交互式终端彩色显示：软件标题浅红、标签青色、数值与版本号黄色、路径绿色，成功/失败分别绿/红；输出重定向到文件/脚本或设置 `NO_COLOR` 时自动回退纯文本，不影响自动化使用
  
  > CLI output overhaul — colorized interactive terminals (light-red title, cyan labels, yellow values, green paths, green/red results); automatically falls back to plain text when output is redirected or `NO_COLOR` is set, so automation is unaffected.

## 🐛 修复 / Bug Fixes

- **🛠️ 批量并发合成临时文件冲突修复** — 每个任务使用独立临时工作区，批量并发处理同名文件时中间文件不再互相覆盖或残留
  
  > Fixed temp-file collisions during parallel batch merges — each task gets an isolated workspace, so same-named files no longer overwrite each other's intermediate files or leave leftovers.

> 📖 **CLI 使用指南 / CLI User Guide** → [English](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.4/docs/CLI-User-Guide.md) · [中文](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.4/docs/CLI-User-Guide.zh-CN.md)

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）⭐ 推荐
[⬇️ **Live-Photo-Box-v2.1.4-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.4/Live-Photo-Box-v2.1.4-x64-setup.exe)
> 一键安装，包含完整桌面应用 + 命令行工具 / Full app + CLI in one step.

#### 其他版本 / Other Packages

**📦 便携版 / Portable（GUI + CLI）** — 免安装  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.4/Live-Photo-Box-v2.1.4-x64-portable.zip"><small>⬇️ Live-Photo-Box-v2.1.4-x64-portable.zip</small></a>

**💻 命令行版 / CLI-only** — 仅脚本 / AI 调用  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.4/Live-Photo-Box-v2.1.4-x64-cli.zip"><small>⬇️ Live-Photo-Box-v2.1.4-x64-cli.zip</small></a>

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

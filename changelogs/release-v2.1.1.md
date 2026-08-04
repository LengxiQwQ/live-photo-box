## 💻 全新命令行模式 / New CLI Mode

这次更新最大的亮点：Live Photo Box 不再只是一款图形界面软件，它同时拥有了一个完整的**命令行工具** `livephotobox`。不用打开窗口，直接在终端就能把图片+视频合成为实况照片，方便脚本、批处理和 AI 自动化调用。

The biggest highlight: Live Photo Box is no longer just a GUI app — it now includes a full **command-line tool**, `livephotobox`. No window needed: merge image + video pairs into live photos right from the terminal, perfect for scripts, batch processing, and AI automation.

> ⚠️ **注意 / Note**：命令行模式目前**只支持合成（Merge）**，拆分（Split）和修复（Repair）请继续使用图形界面。  
> The CLI currently supports **merge only** — split and repair remain in the GUI.

> 📖 **使用方法 / How to use**  →  [English](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.1/docs/CLI-User-Guide.md)  ·  [中文](https://github.com/LengxiQwQ/live-photo-box/blob/v2.1.1/docs/CLI-User-Guide.zh-CN.md)

- **📦 独立命令行工具** — 新增 `livephotobox` 命令，支持单对合成（指定一张图片+一个视频）和整文件夹批量处理两种模式，终端实时显示进度与结果
  
  > A new standalone CLI, `livephotobox`, merges image+video pairs into live photos from the terminal — single-pair and whole-folder batch modes.
  
- **🏷️ 六种调用别名** — 同一程序提供 6 个别名（`livephotobox` / `livephoto` / `livebox` / `lipbox` / `lpb` / `lpbx`），全部等价，挑最短的用
  
  > Ships under 6 identical names — `livephotobox`, `livephoto`, `livebox`, `lipbox`, `lpb`, `lpbx` — pick your favorite.
  
- **📋 完整合成参数** — 协议、输出格式、自定义命名模板、配对方式（文件名/Apple CID/vivo ID）、并行数量、覆写、递归子目录、完成后自动移动或回收源文件，一应俱全，适合脚本和 AI Agent
  > Full merge options — protocol, format, naming templates, pairing (name/CID/vivo), parallelism, overwrite, recursive scan, and after-completion move/recycle — built for scripts and AI agents.

- **🔍 协议兼容查询** — `livephotobox protocols` 一条命令查看所有协议 × 格式组合是否可用，支持 `--json` 输出，方便程序解析
  > `livephotobox protocols` shows every protocol × format combo at a glance, with `--json` for programmatic use.

- **🔗 与 GUI 共用 100% 核心** — 命令行和图形界面共享同一套合成管线，协议逻辑只维护一份，修一个 bug 两端同时受益
  > The CLI and GUI share 100% of the merge pipeline — one codebase, both interfaces stay in sync.

## ✨ 新功能 / New Features

- **⏱️ 华为/荣耀实况照片播放与封面替换** — 编辑页新增华为 Moving Photo 支持，可正常预览播放，还能在时间轴上更换封面
  
  > HUAWEI/Honor Moving Photo now plays in the edit page, and you can replace the cover right from the timeline.
  
- **📤 HEIC + H.265 输出** — 新增 HEIC + MP4 (H.265/HEVC) 输出格式，华为原生编码，同等画质体积更小；源视频本身就是 HEVC 时直接无损拷贝，转得又快又不损失画质
  > New HEIC + H.265 output — HUAWEI-native HEVC encoding, smaller files at equal quality; HEVC sources are copied losslessly, fast and artifact-free.

## ⚡ 优化 / Optimizations

- **🧱 项目核心重构** — 项目拆分为共享核心库 + 图形界面 + 命令行三部分，核心协议逻辑只维护一份，GUI 与 CLI 同步更新、功能完全一致
  > Project restructured into a shared Core library + WinUI app + CLI — protocol logic lives in one place, kept in sync across both interfaces.

## 🐛 修复 / Bug Fixes

- **🛠️ 华为协议修复** — 修复华为实况照片 JPEG 与 HEIC 两种格式的合成，实测文件在华为/荣耀相册中可正常播放
  
  > Fixed HUAWEI Moving Photo merging for both JPEG and HEIC — verified to play correctly in HUAWEI/Honor galleries.

---

## ⚠️ 各家协议适配情况 / Protocol Compatibility

各品牌协议的合成与播放适配情况一览。
A quick look at merge & playback support across phone protocols:

| 协议 / Protocol                  | 支持设备 / Compatible Devices     | 状态 / Status    |
| -------------------------------- | --------------------------------- | ---------------- |
| ✅ **Apple - Live Photo**         | iPhone / iPad                     | 可用 / Supported |
| ✅ **Google - Micro Video (V1)**  | Windows / Xiaomi (MIUI) / Pixel   | 可用 / Supported |
| ✅ **Google - Motion Photo (V2)** | Windows / Xiaomi / Pixel          | 可用 / Supported |
| ✅ **OPPO - O-Live Photo**        | Windows / Xiaomi / OPPO / OnePlus | 可用 / Supported |
| ✅ **HUAWEI - Moving Photo**      | HUAWEI / Honor                    | 可用 / Supported |
| 🟡 **vivo - Live Photo**          | Windows / Xiaomi / vivo (X300+)   | 测试中 / Testing |
| 🟡 **Samsung - Motion Photo**     | Samsung                           | 测试中 / Testing |

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）

[⬇️ **Live-Photo-Box-v2.1.1-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.1/Live-Photo-Box-v2.1.1-x64-setup.exe)

### 📦 便携版 / Portable（GUI + CLI）
[⬇️ **Live-Photo-Box-v2.1.1-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.1/Live-Photo-Box-v2.1.1-x64-portable.zip)

### 💻 命令行版 / CLI-only
[⬇️ **Live-Photo-Box-v2.1.1-x64-cli.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.1/Live-Photo-Box-v2.1.1-x64-cli.zip)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

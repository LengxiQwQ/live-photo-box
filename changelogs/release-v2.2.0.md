## ✨ 新增功能 / New Features

- **🖼️ 全面支持 HDR 效果** — 转换实况照片协议时 HDR 效果不丢失；兼容 Apple 增益图（HEIC hdrgainmap）与 Google 增益图（Ultra HDR JPEG）等标准格式，双向转换。
  > Full HDR support — HDR survives protocol conversion; Apple gain maps (HEIC hdrgainmap) and Google gain maps (Ultra HDR JPEG) convert both ways.

## ✨ 拆分页大重构 / Split Page Redesign

- **🔗 拆分页全面重构** — 完全镜像合成页的面板布局，批量拆分与协议转换一站式完成：协议版本、输出格式、命名模板、覆盖策略、完成后源文件处理，风格与操作体验向合成页看齐。
  > Split page fully redesigned to mirror the Merge page — batch split and protocol conversion in one place, with protocol, output format, naming templates, overwrite policy, and after-completion actions, matching the Merge page's style and workflow.

- **📱 新增 Apple 实况照片协议** — 拆分后可输出 Apple 双文件（照片 + MOV），Apple 元数据已完全打通；受 iOS 系统限制，iPhone / iPad 无法直接导入，需通过爱思助手等第三方软件导入。
  > New Apple Live Photo protocol — split output can be written as Apple dual-file (photo + MOV) with fully paired metadata; due to iOS restrictions, importing to iPhone / iPad requires third-party tools such as 爱思助手 (i4Tools).

- **📱 vivo 实况照片协议（测试中，≤ X200）** — 可输出 JPG + MP4 双文件；作者暂无 vivo 手机，如果你有 vivo 手机且测试通过，欢迎反馈一下。
  > vivo Live Photo protocol (in testing, ≤ X200) — output as JPG + MP4 dual-file pairs; the author has no vivo device yet, so if you test it with a vivo phone and it works, please let us know.

- **📤 HEIC 无损拆分** — 10-bit 色深、HDR 增益图与原始元数据原样保留，不再重编码降质。
  > Lossless HEIC splitting — 10-bit depth, HDR gain map, and original metadata are preserved with no re-encoding quality loss.

## 💻 CLI 大幅增强 / CLI Enhancements

- **💻 新增 `split` / `repair` 命令** — 与 GUI 完全同步：拆分支持单文件 / 批量、Apple / vivo 协议输出、格式转换与全变体导出；修复支持照片方向、内嵌缩略图、HEIC 方向与视频旋转，含批量与预览。
  > New `split` and `repair` commands, fully in sync with the GUI — split handles single files and batches with Apple / vivo protocols, format conversion, and all-variants export; repair fixes rotation, embedded thumbnails, HEIC orientation, and video rotation, with batch and dry-run support.

- **📊 新增 `--json` 结构化输出** — merge / split / repair 均支持，结果以 JSON 返回，专为脚本与 AI 调用设计，解析稳定不受终端宽度影响。
  > New `--json` structured output for merge / split / repair — results are returned as JSON for scripting and AI automation, with stable parsing independent of terminal width.

- **🛠️ 报错信息大幅优化** — 未知选项与参数错误给出 "Did you mean" 建议和用法提示，不再整篇刷帮助；文件不存在、无权限、I/O 等异常显示友好错误并指引日志位置；更新失败也会说明网络超时或响应异常等具体原因，并提示重试或手动下载。
  > Much friendlier errors — unknown options and bad arguments show "Did you mean" suggestions plus usage hints instead of dumping the whole help page; file-not-found, access, and I/O issues become clear messages pointing to the log location; update failures now explain the cause (network timeout, unexpected response) and suggest retrying or downloading manually.

- **🧹 文件夹直接传参** — merge / split / repair 可直接传文件夹路径自动进入批量模式（等价 `-d`）；通配符不再误用，会给出明确提示。
  > Direct folder arguments — merge / split / repair accept a folder path and auto-switch to batch mode (same as `-d`); wildcards are no longer misparsed and get a clear message instead.

## ⚡ 优化 / Optimizations

- **🧹 融合协议暂时下线** — 融合是作者自创的统一协议，目标是把各品牌实况照片合并为一种格式、在任何设备上都能查看；实现难度较高，需要继续打磨，日后会和大家再次见面。
  
  > The Fusion protocol is temporarily retired — it's the author's own universal protocol aiming to merge all brands' live photos into one format viewable on any device; it still needs polishing and will return once ready.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### ⚙️ 安装版 / Installer（GUI + CLI）⭐ 推荐
[⬇️ **Live-Photo-Box-v2.2.0-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.0/Live-Photo-Box-v2.2.0-x64-setup.exe)
> 一键安装，包含完整桌面应用 + 命令行工具 / Full app + CLI in one step.

#### 其他版本 / Other Packages

**📦 便携版 / Portable（GUI + CLI）** — 免安装  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.0/Live-Photo-Box-v2.2.0-x64-portable.zip"><small>⬇️ Live-Photo-Box-v2.2.0-x64-portable.zip</small></a>

**💻 命令行版 / CLI-only** — 仅脚本 / AI 调用  
<a href="https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.2.0/Live-Photo-Box-v2.2.0-x64-cli.zip"><small>⬇️ Live-Photo-Box-v2.2.0-x64-cli.zip</small></a>

**🏷️ WinGet 安装 (CLI-only)**
⬇️ `winget install LengxiQwQ.LivePhotoBox`

> 📖 CLI 使用指南 / CLI User Guide:  <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.2.0/docs/CLI-User-Guide.md">English</a> · <a href="https://github.com/LengxiQwQ/live-photo-box/blob/v2.2.0/docs/CLI-User-Guide.zh-CN.md"> 中文</a>

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

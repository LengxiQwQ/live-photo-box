## 🔗 合成页史诗级重构 / Merge Page Epic Redesign

- **布局全面调整** — 现在左侧统一放置目录选择、协议选择、导出格式、文件命名等设置项，右侧为任务队列，操作流程更清晰
  > Full layout redesign: settings panel (directory, protocol, format, naming) on the left, task queue on the right — a cleaner, more intuitive workflow.

- **自定义文件命名** — 可以按模板自定义输出文件的命名规则，大批量处理时文件好找
  
  > Custom naming templates: define your own output file naming rules for easier batch management.
  
- **原始文件自动归类** — 合成完成后可把源文件自动移到指定文件夹，保持工作区清爽
  > Auto-organize source files: move originals to a designated folder after merging.

- **关于页面重构** — 页面布局优化，界面更清爽
  > About page redesigned: cleaner layout and refreshed look.

## 📱 兼容更多协议 / More Protocol Support

以前合成照片只能在少数几个软件里动起来，现在支持更多品牌手机的原生相册了！

- **vivo 手机** — 支持单文件（≥ X300 系列）和双文件两种实况照片格式，全面适配
  > New vivo Live Photo support — works with both single-file (X300 series and later) and dual-file formats.

- **三星手机** — 新增三星实况照片格式，支持 JPEG 和 HEIC 两种图片类型（待测试）

  > New Samsung Motion Photo support — works with both JPEG and HEIC images (pending testing).
  
- **华为/荣耀手机** — 新增华为实况照片格式，大部分环境可正常播放，封面主图位置可能存在读取异常
  > New HUAWEI Moving Photo support — plays in most cases; cover image position may not be read correctly in some scenarios.

- **融合模式**（作者自创） — 选这个模式，一份文件同时兼容 Google、OPPO、vivo、三星、小米等多品牌相册，甚至 Windows 照片应用也能动，发给谁都能播
  > Fusion Mode (author's original creation) — one file that works across Google, OPPO, vivo, Samsung, and Xiaomi galleries, plus the Windows Photos app. Share it with anyone and it just works.

- **编辑页同步适配** — 编辑页面新增对所有协议的支持，可正常预览和操作各品牌格式；OPPO 协议特有封面图和原始封面图两种显示模式；设置页新增拖拽时文件配对方式选项
  > Edit page updated: supports all new protocols for preview and editing; OPPO protocol now shows both cover frame and original photo; new setting for drag-and-drop file pairing method.

## 🐛 问题修复 / Bug Fixes

- **窗口标题** — 修复 Alt+Tab 切换时窗口标题显示文件名而不是应用名称的问题
  > Fixed Alt+Tab window title showing file name instead of app name.

- **反馈按钮** — 修复反馈按钮跳转到空白页面，现在直接跳转到 GitHub 反馈页
  > Fixed feedback button linking to a blank page — now directs users to the GitHub feedback page.

---

## ⚠️ 已知问题 / Known Issues

| 协议 / Protocol                  | 状态 / Status    |
| -------------------------------- | ---------------- |
| ✅ **Google - Micro Video (V1)**  | 可用 / Supported |
| ✅ **Google - Motion Photo (V2)** | 可用 / Supported |
| ✅ **OPPO - O-Live Photo**        | 可用 / Supported |
| 🟡 **vivo - Live Photo**          | 测试中 / Testing |
| 🟡 **Samsung - Motion Photo**     | 测试中 / Testing |
| 🟡 **HUAWEI - Moving Photo**      | 测试中 / Testing |

---

## ✨ 未来规划 / Roadmap

### 近期计划 / Near-term

- **拆分页面重构** — 参照合成页面布局，左侧设置 + 右侧队列，操作体验统一
  > Split page redesign: unified left-panel settings + right-panel queue layout, matching the new merge page experience.

- **教程页面更新** — 教程内容已落后，后续找时间翻新
  > Tutorial page refresh: current content is outdated and needs a rewrite.

- **命令行模式** — 支持通过 `--cli` 参数启动，在命令行直接控制软件；方便 AI Agent 等自动化工具批量调用
  > CLI mode: launch with `--cli` for command-line control, enabling automation by AI agents and scripts.

### 远期展望 / Long-term

- **智能分类页面** — 自动识别并分类实况照片，优先适配 Apple 设备格式
  > Smart categorization page: auto-detect and classify live photos, with priority support for Apple device formats.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v2.1.0-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.0/Live-Photo-Box-v2.1.0-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v2.1.0-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.1.0/Live-Photo-Box-v2.1.0-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

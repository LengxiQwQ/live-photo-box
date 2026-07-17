## ✨ 新增功能 / New Features

- **🖼️ 新增实况照片编辑页面** — 浏览照片、查看实况照片详情、更换封面，主要功能：
  
  - 大图预览：缩放、平移查看照片细节
  - 时间轴：自动提取视频帧，按时间排列成胶片条，单击任意帧预览
  - 导出帧：支持**导出当前选中帧**或**批量导出全部视频帧**
  - 封面替换：选中某一帧后一键替换为实况照片封面
  - 属性查看：显示照片详细信息（文件信息、实况照片协议类型、视频参数、EXIF 相机参数等）
  > New Live Photo Edit page: browse photos, view Live Photo details, replace cover. Features include zoom/pan viewer, filmstrip timeline, export single or all video frames, replace cover with selected frame, view EXIF properties.
  
- **📂 文件夹拖拽** — 现在所有页面（合成、拆分、修复、实况照片编辑）都支持直接把文件夹拖进去扫描，不用每次点按钮选目录
  > Folder drag-and-drop: now all pages (Merge, Split, Repair, Edit) support dragging folders directly to scan — no need to pick directory manually.

- **⏱️ 时间轴模式切换** — 设置页新增"实况照片编辑"分类，可以切换时间轴显示模式：经典列表或胶片模式
  
  > Timeline mode setting: new "Live Photo Edit" section in Settings lets you switch between classic list and filmstrip timeline modes.
  
- **📋 日志查看增强** — 设置页底部现在同时显示"本次日志"和"上一次日志"，可分别打开查看或导出到任意位置
  > Enhanced log viewer: Settings page now shows both current and previous session logs, each with open and export buttons.

## 🐛 修复 / Bug Fixes

- **🔧 修复 v1.15.2 / v1.15.3 安装包工具缺失问题** — 之前安装后发现除合成外其他功能都用不了，是因为外部工具没被打包进去。这个版本已彻底修好，所有功能恢复正常
  > Fixed critical packaging defect from v1.15.2/v1.15.3 — external tools were missing from releases, causing most features to fail. Now fully resolved.

- **🛠️ 修复工具检测弹窗闪退** — 部分电脑点"检测工具"时会直接崩溃，已修复
  > Fixed tool detection dialog crash on some devices.

- **🌐 修复便携版/安装版语言切换无效** — 自最初版本起，非 Microsoft Store 渠道（便携版、安装版）用户在设置中切换语言后界面无任何变化，此问题现已修复。无论通过何种方式安装，中英文切换均可正常生效
  > Fixed language switching not working in portable/installer versions — an issue present since the initial release. Language switching now works correctly across all installation methods.

- **🩹 实况照片编辑页面细节修复** — 修复了浏览、加载、导出流程中的一些小问题
  > Multiple edge-case fixes in LivePhotoEdit page browsing, loading, and export flows.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v2.0.0-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.0.0/Live-Photo-Box-v2.0.0-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v2.0.0-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v2.0.0/Live-Photo-Box-v2.0.0-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

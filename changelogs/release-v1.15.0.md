## 新增功能 / New Features

- **⬆️ 自动更新** — 便携版和安装版集成 GitHub Releases 自动更新检测，启动时静默检查，发现新版弹窗提示下载安装  
  > Auto-update: checks GitHub Releases on startup, prompts to download & install new versions.

- **🔗 实况照片配对增强** — 配对引擎新增组合匹配模式（GPS 位置 + 相机/设备型号 + iOS 版本），支持 5 种配对模式在设置中选择  
  > Enhanced pairing: new combined metadata matching (GPS + device model + iOS version) with 5 selectable modes in Settings.

- **🎨 合成协议选择器 UI 优化** — 下拉框展开后显示协议品牌副标题（Google/Apple/OPPO），收起时固定高度防布局抖动  
  > Merge protocol selector: shows brand subtitle on expand, fixed height to prevent layout jitter.

- **🛠️ 仅修复 Apple 照片** — 设置新增开关，开启后修复页面只扫描 Apple 设备（iPhone/iPad）拍摄的文件  
  > Apple-only repair filter: toggle in Settings to scan only Apple-device photos.

- **🔍 外部工具检测** — 设置页新增一键检测，验证 ExifTool / FFmpeg / jpegtran 是否可用  
  > External tool checker in Settings: verifies ExifTool, FFmpeg, and jpegtran availability.

## 修复 / Bug Fixes

- **日志路径一致性** — 修复非打包模式下日志路径使用 WinAppSDK 哈希路径的问题，统一为固定路径  
  > Fixed unpackaged-mode log path inconsistency — now uses a fixed path instead of WinAppSDK's composite hash.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v1.15.0-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.0/Live-Photo-Box-v1.15.0-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v1.15.0-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.0/Live-Photo-Box-v1.15.0-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

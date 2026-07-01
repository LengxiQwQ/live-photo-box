## 性能优化 / Performance

- **⚡ 启动速度大幅提升** — 硬件检测（WMI + FFmpeg 编码器扫描）从主线程移到后台异步执行，应用启动时间从 4-5 秒缩短至 1-2 秒；加双检锁防止并发重复检测浪费资源  
  > Startup time dramatically reduced: hardware detection (WMI + FFmpeg encoder scan) moved off the UI thread to background, cutting startup from ~4-5s to ~1-2s; double-check locking prevents redundant concurrent detection.

## 修复 / Bug Fixes

- **非打包模式多处修复** — 修复安装版/便携版下合成协议选择回弹、语言切换误判、设置读写类型不匹配、WebView2 权限导致更新日志白屏等一批仅在非 MSIX 模式触发的底层问题  
  > Fixed multiple unpackaged-mode bugs: ComboBox snapping, language switch misdetection, settings type mismatch, WebView2 blank screen in Program Files installs.

- **自动更新体验** — 安装版更新后自动重启应用、手动检查不再阻塞 3 天自动检查间隔、API 失败不记录时间确保下次可重试  
  > Auto-update improvements: app restarts after silent install, manual checks no longer reset auto-check interval, failed API calls allow retry.

- **卸载残留清理** — Inno Setup 卸载时自动删除日志、WebView2 缓存、运行时配置，不留垃圾文件  
  > Uninstaller now cleans up runtime data: logs, WebView2 cache, and config files.

- **安装包体积** — GitHub Actions 构建加入语言裁剪，不再携带 100+ 无用语言包  
  > CI builds now strip unused locale folders, reducing package size.

- **语言检测可靠性** — 非打包模式下改用 .NET CultureInfo 检测系统语言，WinRT API 不可用时不再失败  
  > Language detection now falls back to .NET CultureInfo when WinRT APIs are unavailable.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v1.15.1-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.1/Live-Photo-Box-v1.15.1-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v1.15.1-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.1/Live-Photo-Box-v1.15.1-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

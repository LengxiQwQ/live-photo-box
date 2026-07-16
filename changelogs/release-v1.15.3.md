## ⚠️ 重要警示 / Critical Warning ⚠️

**🚫 此版本存在严重打包缺陷，请勿下载使用！**

由于构建配置错误，外部命令行工具**全部未被封装进安装包和便携版压缩包**。软件在开发者本机可以通过系统 PATH 找到这些工具而正常运行，但在任何未预装这些工具的用户设备上，**除实况照片合成外的一切功能均将完全失效**。

**请回到 [v1.15.1](https://github.com/LengxiQwQ/live-photo-box/releases/tag/v1.15.1) 下载使用。** 该版本功能完整，未受此缺陷影响。

---

**🚫 This release has a critical packaging defect — DO NOT DOWNLOAD OR USE!**

Due to a build configuration error, all external command-line tools were **not bundled** into the installer or portable archive. On any device without these tools pre-installed, **every feature except Live Photo merging is completely broken**.

**Please use [v1.15.1](https://github.com/LengxiQwQ/live-photo-box/releases/tag/v1.15.1) instead.** That release is complete and unaffected.

---

## ✨ 新增功能 / New Features

- **💡 队列为空提示增加快捷引导** — 每个页面的空队列提示区新增引导小字，告知用户可点击右上角  查看详细教程，或点击  进入高级设置。中英文均支持

  > Empty queue hint now shows icon guides: click  for tutorials or  for advanced settings.

## ⚡ 优化 / Optimizations

- **🎬 启动首屏动画移除** — 软件刚启动进入首页时不再有从下往上的淡入动画，但侧栏切换页面仍然保留原有的过渡动画

  > Initial startup navigation now uses instant transition; sidebar navigation animations preserved.

- **🎯 设置页跳转定位修复** — 从各页面跳转至设置的对应分类时，改用 `ChangeView` 手动计算目标滚动位置，不再依赖 `BringIntoView` + `Loaded` 事件（缓存页 `Loaded` 不触发导致不滚动），每次跳转均能精确显示在"设置"标题正下方

  > Settings page scroll-to-section now uses `ChangeView` instead of `BringIntoView` + `Loaded` event, fixing unreliable positioning on cached page navigations.

- **📐 空队列提示区域居中优化** — 移除底部 60px 偏移，整个提示组（标题 + 说明 + 引导小字）在队列窗口中完美垂直居中

  > Empty queue hint area now perfectly centered vertically.

## 🐛 修复 / Bug Fixes

- **🔧 修复页空队列提示不消失** — 扫描目录后队列已有内容，但空队列占位提示文字仍显示。原因是 `RepairViewModel` 的 `FlushBuffer()` 遗漏了 `UpdateIsQueueEmpty()` 调用，导致 `IsQueueEmpty` 一直卡在 `true`。同步补上 Split/Merge ViewModel 中同样遗漏的 5 处调用

  > Fixed RepairPage empty-queue hint persisting after scan — missing `UpdateIsQueueEmpty()` calls in all three ViewModels' `FlushBuffer` / cancel / cleanup paths.

- **🔤 HistoryPage XAML 警告修复** — `Color` 类型缺少命名空间前缀，改用 `ui:Color` + `xmlns:ui="using:Windows.UI"`

  > Fixed `Color` type WMC0001 warnings in HistoryPage.xaml by adding explicit namespace.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v1.15.3-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.3/Live-Photo-Box-v1.15.3-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v1.15.3-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.3/Live-Photo-Box-v1.15.3-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

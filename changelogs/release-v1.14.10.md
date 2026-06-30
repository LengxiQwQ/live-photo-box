## 🎉 **首个公开发布版本** / First Public Release

<p>
  📋 更新日志 / changelog：<strong><a href="CHANGELOG.zh-CN.md">简体中文</a> &nbsp;·&nbsp; <a href="CHANGELOG.md">English</a></strong>
</p>


### 🧩 实况照片合成 / Merge Live Photo

将静态图片 + 视频素材组合为标准实况照片。

- 支持 `Micro Video V1` / `Motion Photo V2` / `O-Live Photo`（OPPO）等多种协议
- 自动写入完整 `EXIF` + `QuickTime` 元数据（`ContentIdentifier` / `UUID`）
- 智能配对引擎：通过 Apple 元数据中的 `ContentIdentifier`（`UUID`）精确匹配；无法匹配时自动降级为拍摄日期 ±2 秒容差匹配

### ✂️ 实况照片拆分 / Split Live Photo

一键拆分为图片和视频。

- 智能剥离 Google `XMP` 元数据，防止假阳性循环
- 按 `JPEG` 段结构逐段重建，不丢失 `EXIF` / `ICC` / 拍摄参数

### 🛠️ 实况照片修复 / Repair Live Photo

深度修复 iPhone 实况照片导出到 Windows 后的显示异常。

- 多余缩略图及横向拉伸 → `jpegtran` 无损旋转 + 剥离残留缩略图
- 前置摄像头视频旋转 → `FFmpeg` 重编码消除旋转矩阵
- `HEIC` 方向错误修正
- `ContentIdentifier` 丢失修复

### 🗂️ 其他功能 / Other Features

- 🖼️ **封面修改** / Replace Key Photo — 自由更换实况照片封面帧（开发中）
- 📂 **自动整理** / Photo Organize — 按设备 / 日期 / 类型归档（开发中）

---

### ⚡ 优化 / Optimizations

- **安装包体积大幅缩减** — 优化工具链依赖，安装包更小、下载更快
- **更丰富的自定义选项** — 新增深色/浅色/系统跟随主题、`Mica`/`Acrylic` 窗口背景效果、Banner 轮播自定义、硬件编码加速开关等设置项
- **全新现代化设置界面** — 重新设计的设置页面，外观、转码、合成、拆分、修复分类清晰
- **本地化精简** — 仅保留中文简体 + 英文，减少冗余

### 🩹 修复 / Bug Fixes

- **便携版启动崩溃** — 修复非打包模式下 `ModuleInitializer` 引导时序问题
- **安装包签名** — 完善安装程序签名流程

---

## 📥 下载 / Download

仅支持 **x64** 架构（适用于绝大多数 Windows 11 / 10 64 位电脑）。

### 📦 便携版 / Portable

[⬇️ **Live-Photo-Box-v1.14.10-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.14.10/Live-Photo-Box-v1.14.10-x64-portable.zip)

> 解压到任意文件夹 → 双击 `LivePhotoBox.exe` 即可运行。

### ⚙️ 安装版 / Installer

[⬇️ **Live-Photo-Box-v1.14.10-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.14.10/Live-Photo-Box-v1.14.10-x64-setup.exe)
> 双击运行安装程序，跟着指引下一步即可。

---

> 📖 完整功能介绍、使用教程和应用截图见 [README](https://github.com/lengxiqwq/live-photo-box#readme)  
> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

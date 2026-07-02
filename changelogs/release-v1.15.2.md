## 新增功能 / New Features

- **📋 修复页面新增选项对话框** — 点击"查看选项并修复"弹出对话框，按图片/视频分类展示可修复项并附数量统计，支持勾选需要的修复内容，默认全选；当前列表无对应项的自动变灰  

  > Repair page options dialog: choose which fixes to apply with per-category counts, auto-disables irrelevant options.

- **🔴 灯箱全面升级** — Live Photo 识别与播放（LIVE 按钮 + 弹簧动画）；图片智能淡入翻页（缓存命中瞬切）；视频播放控制栏（进度条拖动/点击跳转、暂停、音量、时间显示）；点击画面任意位置关闭灯箱；打开即显无迟滞  

  > Lightbox overhaul: Live Photo playback with spring-animated LIVE button; smart fade-in transitions (instant for cached images); custom video transport bar (drag/click to seek, play/pause, volume, time); tap anywhere to close; instant open with zero delay

- **🔝 一键回到顶部** — 修复/拆分/合成三个页面的队列右下角新增悬浮按钮

  > Scroll-to-top floating button on all three queue pages (Repair / Split / Merge). 

## 优化 / Optimizations

- **📦 安装包体积骤降 80%** — FFmpeg 从 97 MB 通用编译（gyan.dev full_build）瘦身至 5.8 MB 定制编译。精确保留项目所需的全部功能：x264/x265 CPU 软编码、AMD AMF / NVIDIA NVENC / Intel QSV 三大硬件编码、MJPEG 缩略图生成、AAC 音频编码、视频旋转/翻转/裁剪变换滤镜、硬件加速解码（CUDA/QSV/D3D11VA/VAAPI）。移除了上千个项目完全用不上的组件（Whisper 语音识别、AV1 编码、蓝光/DVD、流媒体协议、游戏音频解码、字幕渲染等）。详见 [`docs/FFmpeg-定制编译指南.md`](../docs/FFmpeg-定制编译指南.md)

  > Installer size slashed by 80%! FFmpeg custom-built from 97 MB down to 5.8 MB — see [`docs/FFmpeg-定制编译指南.md`](../docs/FFmpeg-定制编译指南.md)

## 修复 / Bug Fixes

- **🔑 GitHub Token 保存路径** — 非打包模式下 `appsettings.json` 从安装目录迁移到 `%LOCALAPPDATA%\LivePhotoBox\`，Token 重启不再丢失  

  > Fixed GitHub Token persistence in unpackaged mode.

- **🏷️ 修复页"已跳过"细分为具体原因** — 队列状态不再笼统显示"已跳过"，改为带括号写明原因：已跳过（非Apple照片）、已跳过（HEIC/HEIF）、已跳过（时长超过3.5秒）、已跳过（无需处理）。用户能一眼看出文件为什么被跳过  

  > Repair queue "Skipped" now shows specific reasons in parentheses: non-Apple, HEIC/HEIF, duration > 3.5s, no issue.

- **📊 修复选项弹窗统计修复** — 已跳过的文件不再计入修复选项对话框的数量统计，与实际上会被修复的文件数保持一致  

  > Repair options dialog counts now exclude skipped files, matching what will actually be repaired.

- **💥 关闭窗口崩溃修复** — 修正 `WorkViewModelBase.Cleanup()` 中 Token 取消与集合清理的顺序，先取消后台任务再清理资源，消除关闭时的访问违例崩溃 (0xc0000005)  

  > Fixed access violation crash on window close — cancel background tasks before clearing collections.

---

## 📥 下载 / Download

**x64** 架构（Windows 11 / 10 64 位）

### 📦 便携版 / Portable
[⬇️ **Live-Photo-Box-v1.15.2-x64-portable.zip**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.2/Live-Photo-Box-v1.15.2-x64-portable.zip)

### ⚙️ 安装版 / Installer
[⬇️ **Live-Photo-Box-v1.15.2-x64-setup.exe**](https://github.com/LengxiQwQ/live-photo-box/releases/download/v1.15.2/Live-Photo-Box-v1.15.2-x64-setup.exe)

> 🐛 反馈问题 → [Issues](https://github.com/lengxiqwq/live-photo-box/issues)  
> ⭐ 如果喜欢这个项目，欢迎点个 Star！

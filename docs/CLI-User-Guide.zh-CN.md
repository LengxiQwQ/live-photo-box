# Live Photo Box CLI — 使用指南

[![版本](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7)](https://github.com/lengxiqwq/live-photo-box/releases) [![许可证](https://img.shields.io/badge/许可证-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![平台](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![下载](https://img.shields.io/badge/下载-Releases-0078D7?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/releases) [![反馈](https://img.shields.io/badge/反馈-Issues-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## 概述

`livephotobox` 是一个命令行工具，用于将图片与视频文件合并为手机相册可识别的实况照片。实况照片是同时包含静态图像与短视频片段的单一文件，在支持的相册中查看时会自动播放视频。

目前 CLI 支持 **merge**（合成）、**protocols**（查看格式）和 **update-check**（检查更新）三个命令。拆分与修复请使用图形界面。

---

## 分发包说明

[Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 提供三种包：

| 包名 | 内容 | 适用场景 |
|---------|----------|----------|
| `*-x64-setup.exe` | GUI + CLI，安装向导一键安装 | 普通用户，想要完整桌面应用 |
| `*-x64-portable.zip` | GUI + CLI，解压即用 | U 盘便携使用，或不想安装时试用 |
| `*-x64-cli.zip` | 纯 CLI，不含 GUI 及其运行时 | 服务器、脚本、CI/CD，最小体积 |

三种包均包含相同的 `livephotobox.exe` 及六个别名。纯 CLI 包体积最小——省去了 WinUI 图形界面及其运行时（约 80 MB）。

### 如何更新

CLI 不含自动更新功能，但内置了版本检查命令：

```powershell
lpb update-check
```

该命令会查询 GitHub Releases API，将最新发布的版本与你当前安装的版本进行比较。如有新版本可用，会打印版本号和下载链接。

也可以手动访问 [Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases)，下载对应包并替换旧文件。

---

## 快速开始

```powershell
# 查看协议 × 格式兼容矩阵
lpb protocols

# 转换单个文件对（iPhone → Google 相册）
lpb merge photo.heic video.mov -p v2 -y

# 批量转换文件夹（→ 华为格式，自动确认）
lpb merge -d ./MyPhotos -p huawei -o ./Output -y

# 预览操作（不实际执行）
lpb merge -d ./MyPhotos --dry-run

# 一键生成所有协议 × 格式变体（测试/QA 用）
lpb merge photo.jpg video.mp4 --all-variants
```

---

## 可执行文件别名

工具以六个等价名称分发——挑最短的用：

| 别名 | 说明 |
|-------|-------------|
| `livephotobox` | 完整名称 |
| `livephoto` | 简写 |
| `livebox` | 紧凑形式 |
| `lipbox` | 替代拼写 |
| `lpb` | 首字母缩写 |
| `lpbx` | 缩写变体 |

```powershell
livephotobox protocols
lpb protocols
lipbox protocols
# 三条命令输出完全一致。
```

---

## 命令

### `protocols` — 查看格式兼容矩阵

```
lpb protocols
```

```
  Protocol          JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+H265
  ─────────         ────────   ────────   ────────   ────────   ────────
  Fusion               ✅          ✅          ──          ──          ──
  V1_MicroVideo        ✅          ✅          ──          ──          ──
  V2_MotionPhoto       ✅          ✅          ──          ✅          ──
  OPPO_OLive           ✅          ──          ──          ──          ──
  vivo_LivePhoto       ✅          ──          ──          ──          ──
  Samsung_MotionPhoto  ✅          ──          ✅          ──          ──
  HUAWEI_MovingPhoto   ✅          ──          ✅          ──          ✅
```

`✅` — 支持 &nbsp;|&nbsp; `──` — 不支持

`heic+mp4-h265`（索引 4）为华为原生 HEVC (H.265)。

**JSON 输出**（供脚本消费）：

```powershell
lpb protocols --json
```

---

### `merge` — 合成图片与视频

核心命令。两种运行模式：

| 模式 | 参数 | 使用场景 |
|------|------|----------|
| 单对合成 | `photo.jpg video.mp4`（自动识别） | 一张图片 + 一个视频 |
| 批量文件夹 | `-d` | 目录内自动按文件名配对 |

#### 使用示例

```powershell
# iPhone → Google 相册 (V2)
lpb merge IMG_001.HEIC IMG_001.MOV -p v2 -y

# → 华为原生 HEVC
lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y

# 批量 → 华为，输出至 ./Output，自动确认
lpb merge -d ./MyPhotos -p huawei -o ./Output -y

# 批量含子目录，保留文件夹结构
lpb merge -d ./Photos -r -s -p v2 -o ./Output -y

# 预览（不修改文件）
lpb merge -d ./Photos -p v2 --dry-run

# 自定义文件名模板
lpb merge -d ./Photos -p v2 -n "custom:{name}_{protocol}_{date}" -y
```

---

## 完整选项参考

```
lpb merge [options]

═══ 输入 ═══
  <图片> <视频>           图片 + 视频文件对，扩展名自动识别，顺序任意。
                             支持图片：.jpg .jpeg .heic .heif
                            支持视频：.mp4 .mov
  -d, --dir <文件夹>        扫描目录。同基础名称的文件自动配对。批量模式使用。
  -r, --recursive          扫描时包含所有子目录。
  --pairing <方式>          配对策略（仅批量模式）：
                             name  — 按文件名匹配（默认）
                             cid   — 按 Apple ContentIdentifier UUID 匹配
                             vivo  — 按 vivo 相机 livephoto ID 匹配

═══ 输出 ═══
  -o, --output <文件夹>     输出目录（默认：当前工作目录）。
  -w, --overwrite          覆盖已有文件。未指定时，文件名冲突将自动生成
                             带编号副本：photo.jpg → photo (2).jpg。
  -s, --preserve-subdirs   在输出目录中保留源文件的子目录结构。
  --after <操作>            合成成功后对源文件的操作（仅影响成功的文件对）：
                             none        — 保留不动（默认）
                             move:路径   — 移动到指定路径
                             recycle     — 移入 Windows 回收站

═══ 格式 ═══
  -p, --protocol <协议>     目标协议 [默认: v2]。
                             fusion  — 通用安卓
                             v1      — Google Micro Video（旧版）
                             v2      — Google Motion Photo（新版）
                             oppo    — OPPO / 一加 O-Live
                             vivo    — vivo 实况照片
                             samsung — 三星动态照片
                             huawei  — 华为 / 荣耀动态照片
                             运行 'lpb protocols' 查看完整兼容矩阵。

  -f, --format <格式>       输出容器（默认：指定协议的首个可用格式）。
                             jpg+mp4       — JPEG + H.264 MP4（兼容性最广）
                             jpg+mov       — JPEG + MOV（Apple 风格）
                             heic+mp4      — HEIC + H.264 MP4（需 HEIC 源文件）
                             heic+mov      — HEIC + MOV
                             heic+mp4-h265 — HEIC + H.265 MP4（华为原生 HEVC）

  -n, --naming <规则>       输出文件名规则 [默认: keep]。
                             keep           — 与源图片同名
                             suffix         — 追加协议后缀：photo → photov2
                             custom:模板     — 自定义模板，支持以下占位符：
                               {name}          源文件名
                               {protocol}      协议简称
                               {date}          当前日期 (yyyyMMdd)
                               {date:格式}     自定义日期，如 {date:yyyy-MM-dd}
                               {time}          当前时间 (HHmmss)
                               {exif_date}     照片拍摄日期（从文件读取）
                               {exif_time}     照片拍摄时间（从文件读取）
                               {counter}       自增编号 (001, 002, …)
                               {counter:D3}    定宽编号，如 D3 = 001

═══ 执行 ═══
  -j, --parallel <数量>     最大并行任务数（默认：CPU 核心数，上限 5）。
                             增大可提高吞吐量，同时增加 CPU 与磁盘 I/O 负载。
  -y, --yes                跳过所有确认提示。脚本自动化运行时的必要选项。
  --dry-run                仅列出计划操作，不实际处理文件。
  -v, --verbose            逐文件输出状态，而非仅显示汇总。
  --all-variants           生成所有协议 × 格式组合（仅单对模式）。
                             输出到 {目录}/{文件名}_variants/，文件名为 {文件名}_{协议}_{格式}.ext。
```

### `--all-variants` — 一键生成所有变体

无需逐个指定 `-p` / `-f`，一次性生成 7 个协议、14 种格式组合的实况照片，适合开发者快速验证所有协议的输出质量。

```powershell
# 输出到输入文件所在目录的 {name}_variants/ 下
lpb merge photo.jpg video.mp4 --all-variants

# 指定输出目录
lpb merge photo.jpg video.mp4 --all-variants -o ./Out
```

输出：`photo_variants/`（或指定目录下的 `photo_variants/`）生成 14 个文件：
```
photo_Fusion_JPEG+MP4.jpg
photo_Fusion_JPEG+MOV.jpg
photo_V1_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

注意：
- 仅支持单对模式，不支持 `--dir` 批量模式
- 命名固定，不接受 `--naming` / `--protocol` / `--format` 选项
- 输出文件名中的 `(H.265)` 括号和空格在 Windows 上是合法字符，不影响使用

---

### `update-check` — 检查更新

```
lpb update-check
```

查询 GitHub Releases API，比较当前版本与最新发布版本。

输出示例（当前已是最新）：
```
Current version : 2.1.1
Checking GitHub ... OK
Latest version  : 2.1.1

You are running the latest version.
```

输出示例（有新版本）：
```
Current version : 2.1.0
Checking GitHub ... OK
Latest version  : 2.1.1

A newer version is available: v2.1.1
  Live Photo Box v2.1.1
  Download: https://github.com/lengxiqwq/live-photo-box/releases/tag/v2.1.1
```

需要网络连接。失败时（超时、GitHub 不可达）会打印手动下载地址并以退出码 2 退出。

---

## 配对方式

批量模式 (`-d`) 下，工具需要将图片与视频一一对应。以下三种策略可选。

### `name`（默认）

基础名称相同、扩展名不同的文件自动配对。

```
photo_001.jpg  +  photo_001.mp4  →  已配对
photo_002.heic +  photo_002.mov  →  已配对
IMG_1234.jpg   +  VID_1234.mp4   →  未配对（基础名称不同）
```

### `cid` — Apple ContentIdentifier

Apple 实况照片的 `ContentIdentifier` 元数据中包含一个 UUID。两个文件 UUID 一致即配对，与文件名无关。

```
IMG_0001.HEIC  +  IMG_0001.MOV   →  按文件名配对
IMG_0002.HEIC  +  renamed.MOV    →  按 CID 配对（UUID 一致）
```

需要 `exiftool.exe` 位于可执行文件旁的 `Tools\` 目录中。所有分发包均自带该工具。

### `vivo` — vivo 相机 ID

vivo 设备在 JPEG 尾部字节和 MP4 元数据中均写入了 `com.android.camera.livephoto` 标识。ID 匹配即配对。

```
vivo_photo.jpg  +  vivo_video.mp4  →  按 vivo ID 配对
```

无需外部工具——纯文件 I/O。

---

## 命名模板速查

| 目的 | 模板 | 输出示例 |
|------|----------|----------------|
| 保持原名 | `-n keep`（或省略 `-n`） | `IMG_001.jpg` |
| 追加协议后缀 | `-n suffix` | `IMG_001huawei.jpg` |
| 文件名 + 日期 | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| 协议作子目录 | `-n "custom:{protocol}/{name}"` | `huawei/IMG_001.jpg` |
| 顺序编号 | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |
| 完整元数据 | `-n "custom:{name}_{protocol}_{date}_{time}"` | `IMG_001_huawei_20260803_143022.jpg` |

---

## 完成后操作

```powershell
# 归档源文件
lpb merge -d ./Photos -p v2 --after "move:./Archived" -y

# 移入回收站
lpb merge -d ./Photos -p v2 --after recycle -y

# 保留源文件（默认）
lpb merge -d ./Photos -p v2 --after none -y
```

仅**合成成功**的文件对的源文件会受影响。

---

## 工作流示例

```powershell
# iPhone → Google 相册
lpb merge IMG_1234.HEIC IMG_1234.MOV -p v2 -y

# iPhone → 华为（原生 HEVC）
lpb merge IMG_1234.HEIC IMG_1234.MOV -p huawei -f heic+mp4-h265 -y

# 批量转换为通用安卓格式
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y

# 递归批量 + 保留目录结构 + 归档源文件
lpb merge -d ./Photos -r -s -p v2 -o ./Output --after "move:./Originals" -y

# 脚本批处理 + 错误日志
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "部分文件失败，详见 errors.log" }
```

---

## 协议兼容性一览

| 协议 | 兼容设备 | 状态 |
|----------|--------------------|--------|
| Apple Live Photo | iPhone / iPad | 可用 |
| Google Micro Video (V1) | Windows / 小米 (MIUI) / Pixel | 可用 |
| Google Motion Photo (V2) | Windows / 小米 / Pixel | 可用 |
| OPPO O-Live Photo | Windows / 小米 / OPPO / 一加 | 可用 |
| 华为 Moving Photo | 华为 / 荣耀 | 可用 |
| vivo Live Photo | Windows / 小米 / vivo (X300+) | 测试中 |
| Samsung Motion Photo | 三星 | 测试中 |

---

## 退出码

| 退出码 | 含义 |
|:---:|---------|
| 0 | 全部任务成功完成 |
| 1 | 参数错误，或至少有一个文件对失败 |
| 130 | 用户取消 (Ctrl+C) |

---

## 架构

CLI 与桌面 GUI 应用共享同一合成管线，逻辑定义在 `LivePhotoBox.Core` 类库中：

```
LivePhotoBox.Core        ← 协议逻辑、HEIC 转换、视频转码
    ↑               ↑
    │               │
Live Photo Box    LivePhotoBox.CLI
(WinUI 桌面端)     (控制台 CLI)
```

两者调用同一 `LivePhotoMergeRunnerService.ProcessSinglePairAsync()` 方法。对 Core 的任何修复或更新均同时作用于两个界面。

CLI 界面为纯英文，所有字符串编译时嵌入 `LivePhotoBox.Core.dll`，运行时无需额外语言文件。

---

## 故障排查

### 提示未知协议
运行 `lpb protocols` 查看所有有效协议名称及缩写别名。

### 所选格式不适用于该协议
运行 `lpb protocols` 查看兼容矩阵。例如，`heic+mp4-h265` 仅可用于 `huawei`。

### 使用 `--pairing cid` 时提示找不到 exiftool
CID 配对需要 `exiftool.exe` 位于可执行文件旁的 `Tools\` 目录中。所有分发包均自带该工具。

### 输出文件扩展名与源文件不一致
正常现象。源文件为 HEIC 且选择了 JPEG 类格式时，输出使用 `.jpg` 扩展名。内部结构符合所选协议要求。

### 提示"Permission denied"或文件被占用
关闭正在访问源文件的相册 App 或文件管理器。被其他进程锁定的文件无法在 Windows 上读取或移动。

---

## 获取帮助

- **文档：** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug 反馈 / 功能建议：** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **最新版本下载：** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **项目仓库：** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

如果这个项目对你有帮助，欢迎在 GitHub 上点个 ⭐ Star。

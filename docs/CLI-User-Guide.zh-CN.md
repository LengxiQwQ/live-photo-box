# Live Photo Box CLI — 使用指南

[![版本](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7)](https://github.com/lengxiqwq/live-photo-box/releases) [![许可证](https://img.shields.io/badge/许可证-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![平台](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![下载](https://img.shields.io/badge/下载-Releases-0078D7?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/releases) [![反馈](https://img.shields.io/badge/反馈-Issues-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## 概述

`livephotobox` 是一个命令行工具，用于将图片与视频文件合并为手机相册可识别的实况照片。实况照片是同时包含静态图像与短视频片段的单一文件，在支持的相册中查看时会自动播放视频。

目前 CLI 支持 **merge**（合成）、**protocols**（查看格式）、**info**（查看版本与环境信息）、**update-check**（检查更新）和 **update**（检查并自动更新）五个命令。拆分与修复请使用图形界面。

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

CLI 采用手动触发更新（不后台自动升级）。一条命令完成检查 → 询问 → 下载替换：

```powershell
lpb update
```

发现新版本时会先打印安装类型、版本号和手动下载链接，然后询问 `Update now? [Y/n]`——回车或输入 `y` 即自动下载新版并替换（便携版后台替换 / 安装版静默重装）；输入其它内容跳过；管道/CI 等无输入环境自动跳过，不会卡住。脚本环境可用 `lpb update -y`（或 `--yes`）跳过确认。

`lpb update` 会自动区分当前副本类型并选择对应安装包：

- **Portable CLI-only（纯 CLI）**：更新 `*-x64-cli.zip`
- **Portable (GUI + CLI)（GUI + CLI 便携包）**：更新 `*-x64-portable.zip`
- **Inno Setup (GUI + CLI)（安装版）**：下载新版 `*-x64-setup.exe` 静默重装

只想检查不更新，或想先看结果再决定：

```powershell
lpb update-check
```

`update-check` 发现新版本时会打印下载链接并提示运行 `lpb update -y`。手动下载链接始终会显示，也可以自行从 [Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 下载替换。

> 如果你的副本由 WinGet 安装管理，`lpb update` / `lpb update-check` 会显示 `This copy is installed and managed by WinGet`，提示改用 winget 更新，内置更新对此类安装不可用。

---

## 快速开始

```powershell
# 查看协议 × 格式兼容矩阵
lpb protocols

# 查看版本 / 环境信息（纯本地，无需联网）
lpb --version
lpb info

# 转换单个文件对（iPhone → Google 相册）
lpb merge photo.heic video.mov -p v2 -y

# 批量转换文件夹（→ 华为格式，自动确认；输出到 ./MyPhotos/MyPhotos_huawei/）
lpb merge -d ./MyPhotos -p huawei -y

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
  Merge — protocol × format compatibility

  Protocol              JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+MP4(H.265)
  ────────────────────── ────────   ────────   ────────   ────────   ──────────────
  Fusion (testing)         ✅          ✅          ✖️          ✖️          ✖️
  Micro Video              ✅          ✅          ✖️          ✖️          ✖️
  Motion Photo             ✅          ✅          ✖️          ✅          ✖️
  OPPO O-Live              ✅          ✖️          ✖️          ✖️          ✖️
  vivo Live Photo          ✅          ✖️          ✖️          ✖️          ✖️
  Samsung Motion Photo     ✅          ✖️          ✅          ✖️          ✖️
  HUAWEI Moving Photo      ✅          ✖️          ✅          ✖️          ✅
```

`✅` — 支持 &nbsp;|&nbsp; `✖️` — 不支持

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
# iPhone → Google 相册
lpb merge IMG_001.HEIC IMG_001.MOV -p v2 -y

# → 华为原生 HEVC
lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y

# 批量 → 华为，自动确认（默认输出到 ./MyPhotos/MyPhotos_huawei/）
lpb merge -d ./MyPhotos -p huawei -y

# 批量 → 华为，显式指定输出目录
lpb merge -d ./MyPhotos -p huawei -o ./Output -y

# 批量含子目录，保留文件夹结构
lpb merge -d ./Photos -r -s -p v2 -o ./Output -y

# 预览（不修改文件，也不创建任何文件夹）
lpb merge -d ./Photos -p v2 --dry-run

# 自定义文件名模板
lpb merge -d ./Photos -p v2 -n "custom:{name}_{protocol}_{date}" -y

# 直接覆盖已存在的输出，而不是自动重命名为 " (2)"
lpb merge photo.jpg video.mp4 -p huawei -y -w

# 单文件合成：自定义封面在视频中的位置（视频 2.5 秒处）
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y
```

---

### `info` — 查看版本与环境信息

`--version` 仅使用本地数据快速打印版本横幅——不联网、不启动子进程：

```
lpb --version
```

```
Live Photo Box v2.1.4

Build date : 2026-08-11
Runtime    : .NET 9.0.18 (X64)
Platform   : Microsoft Windows 10.0.26200 (X64)
Channel    : Portable CLI-only
Project    : https://github.com/lengxiqwq/live-photo-box
License    : GPL-3.0
Author     : LengxiQwQ (冷汐OωO)
Email      : lengxiowo@gmail.com
QQ         : 3197635836
Feedback   : https://github.com/lengxiqwq/live-photo-box/issues

Tip        : run 'lpb info' for full details

© 2026 LengxiQwQ · Licensed under GPL-3.0
```

`Channel` 显示当前副本的安装方式：纯 CLI 解压为 `Portable CLI-only`，GUI + CLI 便携包为 `Portable (GUI + CLI)`，安装版（setup.exe）为 `Inno Setup (GUI + CLI)`，WinGet 安装为 `WinGet`。

`info` 在相同字段基础上，追加内置外部工具（exiftool、ffmpeg、jpegtran、heif-dec、heif-enc）的版本信息，并在末尾顺带做一次 GitHub 更新检查（与 `update-check` 同一套逻辑）。网络不可用时会在原地提示失败，不会导致命令失败：

```
lpb info
```

```
Live Photo Box v2.1.4 — full environment info

Build date : 2026-08-11
Runtime    : .NET 9.0.18 (X64)
Platform   : Microsoft Windows 10.0.26200 (X64)
Channel    : Portable CLI-only
Project    : https://github.com/lengxiqwq/live-photo-box
License    : GPL-3.0
Author     : LengxiQwQ (冷汐OωO)
Email      : lengxiowo@gmail.com
QQ         : 3197635836
Feedback   : https://github.com/lengxiqwq/live-photo-box/issues

External tools:
exiftool  13.59   ...\Tools\exiftool.exe
ffmpeg    n8.0.1  ...\Tools\ffmpeg.exe
jpegtran  n/a     ...\Tools\jpegtran.exe
heif-dec  1.23.1  ...\Tools\heif-dec.exe
heif-enc  1.23.1  ...\Tools\heif-enc.exe

Update check:
Checking GitHub ... OK

A newer version is available: v2.1.2 → v2.1.3
https://github.com/lengxiqwq/live-photo-box/releases

To update automatically:
lpb update -y

© 2026 LengxiQwQ · Licensed under GPL-3.0
```

工具路径显示为绝对路径，版本值来自内置工具本身；工具缺失时显示 `not found`，不会导致命令失败。

在交互式终端中，标签与数值会着色显示（软件标题浅红色、标签青色、数值/版本号黄色、提示文字海蓝色、`✅` 绿色、`✖️` 表示不支持）。当输出被重定向到文件/脚本，或设置了 `NO_COLOR` 环境变量时，全部自动回退为纯文本。

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
  --key-timestamp <时间>   指定封面在视频时间轴上的位置，仅单文件模式。
                             支持：秒（1.5）、分:秒（1:30）、时:分:秒（0:01:30）。
                             默认：跟随源视频自带时间轴（Apple MOV / vivo 元数据）。
                             批量模式（-d）下不可用。

═══ 输出 ═══
  -o, --output <文件夹>     输出目录。省略时的默认值：
                             • 单文件 → 照片（图片）所在目录（就在照片旁边输出）
                             • 批量   → {输入目录}/{输入目录名}_{协议后缀}/ 子文件夹
                             传 -o 可覆盖；目录会自动创建。
  -w, --overwrite          直接覆盖已存在的输出文件。未指定时，文件名冲突将
                             自动生成带编号副本：photo.jpg → photo (2).jpg。
  -s, --preserve-subdirs   在输出目录中保留源文件的子目录结构。
  --after <操作>            合成成功后对源文件的操作（仅影响成功的文件对）：
                             none        — 保留不动（默认）
                             move:路径   — 移动到指定路径
                             recycle     — 移入 Windows 回收站

═══ 格式 ═══
  -p, --protocol <协议>     目标协议 [默认: motion photo]。
                             fusion  — 通用安卓
                             micro video（别名 v1）— Google Micro Video（旧版）
                             motion photo（别名 v2）— Google Motion Photo（新版）
microvideo / motionphoto（无空格别名）同样可输入。
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

  -n, --naming <规则>       输出文件名规则。默认：单文件=suffix，批量=keep。
                             keep           — 与源图片同名（批量默认）
                             suffix         — 追加协议简称：photo → photomotionphoto（单文件默认）
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

### 默认输出位置

省略 `-o` 时，输出**不会**落到终端当前目录，而是跟随**输入**：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件对 | **照片（图片）所在目录**（照片和视频可能在不同文件夹，以照片为准） | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| 批量（`-d`） | 输入目录下的子文件夹，命名为 `{输入目录名}_{协议后缀}` | `lpb merge -d ./MyPhotos -p v2` → `./MyPhotos/MyPhotos_motionphoto/` |

- 文件夹/文件名均为英文：`MyPhotos_huawei/`、`IMG_001huawei.jpg`。
- 单文件对默认命名为 `{源文件名}{协议后缀}`（如 `IMG_001motionphoto.jpg`），不会覆盖源照片。
- 批量文件名保持源名不变——协议后缀体现在**文件夹名**上。
- `--dry-run` 会打印解析出的输出路径，且**不创建任何文件夹**。

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
photo_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

注意：
- 仅支持单对模式，不支持 `--dir` 批量模式
- 命名固定，不接受 `--naming` / `--protocol` / `--format` 选项
- 支持 `--key-timestamp`，所有变体应用同一时间戳
- 输出文件名中的 `(H.265)` 括号和空格在 Windows 上是合法字符，不影响使用

---

### `--key-timestamp` — 自定义封面在视频中的位置

单文件合成时，实况照片的元数据会记录**封面（key photo）在视频时间轴上的位置**。默认情况下工具会跟随源视频自带的时间轴（如 Apple MOV 的封面时间、vivo 元数据）；指定本参数后则使用你给的值。

```powershell
# 封面位于视频第 2.5 秒处
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y

# 也支持 分:秒 / 时:分:秒 写法
lpb merge photo.jpg video.mp4 -p v2 --key-timestamp 1:30.500 -y
```

- 时间格式：秒（`1.5`）、分:秒（`1:30`）、时:分:秒（`0:01:30`），内部按微秒写入各协议元数据。
- 仅单文件模式可用；批量模式（`-d`）传该参数会直接报错退出。
- 各协议存储方式不同，工具已自动适配：
  - Motion Photo / OPPO / vivo / Samsung / Fusion → 写入 XMP（OPPO / Fusion 同时写主照片时间戳字段）
  - Micro Video → 写入 XMP 的 `MicroVideoPresentationTimestampUs`
  - HUAWEI → 不写 XMP，而是写入 MP4 `covertime` 元数据 + 文件尾包封面帧号
- 可与 `--all-variants` 组合，所有变体使用同一时间戳。
- 超出视频时长的值：HUAWEI 输出会自动钳制到最后一帧；其余协议直接写入元数据，由播放器自行处理。

---

### `update-check` — 检查更新

```
lpb update-check
```

查询 GitHub Releases API，比较当前版本与最新发布版本。`--version` 完全本地运行；`info` 会在其报告中顺带执行同样的检查。需要自动下载替换请使用 `lpb update`。

输出示例（当前已是最新）：
```
Checking GitHub ... OK

You are running the latest version.
```

输出示例（有新版本）：
```
Checking GitHub ... OK

A newer version is available: v2.1.0 → v2.1.1
https://github.com/lengxiqwq/live-photo-box/releases

To update automatically:
lpb update -y
```

需要网络连接。失败时（超时、GitHub 不可达）不会显示版本信息，直接打印失败原因与手动下载地址，并以退出码 2 退出。

如果当前副本是通过 WinGet 安装的（portable 包），内置更新会被禁用：
```
Checking GitHub ... skipped

This copy is installed and managed by WinGet.
Built-in update is disabled for WinGet-managed installs.
Update with: winget upgrade LengxiQwQ.LivePhotoBox
```
此时请使用 `winget upgrade LengxiQwQ.LivePhotoBox` 更新，命令以退出码 3 退出。

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
| 保持原名 | `-n keep` | `IMG_001.jpg` |
| 追加协议后缀 | `-n suffix` | `IMG_001huawei.jpg` |
| 文件名 + 日期 | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| 协议作子目录 | `-n "custom:{protocol}/{name}"` | `huawei/IMG_001.jpg` |
| 顺序编号 | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |
| 完整元数据 | `-n "custom:{name}_{protocol}_{date}_{time}"` | `IMG_001_huawei_20260803_143022.jpg` |

> **说明：** 省略 `-n` 时，**单文件**合成默认 `suffix`（输出在照片原目录，加协议后缀避免覆盖源照片）；**批量**合成默认 `keep`（输出进独立子文件夹，文件名不变）。显式传 `-n` 始终以你为准。

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
| Fusion Motion Photo | Windows / Android（通用） | 测试中 |
| Apple Live Photo | iPhone / iPad | 可用 |
| Google Micro Video | Windows / 小米 (MIUI) / Pixel | 可用 |
| Google Motion Photo | Windows / 小米 / Pixel | 可用 |
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

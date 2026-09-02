# Live Photo Box CLI — 使用指南

[![最新发布](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=%E6%9C%80%E6%96%B0%E5%8F%91%E5%B8%83)](https://github.com/lengxiqwq/live-photo-box/releases) [![许可证](https://img.shields.io/badge/许可证-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![平台](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![项目仓库](https://img.shields.io/badge/%E9%A1%B9%E7%9B%AE%E4%BB%93%E5%BA%93-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![反馈](https://img.shields.io/badge/反馈-Issues-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## 概述

Live Photo Box 同时提供图形界面与命令行两种形态。命令行入口 `livephotobox`（别名 `lpb`）专为脚本、AI 自动化调用设计；如需日常人工操作，请使用图形界面版本：[Microsoft Store](https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full) 或 [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)。

---

## 安装

[Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 提供四种安装方式：

| 方式 | 安装 | 内容 | PATH |
|------|------|------|------|
| WinGet | `winget install LengxiQwQ.LivePhotoBox` | 仅 CLI | 自动加入，无需手动 |
| 安装版 | 运行 `*-x64-setup.exe` | GUI + CLI | 安装时可选加入，无需手动 |
| 便携版 | 解压 `*-x64-portable.zip` | GUI + CLI | 需手动添加 |
| 纯 CLI | 解压 `*-x64-cli.zip` | 仅 CLI | 需手动添加 |

所有包均包含相同的 `livephotobox.exe` 及四个别名。WinGet 与安装版在安装时自动配置 PATH，无需手动添加；仅便携版与纯 CLI 包需手动添加（见下文）。WinGet 安装的副本由 WinGet 负责更新/卸载，不使用内置更新（见下文「更新」）。

---

## 将 CLI 加入 PATH

在 Windows 上，直接运行当前目录里的可执行文件需要 `.\` 前缀——例如 `.\lpb --version`。想让 `lpb`（或任意别名）在任何目录下都能直接调用，需要把安装文件夹加入**用户 PATH**。WinGet 与安装版在安装时已自动加入 PATH，以下步骤仅适用于便携版与纯 CLI 包。

安装包根目录附带两个辅助脚本，可一键完成：

- `add-to-path.cmd` — 双击即可把本文件夹加入用户 PATH（无需管理员权限）
- `remove-from-path.cmd` — 双击即可把它从用户 PATH 移除

脚本需在包含 `livephotobox-boot.exe` 的目录（便携版 / CLI 包根目录）运行。执行后重启终端，别名即可全局使用：

| 未加入 PATH | 已加入 PATH |
|--------------|-----------|
| `.\lpb merge photo.heic video.mov` — 只能在 CLI 所在目录用 | `lpb merge photo.heic video.mov` — 任意目录可用 |

---

## 可执行文件别名

工具以四个等价名称分发——挑最短的用：

| 别名 | 说明 |
|-------|-------------|
| `livephotobox` | 完整名称 |
| `livephoto` | 简写 |
| `livebox` | 紧凑形式 |
| `lpb` | 首字母缩写 |

---

## 更新

更新需要**手动触发**。

| 命令 | 作用 |
|------|------|
| `lpb update` | 检查 GitHub，有新版本则下载匹配的安装包并安装 |
| `lpb update-check` | 只检查、不安装 |

**参数：**

| 参数 | 适用 | 说明 |
|------|------|------|
| `-y`, `--yes` | `update` | 跳过确认提示，直接自动更新（脚本环境必需） |

### WinGet 安装的副本

通过 WinGet 安装的副本**不使用内置更新**——安装、升级、卸载均由 WinGet 负责：

- `lpb update` / `update-check` 仍会报告新版本，但 `lpb update` 不会安装——只打印 `Update with: winget upgrade LengxiQwQ.LivePhotoBox` 后退出。
- 更新：`winget upgrade LengxiQwQ.LivePhotoBox` · 卸载：`winget uninstall LengxiQwQ.LivePhotoBox`。
- 不确定自己的副本是哪种渠道？运行 `lpb --info`——WinGet 副本会显示 `Channel: WinGet (CLI-only)`。

### 便携版与安装版

`lpb update` 自行完成更新，先询问 `Update now? [Y/n]`（回车/`y` 继续），匹配的安装包会自动选择。

两者都需联网，失败时会打印失败原因及 `Manual download: …` 手动下载链接。

---

## 快速开始

```powershell
# 查看版本号（单行）；`lpb -v` 是 `lpb --version` 的快捷方式
lpb --version

# 查看详细环境信息（安装详情、内置工具版本）
lpb --info

# 查看协议 × 格式兼容矩阵
lpb protocols

# 查看 GUI 与 CLI 共用的处理分支配置
lpb backend

# 通过新版 Native 媒体管线转换独立媒体（不启动外部媒体 CLI）
lpb convert input.mov -o output.mp4 --codec h264
lpb convert input.heic -o output.jpg

# 转换单个文件对（iPhone → Google 相册）
lpb merge photo.heic video.mov -p motionphoto -y

# 批量转换文件夹（→ 华为格式，自动确认；输出到 ./MyPhotos/MyPhotos_huawei/）
lpb merge -d ./MyPhotos -p huawei -y

# 把单文件实况照片拆回图片 + 视频
lpb split photo.jpg -y

# 批量拆分文件夹（自动识别目录；-d 也可用）
lpb split ./MyPhotos -y

# 查看现有实况照片的封面位置与协议信息（不修改）
lpb cover photo.jpg

# 把封面改到视频 1.5 秒处（输出 源名_cover{帧号}.jpg 到原目录）
lpb cover photo.jpg --at 1.5 -y
```

---

## 命令

| 命令 | 说明 |
|------|------|
| `lpb convert` | 通过新版 Native 媒体管线转换独立 JPEG/HEIC 图片或 MOV/MP4 视频 |
| `lpb protocols` | 查看协议 × 格式兼容矩阵与设备支持 |
| `lpb merge` | 合成图片 + 视频（单对或批量） |
| `lpb split` | 把单文件实况照片拆回独立的图片与视频 |
| `lpb cover` | 修改已有实况照片的封面帧（Key Photo），别名 `keyphoto` |
| `lpb repair` | 分析并修复实况照片元数据 |
| `lpb backend` | 查看或配置全局 `rebuilt` / `legacy` 分支 |
| `lpb --info` / `lpb --version`（`-v`） | 查看版本、环境与内置工具版本 |

`update` / `update-check` 命令见上文「更新」一节。

### `backend` — 配置全局处理分支

GUI 与 CLI 共用 `%LOCALAPPDATA%\LivePhotoBox\backend-settings.json`。这里只有一个全局开关，不按协议分别设置。默认是 `rebuilt`。新版独立媒体转换通过 Native C++ 媒体库执行，不启动 FFmpeg 或其他外部媒体 CLI。目标协议写入器尚未接入，因此 `merge`、`split`、`cover`、`repair` 会在调用旧逻辑前明确停止。只有明确需要保留的 `v2.2.1` 兼容实现时才设为 `legacy`。

| 目标 | 命令 |
|------|------|
| 查看配置路径和当前分支 | `lpb backend` |
| 使用保留的旧版兼容实现 | `lpb backend mode legacy` |
| 使用隔离的新重构分支 | `lpb backend mode rebuilt` |
| 删除共享配置并恢复“新重构分支”默认值 | `lpb backend reset` |

`rebuilt` 绝不自动回退到 `legacy`。`lpb convert` 是第一个面向用户的新版媒体操作；Native 探测或转换失败会明确失败，不会猜测媒体事实或继续产出文件。协议命令仍使用 `--json` 返回固定的 `errorCode=rebuilt_not_ready`，退出码为 1。

### `convert` — 新版 Native 独立媒体转换

`convert` 接受独立的 JPEG/PNG/HEIC 图片或 MOV/MP4 视频。图片输出格式由扩展名决定（`.jpg`/`.jpeg`、`.png` 或 `.heic`/`.heif`），视频输出格式由扩展名决定（`.mp4` 或 `.mov`）。视频可用 `--codec copy`、`--codec h264` 或 `--codec hevc` 选择流复制/remux 或真正的 Native 重编码。转换前一定由 Native 探测源文件；探测失败会明确失败，不会使用调用方提供的猜测值继续转换。

```powershell
lpb convert input.mov -o output.mp4 --codec h264
lpb convert input.mp4 -o output.mov --codec hevc
lpb convert input.heic -o output.jpg
lpb convert input.jpg -o output.heic --overwrite
lpb convert input.jpg -o output.png
```

---

### `protocols` — 查看协议 × 格式兼容矩阵与设备支持

运行 `lpb protocols` 可交互查看，或 `lpb protocols --json` 获取结构化输出。

该命令会报告当前全局后端。默认 `rebuilt` 模式下，下面的矩阵仅表示 Legacy 兼容资料；协议 `merge`、`split`、`cover`、`repair` 写入器尚未启用。当前可通过 `lpb convert` 使用 Native 后端进行独立媒体格式转换。

**兼容矩阵** — 每个协议支持的输出格式：

| 协议 | JPEG + MP4 | JPEG + MOV | HEIC + MP4 | HEIC + MOV | HEIC + MP4 (H.265) |
|---|---|---|---|---|---|
| Google Micro Video (v1) | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
| Google Motion Photo (v2) | ✅ | ✅ | ✖️ | ✅ | ✖️ |
| OPPO O-Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| vivo Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| Samsung Motion Photo | ✅ | ✖️ | ✅ | ✖️ | ✖️ |
| HUAWEI Moving Photo | ✅ | ✖️ | ✅ | ✖️ | ✅ |

`✅` — 支持 &nbsp;|&nbsp; `✖️` — 不支持

**合成 — 设备支持：**

| 协议 | 支持设备 | 状态 |
|---|---|---|
| Google Micro Video (v1) | Windows / 小米 (旧版 MIUI) / Pixel | ✅ 可用 |
| Google Motion Photo (v2) | Windows / 小米 / Pixel | ✅ 可用 |
| OPPO O-Live Photo | Windows / 小米 / OPPO | ✅ 可用 |
| vivo Live Photo | Windows / vivo（≥ X300） | 🟡 测试中 |
| Samsung Motion Photo | Windows / Samsung | ✅ 可用 |
| HUAWEI Moving Photo | 华为 / 荣耀 | ✅ 可用 |

**拆分 — 设备支持：**

| 协议 | 支持机型 | 状态 |
|---|---|---|
| Apple Live Photo | iPhone / iPad | ✅ 可用 |
| vivo Live Photo | vivo（≤ X200） | 🟡 测试中 |

**拆分 — 协议 × 格式兼容矩阵：**

| 协议 | Keep | JPG + MOV | HEIC + MOV | JPG + MP4 |
|---|---|---|---|---|
| None（仅拆分） | ✅ | ✅ | ✅ | ✅ |
| Apple Live Photo | ✖️ | ✅ | ✅ | ✖️ |
| vivo Live Photo | ✖️ | ✖️ | ✖️ | ✅ |

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
| 批量文件夹 | `<路径>`（无扩展名自动识别）或 `-d` | 目录内自动按文件名配对 |

#### 使用示例

| 目标 | 命令 |
|------|------|
| 批量合成文件夹，自动确认 | `lpb merge ./MyPhotos -p motionphoto -y`（目录自动识别；`-d` 也可用） |
| 华为原生 HEVC（单对） | `lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y` |
| 批量 → 华为，显式输出目录 | `lpb merge -d ./MyPhotos -p huawei -o ./Output -y` |
| 批量含子目录，保留文件夹结构 | `lpb merge -d ./Photos -r -s -p motionphoto -o ./Output -y` |
| 预览（不创建任何文件夹） | `lpb merge -d ./Photos -p motionphoto --dry-run` |
| 自定义文件名模板 | `lpb merge -d ./Photos -p motionphoto -n "custom:{name}_{protocol}_{date}" -y` |
| 覆盖已存在输出而非自动重命名 | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| 自定义封面位置（视频 2.500 秒处） | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.500 -y` |

> **注意：** 不支持通配符（`*.jpg`）。请用 `-d` 指定文件夹，或显式列出文件。

---

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<图片> <视频>` | 图片 + 视频文件对，扩展名自动识别，顺序任意。图片：`.jpg .jpeg .heic .heif`；视频：`.mp4 .mov`；只传一个无扩展名的文件夹路径时自动识别为批量模式 |
| `-d, --dir <路径>` | 扫描目录（批量模式），同基础名称的文件自动配对；也可直接作为位置参数传入 |
| `-r, --recursive` | 扫描时包含所有子目录 |
| `--pairing <方式>` | 配对策略（仅批量）：`name` — 按文件名（默认）；`cid` — Apple ContentIdentifier UUID；`vivo` — vivo 相机 ID |
| `--key-timestamp <时间>` | 封面在视频时间轴上的位置（仅单文件）。支持秒（`2.500`）、分:秒（`1:30.500`）、时:分:秒（`0:01:30.500`）；默认跟随源视频 |

**输出**

| 选项 | 说明 |
|------|------|
| `-o, --output <文件夹>` | 输出目录。默认：单文件 → 照片所在目录；批量 → `{输入目录}/{输入目录名}_<协议后缀>/`。自动创建 |
| `-w, --overwrite` | 直接覆盖已存在输出；否则自动重命名（`photo.jpg` → `photo (2).jpg`） |
| `-s, --preserve-subdirs` | 在输出目录中保留源文件的子目录结构 |
| `--after <操作>` | 合成成功后对源文件的操作：`none`（默认）、`move:路径`、`recycle` |

**格式**

| 选项 | 说明 |
|------|------|
| `-p, --protocol <协议>` | 目标协议（默认 `motion photo`）：`micro video` (v1)、`motion photo` (v2)、`oppo`、`vivo`、`samsung`、`huawei`。运行 `lpb protocols` 查看完整矩阵。多词协议名也可写无空格形式（无需引号）：`microvideo`、`motionphoto` |
| `-f, --format <格式>` | 输出容器（默认：指定协议的首个可用格式）：`jpg+mp4`、`jpg+mov`、`heic+mp4`、`heic+mov`、`heic+mp4-h265` |
| `-n, --naming <规则>` | 输出文件名规则。默认：单文件 = `suffix`，批量 = `keep`。`keep`、`suffix` 或 `custom:模板`（占位符见下） |

命名占位符：

| 占位符 | 含义 |
|--------|------|
| `{name}` | 源文件名 |
| `{protocol}` | 协议简称 |
| `{date}` | 当前日期 (yyyyMMdd) |
| `{date:格式}` | 自定义日期，如 `{date:yyyy-MM-dd}` |
| `{time}` | 当前时间 (HHmmss) |
| `{exif_date}` | 照片拍摄日期（从文件读取） |
| `{exif_time}` | 照片拍摄时间（从文件读取） |
| `{counter}` | 自增编号 (001, 002, …) |
| `{counter:D3}` | 定宽编号，如 D3 = 001 |
| `{frame}` | 封面帧序号（1-based；仅 cover 命令使用） |

**执行**

| 选项 | 说明 |
|------|------|
| `-j, --parallel <数量>` | 最大并行任务数（默认：CPU 核心数，上限 5） |
| `-y, --yes` | 跳过所有确认提示。脚本自动化运行时的必要选项 |
| `--dry-run` | 仅列出计划操作，不实际处理文件 |
| `-v, --verbose` | 逐文件输出状态，而非仅显示汇总 |
| `--all-variants` | 生成所有协议 × 格式组合（仅单对模式）；输出到 `{目录}/{文件名}_variants/` |

#### 默认输出位置

省略 `-o` 时，输出**不会**落到终端当前目录，而是跟随**输入**：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件对 | **照片（图片）所在目录**（照片和视频可能在不同文件夹，以照片为准） | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| 批量（文件夹 / `-d`） | 输入目录下的子文件夹，命名为 `{输入目录名}_{协议后缀}` | `lpb merge ./MyPhotos -p motionphoto` → `./MyPhotos/MyPhotos_motionphoto/` |

- 文件夹/文件名均为英文：`MyPhotos_huawei/`、`IMG_001huawei.jpg`。
- 单文件对默认命名为 `{源文件名}{协议后缀}`（如 `IMG_001motionphoto.jpg`），不会覆盖源照片。
- 批量文件名保持源名不变——协议后缀体现在**文件夹名**上。
- `--dry-run` 会打印解析出的输出路径，且**不创建任何文件夹**。

#### `--all-variants` — 一键生成所有变体

无需逐个指定 `-p` / `-f`，一次性生成 7 个协议、14 种格式组合的实况照片，适合开发者快速验证所有协议的输出质量。

```powershell
# 输出到输入文件所在目录的 {name}_variants/ 下
lpb merge photo.jpg video.mp4 --all-variants

# 指定输出目录
lpb merge photo.jpg video.mp4 --all-variants -o ./Out
```

输出：`photo_variants/`（或指定目录下的 `photo_variants/`）生成 12 个文件：
```
photo_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

注意：
- 仅支持单对模式，不支持 `--dir` 批量模式
- 命名固定，不接受 `--naming` / `--protocol` / `--format` 选项

#### `--key-timestamp` — 自定义封面在视频中的位置

设置**封面（key photo）在视频时间轴上的位置**。默认跟随源视频自带的时间轴。

```powershell
# 封面位于视频第 2.500 秒处
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.500 -y

# 也支持 分:秒 / 时:分:秒 写法
lpb merge photo.jpg video.mp4 -p motionphoto --key-timestamp 1:30.500 -y
```

- 时间格式：秒（`2.500`）、分:秒（`1:30.500`）、时:分:秒（`0:01:30.500`）。
- 仅单文件模式可用；批量模式（`-d`）传该参数会直接报错退出。
- 可与 `--all-variants` 组合，所有变体使用同一时间戳。

#### 配对方式

批量模式 (`-d`) 下，工具需要将图片与视频一一对应：

| 方式 | 配对依据 | 示例 |
|------|----------|------|
| `name`（默认） | 基础名称相同、扩展名不同 | `photo_001.jpg` + `photo_001.mp4` → 配对 |
| `cid` | Apple `ContentIdentifier` UUID 一致，与文件名无关 | `IMG_0002.HEIC` + `renamed.MOV` → 配对 |
| `vivo` | JPEG 尾部 + MP4 元数据中的 vivo 相机 ID | `vivo_photo.jpg` + `vivo_video.mp4` → 配对 |

`cid` 需要 `exiftool.exe` 位于可执行文件旁的 `Tools\` 目录中（所有分发包均自带）；`name` 与 `vivo` 无需外部工具。

#### 命名模板速查

| 目的 | 模板 | 输出示例 |
|------|----------|----------------|
| 保持原名 | `-n keep` | `IMG_001.jpg` |
| 追加协议后缀 | `-n suffix` | `IMG_001huawei.jpg` |
| 文件名 + 日期 | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| 协议作子目录 | `-n "custom:{protocol}/{name}"` | `huawei/IMG_001.jpg` |
| 顺序编号 | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |
| 完整元数据 | `-n "custom:{name}_{protocol}_{date}_{time}"` | `IMG_001_huawei_20260803_143022.jpg` |

> **说明：** 省略 `-n` 时，单文件默认 `suffix`、批量默认 `keep`。显式传 `-n` 始终以你为准。

#### 完成后操作

| 操作 | 命令 |
|------|------|
| 归档源文件 | `lpb merge -d ./Photos -p motionphoto --after "move:./Archived" -y` |
| 移入回收站 | `lpb merge -d ./Photos -p motionphoto --after recycle -y` |
| 保留源文件（默认） | `lpb merge -d ./Photos -p motionphoto --after none -y` |

仅**合成成功**的文件对的源文件会受影响。

---

### `split` — 拆分单文件实况照片

`merge` 的反向操作：把单文件实况照片（图片 + 追加视频）拆回独立的图片与视频。两种运行模式：

| 模式 | 参数 | 使用场景 |
|------|------|----------|
| 单文件 | `<文件>`（按扩展名自动识别） | 拆分单个单文件实况照片 |
| 批量文件夹 | `<路径>`（无扩展名自动识别）或 `-d` | 拆分目录内所有单文件实况照片 |

#### 使用示例

| 目标 | 命令 |
|------|------|
| 拆分单个文件（图片 + 视频输出到源文件旁） | `lpb split photo.jpg` |
| 批量拆分文件夹，自动确认 | `lpb split ./MyPhotos -y`（目录自动识别；`-d` 也可用） |
| 把视频转换为 JPG+MP4 (H.264) | `lpb split photo.jpg -f jpg+mp4` |
| 预览（不实际处理） | `lpb split -d ./MyPhotos --dry-run` |
| 只拆分 vivo 实况照片 | `lpb split -d ./MyPhotos --pairing vivo -y` |
| 覆盖已存在输出 | `lpb split photo.jpg -w` |
| 导出所有变体（Apple + vivo + 无协议） | `lpb split photo.jpg --all-variants` |
| 设置封面位置（Apple 转换） | `lpb split photo.jpg -p apple --key-timestamp 2.500 -y` |

> **注意：** 不支持通配符（`*.jpg`）。请用 `-d` 指定文件夹，或显式列出文件。

---

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<文件>` | 单个待拆分的单文件实况照片：`.jpg .jpeg .heic .heif`（图片 + 追加视频）；也可传文件夹路径，无扩展名即自动识别为批量模式 |
| `-d, --dir <路径>` | 包含单文件实况照片的文件夹（批量模式），所有检测到的实况照片都会被拆分；也可直接作为位置参数传入 |
| `--pairing <协议>` | 只拆分该协议的实况照片：`all`（不过滤，默认）、`v1`（MicroVideo）、`v2`（MotionPhoto）、`oppo`、`vivo`、`samsung`、`huawei` |
| `-r, --recursive` | 扫描时包含所有子目录 |

**输出**

| 选项 | 说明 |
|------|------|
| `-o, --output <文件夹>` | 输出目录。默认：单文件 → 源文件所在目录；批量 → 输入目录下的 `{文件夹名}_split` 子文件夹。自动创建 |
| `-w, --overwrite` | 直接覆盖已存在文件；否则自动重命名（`photo.jpg` → `photo (2).jpg`） |
| `-s, --preserve-subdirs` | 在输出目录中保留源文件的子目录结构 |
| `--after <操作>` | 拆分成功后对源文件的操作：`none`（默认）、`move:路径`、`recycle` |

**格式**

| 选项 | 说明 |
|------|------|
| `-p, --protocol <协议>` | 目标手机格式（默认 `none`）：`none`（仅拆分）、`apple`（Apple Live Photo）、`vivo`（vivo Live Photo，≤ X200）。apple/vivo 会写入配对元数据（ContentIdentifier / vivo 尾标 + uuid box） |
| `-f, --format <格式>` | 输出格式（默认：指定协议的首个可用格式）：`keep`（不转换）、`jpg+mov` (H.265)、`heic+mov` (H.265)、`jpg+mp4` (H.264) |
| `--key-timestamp <时间>` | 覆盖封面（key photo）在视频时间轴上的位置（仅 Apple 转换、单文件）。支持秒（`2.500`）、分:秒（`1:30.500`）、时:分:秒（`0:01:30.500`）；默认跟随源 |
| `-n, --naming <规则>` | 输出文件名规则。默认：`keep`。`keep`（保持原名）或 `custom:模板`（占位符见下） |

命名占位符：

| 占位符 | 含义 |
|--------|------|
| `{name}` | 源文件名 |
| `{date}` | 当前日期 (yyyyMMdd) |
| `{date:格式}` | 自定义日期，如 `{date:yyyy-MM-dd}` |
| `{time}` | 当前时间 (HHmmss) |
| `{exif_date}` | 照片拍摄日期（从文件读取） |
| `{exif_time}` | 照片拍摄时间（从文件读取） |
| `{counter}` | 自增编号 (001, 002, …) |
| `{counter:D3}` | 定宽编号，如 D3 = 001 |
| `{frame}` | 封面帧序号（1-based；仅 cover 命令使用） |

**执行**

| 选项 | 说明 |
|------|------|
| `-j, --parallel <数量>` | 同时处理的文件数（默认：CPU 核心数，上限 5） |
| `-y, --yes` | 跳过确认提示。适用于脚本 / 自动化 |
| `--dry-run` | 预览：显示将要执行的操作，不实际处理文件 |
| `-v, --verbose` | 逐文件输出状态，而非仅显示汇总 |
| `--all-variants` | 从单个单文件实况照片导出所有拆分变体（仅单文件模式）；输出到 `{输出目录}/split_{名称}_All_Variants/` |

#### 默认输出位置

省略 `-o` 时，输出**不会**落到终端当前目录，而是跟随**输入**：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件 | **源文件所在目录** | `lpb split photo.jpg` → 图片 + 视频输出到源文件旁 |
| 批量（文件夹 / `-d`） | 输入目录下的子文件夹，命名为 `{文件夹名}_split` | `lpb split ./MyPhotos` → `./MyPhotos/MyPhotos_split/` |

- 图片保持源基础名与扩展名；视频保持源视频的容器（`.mov` 或 `.mp4`）。
- 就地拆分：图片名与源文件冲突时自动重命名（`photo.jpg` → `photo (2).jpg`）；传 `-w` 则覆盖。
- 批量文件名保持源名不变——它们进入独立的 `{文件夹名}_split/` 子文件夹。
- `--dry-run` 会打印解析出的输出路径，且**不创建任何文件夹**。

#### `--all-variants` — 一键导出所有拆分变体

从单个单文件实况照片一键导出全部 7 组拆分变体。仅单文件模式——批量（`-d`）会被拒绝。适合开发者快速验证各协议的拆分输出。

| 变体 | 输出文件对 |
|------|-----------|
| 无协议（保持原样） | `none_keep.<图片扩展名>` + `none_keep.<视频容器>` |
| 无协议（JPG+MOV） | `none_jpg+mov.JPG` + `none_jpg+mov.MOV` |
| 无协议（HEIC+MOV） | `none_heic+mov.HEIC` + `none_heic+mov.MOV` |
| 无协议（JPG+MP4） | `none_jpg+mp4.JPG` + `none_jpg+mp4.MP4` |
| Apple Live Photo (JPG+MOV) | `apple_jpg+mov.JPG` + `apple_jpg+mov.MOV` |
| Apple Live Photo (HEIC+MOV) | `apple_heic+mov.HEIC` + `apple_heic+mov.MOV` |
| vivo Live Photo (JPG+MP4) | `vivo_jpg+mp4.JPG` + `vivo_jpg+mp4.MP4` |

```powershell
# 默认输出到源文件所在目录的 split_{名称}_All_Variants/
lpb split photo.jpg --all-variants

# 指定输出目录
lpb split photo.jpg --all-variants -o ./Out
```

文件名按 `{协议}_{格式}` 命名（小写 CLI 规范值，如 `-p apple -f jpg+mov` → `apple_jpg+mov`）；原文件名只进**文件夹名**，所有名称不含空格。keep 变体图片跟随源扩展名、视频跟随源容器（`.MOV` / `.MP4`）。`-p` / `-f` / `-n` / `-w` / `--after` 被忽略；`-j` 仍控制并行度。

#### 协议 × 格式矩阵

每个拆分协议支持的输出格式：

| 协议 | Keep | JPG + MOV | HEIC + MOV | JPG + MP4 |
|---|---|---|---|---|
| `none`（仅拆分） | ✅ | ✅ | ✅ | ✅ |
| `apple`（Apple Live Photo） | ✖️ | ✅ | ✅ | ✖️ |
| `vivo`（vivo Live Photo） | ✖️ | ✖️ | ✖️ | ✅ |

省略 `--format` 时，默认取该协议的首个可用格式：`none` → `keep`、`apple` → `jpg+mov`、`vivo` → `jpg+mp4`。

#### 配对过滤

`--pairing` 把拆分限定为某一协议，其他协议会被跳过。

| 值 | 协议 |
|----|------|
| `all` | 不过滤（默认） |
| `v1` | Google Micro Video (v1) |
| `v2` | Google Motion Photo (v2) |
| `oppo` | OPPO O-Live Photo |
| `vivo` | vivo Live Photo |
| `samsung` | Samsung Motion Photo |
| `huawei` | HUAWEI Moving Photo |

#### 命名模板速查

split 只支持 `keep`（默认）和 `custom:模板`（无 `suffix` 模式）。模板同时命名图片与视频，各自保留自己的扩展名。

| 目的 | 模板 | 输出示例 |
|------|----------|----------------|
| 保持原名 | `-n keep`（默认） | `IMG_001.jpg`（图片保持原名） |
| 文件名 + 日期 | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| 顺序编号 | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |

#### 完成后操作

| 操作 | 命令 |
|------|------|
| 归档源文件 | `lpb split -d ./Photos --after "move:./Archived" -y` |
| 移入回收站 | `lpb split -d ./Photos --after recycle -y` |
| 保留源文件（默认） | `lpb split -d ./Photos --after none -y` |

仅**拆分成功**的实况照片的源文件会受影响。

---

### `cover` — 修改实况照片封面帧（Key Photo）

修改已有实况照片的封面帧（Key Photo），不重新合成视频。别名 `keyphoto`（对齐 Apple 术语）。

自动识别单文件（华为/V2/OPPO/Samsung/Fusion）和双文件（Apple HEIC+MOV、Vivo 旧双文件）格式。

#### 三种模式

`cover` 有三种模式：

| 模式 | 命令 | 效果 |
|------|------|------|
| 只读查看 | `lpb cover photo.jpg` | 显示协议、总帧数、时长、当前封面帧。**不创建、不修改任何文件**。也可加 `--json` 给脚本用 |
| 预览（dry-run） | `lpb cover photo.jpg --at 2.5 --dry-run` 或 `--frame 10 --dry-run` | 在只读信息之外，再显示新封面位置和输出路径，然后停止。**不创建、不修改任何文件** |
| 执行 | `lpb cover photo.jpg --at 2.5 -y` 或 `--frame 10 -y` | 真正写出带新封面帧的实况照片文件 |

`--at` 和 `--frame` 是两种指定**同一件事**的方式：新封面在哪里。一个按“视频时间”指定，一个按“帧序号”指定，所以两者互斥——同时给会冲突。它们本身都不代表“检查”或“执行”；是否执行由 `--dry-run` / 确认提示 / `--json` 控制。

> **OPPO/一加 说明**：OPPO 相册按时间戳定位封面时总会落在后一帧。对 OPPO/一加实况照片使用 `--frame` 或 `--at` 时，CLI 会自动把时间戳往前让一帧，让 OPPO 相册显示、导出和存储的封面，都正好是你选中的那一帧。

#### 使用示例

| 目标 | 命令 |
|------|------|
| 查看总帧数、时长与当前封面（不修改） | `lpb cover photo.jpg` |
| 把封面改到视频 2.5 秒处 | `lpb cover photo.jpg --at 2.5 -y` |
| 双文件 Apple 实况 | `lpb cover photo.heic video.mov --at 2.5 -y` |
| 帧序号模式（1-based，10 = 第 10 帧） | `lpb cover photo.jpg --frame 10 -y` |
| 仅预览不修改 | `lpb cover photo.jpg --at 2.5 --dry-run` |
| 推荐流程：先查看，再设第 10 帧 | `lpb cover photo.jpg` → `lpb cover photo.jpg --frame 10 -y` |
| 别名 keyphoto | `lpb keyphoto photo.jpg --at 2.5 -y` |
| 自定义命名模板 | `lpb cover photo.jpg --at 2.5 -n "custom:{name}_frame{frame}" -y` |
| 覆盖已有输出 | `lpb cover photo.jpg --at 2.5 -y -w` |
| 指定输出目录 | `lpb cover photo.jpg --at 2.5 -o ./Output -y` |
| JSON 脚本模式 | `lpb cover photo.jpg --at 2.5 --json` |

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<文件>` | 实况照片文件路径。单文件自动识别协议；双文件自动配对（Apple CID / 文件名） |
| `<图片> <视频>` | 双文件实况时显式指定图片和视频文件，自动识别顺序 |
| `--at <时间>` | 按**视频时间**指定新封面位置。支持秒（`2.500`）、分:秒（`1:30.500`）、时:分:秒（`0:01:30.500`）。与 `--frame` 互斥 |
| `--frame <序号>` | 按**帧序号**指定新封面位置，**1-based**（1 = 第 1 帧，10 = 第 10 帧）。与 `--at` 互斥 |

**输出**

| 选项 | 说明 |
|------|------|
| `-o, --output <目录>` | 输出目录（默认：源文件所在目录） |
| `-n, --naming <规则>` | 输出文件名规则。默认：`suffix`（追加 `_cover{帧号}`）。`keep`（保持原名）或 `custom:模板` |
| `-w, --overwrite` | 覆盖已有文件（默认自动重命名） |

**执行**

| 选项 | 说明 |
|------|------|
| `-y, --yes` | 跳过确认提示 |
| `--dry-run` | 预览：显示当前封面信息、新封面位置和输出路径，不修改文件 |
| `-v, --verbose` | 详细输出 |
| `--json` | JSON 格式输出（隐含 `--yes`） |

#### 命名模板

命令默认生成 `{源文件名}_cover{帧号}` 格式（如 `IMG_1234_cover46.jpg`），支持 `-n` 自定义：

| 占位符 | 含义 | 示例 |
|--------|------|------|
| `{name}` | 源文件名 | `IMG_1234` |
| `{protocol}` | 协议简称 | `huawei` |
| `{date}` | 当前日期 | `20260820` |
| `{date:格式}` | 自定义日期 | `{date:yyyy-MM-dd}` → `2026-08-20` |
| `{time}` | 当前时间 | `143022` |
| `{exif_date}` | 拍摄日期 | `20260803` |
| `{exif_time}` | 拍摄时间 | `100530` |
| `{frame}` | **封面帧序号（1-based）** | `46` |
| `{counter}` | 自增编号 | `1` |
| `{counter:D3}` | 定宽编号 | `001` |

**命名模板速查**

| 目的 | 模板 | 输出示例 |
|------|------|---------|
| 默认（含帧号） | `-n "custom:{name}_cover{frame}"` | `IMG_1234_cover46.jpg` |
| 保持原名覆盖 | `-n keep` | `IMG_1234.jpg` |
| 含日期 | `-n "custom:{name}_{protocol}_{date}_{frame}"` | `IMG_1234_huawei_20260820_46.jpg` |
| 含时间戳 | `-n "custom:{name}_at{frame}_{time}"` | `IMG_1234_at46_143022.jpg` |

#### 默认输出位置

| 模式 | 默认输出目录 | 示例 |
|------|-------------|------|
| 单文件 | 源文件所在目录 | `lpb cover photo.jpg --at 2.5` → `./photo_cover46.jpg` |
| 双文件 | 图片所在目录 | `lpb cover photo.heic --at 2.5` → `./photo_cover46.HEIC` + `.MOV` |

---

### `repair` — 修复实况照片元数据

分析并修复现有实况照片文件的四类元数据问题：图片旋转、内嵌缩略图、HEIC 方向、视频旋转。图片：`.jpg .jpeg .heic .heif`；视频：`.mov .mp4`。

| 模式 | 参数 | 使用场景 |
|------|------|----------|
| 单文件 | `<文件>`（按扩展名自动识别） | 修复单个图片或视频 |
| 批量文件夹 | `<路径>`（无扩展名自动识别）或 `-d` | 目录内所有媒体文件 |

#### 使用示例

| 目标 | 命令 |
|------|------|
| 修复单个文件 | `lpb repair photo.jpg` |
| 批量修复文件夹，自动确认 | `lpb repair ./MyPhotos -y`（目录自动识别；`-d` 也可用） |
| 预览（不写入） | `lpb repair -d ./MyPhotos --dry-run` |
| 只修图片旋转 | `lpb repair -d ./Photos --no-thumbnail --no-heic --no-video -y` |
| 修复所有设备文件 | `lpb repair -d ./MyPhotos --all-devices -y` |
| 同时复制完好文件 | `lpb repair -d ./MyPhotos --copy-perfect -y` |

> **注意：** 不支持通配符（`*.jpg`）。请用 `-d` 指定文件夹，或显式列出文件。

---

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<文件>` | 单个待修复的图片或视频。图片：`.jpg .jpeg .heic .heif`；视频：`.mov .mp4`；无扩展名的文件夹路径自动识别为批量模式 |
| `-d, --dir <路径>` | 扫描目录（批量模式）。每个媒体文件都会被分析，只有需要修复的文件才会被修复；也可直接作为位置参数传入 |
| `-r, --recursive` | 扫描时包含所有子目录 |

**修复项**

| 选项 | 说明 |
|------|------|
| `--no-rotate` | 关闭图片旋转修正（jpegtran 无损旋转） |
| `--no-thumbnail` | 关闭内嵌缩略图剥离 |
| `--no-heic` | 关闭 HEIC/HEIF 方向修正 |
| `--no-video` | 关闭视频旋转烘焙（FFmpeg 重编码） |
| `--all-devices` | 修复所有设备的文件。默认只修复 Apple 实况照片（通过 `ContentIdentifier` UUID 识别） |
| `--repair-long-videos` | 同时修复时长超过 3.5 秒的视频（非实况照片）。默认跳过 |
| `--copy-perfect` | 把无需修复的完好文件也复制到输出目录（仅批量模式） |

四项修复**默认全部开启**——用 `--no-*` 开关按需关闭单项。

**输出**

| 选项 | 说明 |
|------|------|
| `-o, --output <文件夹>` | 输出目录。默认：单文件 → 源文件旁 `{文件名}_repaired{扩展名}`；批量 → `{输入目录}/{输入目录名}_repaired/`。自动创建 |
| `-w, --overwrite` | 直接覆盖已存在输出；否则自动重命名（`photo.jpg` → `photo (2).jpg`） |
| `-s, --preserve-subdirs` | 在输出目录中保留源文件的子目录结构 |

**执行**

| 选项 | 说明 |
|------|------|
| `-j, --parallel <数量>` | 最大并行任务数（默认：CPU 核心数，上限 5） |
| `-y, --yes` | 跳过所有确认提示。脚本自动化运行时的必要选项 |
| `--dry-run` | 仅列出计划操作，不实际处理文件 |
| `-v, --verbose` | 逐文件输出状态，而非仅显示汇总 |

#### 四种修复

| 修复项 | 作用 | 适用 |
|--------|------|------|
| 图片旋转 | jpegtran 无损旋转后重置 EXIF 方向标签 | JPEG |
| 缩略图剥离 | 剥离内嵌缩略图/预览图（减小文件体积） | JPEG |
| HEIC 方向 | 修正 EXIF 方向以匹配 QuickTime `Rotation`（镜像标记或角度不一致） | HEIC/HEIF |
| 视频旋转烘焙 | FFmpeg 重编码，把旋转矩阵烘焙进像素 | MOV/MP4 |

> **说明：** 四项修复默认全开；传 `--no-heic` 可关闭 HEIC 方向修复（对齐 GUI 默认行为）。

#### 默认输出位置

修复**不会覆盖**源文件。省略 `-o` 时：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件 | 源文件所在目录下的 `{文件名}_repaired{扩展名}` | `IMG_001.jpg` → `IMG_001_repaired.jpg` |
| 批量（文件夹 / `-d`） | `{输入目录}/{输入目录名}_repaired/`，文件名保持源名 | `lpb repair ./MyPhotos` → `./MyPhotos/MyPhotos_repaired/` |

#### Apple 实况照片过滤

默认只修复 **Apple 实况照片**——通过 `ContentIdentifier` UUID（图片和配对视频都携带）识别，其余跳过。传 `--all-devices` 可修复所有设备。

#### 脚本模式（JSON 输出）

使用 `--json` 时，`repair` 向 stdout 输出一份 UTF-8 JSON 文档（无颜色、无提示）。`--json` 隐含 `--yes`（跳过确认）。

批量模式输出：

```json
{
  "command": "repair",
  "mode": "batch",
  "input": "C:\\...\\Photos",
  "output": "C:\\...\\Photos_repaired",
  "scanned": 47,
  "apple": 39,
  "needsRepair": 27,
  "repaired": 27,
  "failed": 0,
  "skipped": 20,
  "errors": 0,
  "files": [
    { "Path": "C:\\...\\IMG_0139.JPG", "Name": "IMG_0139", "Status": "repaired", "Issue": "[90° rotation tag]", "Reason": "" },
    { "Path": "C:\\...\\other.mov", "Name": "other", "Status": "skipped", "Issue": "", "Reason": "non-Apple device" }
  ]
}
```

顶层计数：`scanned`（发现的媒体文件）、`apple`（通过 ContentIdentifier 识别的 Apple 实况照片）、`needsRepair`、`repaired`、`failed`、`skipped`、`errors`。`--all-devices` 下关闭过滤，`apple` 等于 `scanned`（全部视作 Apple）。

`files[].Status` 取值：`repaired`、`failed`、`skipped`、`copied`（`--copy-perfect`），以及 `--dry-run` 下的 `would-repair` / `would-copy`。单文件模式还可能返回 `cancelled`（被中断）。

单文件模式返回扁平对象：`command`、`mode`、`input`、`output`、`status`、`issue`、`reason`。

JSON 为 UTF-8 编码；脚本读取时请按 UTF-8 解码（如 Python `json.loads(sys.stdin.buffer.read().decode("utf-8"))`）。

---

### `--info` / `--version` — 查看版本与环境信息

`lpb --version`（`-v`）单行打印版本号；`lpb --info` 打印安装详情（构建日期、运行时、平台、渠道、位置）、日志目录与当前日志文件、内置工具版本、仓库与反馈入口。两者均瞬间完成、不联网；输出在交互终端着色，重定向或设置 `NO_COLOR` 时回退为纯文本。

注意：根级 `-v` 表示 `--version`；子命令内（如 `lpb merge -v`）保持子命令自身含义（`--verbose`）。

---

## 退出码

| 退出码 | 含义 |
|:---:|---------|
| 0 | 全部任务成功完成 |
| 1 | 参数错误，或至少有一个任务失败 |
| 2 | 更新检查失败（网络 / GitHub 不可达） |
| 130 | 用户取消 (Ctrl+C) |

---

## 故障排查

#### 提示未知协议
运行 `lpb protocols` 查看所有有效协议名称及缩写别名。

#### 所选格式不适用于该协议
运行 `lpb protocols` 查看兼容矩阵。例如，`heic+mp4-h265` 仅可用于 `huawei`。

#### 使用 `--pairing cid` 时提示找不到 exiftool
把 `exiftool.exe` 放到可执行文件旁的 `Tools\` 目录即可。

#### 输出文件扩展名与源文件不一致
正常现象。源文件为 HEIC 且选择了 JPEG 类格式时，输出使用 `.jpg` 扩展名。

#### 提示"Permission denied"或文件被占用
关闭正在访问源文件的相册 App 或文件管理器。被其他进程锁定的文件无法在 Windows 上读取或移动。

---

## 获取帮助

- **文档：** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug 反馈 / 功能建议：** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **最新版本下载：** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **项目仓库：** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

如果这个项目对你有帮助，欢迎在 GitHub 上点个 ⭐ Star。

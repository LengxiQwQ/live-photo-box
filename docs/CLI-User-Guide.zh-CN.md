# Live Photo Box CLI — 使用指南

[![最新发布](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=%E6%9C%80%E6%96%B0%E5%8F%91%E5%B8%83)](https://github.com/lengxiqwq/live-photo-box/releases) [![许可证](https://img.shields.io/badge/许可证-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![平台](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![项目仓库](https://img.shields.io/badge/%E9%A1%B9%E7%9B%AE%E4%BB%93%E5%BA%93-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![反馈](https://img.shields.io/badge/反馈-Issues-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## 概述

`livephotobox`（`lpb`）是 **Live Photo Box** 的命令行版。它把一张图片（`JPG` / `HEIC`）和一段视频（`MP4` / `MOV`）合并成单文件**实况照片**——手机相册里会动起来的那种格式。

它与 GUI 共享 100% 核心逻辑，适合脚本与 AI Agent 调用。目前提供五个命令：`merge`、`protocols`、`info`、`update-check`、`update`；拆分与修复暂时仅 GUI 支持。

---

## 分发包说明

[Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 提供三种包：

| 包名 | 内容 | 适用场景 | PATH |
|---------|----------|----------|------|
| `*-x64-setup.exe` | GUI + CLI，安装向导一键安装 | 普通用户，想要完整桌面应用 | 安装时可选择加入 |
| `*-x64-portable.zip` | GUI + CLI，解压即用 | U 盘便携使用，或不想安装时试用 | 需手动添加 |
| `*-x64-cli.zip` | 纯 CLI，不含 GUI 及其运行时 | 服务器、脚本、CI/CD，最小体积 | 需手动添加 |

三种包均包含相同的 `livephotobox.exe` 及四个别名。纯 CLI 包体积最小——省去了 WinUI 图形界面及其运行时（约 80 MB）。

---

## 将 CLI 加入 PATH

在 Windows 上，直接运行当前目录里的可执行文件需要 `.\` 前缀——例如 `.\lpb --version`。想让 `lpb`（或任意别名）在任何目录下都能直接调用，需要把安装文件夹加入**用户 PATH**。

安装包根目录附带两个辅助脚本，可一键完成：

- `add-to-path.cmd` — 双击即可把本文件夹加入用户 PATH（无需管理员权限）
- `remove-from-path.cmd` — 双击即可把它从用户 PATH 移除

脚本需在包含 `livephotobox-boot.exe` 的目录（便携版 / CLI 包根目录）运行。执行后重启终端，别名即可全局使用：

| 未加入 PATH | 已加入 PATH |
|--------------|-----------|
| `.\lpb merge photo.heic video.mov` — 只能在 CLI 所在目录用 | `lpb merge photo.heic video.mov` — 任意目录可用 |

---

## 更新

更新需要**手动触发**——CLI 不会在后台自行检查。

| 命令 | 作用 |
|------|------|
| `lpb update` | 检查 GitHub，有新版本则下载匹配的安装包并安装 |
| `lpb update-check` | 只检查、不安装 |

**参数：**

| 参数 | 适用 | 说明 |
|------|------|------|
| `-y`, `--yes` | `update` | 跳过确认提示，直接自动更新（脚本环境必需） |

发现新版本时，`lpb update` 会打印新版本号和匹配的安装包，然后询问 `Update now? [Y/n]`——回车或输入 `y` 继续。安装包按安装类型自动选择：

| 安装类型 | 安装包 |
|---------|--------|
| Portable CLI-only（纯 CLI） | `*-x64-cli.zip` |
| Portable bundle (GUI + CLI)（便携包） | `*-x64-portable.zip` |
| Installer (Inno Setup, GUI + CLI)（安装版） | `*-x64-setup.exe` |

两者都需联网，失败时会打印失败原因及 `Manual download: …` 手动下载链接。WinGet 安装的副本跳过内置更新，请改用 `winget upgrade LengxiQwQ.LivePhotoBox`。

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

## 快速开始

```powershell
# 查看版本号
lpb --version

# 查看详细环境信息（含内置工具版本、附带一次更新检查）
lpb info

# 查看协议 × 格式兼容矩阵
lpb protocols

# 转换单个文件对（iPhone → Google 相册）
lpb merge photo.heic video.mov -p motion photo -y

# 批量转换文件夹（→ 华为格式，自动确认；输出到 ./MyPhotos/MyPhotos_huawei/）
lpb merge -d ./MyPhotos -p huawei -y
```

---

## 命令

| 命令 | 说明 |
|------|------|
| `lpb protocols` | 查看协议 × 格式兼容矩阵 |
| `lpb merge` | 合成图片 + 视频（单对或批量） |
| `lpb info` / `lpb --version` | 查看版本、环境与内置工具版本 |

`update` / `update-check` 命令见上文「更新」一节。

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

  Split — single-file live photo splitting (split not yet supported — use the GUI app)

  Protocol            Devices
  ─────────────────────────────────────────
  Apple Live Photo    iPhone / iPad
  vivo Live Photo     vivo (≤ X300)
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

| 目标 | 命令 |
|------|------|
| 华为原生 HEVC（单对） | `lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y` |
| 批量 → 华为，显式输出目录 | `lpb merge -d ./MyPhotos -p huawei -o ./Output -y` |
| 批量含子目录，保留文件夹结构 | `lpb merge -d ./Photos -r -s -p motion photo -o ./Output -y` |
| 预览（不创建任何文件夹） | `lpb merge -d ./Photos -p motion photo --dry-run` |
| 自定义文件名模板 | `lpb merge -d ./Photos -p motion photo -n "custom:{name}_{protocol}_{date}" -y` |
| 覆盖已存在输出而非自动重命名 | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| 自定义封面位置（视频 2.5 秒处） | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y` |

---

### `info` / `--version` — 查看版本与环境信息

| 命令 | 打印内容 |
|------|----------|
| `lpb --version` | 精简版本横幅：版本号、构建日期、运行时、安装渠道、位置 |
| `lpb info` | 同上，另加内置工具版本（exiftool、ffmpeg、…）并附带一次更新检查 |

两者均瞬间完成、不联网——仅 `info` 末尾的更新检查需要网络，失败时会原地提示、不影响命令。输出在交互终端中着色，重定向或设置 `NO_COLOR` 时自动回退为纯文本。

---

## 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<图片> <视频>` | 图片 + 视频文件对，扩展名自动识别，顺序任意。图片：`.jpg .jpeg .heic .heif`；视频：`.mp4 .mov` |
| `-d, --dir <文件夹>` | 扫描目录（批量模式），同基础名称的文件自动配对 |
| `-r, --recursive` | 扫描时包含所有子目录 |
| `--pairing <方式>` | 配对策略（仅批量）：`name` — 按文件名（默认）；`cid` — Apple ContentIdentifier UUID；`vivo` — vivo 相机 ID |
| `--key-timestamp <时间>` | 封面在视频时间轴上的位置（仅单文件）。支持秒（`1.5`）、分:秒（`1:30`）、时:分:秒（`0:01:30`）；默认跟随源视频 |

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
| `-p, --protocol <协议>` | 目标协议（默认 `motion photo`）：`fusion`、`micro video` (V1)、`motion photo` (V2)、`oppo`、`vivo`、`samsung`、`huawei`。运行 `lpb protocols` 查看完整矩阵 |
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

**执行**

| 选项 | 说明 |
|------|------|
| `-j, --parallel <数量>` | 最大并行任务数（默认：CPU 核心数，上限 5） |
| `-y, --yes` | 跳过所有确认提示。脚本自动化运行时的必要选项 |
| `--dry-run` | 仅列出计划操作，不实际处理文件 |
| `-v, --verbose` | 逐文件输出状态，而非仅显示汇总 |
| `--all-variants` | 生成所有协议 × 格式组合（仅单对模式）；输出到 `{目录}/{文件名}_variants/` |

### 默认输出位置

省略 `-o` 时，输出**不会**落到终端当前目录，而是跟随**输入**：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件对 | **照片（图片）所在目录**（照片和视频可能在不同文件夹，以照片为准） | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| 批量（`-d`） | 输入目录下的子文件夹，命名为 `{输入目录名}_{协议后缀}` | `lpb merge -d ./MyPhotos -p motion photo` → `./MyPhotos/MyPhotos_motionphoto/` |

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
lpb merge photo.jpg video.mp4 -p motion photo --key-timestamp 1:30.500 -y
```

- 时间格式：秒（`1.5`）、分:秒（`1:30`）、时:分:秒（`0:01:30`），内部按微秒写入各协议元数据。
- 仅单文件模式可用；批量模式（`-d`）传该参数会直接报错退出。
- 各协议存储方式不同，工具已自动适配：

| 协议 | 存储位置 |
|------|----------|
| Motion Photo / OPPO / vivo / Samsung / Fusion | XMP（OPPO / Fusion 同时写主照片时间戳字段） |
| Micro Video | XMP `MicroVideoPresentationTimestampUs` |
| HUAWEI | MP4 `covertime` 元数据 + 文件尾包封面帧号（不写 XMP） |

- 可与 `--all-variants` 组合，所有变体使用同一时间戳。
- 超出视频时长的值：HUAWEI 自动钳制到最后一帧；其余协议直接写入元数据。

---

## 配对方式

批量模式 (`-d`) 下，工具需要将图片与视频一一对应：

| 方式 | 配对依据 | 示例 |
|------|----------|------|
| `name`（默认） | 基础名称相同、扩展名不同 | `photo_001.jpg` + `photo_001.mp4` → 配对 |
| `cid` | Apple `ContentIdentifier` UUID 一致，与文件名无关 | `IMG_0002.HEIC` + `renamed.MOV` → 配对 |
| `vivo` | JPEG 尾部 + MP4 元数据中的 vivo 相机 ID | `vivo_photo.jpg` + `vivo_video.mp4` → 配对 |

`cid` 需要 `exiftool.exe` 位于可执行文件旁的 `Tools\` 目录中（所有分发包均自带）；`name` 与 `vivo` 无需外部工具——纯文件 I/O。

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

| 操作 | 命令 |
|------|------|
| 归档源文件 | `lpb merge -d ./Photos -p motion photo --after "move:./Archived" -y` |
| 移入回收站 | `lpb merge -d ./Photos -p motion photo --after recycle -y` |
| 保留源文件（默认） | `lpb merge -d ./Photos -p motion photo --after none -y` |

仅**合成成功**的文件对的源文件会受影响。

---

## 工作流示例

```powershell
# 批量转换为通用安卓格式
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y

# 递归批量 + 保留目录结构 + 归档源文件
lpb merge -d ./Photos -r -s -p motion photo -o ./Output --after "move:./Originals" -y

# 脚本批处理 + 错误日志
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "部分文件失败，详见 errors.log" }
```

---

## 协议兼容性一览

**合成** —— `lpb merge` 可输出的协议：

| 合成协议 | 支持设备 | 状态 |
|---|---|---|
| Fusion Motion Photo | Windows / Android（通用） | 🟡 测试中 |
| Google Micro Video | Windows / 小米 (旧版 MIUI) / Pixel | ✅ 可用 |
| Google Motion Photo | Windows / 小米 / Pixel | ✅ 可用 |
| OPPO O-Live Photo | Windows / 小米 / OPPO | ✅ 可用 |
| HUAWEI Moving Photo | 华为 / 荣耀 | ✅ 可用 |
| Samsung Motion Photo | Windows / Samsung | 🟡 测试中 |
| vivo Live Photo | Windows / vivo（≥ X300） | 🟡 测试中 |

**拆分** —— 单文件实况照片协议（暂不支持拆分，需要拆分请使用 GUI）：

| 拆分协议 | 支持机型 | 状态 |
|---|---|---|
| Apple Live Photo | iPhone / iPad | 🟡 测试中 |
| vivo Live Photo | vivo（≤ x300） | 🟡 测试中 |

---

## 退出码

| 退出码 | 含义 |
|:---:|---------|
| 0 | 全部任务成功完成 |
| 1 | 参数错误，或至少有一个任务失败 |
| 2 | 更新检查失败（网络 / GitHub 不可达） |
| 3 | 更新跳过——副本由 WinGet 管理（请用 winget） |
| 130 | 用户取消 (Ctrl+C) |

---

## 架构

CLI 与桌面 GUI 应用共享同一合成管线，逻辑定义在 `LivePhotoBox.Core` 类库中——两者调用同一 `LivePhotoMergeRunnerService.ProcessSinglePairAsync()` 方法，对 Core 的任何修复或更新均同时作用于两个界面。CLI 界面为纯英文，所有字符串编译时嵌入 `LivePhotoBox.Core.dll`。

---

## 故障排查

#### 提示未知协议
运行 `lpb protocols` 查看所有有效协议名称及缩写别名。

#### 所选格式不适用于该协议
运行 `lpb protocols` 查看兼容矩阵。例如，`heic+mp4-h265` 仅可用于 `huawei`。

#### 使用 `--pairing cid` 时提示找不到 exiftool
把 `exiftool.exe` 放到可执行文件旁的 `Tools\` 目录即可。

#### 输出文件扩展名与源文件不一致
正常现象。源文件为 HEIC 且选择了 JPEG 类格式时，输出使用 `.jpg` 扩展名。内部结构符合所选协议要求。

#### 提示"Permission denied"或文件被占用
关闭正在访问源文件的相册 App 或文件管理器。被其他进程锁定的文件无法在 Windows 上读取或移动。

---

## 获取帮助

- **文档：** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug 反馈 / 功能建议：** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **最新版本下载：** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **项目仓库：** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

如果这个项目对你有帮助，欢迎在 GitHub 上点个 ⭐ Star。

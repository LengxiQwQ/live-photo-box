# Live Photo Box CLI — 使用指南

[![最新发布](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=%E6%9C%80%E6%96%B0%E5%8F%91%E5%B8%83)](https://github.com/lengxiqwq/live-photo-box/releases) [![许可证](https://img.shields.io/badge/许可证-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![平台](https://img.shields.io/badge/平台-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![项目仓库](https://img.shields.io/badge/%E9%A1%B9%E7%9B%AE%E4%BB%93%E5%BA%93-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![反馈](https://img.shields.io/badge/反馈-Issues-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## 概述

Live Photo Box 同时提供图形界面与命令行两种形态。命令行入口 `livephotobox`（别名 `lpb`）专为脚本、AI 自动化调用设计；如需日常人工操作，请使用图形界面版本：[Microsoft Store](https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full) 或 [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)。

---

## 分发包说明

[Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 提供三种包：

| 包名 | 内容 | 适用场景 | PATH |
|---------|----------|----------|------|
| `*-x64-setup.exe` | GUI + CLI，安装向导一键安装 | 普通用户，想要完整桌面应用 | 安装时可选择加入 |
| `*-x64-portable.zip` | GUI + CLI，解压即用 | U 盘便携使用，或不想安装时试用 | 需手动添加 |
| `*-x64-cli.zip` | 纯 CLI，不含 GUI 及其运行时 | 服务器、脚本、CI/CD，最小体积 | 需手动添加 |

三种包均包含相同的 `livephotobox.exe` 及四个别名。纯 CLI 包体积最小。

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

### WinGet 安装

通过 WinGet 安装的副本（`winget install LengxiQwQ.LivePhotoBox`）**不使用内置更新**——安装、升级、卸载均由 WinGet 负责：

- `lpb update` 与 `lpb update-check` 仍会联网检查并在有新版时报告，但 `lpb update` **不会**下载或安装任何内容——它只打印 `Update with: winget upgrade LengxiQwQ.LivePhotoBox` 后退出。
- 更新 WinGet 副本：
  ```
  winget upgrade LengxiQwQ.LivePhotoBox
  ```
- 卸载同理：`winget uninstall LengxiQwQ.LivePhotoBox`。
- 不确定自己的副本是哪种渠道？运行 `lpb --info`——WinGet 副本会显示 `Channel: WinGet (CLI-only)`。

### 其他安装方式

便携版、便携包（GUI + CLI）和安装版副本由 `lpb update` 自行更新。发现新版本时，会打印新版本号和匹配的安装包，然后询问 `Update now? [Y/n]`——回车或输入 `y` 继续。安装包按安装类型自动选择：

| 安装类型 | 安装包 |
|---------|--------|
| Portable CLI-only（纯 CLI） | `*-x64-cli.zip` |
| Portable bundle (GUI + CLI)（便携包） | `*-x64-portable.zip` |
| Installer (Inno Setup, GUI + CLI)（安装版） | `*-x64-setup.exe` |

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

# 转换单个文件对（iPhone → Google 相册）
lpb merge photo.heic video.mov -p motionphoto -y

# 批量转换文件夹（→ 华为格式，自动确认；输出到 ./MyPhotos/MyPhotos_huawei/）
lpb merge -d ./MyPhotos -p huawei -y

# 把单文件实况照片拆回图片 + 视频
lpb split photo.jpg -y
```

---

## 命令

| 命令 | 说明 |
|------|------|
| `lpb protocols` | 查看协议 × 格式兼容矩阵与设备支持 |
| `lpb merge` | 合成图片 + 视频（单对或批量） |
| `lpb split` | 把单文件实况照片拆回独立的图片与视频 |
| `lpb repair` | 分析并修复实况照片元数据 |
| `lpb --info` / `lpb --version`（`-v`） | 查看版本、环境与内置工具版本 |

`update` / `update-check` 命令见上文「更新」一节。

### `protocols` — 查看协议 × 格式兼容矩阵与设备支持

运行 `lpb protocols` 可交互查看，或 `lpb protocols --json` 获取结构化输出。

**兼容矩阵** — 每个协议支持的输出格式：

| 协议 | JPEG+MP4 | JPEG+MOV | HEIC+MP4 | HEIC+MOV | HEIC+MP4 (H.265) |
|---|---|---|---|---|---|
| Micro Video | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
| Motion Photo | ✅ | ✅ | ✖️ | ✅ | ✖️ |
| OPPO O-Live | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| vivo Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| Samsung Motion Photo | ✅ | ✖️ | ✅ | ✖️ | ✖️ |
| HUAWEI Moving Photo | ✅ | ✖️ | ✅ | ✖️ | ✅ |

`✅` — 支持 &nbsp;|&nbsp; `✖️` — 不支持

`heic+mp4-h265`（索引 4）为华为原生 HEVC (H.265)。

**合成 — 设备支持：**

| 协议 | 支持设备 | 状态 |
|---|---|---|
| Micro Video | Windows / 小米 (旧版 MIUI) / Pixel | ✅ 可用 |
| Motion Photo | Windows / 小米 / Pixel | ✅ 可用 |
| OPPO O-Live | Windows / 小米 / OPPO | ✅ 可用 |
| vivo Live Photo | Windows / vivo（≥ X300） | 🟡 测试中 |
| Samsung Motion Photo | Windows / Samsung | 🟡 测试中 |
| HUAWEI Moving Photo | 华为 / 荣耀 | ✅ 可用 |

**拆分 — 设备支持**（CLI 已支持拆分，见下文 `split` 一节）：

| 协议 | 支持机型 | 状态 |
|---|---|---|
| Apple Live Photo | iPhone / iPad | ✅ 可用 |
| vivo Live Photo | vivo（≤ X200） | 🟡 测试中 |

**拆分 — 协议 × 格式兼容矩阵：**

| 协议 | keep | jpg+mov | heic+mov | jpg+mp4 |
|---|---|---|---|---|
| None（仅拆分） | ✅ | ✅ | ✅ | ✅ |
| Apple Live Photo | ✖️ | ✅ | ✅ | ✖️ |
| vivo Live Photo | ✖️ | ✖️ | ✖️ | ✅ |

**JSON 输出**（供脚本消费）——包含每个协议的索引、显示名、支持设备、状态与格式，以及拆分表：

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
| 批量含子目录，保留文件夹结构 | `lpb merge -d ./Photos -r -s -p motionphoto -o ./Output -y` |
| 预览（不创建任何文件夹） | `lpb merge -d ./Photos -p motionphoto --dry-run` |
| 自定义文件名模板 | `lpb merge -d ./Photos -p motionphoto -n "custom:{name}_{protocol}_{date}" -y` |
| 覆盖已存在输出而非自动重命名 | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| 自定义封面位置（视频 2.5 秒处） | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y` |

---

#### 完整选项参考

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
| `-p, --protocol <协议>` | 目标协议（默认 `motion photo`）：`micro video` (V1)、`motion photo` (V2)、`oppo`、`vivo`、`samsung`、`huawei`。运行 `lpb protocols` 查看完整矩阵。多词协议名也可写无空格形式（无需引号）：`microvideo`、`motionphoto` |
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

#### 默认输出位置

省略 `-o` 时，输出**不会**落到终端当前目录，而是跟随**输入**：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件对 | **照片（图片）所在目录**（照片和视频可能在不同文件夹，以照片为准） | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| 批量（`-d`） | 输入目录下的子文件夹，命名为 `{输入目录名}_{协议后缀}` | `lpb merge -d ./MyPhotos -p motionphoto` → `./MyPhotos/MyPhotos_motionphoto/` |

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
- 支持 `--key-timestamp`，所有变体应用同一时间戳

#### `--key-timestamp` — 自定义封面在视频中的位置

单文件合成时，实况照片的元数据会记录**封面（key photo）在视频时间轴上的位置**。默认情况下工具会跟随源视频自带的时间轴；指定本参数后则使用你给的值。

```powershell
# 封面位于视频第 2.5 秒处
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y

# 也支持 分:秒 / 时:分:秒 写法
lpb merge photo.jpg video.mp4 -p motionphoto --key-timestamp 1:30.500 -y
```

- 时间格式：秒（`1.5`）、分:秒（`1:30`）、时:分:秒（`0:01:30`）。
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

> **说明：** 省略 `-n` 时，**单文件**合成默认 `suffix`（输出在照片原目录，加协议后缀避免覆盖源照片）；**批量**合成默认 `keep`（输出进独立子文件夹，文件名不变）。显式传 `-n` 始终以你为准。

#### 完成后操作

| 操作 | 命令 |
|------|------|
| 归档源文件 | `lpb merge -d ./Photos -p motionphoto --after "move:./Archived" -y` |
| 移入回收站 | `lpb merge -d ./Photos -p motionphoto --after recycle -y` |
| 保留源文件（默认） | `lpb merge -d ./Photos -p motionphoto --after none -y` |

仅**合成成功**的文件对的源文件会受影响。

#### 工作流示例

```powershell
# 批量转换为 Google Motion Photo 格式
lpb merge -d ./DCIM/Camera -p motionphoto -o ./LivePhotos -y

# 递归批量 + 保留目录结构 + 归档源文件
lpb merge -d ./Photos -r -s -p motionphoto -o ./Output --after "move:./Originals" -y

# 脚本批处理 + 错误日志
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "部分文件失败，详见 errors.log" }
```

---

### `split` — 拆分单文件实况照片

`merge` 的反向操作：把单文件实况照片（图片 + 追加视频）拆回独立的图片与视频。两种运行模式：

| 模式 | 参数 | 使用场景 |
|------|------|----------|
| 单文件 | `<文件>`（按扩展名自动识别） | 拆分单个单文件实况照片 |
| 批量文件夹 | `-d` | 拆分目录内所有单文件实况照片 |

#### 使用示例

| 目标 | 命令 |
|------|------|
| 拆分单个文件（图片 + 视频输出到源文件旁） | `lpb split photo.jpg` |
| 批量拆分文件夹，自动确认 | `lpb split -d ./MyPhotos -y` |
| 把视频转换为 JPG+MP4 (H.264) | `lpb split photo.jpg -f jpg+mp4` |
| 预览（不实际处理） | `lpb split -d ./MyPhotos --dry-run` |
| 只拆分 vivo 实况照片 | `lpb split -d ./MyPhotos --pairing vivo -y` |
| 覆盖已存在输出 | `lpb split photo.jpg -w` |
| 导出所有变体（Apple + vivo + 无协议） | `lpb split photo.jpg --all-variants` |

---

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<文件>` | 单个待拆分的单文件实况照片：`.jpg .jpeg .heic .heif`（图片 + 追加视频） |
| `-d, --dir <文件夹>` | 包含单文件实况照片的文件夹（批量模式），所有检测到的实况照片都会被拆分 |
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
| `-p, --protocol <协议>` | 目标手机格式（默认 `none`）：`none`（仅拆分）、`apple`（Apple Live Photo）、`vivo`（vivo Live Photo，≤ X200）。本迭代仅拆分文件——尚未写入配对元数据 |
| `-f, --format <格式>` | 输出格式（默认：指定协议的首个可用格式）：`keep`（不转换）、`jpg+mov` (H.265)、`heic+mov` (H.265)、`jpg+mp4` (H.264) |
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
| 批量（`-d`） | 输入目录下的子文件夹，命名为 `{文件夹名}_split` | `lpb split -d ./MyPhotos` → `./MyPhotos/MyPhotos_split/` |

- 图片保持源基础名与扩展名；视频保持源视频的容器（`.mov` 或 `.mp4`）。
- 就地拆分：图片名与源文件冲突时自动重命名（`photo.jpg` → `photo (2).jpg`）；传 `-w` 则覆盖。
- 批量文件名保持源名不变——它们进入独立的 `{文件夹名}_split/` 子文件夹。
- `--dry-run` 会打印解析出的输出路径，且**不创建任何文件夹**。

#### `--all-variants` — 一键导出所有拆分变体

从单个单文件实况照片一键导出全部 7 组拆分变体（GUI 开放的所有协议 × 格式组合）。仅单文件模式——批量（`-d`）会被拒绝。适合开发者快速验证各协议的拆分输出。

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

输出：`split_photo_All_Variants/` 内含上述 14 个文件。文件名按 `{协议}_{格式}` 命名（小写 CLI 规范值，如 `-p apple -f jpg+mov` → `apple_jpg+mov`）——原文件名只进**文件夹名**；所有文件名/文件夹名一律不含空格。keep 变体的图片跟随源扩展名、视频跟随源视频容器（`.MOV` / `.MP4`）。此模式下 `-p` / `-f` / `-n` / `-w` / `--after` 均被忽略；`-j` 仍控制并行度。

#### 协议 × 格式矩阵

每个拆分协议支持的输出格式：

| 协议 | keep | jpg+mov | heic+mov | jpg+mp4 |
|---|---|---|---|---|
| `none`（仅拆分） | ✅ | ✅ | ✅ | ✅ |
| `apple`（Apple Live Photo） | ✖️ | ✅ | ✅ | ✖️ |
| `vivo`（vivo Live Photo） | ✖️ | ✖️ | ✖️ | ✅ |

`✅` — 支持 &nbsp;|&nbsp; `✖️` — 不支持

省略 `--format` 时，默认取该协议的首个可用格式：`none` 为 `keep`、`apple` 为 `jpg+mov`、`vivo` 为 `jpg+mp4`。传入协议不支持的 `--format` 会报错（可用 `lpb protocols` 查看）。

#### 配对过滤

`--pairing` 把拆分限定为某一协议，其他协议的实况照片会被跳过。`all`（默认）扫描全部。

| 值 | 协议 |
|----|------|
| `all` | 不过滤（默认） |
| `v1` | Micro Video (V1) |
| `v2` | Motion Photo (V2) |
| `oppo` | OPPO O-Live |
| `vivo` | vivo Live Photo |
| `samsung` | Samsung Motion Photo |
| `huawei` | HUAWEI Moving Photo |

#### 命名模板速查

split 只支持 `keep`（默认）和 `custom:模板`——没有 `suffix` 模式。模板同时命名图片与视频（各自保留自己的扩展名，如 `.jpg` / `.mov`）。

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

#### 工作流示例

```powershell
# 拆分单个实况照片，视频转换为 JPG+MP4 (H.264)
lpb split photo.jpg -f jpg+mp4 -y

# 批量拆分文件夹，只拆分 vivo 实况照片，自动确认
lpb split -d ./DCIM/Camera --pairing vivo -y

# 递归批量 + 保留目录结构 + 归档源文件
lpb split -d ./Photos -r -s -o ./Output --after "move:./Originals" -y

# 脚本批处理 + 错误日志
lpb split -d ./Photos -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "部分文件失败，详见 errors.log" }
```

---

### `repair` — 修复实况照片元数据

分析并修复现有实况照片文件的四类元数据问题：图片旋转、内嵌缩略图、HEIC 方向、视频旋转。图片：`.jpg .jpeg .heic .heif`；视频：`.mov .mp4`。

| 模式 | 参数 | 使用场景 |
|------|------|----------|
| 单文件 | `<文件>`（按扩展名自动识别） | 修复单个图片或视频 |
| 批量文件夹 | `-d` | 目录内所有媒体文件 |

#### 使用示例

| 目标 | 命令 |
|------|------|
| 修复单个文件 | `lpb repair photo.jpg` |
| 批量修复文件夹 | `lpb repair -d ./MyPhotos -y` |
| 预览（不写入） | `lpb repair -d ./MyPhotos --dry-run` |
| 只修图片旋转 | `lpb repair -d ./Photos --no-thumbnail --no-heic --no-video -y` |
| 修复所有设备文件 | `lpb repair -d ./MyPhotos --all-devices -y` |
| 同时复制完好文件 | `lpb repair -d ./MyPhotos --copy-perfect -y` |

---

#### 完整选项参考

**输入**

| 选项 | 说明 |
|------|------|
| `<文件>` | 单个待修复的图片或视频。图片：`.jpg .jpeg .heic .heif`；视频：`.mov .mp4` |
| `-d, --dir <文件夹>` | 扫描目录（批量模式）。每个媒体文件都会被分析，只有需要修复的文件才会被修复 |
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

> **HEIC 说明：** CLI 默认开启 HEIC 方向修复（四项全开）；GUI 的 `IsHeicRepairEnabled` 设置默认关闭。传 `--no-heic` 可对齐 GUI 的默认行为。

#### 默认输出位置

修复**不会覆盖**源文件。省略 `-o` 时：

| 模式 | 默认输出 | 示例 |
|------|----------|------|
| 单文件 | 源文件所在目录下的 `{文件名}_repaired{扩展名}` | `IMG_001.jpg` → `IMG_001_repaired.jpg` |
| 批量（`-d`） | `{输入目录}/{输入目录名}_repaired/`，文件名保持源名 | `lpb repair -d ./MyPhotos` → `./MyPhotos/MyPhotos_repaired/` |

#### Apple 实况照片过滤

默认只修复 **Apple 实况照片**——通过 `ContentIdentifier` UUID（图片和配对视频都携带）识别，没有该标识的文件自动跳过。传 `--all-devices` 可修复所有设备的文件。

#### 脚本模式（JSON 输出）

使用 `--json` 时，`repair` 会向 stdout 输出一份 UTF-8 编码的 JSON 文档——没有颜色、对齐和交互提示，脚本可以稳定解析，不受文件名长度或终端宽度影响。`--json` 隐含 `--yes`（跳过确认）。

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

JSON 为 UTF-8 编码；脚本管道读取时请按 UTF-8 解码（如 Python `json.loads(sys.stdin.buffer.read().decode("utf-8"))`）。

#### 工作流示例

```powershell
# 修复文件夹内所有实况照片（仅 Apple 实况照片，自动确认）
lpb repair -d ./DCIM/Camera -y

# 修复所有设备、递归、保留目录结构——先预览
lpb repair -d ./Photos --all-devices -r -s --dry-run

# 仅剥离缩略图——不动旋转和视频
lpb repair -d ./Photos --no-rotate --no-heic --no-video -y
```

---

### `--info` / `--version` — 查看版本与环境信息

| 选项 | 打印内容 |
|------|----------|
| `lpb --version` / `lpb -v` | 仅版本号（单行） |
| `lpb --info` | 版本号、安装详情（构建日期、运行时、平台、渠道、位置）、日志目录与当前日志文件、内置工具版本（exiftool、ffmpeg、…）、仓库与反馈入口、版权 |

两者均瞬间完成、不联网——只报本地环境信息；检查更新请用 `lpb update-check`。输出在交互终端中着色，重定向或设置 `NO_COLOR` 时自动回退为纯文本。

`-v` 是根级 `--version` 的快捷方式。在子命令内（如 `lpb merge -v`），`-v` 保持子命令自身的含义（`--verbose`）。

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

# LivePhotoBox CLI — 使用指南

**版本：** v2.1.1 &nbsp;|&nbsp; **平台：** Windows 10/11 x64 &nbsp;|&nbsp; **许可证：** MIT

[下载最新版](https://github.com/lengxiqwq/live-photo-box/releases) &nbsp;·&nbsp; [反馈问题](https://github.com/lengxiqwq/live-photo-box/issues) &nbsp;·&nbsp; [项目仓库](https://github.com/lengxiqwq/live-photo-box)

---

## 概述

`livephotobox` 是一个命令行工具，用于将图片与视频文件合并为手机相册可识别的实况照片。实况照片是同时包含静态图像与短视频片段的单一文件，在支持的相册中查看时会自动播放视频。

目前 CLI 仅支持**合成（Merge）**功能，拆分与修复请使用图形界面。

---

## 分发包说明

[Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases) 提供三种包：

| 包名 | 内容 | 适用场景 |
|---------|----------|----------|
| `*-x64-setup.exe` | GUI + CLI，安装向导一键安装 | 普通用户，想要完整桌面应用 |
| `*-x64-portable.zip` | GUI + CLI，解压即用 | U 盘便携使用，或不想安装时试用 |
| `*-x64-cli.zip` | 纯 CLI，不含 GUI 及其运行时 | 服务器、脚本、CI/CD，最小体积 |

三种包均包含相同的 `livephotobox.exe` 及五个别名。纯 CLI 包体积最小——省去了 WinUI 图形界面及其运行时（约 80 MB）。

### 如何更新

CLI 不含自动更新功能，但内置了版本检查命令：

```powershell
livephotobox update-check
```

该命令会查询 GitHub Releases API，将最新发布的版本与你当前安装的版本进行比较。如有新版本可用，会打印版本号和下载链接。

也可以手动访问 [Releases 页面](https://github.com/lengxiqwq/live-photo-box/releases)，下载对应包并替换旧文件。

---

## 快速开始

```powershell
# 查看协议 × 格式兼容矩阵
livephotobox protocols

# 转换单个文件对（iPhone → Google 相册）
livephotobox merge -i photo.heic -vid video.mov -p v2 -y

# 批量转换文件夹（→ 华为格式，自动确认）
livephotobox merge -d ./MyPhotos -p huawei -o ./Output -y

# 预览操作（不实际执行）
livephotobox merge -d ./MyPhotos --dry-run
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
livephotobox protocols
```

```
  Protocol          JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+H265
  ─────────         ────────   ────────   ────────   ────────   ────────
  Fusion               ✅          ✅          ──          ──          ──
  V1_MicroVideo        ✅          ✅          ──          ──          ──
  V2_MotionPhoto       ✅          ✅          ──          ✅          ──
  OPPO_OLive           ✅          ──          ──          ──          ──
  vivo_LivePhoto       ✅          ──          ──          ──          ──
  Samsung_MotionPhoto   ✅          ──          ✅          ──          ──
  HUAWEI_MovingPhoto   ✅          ──          ✅          ──          ✅
```

`✅` — 支持 &nbsp;|&nbsp; `──` — 不支持

`heic+mp4-h265`（索引 4）为华为原生 HEVC (H.265)。

**JSON 输出**（供脚本消费）：

```powershell
livephotobox protocols --json
```

---

### `merge` — 合成图片与视频

核心命令。两种运行模式：

| 模式 | 必需参数 | 使用场景 |
|------|-------|----------|
| 单对合成 | `-i` + `-vid` | 一张图片 + 一个视频 |
| 批量文件夹 | `-d` | 目录内自动按文件名配对 |

#### 使用示例

```powershell
# iPhone → Google 相册 (V2)
livephotobox merge -i IMG_001.HEIC -vid IMG_001.MOV -p v2 -y

# → 华为原生 HEVC
livephotobox merge -i photo.jpg -vid video.mp4 -p huawei -f heic+mp4-h265 -y

# 批量 → 华为，输出至 ./Output，自动确认
livephotobox merge -d ./MyPhotos -p huawei -o ./Output -y

# 批量含子目录，保留文件夹结构
livephotobox merge -d ./Photos -r -s -p v2 -o ./Output -y

# 预览（不修改文件）
livephotobox merge -d ./Photos -p v2 --dry-run

# 自定义文件名模板
livephotobox merge -d ./Photos -p v2 -n "custom:{name}_{protocol}_{date}" -y
```

---

## 完整选项参考

```
livephotobox merge [options]

═══ 输入 ═══
  -i, --image <文件>       图片文件（JPEG, HEIC, HEIF, PNG）。单对模式使用。
  -vid, --video <文件>     视频文件（MP4, MOV）。单对模式使用。
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
                             v1      — Google Motion Photo（旧版）
                             v2      — Google Motion Photo（新版）
                             oppo    — OPPO / 一加 O-Live
                             vivo    — vivo 实况照片
                             samsung — 三星动态照片
                             huawei  — 华为 / 荣耀动态照片
                             运行 'livephotobox protocols' 查看完整兼容矩阵。

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
```

---

### `update-check` — 检查更新

```
livephotobox update-check
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
livephotobox merge -d ./Photos -p v2 --after "move:./Archived" -y

# 移入回收站
livephotobox merge -d ./Photos -p v2 --after recycle -y

# 保留源文件（默认）
livephotobox merge -d ./Photos -p v2 --after none -y
```

仅**合成成功**的文件对的源文件会受影响。

---

## 工作流示例

```powershell
# iPhone → Google 相册
livephotobox merge -i IMG_1234.HEIC -vid IMG_1234.MOV -p v2 -y

# iPhone → 华为（原生 HEVC）
livephotobox merge -i IMG_1234.HEIC -vid IMG_1234.MOV -p huawei -f heic+mp4-h265 -y

# 批量转换为通用安卓格式
livephotobox merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y

# 递归批量 + 保留目录结构 + 归档源文件
livephotobox merge -d ./Photos -r -s -p v2 -o ./Output --after "move:./Originals" -y

# 脚本批处理 + 错误日志
livephotobox merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
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
运行 `livephotobox protocols` 查看所有有效协议名称及缩写别名。

### 所选格式不适用于该协议
运行 `livephotobox protocols` 查看兼容矩阵。例如，`heic+mp4-h265` 仅可用于 `huawei`。

### 使用 `--pairing cid` 时提示找不到 exiftool
CID 配对需要 `exiftool.exe` 位于可执行文件旁的 `Tools\` 目录中。所有分发包均自带该工具。

### 输出文件扩展名与源文件不一致
正常现象。源文件为 HEIC 且选择了 JPEG 类格式时，输出使用 `.jpg` 扩展名。内部结构符合所选协议要求。

### 提示"Permission denied"或文件被占用
关闭正在访问源文件的相册 App 或文件管理器。被其他进程锁定的文件无法在 Windows 上读取或移动。

---

## 获取帮助

- **使用文档：** [CLI-User-Guide-en.md](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide-en.md)（英文） · [CLI-使用指南-zh-CN.md](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97-zh-CN.md)（简体中文）
- **Bug 反馈 / 功能建议：** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **最新版本下载：** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **项目仓库：** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

如果这个项目对你有帮助，欢迎在 GitHub 上点个 ⭐ Star。

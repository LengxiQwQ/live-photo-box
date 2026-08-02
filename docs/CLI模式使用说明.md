# CLI 模式使用说明

> 版本：v2.1.0+ | 架构：LivePhotoBox.Core + LivePhotoBox.Cli

## 概述

CLI 工具 `livephotobox` 是一个独立的控制台程序，和 GUI 共享 100% 后端代码（合并管道、协议处理、视频转码）。
改 bug 一处修，两边都生效。

别名：`livebox` `lipbox` `lpb` `lpbx` `livephoto`，任意名字均可调用。下文统一用 `livephotobox` 示例。

开发时提供两个 `.cmd` 启动器：`livephotobox.cmd`（主）和 `lpb.cmd`（别名）。

## 快速开始

### 开发时（源码运行）

```powershell
# 用 .cmd 快捷脚本（推荐，任意名字均可）
.\livephotobox.cmd --version
.\livephotobox.cmd protocols
.\livephotobox.cmd merge -i "a.jpg" -vid "a.mov" -p v2

# 或者直接用 dotnet run
dotnet run --project LivePhotoBox.CLI -- --version
```

### 编译后（发布产物）

```powershell
# 编译 CLI 项目
dotnet build LivePhotoBox.CLI

# 产物路径（主名 + 别名 exe）
.\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\livephotobox.exe
.\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\lpb.exe         # 别名

# 直接运行
.\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\livephotobox.exe --version
.\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\livephotobox.exe protocols
```

### 安装后（便携版 / 安装版）

安装版（Inno Setup）会自动将 `livephotobox.exe` 所在目录注册到系统 PATH。
安装后在任意终端直接输入：

```powershell
livephotobox --version
livephotobox protocols
livephotobox merge -i "a.jpg" -vid "a.mov" -p v2
```

便携版需要手动加 PATH，或把 `livephotobox.exe`（及别名 exe）复制到已在 PATH 中的目录。

## 命令列表

### `livephotobox --version`
输出版本号。

```
lpb --version
→ 2.1.0.0
```

### `livephotobox --help`
显示所有可用命令。

### `livephotobox protocols`
列出 7 种协议 × 4 种输出格式的兼容矩阵。

```
lpb protocols
→ 表格形式，✅ 表示支持，── 表示不支持

lpb protocols --json
→ JSON 格式，供 AI Agent / 脚本消费
```

### `livephotobox merge`
将图片+视频合成为实况照片。

#### 单对合成
```powershell
livephotobox merge -i "photo.jpg" -vid "video.mp4" [选项]
```

| 选项 | 简写 | 说明 | 默认值 |
|------|:---:|------|:---:|
| `--image` | `-i` | 源图片路径 | 必填 |
| `--video` | `-vid` | 源视频路径 | 必填 |
| `--protocol` | `-p` | 协议：fusion\|v1\|v2\|oppo\|vivo\|samsung\|huawei | v2 |
| `--output` | `-o` | 输出目录 | 当前目录 |
| `--format` | `-f` | 格式：jpg+mp4\|jpg+mov\|heic+mp4\|heic+mov | 自动选第一个可用 |
| `--naming` | `-n` | 命名：keep\|suffix\|custom:<模板> | keep |
| `--parallel` | `-j` | 并行数 | min(CPU, 5) |
| `--yes` | `-y` | 跳过确认 | false |
| `--dry-run` | | 预览不执行 | false |
| `--verbose` | `-v` | 详细输出 | false |

#### 示例

```powershell
# 最简单的用法：默认 V2 协议，输出到当前目录
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -y

# 指定协议和输出目录
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -p oppo -o "D:\Output" -y

# 指定格式（注意：不是所有协议都支持所有格式）
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -p samsung -f heic+mp4 -y

# HUAWEI 协议 + 自定义命名模板
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -p huawei -n "custom:华为_{date}" -y

# 预览模式（不实际执行）
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -p v2 --dry-run

# 交互模式（不加 -y，会提示确认）
livephotobox merge -i "IMG_001.JPG" -vid "IMG_001.MOV" -p v2
→ Proceed? [y/N]
```

#### 批量合成（待实现）
```powershell
livephotobox merge -d "D:\Photos\LivePhotos" -p v2 -o "D:\Output" -j 8 -y
```

## 协议索引

| 索引 | 名称 | 简写 | 支持格式 |
|:---:|------|------|----------|
| 0 | Fusion | `f` | JPEG+MP4, JPEG+MOV |
| 1 | V1 (MicroVideo) | `v1` | JPEG+MP4, JPEG+MOV |
| 2 | V2 (MotionPhoto) | `v2`, `mp` | JPEG+MP4, JPEG+MOV, HEIC+MOV |
| 3 | OPPO (O-Live) | `oppo`, `o` | JPEG+MP4 |
| 4 | vivo (LivePhoto) | `vivo`, `v` | JPEG+MP4 |
| 5 | Samsung (MotionPhoto) | `ss`, `sam` | JPEG+MP4, HEIC+MP4 |
| 6 | HUAWEI (MovingPhoto) | `hw`, `h` | JPEG+MP4, HEIC+MP4 |

## 格式名称

| 格式 | 说明 |
|------|------|
| `jpg+mp4` | JPEG 图片 + MP4 视频容器 |
| `jpg+mov` | JPEG 图片 + MOV 视频容器 |
| `heic+mp4` | HEIC 图片 + MP4 视频容器 |
| `heic+mov` | HEIC 图片 + MOV 视频容器 |

## 退出码

| 退出码 | 含义 |
|:---:|------|
| 0 | 成功 |
| 1 | 参数错误 / 合成失败 |
| 130 | 用户取消 (Ctrl+C) |

## 系统 PATH 配置

### 便携版
解压后将 `livephotobox.exe` 所在目录添加到 PATH：

```powershell
# 临时（当前终端窗口）
$env:Path += ";D:\live-photo-box"

# 永久（用户级别）
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";D:\live-photo-box", "User")
```

### 安装版
Inno Setup 安装程序自动注册。无需手动配置。

### 商店版
MSIX 沙盒限制，不支持 CLI。仅通过 GUI 操作。

## 技术架构

```
LivePhotoBox.Core     ← 纯类库（所有合并管道、协议、服务）
    ↑               ↑
    │               │
Live Photo Box    LivePhotoBox.CLI
(WinUI GUI)       (控制台 CLI)
```

CLI 和 GUI 调用完全相同的 `LivePhotoMergeRunnerService.ProcessSinglePairAsync()`，
走同一套 HEIC 转换 → 视频转码 → 协议预处理 → 写入目标的管道。

# CLI 模式使用说明

> 版本：v2.0.3+ | 状态：测试工具

## 概述

CLI 模式内嵌在 `Live Photo Box.exe` 中，通过命令行参数触发。
不加参数正常启动图形界面，加 `--export-all-protocols` 进入批量导出模式。

## 全协议导出

将一个 Apple 实况照片（JPG + MOV 双文件）转换为所有单文件协议的变体。

### 命令

```powershell
& "路径\Live Photo Box.exe" --export-all-protocols <源图JPG> <源视频MOV> [输出目录]
```

### 参数

| 参数 | 必填 | 说明 |
|------|:---:|------|
| `--export-all-protocols` | ✅ | CLI 模式标识 |
| 源图 JPG | ✅ | Apple 实况照片的图片文件（.jpg） |
| 源视频 MOV | ✅ | Apple 实况照片的视频文件（.mov） |
| 输出目录 | ❌ | 输出目录，默认 `程序目录/ProtocolTestOutput` |

### 示例

```powershell
# 基本用法
& "D:\Projects\live-photo-box\Live Photo Box\bin\Debug\net9.0-windows10.0.19041.0\win-x64\Live Photo Box.exe" `
  --export-all-protocols `
  "D:\photos\IMG_6891.JPG" `
  "D:\photos\IMG_6891.MOV" `
  "D:\output"

# 省略输出目录（使用默认路径）
& "Live Photo Box.exe" --export-all-protocols "photo.jpg" "video.mov"
```

### 输出结构

所有文件平铺在输出目录下，以 `{协议名}_{格式}.{ext}` 命名：

```
输出目录/
├── Fusion_JPEG+MP4.jpg
├── Fusion_JPEG+MOV.jpg
├── V1_MicroVideo_JPEG+MP4.jpg
├── V1_MicroVideo_JPEG+MOV.jpg
├── V2_MotionPhoto_JPEG+MP4.jpg
├── V2_MotionPhoto_JPEG+MOV.jpg
├── V2_MotionPhoto_HEIC+MOV.heic
├── OPPO_OLive_JPEG+MP4.jpg
├── vivo_LivePhoto_JPEG+MP4.jpg
├── Samsung_MotionPhoto_JPEG+MP4.jpg
├── Samsung_MotionPhoto_HEIC+MP4.heic
├── HUAWEI_MovingPhoto_JPEG+MP4.jpg
├── HUAWEI_MovingPhoto_HEIC+MP4.heic
└── _progress.txt          ← 导出进度日志
```

共 13 个文件，覆盖 7 大协议体系的所有 JPEG 和 HEIC 变体。
格式矩阵与 GUI 合并页完全一致（见 `MergePage.xaml.cs` 的 `ProtocolFormatMap`）。

### 进度查看

导出过程中，`_progress.txt` 实时记录每个文件的导出状态：

```
Starting 13 jobs...
[1/13] Fusion JPEG+MP4 ... OK (5839517 bytes)
[2/13] Fusion JPEG+MOV ... OK (5840123 bytes)
...
Done: 13 OK, 0 FAIL
```

### 退出码

| 退出码 | 含义 |
|:---:|------|
| 0 | 全部导出成功 |
| 非 0 | 有文件导出失败（详见 `EXPORT_ERROR.txt`） |

## 实现原理

`App.xaml.cs` 构造函数中检测 `--export-all-protocols` 参数：

1. 如果存在 → 在线程池上运行 `ProtocolTestExporter.Run()`，完成后正常回到 GUI
2. 如果不存在 → 正常启动图形界面

导出逻辑通过 `LivePhotoMergeRunnerService.ProcessSinglePairAsync()` 为每个协议×格式组合执行完整合并管道（HEIC 转换、视频转码、协议预处理、写入），与 GUI 合并页使用完全相同的代码路径。

## 注意

- 当前版本为测试工具，仅支持从 Apple 双文件实况照片导出全协议变体
- 暂不支持单独指定协议、单独指定格式
- HEIC 变体通过管道内置的 JPG→HEIC 转换生成（依赖系统 HEIF 图像扩展；Windows 11 内置，Windows 10 需手动安装）

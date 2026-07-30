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

```
输出目录/
├── V1_MicroVideo/
│   └── JPEG+MP4/merge_sample_01.jpg
├── V2_MotionPhoto/
│   ├── JPEG+MP4/merge_sample_01.jpg
│   └── JPEG+MOV/merge_sample_01.jpg
├── OPPO_OLive/
│   └── JPEG+MP4/merge_sample_01.jpg
├── vivo_LivePhoto/
│   ├── JPEG+MP4/merge_sample_01.jpg
│   └── JPEG+MOV/merge_sample_01.jpg
├── Samsung_MotionPhoto/
│   ├── JPEG+MP4/merge_sample_01.jpg
│   └── JPEG+MOV/merge_sample_01.jpg
├── HUAWEI_MovingPhoto/
│   └── JPEG+MP4/merge_sample_01.jpg
├── Fusion/
│   ├── JPEG+MP4/merge_sample_01.jpg
│   └── JPEG+MOV/merge_sample_01.jpg
└── _progress.txt          ← 导出进度日志
```

共 11 个文件，覆盖 6 大协议体系的所有 JPEG 变体。

### 进度查看

导出过程中，`_progress.txt` 实时记录每个文件的导出状态：

```
Starting 11 jobs...
[1/11] V1 JPEG+MP4 ... OK (5839517 bytes)
[2/11] V2 JPEG+MP4 ... OK (5839974 bytes)
...
Done: 11 OK, 0 FAIL
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

导出逻辑位于 `Services/ProtocolTestExporter.cs`，直接调用项目内的 `LivePhotoMergeService.WriteLivePhotoAsync()`，与 GUI 合并页使用完全相同的代码路径。

## 注意

- 当前版本为测试工具，仅支持从 Apple 双文件实况照片导出全协议变体
- 暂不支持单独指定协议、单独指定格式
- 完整的 CLI 模式（所有软件功能）计划在后续版本实现

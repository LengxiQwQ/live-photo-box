# Fusion 隐藏备份 / Fusion Removal Backup

> 本文件记录本次“隐藏 Fusion 协议”的所有改动点，方便以后一键还原。
> 核心协议代码（`LivePhotoBox.Core/Services/Protocols/MotionPhotoFusionProtocol.cs`、注册表、检测器等）**未修改**。

## 一、代码注释 / 隐藏点

### GUI

1. `LivePhotoBox/Views/MergePage.xaml`
   - 原内容：`<ComboBoxItem x:Uid="MergePage_Protocol_Fusion"/>`
   - 现状：`<ComboBoxItem x:Uid="MergePage_Protocol_Fusion" Visibility="Collapsed"/>`
   - 还原方式：删除 `Visibility="Collapsed"` 属性。

2. `LivePhotoBox/Views/SplitPage.xaml`
   - 原内容：`<ComboBoxItem x:Uid="SplitPage_Match_Fusion"/>`
   - 现状：`<ComboBoxItem x:Uid="SplitPage_Match_Fusion" Visibility="Collapsed"/>`
   - 还原方式：删除 `Visibility="Collapsed"` 属性。

3. `LivePhotoBox/ViewModels/EditViewModel.cs`
   - 原内容：`[LivePhotoProtocolType.Fusion]   = "EditPage_Protocol_Fusion",`
   - 现状：`// [LivePhotoProtocolType.Fusion]   = "EditPage_Protocol_Fusion",`
   - 还原方式：取消注释。

4. `LivePhotoBox/ViewModels/MergeViewModel.cs`
   - `SelectedModeIndex` getter 增加回退：历史保存值 `0`（Fusion）时返回 `2`（Motion Photo V2）。
   - 还原方式：改回原来的单行 `get => AppSettingsService.GetValue(nameof(SelectedModeIndex), 2);`。

5. `LivePhotoBox/ViewModels/SplitViewModel.cs`
   - `MatchProtocolIndex` getter 增加回退：历史保存值 `1`（Fusion）时返回 `0`（所有单文件）。
   - 还原方式：改回原来的单行 `get => AppSettingsService.GetValue("SplitMatchProtocolIndex", 0);`。

### CLI

1. `LivePhotoBox.CLI/Infrastructure/ProtocolNameResolver.cs`
   - 原内容：`["fusion"]   = 0, ["f"] = 0,`
   - 现状：`// ["fusion"]   = 0, ["f"] = 0,`
   - 还原方式：取消注释。

2. `LivePhotoBox.CLI/Commands/ProtocolsCommand.cs`
   - 现状：合成协议矩阵、设备表、JSON 均从 `p = 1` 开始（跳过 Fusion）；设备表使用 `Skip(1).ToArray()`；协议索引行已移除 `fusion=0`。
   - 还原方式：将循环改回 `p = 0`，去掉 `Skip(1)`，恢复 `fusion=0` 索引行。

3. `LivePhotoBox.CLI/Commands/MergeCommand.cs`
   - 现状：`--protocol` 帮助文本移除 `fusion`；`DidYouMean` 移除 `"fusion"`；`RunAllVariantsAsync` 循环从 `p = 1` 开始。
   - 还原方式：恢复帮助文本中的 `fusion`，恢复 `DidYouMean` 数组中的 `"fusion"`，循环改回 `p = 0`。

4. `LivePhotoBox.CLI/Commands/SplitCommand.cs`
   - 原内容：`["fusion"]  = LivePhotoProtocolType.Fusion,`
   - 现状：`// ["fusion"]  = LivePhotoProtocolType.Fusion,`
   - 还原方式：取消注释；同时恢复 `--pairing` 帮助文本和 `DidYouMean` 中的 `fusion`。

## 二、文档删除内容备份

> 以下为从文档中删除的原始内容，按文件列出。还原时把对应行放回原文件即可。

### README.md

```
| Fusion Motion Photo | Windows / Android (universal) | 🟡 In testing |
```

### README.zh-CN.md

> 该文件原本没有 Fusion 表格行，未做 Fusion 删除。

### docs/CLI-User-Guide.md

```
| Fusion | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
```

```
| Fusion | Windows / Android (universal) | 🟡 In testing |
```

```
| `-p, --protocol <p>` | Target protocol (default `motion photo`): `fusion`, `micro video` (V1), `motion photo` (V2), `oppo`, `vivo`, `samsung`, `huawei`. Run `lpb protocols` for the full matrix. Multi-word names also work without spaces (no quotes needed): `microvideo`, `motionphoto` |
```

```
Output: `photo_variants/` (in the image's directory or specified output) contains 14 files:
photo_Fusion_JPEG+MP4.jpg
photo_Fusion_JPEG+MOV.jpg
```

```
# Batch to universal Android format
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y
```

```
| `--pairing <protocol>` | Only split live photos of this protocol: `all` (no filter, default), `fusion`, `v1` (MicroVideo), `v2` (MotionPhoto), `oppo`, `vivo`, `samsung`, `huawei` |
```

```
| `fusion` | Fusion |
```

### docs/CLI-User-Guide.zh-CN.md

```
| Fusion | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
```

```
| Fusion | Windows / Android（通用） | 🟡 测试中 |
```

```
| `-p, --protocol <协议>` | 目标协议（默认 `motion photo`）：`fusion`、`micro video` (V1)、`motion photo` (V2)、`oppo`、`vivo`、`samsung`、`huawei`。运行 `lpb protocols` 查看完整矩阵。多词协议名也可写无空格形式（无需引号）：`microvideo`、`motionphoto` |
```

```
输出：`photo_variants/`（或指定目录下的 `photo_variants/`）生成 14 个文件：
photo_Fusion_JPEG+MP4.jpg
photo_Fusion_JPEG+MOV.jpg
```

```
# 批量转换为通用安卓格式
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y
```

```
| `--pairing <协议>` | 只拆分该协议的实况照片：`all`（不过滤，默认）、`fusion`、`v1`（MicroVideo）、`v2`（MotionPhoto）、`oppo`、`vivo`、`samsung`、`huawei` |
```

```
| `fusion` | Fusion |
```

### docs/项目总览.md

```
| **🔗 实况照片合成 (Combo)** | 将任意静态图片 + 视频素材组合为标准实况照片。支持 Fusion / `Micro Video` / `Motion Photo` / OPPO / vivo / Samsung / HUAWEI 等协议，自动写入完整 `EXIF` + `QuickTime` 元数据 |
```

```
| `Fusion Motion Photo` | 多厂商 | 融合 Motion Photo + OPPO + vivo + Samsung 元数据为一个文件，可在 Google / OPPO / vivo / Samsung / 小米 / Windows 照片上播放（测试中） |
```

```
│       └── Protocols/               # 实况照片协议（1 抽象基类 + 7 注册实现）
│           ├── MotionPhotoFusionProtocol.cs / MicroVideoV1Protocol.cs
```

```
├── MotionPhotoFusionProtocol   (Id=0) — Fusion（Motion Photo + OPPO + vivo + Samsung 元数据融合）
```

```
            FUS["Fusion"]
```

```
| 支持的实况协议 | 7 (Fusion / Micro Video / Motion Photo / OPPO / vivo / Samsung / HUAWEI) |
```

### docs/发布流程.md

```
- 🚀 融合模式（作者自创）— 一份文件同时兼容 Google、OPPO、vivo、三星、小米相册及 Windows 照片应用
```

```
- 🚀 Fusion Mode (original creation) — one file works across Google, OPPO, vivo, Samsung, Xiaomi galleries and Windows Photos app
```

### docs/开发规范.md

```
  索引行（`fusion=0 …`）名字白、`=数字` 黄色；表格标题青色、✅/✖️/🟡 状态标记保留颜色。
```

## 三、说明

- `changelogs/` 目录属于历史发布记录，本次未改动。
- 核心协议代码、`LivePhotoBox.Core/Services/ProtocolFormatMatrix.cs`、`LivePhotoProtocol` 注册表均保留，未删除。

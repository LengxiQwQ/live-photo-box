# LivePhotoBox 照片操作历史记录 — 完整技术规范

> 本文档详细记录了 LivePhotoBox 在 Combo（合成）、Split（拆分）、Repair（修复）三个模块中注入的追踪标记，以及如何通过 ExifTool 或 XML 解析读取这些标记。

---

## 目录

1. [三种标记总览](#1-三种标记总览)
2. [标记一：Combo（合成）— XMP 命名空间属性](#2-标记一combo合成--xmp-命名空间属性)
3. [标记二：Split（拆分）— dc:subject 条目](#3-标记二splits拆分--dcsubject-条目)
4. [标记三：Repair（修复）— dc:subject 条目](#4-标记三repair修复--dcsubject-条目)
5. [协议检测](#5-协议检测)
6. [解析方法](#6-解析方法)
7. [完整时间线示例](#7-完整时间线示例)
8. [已知问题与注意事项](#8-已知问题与注意事项)

---

## 1. 三种标记总览

| 操作 | 注入位置 | 数据格式 | 注入方式 | 是否影响 Windows 识别 |
|------|---------|---------|---------|---------------------|
| **Combo** | `rdf:Description` 标签属性 | XML 命名空间属性 | 代码字符串拼接 | ❌ 不影响 |
| **Split** | `XMP-dc:Subject` 数组 | `LivePhotoBox:Split@{time}@v{ver}@{detail}` | exiftool `-XMP-dc:Subject+=` | ❌ 不影响 |
| **Repair** | `XMP-dc:Subject` 数组 | `LivePhotoBox:Repair@{time}@v{ver}@{detail}` | exiftool `-XMP-dc:Subject+=` | ❌ 不影响 |

### 为什么这些标记不影响实况照片识别

- **Combo 标记**：XMP 解析器（包括 Windows 11 的 Motion Photo 识别）会**静默忽略**不认识的名字空间属性。只要不往 `rdf:RDF` 内部插入 XML 注释、不破坏 XML 结构，任何平台都不会受影响。
- **Split/Repair 标记**：`dc:subject` 是 XMP 标准字段，用于存放"关键词/标签"。追加条目不影响其他解析器对 `GCamera:MotionPhoto`、`Container:Directory` 等关键字段的读取。

---

## 2. 标记一：Combo（合成）— XMP 命名空间属性

### 注入位置

`Services/Protocols/LivePhotoProtocol.cs` → `WrapXmp()` 方法

### 注入时机

用户点击"合成"时，根据所选协议调用对应的 `BuildXmpMetadata()`，内部调用 `WrapXmp(rdfTemplate, protocolKey)`。

### 注入内容

```xml
<rdf:Description rdf:about=""
    ...
    xmlns:LivePhotoBox="https://github.com/LengxiQwQ/live-photo-box"
    LivePhotoBox:Action="Combo"
    LivePhotoBox:Protocol="MotionPhotoV2"
    LivePhotoBox:Version="1.2.0"
    ...>
```

### 字段说明

| 属性 | 值示例 | 说明 |
|------|--------|------|
| `xmlns:LivePhotoBox` | `https://github.com/LengxiQwQ/live-photo-box` | 自定义命名空间声明。**这是识别文件是否由本工具生成的关键标记** |
| `LivePhotoBox:Action` | `Combo` | 固定为 `"Combo"`，表示该文件由合成操作生成 |
| `LivePhotoBox:Protocol` | `MicroVideoV1` / `MotionPhotoV2` / `OppoLivePhoto` | 合成时使用的协议标识（取每个协议的 `Key` 属性）。**注意：** 仅当协议有唯一标识时才注入 |
| `LivePhotoBox:Version` | `1.2.0` | 合成时的应用版本号，读取自 `Assembly.GetEntryAssembly()?.GetName()?.Version`，格式 `Major.Minor.Build`，获取失败则回退为 `"0.0.0"` |

### 注入逻辑细节

```csharp
string marker = " xmlns:LivePhotoBox=\"https://github.com/LengxiQwQ/live-photo-box\"" +
               $" LivePhotoBox:Action=\"Combo\"";
if (!string.IsNullOrEmpty(protocolKey))
    marker += $" LivePhotoBox:Protocol=\"{protocolKey}\"";
marker += $" LivePhotoBox:Version=\"{_appVersion}\"";
```

插入位置由标签类型决定：

| 协议 | rdf:Description 标签形式 | 标记插入位置 |
|------|-------------------------|-------------|
| MicroVideo V1 | `<rdf:Description ... attr="val"/>`（自闭合） | 在 `/>` 之前插入 |
| Motion Photo V2 | `<rdf:Description ... attr="val">`（普通开标签） | 在 `>` 之前插入 |
| OPPO Live Photo | `<rdf:Description ... attr="val">`（普通开标签） | 在 `>` 之前插入 |

### 最终生成的 XMP 结构

```xml
<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about=""
        xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
        xmlns:Container="http://ns.google.com/photos/1.0/container/"
        xmlns:LivePhotoBox="https://github.com/LengxiQwQ/live-photo-box"
        GCamera:MotionPhoto="1"
        GCamera:MotionPhotoVersion="1"
        LivePhotoBox:Action="Combo"
        LivePhotoBox:Protocol="MotionPhotoV2"
        LivePhotoBox:Version="1.2.0">
      <Container:Directory>
        <rdf:Seq>
          <rdf:li rdf:parseType="Resource">
            <Container:Item Item:Mime="image/jpeg" Item:Semantic="Primary" Item:Length="0" Item:Padding="0"/>
          </rdf:li>
          <rdf:li rdf:parseType="Resource">
            <Container:Item Item:Mime="video/mp4" Item:Semantic="MotionPhoto" Item:Length="{videoSize}" Item:Padding="0"/>
          </rdf:li>
        </rdf:Seq>
      </Container:Directory>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>
```

---

## 3. 标记二：Split（拆分）— dc:subject 条目

### 注入位置

`Services/LivePhotoSplitService.cs` → `SplitAsync()` 方法末尾（第 184-187 行）

### 注入时机

拆分完成后，分别对**输出的图片**和**输出的视频**各写入一条记录。

### 调用方式

```csharp
// 给图片打标记
await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
    imageOutputPath, "Split", "", token);

// 给视频打标记
await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
    videoOutputPath, "Split", "", token);
```

### 写入的标记条目

```
LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@
```

### 存储位置

写入到 `XMP-dc:Subject` 字段。这是一个 XMP 数组字段，可以包含多个值。工具使用 `+=` 操作符**追加**条目，不会覆盖已有内容。

```xml
<dc:subject>
  <rdf:Seq>
    <rdf:li>LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@</rdf:li>
  </rdf:Seq>
</dc:subject>
```

### 字段格式解析

```
LivePhotoBox:Split@{timestamp}@v{version}@{details}
```

| 段 | 说明 | 示例 |
|----|------|------|
| `LivePhotoBox:` | 固定前缀，用于识别为本工具标记 | - |
| `Split` | 操作类型 | `Split` |
| `{timestamp}` | ISO 8601 格式时间戳，带时区 | `2026-06-25T14:30:22+08:00` |
| `v{version}` | 应用版本号 | `v1.2.0` |
| `{details}` | （当前 Split 操作为空字符串） | - |

> **注意：** Split 操作的 details 目前为空。后续可以考虑在 details 中记录输出格式等信息，例如 `Format=JPEG+MP4`（格式同上 Repari 的 `Fix=` 风格）。

---

## 4. 标记三：Repair（修复）— dc:subject 条目

### 注入位置

`Services/LivePhotoRepairService.cs`

### 注入时机

- **JPEG/HEIC 修复后**（`RepairAsync` 第 637 行）：写入图片的修复记录
- **视频修复后**（`RepairVideoAsync` 第 759 行）：写入视频的修复记录

### 调用方式

**JPEG/HEIC 修复：**
```csharp
var fixes = new List<string>();
if (needsRotation) fixes.Add("Rotation");
if (hasThumbnail) fixes.Add("Thumbnail");
await TryWriteLivePhotoBoxMarkerAsync(targetPath, "Repair",
    fixes.Count > 0 ? $"Fix={string.Join("+", fixes)}" : "", token);
```

**视频修复：**
```csharp
await TryWriteLivePhotoBoxMarkerAsync(
    targetPath, "Repair", "Fix=Rotation", token);
```

### 写入的标记条目示例

```
LivePhotoBox:Repair@2026-06-25T14:35:10+08:00@v1.2.0@Fix=Rotation+Thumbnail
LivePhotoBox:Repair@2026-06-25T14:35:10+08:00@v1.2.0@Fix=Rotation
```

### 字段格式解析

同上 Split，但 details 字段有具体内容：

```
LivePhotoBox:Repair@{timestamp}@v{version}@{details}
```

其中 details 格式为 `Fix={value1}+{value2}`，当前可能的值：

| 值 | 含义 |
|----|------|
| `Fix=Rotation` | 旋转修复（JPEG 方向纠正 或 视频旋转） |
| `Fix=Thumbnail` | 缩略图修复 |
| `Fix=Rotation+Thumbnail` | 同时修复了旋转和缩略图 |

---

## 5. 协议检测

### 三种协议的 XMP 特征

**MicroVideo V1（Id=0）：**
```xml
<rdf:Description rdf:about=""
    xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
    GCamera:MicroVideo="1"
    GCamera:MicroVideoVersion="1"
    GCamera:MicroVideoOffset="{videoOffset}"
    GCamera:MicroVideoPresentationTimestampUs="0"/>
```
- 自闭合标签 `/>`（这是 V1 的显著特征）
- 只用 `GCamera` 命名空间，没有 `Container`/`Item`
- 用 `MicroVideoOffset` 标记视频偏移位置

**Motion Photo V2（Id=1）：**
```xml
<rdf:Description rdf:about=""
    xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
    xmlns:Container="http://ns.google.com/photos/1.0/container/"
    xmlns:Item="http://ns.google.com/photos/1.0/container/item/"
    GCamera:MotionPhoto="1"
    GCamera:MotionPhotoVersion="1"
    GCamera:MotionPhotoPresentationTimestampUs="0">
  <Container:Directory>
    <rdf:Seq>
      <rdf:li rdf:parseType="Resource">
        <Container:Item Item:Mime="image/jpeg" Item:Semantic="Primary" Item:Length="0" Item:Padding="0"/>
      </rdf:li>
      <rdf:li rdf:parseType="Resource">
        <Container:Item Item:Mime="video/mp4" Item:Semantic="MotionPhoto" Item:Length="{videoSize}" Item:Padding="0"/>
      </rdf:li>
    </rdf:Seq>
  </Container:Directory>
</rdf:Description>
```
- 普通开标签 `>`
- 包含 `Container:Directory` + `Item` 子元素
- `Item:Semantic="MotionPhoto"` 标记视频段

**OPPO Live Photo（Id=2）：**
- 包含 V2 的全部特征
- 额外增加 `OpCamera` 命名空间：
  ```xml
  xmlns:OpCamera="http://ns.oplus.com/photos/1.0/camera/"
  OpCamera:MotionPhotoPrimaryPresentationTimestampUs="0"
  OpCamera:MotionPhotoOwner="oplus"
  OpCamera:OLivePhotoVersion="2"
  OpCamera:VideoLength="{videoSize}"
  ```
- 需要额外写入 EXIF UserComment 标记：`oplus_10485792`

### 检测优先级

```
hasMicroVideo = GCamera:MicroVideo == "1" || GCamera:MicroVideoVersion != null
hasOppo       = OpCamera:OLivePhotoVersion != null || OpCamera:MotionPhotoOwner != null
hasMotionPhoto = GCamera:MotionPhoto == "1" || GCamera:MotionPhotoVersion != null
```

优先级：MicroVideo > OPPO > MotionPhoto（MotionPhoto 作为兜底判断）

---

## 6. 解析方法

### 方法一：使用 HistoryPage（本工具内置）

直接打开"照片历史"页面，选择文件夹，扫描后自动展示时间线。

### 方法二：ExifTool 命令行

**读取 dc:subject（获取 Split/Repair 历史）：**
```bash
exiftool -json -XMP-dc:Subject photo.jpg
```

输出示例：
```json
[
  {
    "SourceFile": "photo.jpg",
    "Subject": [
      "LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@",
      "LivePhotoBox:Repair@2026-06-25T14:35:10+08:00@v1.2.0@Fix=Rotation+Thumbnail"
    ]
  }
]
```

**读取完整 XMP（含 LivePhotoBox 命名空间属性）：**
```bash
exiftool -xmp:all -b photo.jpg
```
输出原始 XMP XML（包含 `<?xpacket...?>` 包装）。需要在解析前裁剪尾部的填充字节。

### 方法三：XML 解析（历史页面内部实现）

完整解析流程（参考 `HistoryViewModel.cs`）：

```
1. exiftool -xmp:all -b file.jpg → 获取原始 XMP XML
2. 裁剪 xpacket 包装：
   - 找到最后一个 <?xpacket end= → 截取到 > 为止
3. XDocument.Parse() 解析 XML
4. 查找 rdf:Description 元素
5. 读取 LivePhotoBox 命名空间属性（生成标记）：
   - 检查 xmlns:LivePhotoBox 是否存在
   - 读取 LivePhotoBox:Action → "Combo"
   - 读取 LivePhotoBox:Protocol → "MotionPhotoV2"
   - 读取 LivePhotoBox:Version → "1.2.0"
6. 读取 dc:subject/rdf:Seq/rdf:li（操作历史）：
   - 过滤以 "LivePhotoBox:" 开头的条目
   - 按 @ 分割解析
7. 检测协议类型（GCamera/OpCamera 属性）
8. 按时间排序所有条目，构建时间线
```

### 解析 dc:subject 条目（伪代码）

```
输入: "LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@Format=JPEG+MP4"

1. 去掉前缀 "LivePhotoBox:" → "Split@2026-06-25T14:30:22+08:00@v1.2.0@Format=JPEG+MP4"
2. 按 @ 分割 → ["Split", "2026-06-25T14:30:22+08:00", "v1.2.0", "Format=JPEG+MP4"]
3. parts[0] = "Split"  → action
4. parts[1] = "2026-06-25T14:30:22+08:00" → DateTime.TryParse → timestamp
5. parts[2] = "v1.2.0" → 去掉 v 前缀 → "1.2.0" → version
6. parts[3] = "Format=JPEG+MP4" → 按 = 分割 → key="Format", value="JPEG+MP4"
   → 替换 + 为 " + " → "JPEG + MP4" → description
```

---

## 7. 完整时间线示例

一张照片经历 Combo → Split → Repair 三次操作后，其 XMP 中应包含以下数据：

**LivePhotoBox 命名空间属性（rdf:Description 上）：**
```
xmlns:LivePhotoBox="https://github.com/LengxiQwQ/live-photo-box"
LivePhotoBox:Action="Combo"
LivePhotoBox:Protocol="MotionPhotoV2"
LivePhotoBox:Version="1.2.0"
```

**dc:subject 条目（追加在数组中）：**
```
LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@
LivePhotoBox:Repair@2026-06-25T14:35:10+08:00@v1.2.0@Fix=Rotation+Thumbnail
```

**解析后的时间线显示：**
```
🟢 Combo  · MotionPhotoV2 · v1.2.0
    2026-06-25 14:25:00（无时间戳，由 Combo 合成时间推断）

🔵 Split  · v1.2.0
    2026-06-25 14:30:22

🟠 Repair · v1.2.0
    2026-06-25 14:35:10
    修复 Rotation + Thumbnail
```

---

## 8. 已知问题与注意事项

### 8.1 SplitService 检测 URL 不一致

`LivePhotoSplitService.ContainsLivePhotoMarker()` 中检查的 LivePhotoBox 命名空间 URL 是：
```
http://ns.livephotobox.app/1.0/
```

而 `WrapXmp()` 实际注入的 URL 是：
```
https://github.com/LengxiQwQ/live-photo-box
```

**影响：** 拆分时 LivePhotoBox 的精确检测不会命中，但会回退到 GCamera/Container/OpCamera/MiCamera 的通用检测，因此拆分功能仍正常工作。

**建议：** 将 `ContainsLivePhotoMarker` 中的 URL 修改为与实际注入一致，并保留原有 URL 做兼容。

### 8.2 Combo 条目无时间戳

Combo 标记作为 XML 属性注入，只有版本号，**没有时间戳**。当前时间线中将 Combo 排在最前面，但不显示具体时间。如需添加时间戳，可以为 `WrapXmp()` 增加一个 `LivePhotoBox:Timestamp` 属性。

### 8.3 Split 的 details 为空

当前 Split 操作的 details 字段为空字符串，生成的条目格式为：
```
LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@
```
末尾带一个孤立的 `@`。这在解析时会被正确处理（`parts[3]` 为空字符串），但不美观。建议后续改为记录输出格式：
```
LivePhotoBox:Split@2026-06-25T14:30:22+08:00@v1.2.0@Format=JPEG+MP4
```

### 8.4 XML 注释的教训

**不要把 XML 注释（`<!-- -->`）放在 `rdf:RDF` 内部！** Windows 11 的 Motion Photo XMP 解析器遇到注释会直接中止识别，导致所有协议（V1/V2/OPPO）失效。移动端相册不受影响。

### 8.5 ExifTool 依赖

Split 和 Repair 的标记写入依赖 ExifTool（`-XMP-dc:Subject+=`）。如果 ExifTool 不可用，标记写入会被静默跳过，不影响功能。

---

## 附录：代码参考

| 功能 | 文件 | 关键方法 |
|------|------|---------|
| Combo 标记注入 | `Services/Protocols/LivePhotoProtocol.cs` | `WrapXmp()` |
| Split 标记写入 | `Services/LivePhotoSplitService.cs` | `SplitAsync()` 第 184-187 行 |
| Repair 标记写入 | `Services/LivePhotoRepairService.cs` | `TryWriteLivePhotoBoxMarkerAsync()` |
| ExifTool 快捷写入 | `Services/LivePhotoRepairService.cs` | `RunExifToolAsync()` |
| 历史记录解析 | `ViewModels/HistoryViewModel.cs` | `ParseXmp()`, `ParseHistorySubject()` |
| 历史记录数据模型 | `Models/FileHistoryInfo.cs` | `FileHistoryInfo`, `HistoryEntry` |
| 历史页面 UI | `Views/HistoryPage.xaml` | - |
| 历史页面逻辑 | `Views/HistoryPage.xaml.cs` | `SelectFolder_Click()` |
| 自定义命名空间 URL | - | `https://github.com/LengxiQwQ/live-photo-box` |

---

*文档版本：1.0 | 最后更新：2026-06-25*

# Live Photo Box — Edit Page 重构路线图

> 目标：把 Edit Page 从旧版“所有逻辑塞进一个 ViewModel”的结构，重构成清晰的 **C# Control Plane + Native Data Plane**。
>
> 本路线图只解决 **编辑页基础架构、媒体加载与预览、Native Inspection 接入、未来时间轴接口准备**。
>
> 本轮 **不实现完整时间轴编辑、不实现真正的帧增删重排、不实现最终媒体导出重写**。

---

# 总体原则

Edit Page 后续必须遵守以下边界：

```text
Native 决定：
这是什么
里面有什么
协议是什么
媒体结构是什么
视频在哪里
哪些数据会影响正式输出
以后应该怎么安全修改

C# 决定：
怎么显示
怎么播放
Loading 怎么表现
按钮是否可用
当前编辑状态
用户交互
页面状态
```

长期结构：

```text
EditPage.xaml
│
├── PhotoViewer
├── PureMediaViewer
├── Timeline UI
└── Inspector UI
        │
        ▼
   EditViewModel
        │
        ├── EditDocument
        ├── EditPreviewService
        ├── NativeInspectionAdapter
        └── TimelineProvider
                 │
                 ▼
        LivePhotoBox.Native
```

---

# 执行规则

每一个阶段完成并验收后，才能进入下一阶段。

禁止为了“顺便优化”扩大范围。

本路线图期间：

- 不重做整个项目。
- 不提前实现完整时间轴。
- 不为了未来 Linux / Web 强制重写 UI 层。
- 不把 UI-only 预览强行迁入 Native。
- 不允许继续把新逻辑堆进巨型 `EditViewModel`。
- 不允许 C# 自己重新实现协议识别。
- 不允许新代码重新依赖旧版协议猜测逻辑。
- 不允许为了显示缩略图或预览而改变正式媒体文件。

---

# EP0 — 旧 Edit Page 代码审计与清理

## 目标

先搞清楚旧 Edit Page 里有哪些东西。

不要直接开始加新功能。

## 需要分类的内容

当前旧逻辑至少需要检查：

```text
文件扫描
拖拽
文件列表
图片缩略图
视频缩略图
图片大图预览
视频预览
EXIF / metadata
协议判断
Native Inspection
时间轴生成
帧提取
封面帧
导出
临时文件
缓存
FFmpeg / 外部工具调用
CancellationToken
UI 状态
播放状态
错误状态
```

每一项必须被归类成：

```text
A. 保留
B. 搬到新 Service
C. 搬到 Native
D. 只作为旧版兼容临时保留
E. 删除
F. 后续时间轴阶段再处理
```

## 本阶段重点

不要因为代码旧就直接全部删除。

以下已有能力如果仍然可靠，应优先复用：

```text
PhotoViewer
PureMediaViewer
已有缩放 / 平移交互
拖放基础 UI
部分可靠缓存逻辑
已有 WinUI 控件
```

重点清除：

```text
ViewModel 内直接做媒体处理
ViewModel 内直接做协议解析
重复的媒体识别
旧时间轴与新 UI 强耦合
废弃的旧导出路径
没有调用者的旧字段
为了老 UI 留下的无效状态
```

## 完成标准

- 已列出旧 Edit Page 主要职责。
- 每项已经明确去留。
- 没有误删仍在 production 使用的路径。
- 新功能尚未继续堆进旧结构。

---

# EP1 — 建立 EditDocument

## 目标

建立新的“当前编辑媒体”统一模型。

ViewModel 不再到处保存零散路径与协议字段。

建议核心概念：

```text
EditDocument
```

## EditDocument 表达的内容

至少能够表示：

```text
PrimaryPath
MotionPath

MediaKind
Protocol

Width
Height

Duration
FrameRate

IsLivePhoto
IsPairComplete

OriginalKeyPhoto
CurrentKeyPhoto
```

具体字段名称可以根据项目现有模型调整，但语义必须清楚。

## 规则

`EditDocument`：

- 不负责 UI 绘制。
- 不负责解码。
- 不负责协议解析。
- 不直接调用 Native。
- 不持有巨量媒体 buffer。
- 不包含 `BitmapImage` / `ImageSource` / `MediaSource` 等 WinUI 显示对象。

它只是：

> 当前编辑对象的稳定、可观察、可扩展状态模型。

## 完成标准

用户打开任意媒体后，都可以用一个 `EditDocument` 表示当前编辑对象。

---

# EP2 — 瘦身 EditViewModel

## 目标

把 `EditViewModel` 从“万能类”变成真正的 ViewModel。

## 新 EditViewModel 主要负责

```text
CurrentDocument
HasDocument

IsLoading
ErrorMessage

PreviewKind

当前命令状态
Undo / Redo 状态
Save 状态

TimelineState
SelectedFrame
```

## ViewModel 不再负责

```text
HEIC 结构解析
MP4 / MOV 容器解析
Motion Photo offset 查找
协议识别
媒体抽帧
图片实际解码
视频 codec 处理
metadata 写入
正式媒体转换
```

## 规则

ViewModel 可以：

```text
调用 Service
调用 Native Adapter
更新 ObservableProperty
决定按钮 CanExecute
控制编辑器状态
```

但不能重新成为新的“超级类”。

## 完成标准

ViewModel 主要是状态和 orchestration。

重媒体逻辑已经有明确外部 owner。

---

# EP3 — 建立 EditPreviewService

## 目标

建立专门处理 UI 预览的 C# 层。

建议：

```text
EditPreviewService
```

## 负责

```text
图片预览加载
视频预览源创建
预览取消
预览缓存
切换媒体
加载失败
快速切换时防止旧结果覆盖新结果
```

## 继续复用

```text
PhotoViewer
PureMediaViewer
```

如果有必要，可以再增加：

```text
EditPreviewHost
```

统一管理：

```text
None
Loading
Image
Video
LivePhoto
Error
```

## 规则

这一层属于 UI-only。

所以：

```text
图片怎么显示
视频怎么播放
缩略图怎么加载
preview cache
display resize
```

可以继续在 C# / Windows UI 层完成。

## 完成标准

ViewModel 不再直接包含预览解码细节。

---

# EP4 — 建立统一 Open / Drag & Drop Pipeline

## 目标

把“拖一个东西进来”变成一条统一、可取消、可诊断的加载流程。

推荐流程：

```text
Drag / Open
    ↓
Validate input
    ↓
Cancel previous load
    ↓
Native Inspection
    ↓
Build EditDocument
    ↓
Load Preview
    ↓
Update UI
```

## 第一阶段支持

```text
JPG / JPEG
HEIC / HEIF
MOV
MP4
Live Photo / Motion Photo
```

## 必须处理

```text
快速连续拖入多个文件
损坏文件
不支持格式
缺少配对视频
Native inspection 失败
preview decode 失败
用户中途切换文件
页面关闭 / 导航离开
```

## 完成标准

任何旧加载任务都不能覆盖新文件。

失败不能导致页面崩溃。

---

# EP5 — 接入 Native Inspection

## 目标

所有“媒体事实”由 Native 提供。

C# 不重新判断协议。

## Native 应负责提供

例如：

```text
Media type
Protocol
Primary asset
Motion asset

Width
Height

Duration
FPS

Pair state

Embedded video offset
Embedded video length

Container identity

必要 metadata facts
```

## 规则

例如 Google Motion Photo：

```text
JPEG
└── embedded MP4
```

应该由 Native 判断：

```text
offset
length
container
protocol
```

C# 只负责：

```text
需要播放
→ 请求可播放 motion asset
→ PureMediaViewer
```

## 核心原则

```text
找到视频在哪里 = Native
播放视频 = C#
```

```text
HEIC 内部结构是什么 = Native
把图片显示出来 = C#
```

## 完成标准

Edit Page 不再出现独立于 Native 的正式协议判断逻辑。

---

# EP6 — 完成基础 Edit Page 可用状态

## 本阶段必须完成

| 功能 | 要求 |
|---|---|
| 拖入 JPG / JPEG | 完成 |
| 拖入 HEIC / HEIF | 完成 |
| 拖入 MOV / MP4 | 完成 |
| 拖入 Live Photo | 完成 |
| 图片大图预览 | 完成 |
| 图片缩放 / 平移 | 完成 |
| 视频播放 | 完成 |
| Loading 状态 | 完成 |
| Error 状态 | 完成 |
| Native 协议识别 | 完成 |
| 基础媒体信息 | 完成 |
| 快速切换文件 | 完成 |
| 旧加载自动取消 | 完成 |

## 本阶段明确不做

```text
真实完整时间轴
删除帧
添加帧
帧重排
播放区间正式修改
完整音频编辑
正式输出重编码
完整 Undo / Redo 编辑历史
```

UI 可以先存在。

未实现控件：

```text
Disabled
或
Placeholder
```

不能伪装成已经可用。

---

# EP7 — 时间轴接口准备

## 目标

现在不做时间轴，但让未来不需要重新推翻架构。

建议先准备：

```text
TimelineState

NotLoaded
Loading
Ready
Failed
```

以及：

```text
TimelineFrames
SelectedFrame
CurrentTime
Duration
```

建议预留：

```text
ITimelineProvider
```

当前允许：

```text
TimelineState = NotLoaded
TimelineFrames = Empty
```

## 未来接入方式

```text
EditDocument
    ↓
Motion Asset
    ↓
TimelineProvider
    ↓
Native / Media Backend
    ↓
EditViewModel
    ↓
Timeline UI
```

## 完成标准

Timeline UI 可以存在，但当前不需要真实抽帧。

---

# EP8 — 清除旧路径与最终验收

前面全部稳定后，开始清理旧代码。

## 清理目标

删除已经没有 production caller 的：

```text
旧 preview pipeline
旧协议识别逻辑
旧时间轴入口
旧 ViewModel 媒体操作
重复缓存
废弃字段
废弃 command
旧临时文件流程
无效 helper
```

不要为了“代码看起来干净”删除仍有调用者的兼容代码。

必须从调用链确认。

---

# 最终验收标准

本轮 Edit Page 重构完成时，应满足：

```text
用户拖入照片
→ 正确识别
→ 正常显示
→ 正常缩放 / 平移

用户拖入视频
→ 正确识别
→ 正常播放

用户拖入 Live Photo
→ Native 正确识别协议和 motion asset
→ 静态图片正常显示
→ 动态视频可以正常播放

用户快速切换文件
→ 旧任务自动取消
→ 不串图
→ 不串状态

损坏 / 不支持文件
→ 不崩溃
→ 有明确 Error 状态
```

架构上：

```text
EditViewModel 不再承担重媒体处理

EditDocument 已成为统一编辑对象

Preview 已由独立 C# Service 负责

Native 成为协议 / 媒体结构事实来源

时间轴已经留好接口，但没有被提前实现

旧版一体化 ViewModel 路径已经逐步退出
```

满足以上条件后：

```text
EDIT PAGE FOUNDATION = ACCEPT
```

然后再开始下一阶段：

```text
Timeline
→ Frame Model
→ Key Photo
→ Trim
→ Delete
→ Add
→ Reorder
→ Audio
→ Replace Video
→ Replace Image
→ Undo / Redo
→ Export
```

---

# 推荐执行顺序

```text
EP0 旧代码审计 / 清理规划
 ↓
EP1 EditDocument
 ↓
EP2 EditViewModel 瘦身
 ↓
EP3 EditPreviewService
 ↓
EP4 Open / Drag & Drop Pipeline
 ↓
EP5 Native Inspection
 ↓
EP6 基础 Edit Page 可用
 ↓
EP7 Timeline 接口准备
 ↓
EP8 删除旧路径 + 最终验收
```

不要跳阶段。

不要在 EP0～EP6 期间提前开始完整 Timeline 编辑。

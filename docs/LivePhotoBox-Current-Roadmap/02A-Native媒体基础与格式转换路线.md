# Phase 02A：Native 媒体基础与格式转换路线

> **状态**：进行中 / 核心 Native 架构已落地，待完成能力门禁  
> **前置条件**：`01-协议后端分流开关与路由审计路线.md`（Phase 01）已完成并验收  
> **后续路线**：`02B-来源实况协议清理与中性语义模型路线.md`、`03-自动化测试脚本与发布验证路线.md`  
> **核心原则**：
> - **C# = Control Plane，C++ Native = Execution / Data Plane**
> - **Rebuilt 生产路径 100% 杜绝外部 CLI 工具（`ffmpeg.exe`, `exiftool.exe`, `heif-enc.exe`, `jpegtran.exe` 等）调用**
> - **允许 Native 编译期/运行时直接链接成熟 C/C++ 库（如 FFmpeg libraries、WIC、libheif、libjpeg-turbo 等），严禁在托管层启动 CLI 子进程完成核心媒体处理**

---

## 1. 架构定位与职责边界

### 1.1 C# Control Plane (`LivePhotoBox.Core`)
- **职责**：
  - 负责 WinUI / CLI 交互编排与 Task Orchestration；
  - 承载不可变 DTO、请求对象 (`MediaConversionRequest`) 与结果事实模型 (`SourceMediaFacts`, `ExtractedMediaBundle`)；
  - 管理临时事务工作区生命周期 (`IMediaWorkspace` / `MediaWorkspace`) 与路径分配；
  - **双向不可变性校验**：提取与转换前计算源文件 SHA256，处理完成后再次断言源文件未被修改；
  - 通过 P/Invoke (`LibraryImport`) 桥接 C ABI，处理异常映射、取消令牌传递与诊断捕获。

### 1.2 C++ Native Execution Plane (`LivePhotoBox.Native`)
- **职责**：
  - **二进制容器解析**：JPEG APP 段、TIFF/EXIF、XMP RDF、HEIF item/iloc/iinf、ISO-BMFF 盒结构；
  - **实况格式识别与提取**：多厂商实况照片协议特征探测与只读切片提取；
  - **图片格式转换**：同容器无损结构复制，跨容器（如 HEIC <-> JPEG）使用进程内 Native 原生管线（如 Windows WIC）；
  - **视频探测与封装**：原生解析视频元数据（分辨率、时长、FPS、编解码、旋转矩阵、音频），执行零重编码的 MP4/MOV 容器流级 Remux 与视频转码；
  - **边界契约**：严格使用 C ABI、POD 结构体、不透明句柄（Opaque Handle）与显式缓冲区，C++ 类型与异常绝不穿透托管边界。

---

## 2. 当前已落地实现与架构资产

当前代码已成功建立 Native-First 基础，并交付以下粗粒度 C ABI 导出：

```c
/* Public C ABI (LivePhotoBox.Native/include/livephotobox_native.h) */
LPB_API lpb_result LPB_CALL lpb_inspect_media(
    lpb_context* context, const char* primary_path, const char* secondary_path, lpb_source_media_facts* out_facts);

LPB_API lpb_result LPB_CALL lpb_extract_media(
    lpb_context* context, const char* primary_path, const char* secondary_path,
    const lpb_source_media_facts* facts, const char* output_image_path,
    const char* output_video_path, const char* output_gainmap_path);

LPB_API lpb_result LPB_CALL lpb_probe_video(
    lpb_context* context, const char* video_path, lpb_video_item_facts* out_video_facts);

LPB_API lpb_result LPB_CALL lpb_remux_video(
    lpb_context* context, const char* input_video_path, const char* output_video_path, lpb_video_container target_container);

LPB_API lpb_result LPB_CALL lpb_convert_image(
    lpb_context* context, const char* input_image_path, const char* output_image_path,
    lpb_image_container target_container, int32_t quality, int32_t* out_reencoded);

LPB_API lpb_result LPB_CALL lpb_transcode_video(
    lpb_context* context, const char* input_video_path, const char* output_video_path,
    lpb_video_container target_container, lpb_video_codec target_codec, int32_t crf,
    char* out_encoder_used, size_t encoder_buf_len);
```

### 已实现并验证能力：
1. **源码结构**：`LivePhotoBox.Native/src/media/` 包含 `media_inspector.cpp`、`media_extractor.cpp`、`image_converter.cpp`、`video_converter.cpp`、`media_api.cpp`。
2. **多机型来源识别**：通过真机样本验证了 Apple 双文件、华为/荣耀 `LIVE_` 尾标、小米/Redmi/Google V1/V2、OPPO/OnePlus、vivo X300+ 3-item、vivo 旧款双文件、三星 SEF JPEG 及三星 `mpvd` HEIC 的识别与媒体区间解析。
3. **C# 托管服务收口**：`SourceInspector`、`SourceExtractor`、`ImageConverter`、`VideoConverter` 成为薄 control-plane 包装器。
4. **工作区安全**：`MediaWorkspace` 实现了工作区隔离与前后 SHA256 不可变性校验。

---

## 3. Phase 02A 未完成门禁与核心缺口

虽然 ABI 与基础骨架已经就绪，但 **“ABI exists ≠ capability complete”**。在宣布 Phase 02A 验收完成前，必须补齐以下 6 项真实门禁：

### 3.1 门禁一：真实 Native 视频转码 (Video Transcode)
- **当前现状**：当前 `lpb_transcode_video` 在 `target_codec != input_codec` 时仍执行 remux/passthrough，并未真正实现 H.264 ↔ HEVC 编解码转码。
- **目标要求**：
  - 必须支持 H.264 与 HEVC 之间的真实 Native 视频转码；
  - **实现方案**：建议直接在 C++ Native 中链接成熟媒体库（如 FFmpeg C libraries：`libavcodec` / `libavformat` / `libswscale`，或 Windows Media Foundation 硬件编码器 MFT），**严禁手写或从零造视频 Codec 轮子**，也严禁在 Rebuilt 路径调用 `ffmpeg.exe` 外部进程；
  - 转码必须正确填充实际使用的编码器名称（如 `libx264` / `hevc_nvenc` / `wmf_h264`），并在发生硬件回退时如实记录。

### 3.2 门禁二：容器感知的真实视频 Remux (Video Remux)
- **当前现状**：当前 `lpb_remux_video` 主要是文件复制 + `ftyp` brand 修正。
- **目标要求**：
  - 升级为真正的容器感知（Container-Aware）ISOBMFF / QuickTime Remux；
  - 在不重编码音视频 Sample 的前提下，正确重构 moov/trak/stbl/stsc/stco/co64 结构，妥善处理 MP4 与 MOV 之间的 timescale、handler type (`vide`/`soun`) 与 edit list (elst) 差异。

### 3.3 门禁三：可靠的视频属性探测 (Video Probe)
- **当前现状**：当前 Native probe 为第一版 ISO-BMFF 盒遍历探测。
- **目标要求**：
  - 严禁对无法解析的 FPS 默认伪装成 30 fps；**Unknown 必须保持 Unknown**；
  - 探测结果必须可靠包含：Codec (`H264`/`HEVC`)、宽/高、精准时长（微秒/秒）、FPS / TimeBase、旋转角度 (`0/90/180/270`)、是否存在音频轨以及色彩/HDR 标记（若有）。

### 3.4 门禁四：Native Context、取消令牌与诊断日志 (Diagnostics & Cancellation)
- **当前现状**：当前 Interop 多数传递 `nullptr` 或虚拟上下文，C# 仅在调用 P/Invoke 前检查一次 CancellationToken。
- **目标要求**：
  - C# 必须向 Native 传递有效的 `lpb_context*`；
  - Native 耗时操作（分块提取、图片转码、视频 remux/transcode）必须周期性检查 `lpb_is_cancelled(context)`，响应托管侧的取消请求；
  - Native 内部发生错误时必须写入 `context` 的 last_error 诊断信息，供 C# 获取结构化错误描述。

### 3.5 门禁五：图片元数据保留事实真实性 (Preservation Truthfulness)
- **当前现状**：当前使用 Windows WIC 进行跨容器图片转码（HEIC <-> JPEG），同容器执行结构复制。
- **原则要求**：
  - **严禁“转换成功即自动报告元数据已保留”**；
  - 对 EXIF、ICC、MakerNote、XMP、GainMap、HDR、Orientation 的保留情况必须以实际测试取证结果填写；
  - 未经证实或仅做尽力而为（BestEffort）未验证的项，**不得在 `PreservationReport` 中标记为 `Preserved`**；
  - WIC 是进程内 Native API，可作为当前有效方案；长期是否引入 `libheif` / `libjpeg-turbo` 视元数据保真度、跨平台和兼容性需求评估决定，不为理论纯洁性做无意义替换。

### 3.6 门禁六：Source Inspector 结构化加固 (Hardening)
- **当前现状**：`media_inspector.cpp` 包含部分字符串搜索与固定偏移推算。
- **目标要求**：
  - 后续逐步强化复用 `src/binary/`、`src/containers/`、`src/metadata/` 基础模块；
  - 避免 `media_inspector.cpp` 膨胀为单一庞大文件，将各厂商私有数据结构定位（如 Samsung SEF 目录解析、Huawei 尾部解析、ISO-BMFF 盒解析）沉淀到底层通用容器/元数据组件中。

---

## 4. Phase 02A 验收检查清单

在开启 Phase 02B 前，本阶段必须满足以下验收标准：

- [x] C ABI 包含完整的 inspect, extract, probe, remux, convert, transcode 接口
- [x] C# `MediaWorkspace` 具备前后 SHA256 不可变性校验与安全清理机制
- [x] 10 大主流实况照片格式的 Native 来源识别与提取测试通过
- [x] 生产 Rebuilt 路径 0 外部 CLI 进程调用
- [ ] 真实 Native 视频转码（H.264 ↔ HEVC）实现并完成测试验证
- [ ] 真实 Native 容器感知 MP4 ↔ MOV Remux 实现并完成测试验证
- [ ] 视频 Probe 不伪造默认 FPS，Unknown 保持 Unknown
- [ ] Native 调用链路打通真实 `lpb_context`、支持中途 Cancellation 与 LastError 捕获
- [ ] 图片转码 Preservation Report 如实反映验证结果，不虚标保留状态
- [ ] 通过 `verify.ps1 -Scope Release -Configuration Release` 门禁与真实机型样本测试

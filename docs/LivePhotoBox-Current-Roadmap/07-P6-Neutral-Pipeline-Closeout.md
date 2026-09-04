# P6 — Neutral Pipeline Closeout

> **Entry:** P1–P5 reliable  
> **Goal:** 把 Inspect → Extract → Clean → Convert 串成稳定 NeutralMediaBundle 生产线。

## Pipeline

```text
Source
→ Inspect
→ Extract
→ Clean
→ Convert only when required
→ post-clean inspection
→ NeutralMediaBundle
→ manifest / preservation outcomes
```

## Auxiliary representation gate

P6 在进入 P9 前必须确保 Neutral contract 能无歧义表达：

```text
GainMap/Auxiliary 已嵌在 PrimaryImage
vs
仅存在 detached working artifact
```

Target Writer 不能通过 `GainMap != null` 推断“需要再次 append”。如果当前 model/manifest 不能表达该事实，P6 必须先扩展 contract，再宣告 Neutral Pipeline 完成。

## Neutral Gate

必须服从：

```text
12-Neutral-Media-Contract.md
```

尤其：

- Neutral 不含 source live binding；
- image/video 是独立有效媒体；
- timing/orientation/preservation 语义明确；
- Target Writer 不得依赖 source vendor private state。

## Tests — 在 P6 当场完成

组合真实样本：

```text
Apple
Google/Xiaomi
OPPO/OnePlus
vivo
Samsung
Huawei/Honor
normal media
```

覆盖：

- keep；
- image conversion；
- video remux；
- video transcode；
- HDR/GainMap；
- cancellation；
- batch;
- disk errors；
- manifest/hash；
- Source Inspector final guard。

## Exit Criteria

- Neutral Contract 全部满足；
- P1–P5 不需要产品层特殊绕过；
- 对所有声明支持的来源均可得到明确 Neutral / Unsupported；
- 为 P8 产品接入提供稳定 API。


## AI 执行硬规则

1. **先读当前 HEAD，再动代码。** Roadmap 定义目标、边界和验收，不替代当前实现事实；文档中记录的审计点必须在执行时重新核对。
2. **只执行当前阶段。** 未满足当前 Phase Gate，不提前实现后续阶段，不以“顺手整理”为理由扩大范围。
3. **验证证据必须与风险匹配。** 任何影响媒体产物、协议 correctness、兼容性或 preservation 的 Process，都必须同时具备代码证据、自动化测试和真实媒体样本证据；纯 build/platform/diagnostic infrastructure 使用与其风险匹配的 build smoke、dependency report、fault injection、platform proof 等证据。真实媒体样本不是 P7 才开始。
4. **测试失败必须可定位。** 错误应携带阶段、能力、关键输入事实、失败类别和技术原因；CLI/产品层必须能把核心错误清楚呈现。
5. **Production runtime 不恢复外部媒体 CLI 子进程依赖。** `ffmpeg.exe`、`ffprobe.exe`、`ExifTool.exe`、`jpegtran.exe`、`heif-enc.exe`、`heif-dec.exe`、`magick.exe` 等可以用于研究、测试和独立验证，但不能重新成为正式处理后端。
6. **第三方 C/C++ 媒体库是候选能力，不是预先锁定的架构承诺。** 是否采用、如何裁剪、静态或动态链接、平台 backend 组合等，由 P4 的实际证据决定；不得因为 Roadmap 举例提到某个库就机械引入，也不得为了“全部自研”重复实现成熟 codec。
7. **C# 是 Control Plane；Native 是 result-affecting Data Plane。** 产品级命名、目标目录、队列、UI/CLI orchestration 由 Core 管理；底层临时文件、原子 replace/publish 等事务 primitive 可以保留在 Native，但应置于明确的 platform/filesystem boundary。UI-only 的缩略图、预览、显示缩放和缓存不强制进入 Native。
8. **不猜协议事实，也不猜 Writer 写入位置。** 字符串命中、固定 offset、同文件名、magic number、`hit + N` 只能用于 research/candidate/evidence，不能直接成为 destructive authority 或 production Writer authoritative write location。
9. **源文件默认不原地覆盖。** Destructive/repair/write 流程优先使用新文件或安全临时输出；只有在结构与媒体验证通过后，才进入正式 publish。失败或取消不得留下“看起来成功”的半成品。
10. **所有 Done 都由证据宣布。** 代码存在、API 返回成功、文件能打开、播放器能播放，都不能单独证明阶段完成。
11. **当前 Roadmap 不主动修改用户可见文档。** README、CLI 用户手册、商店/Release 用户说明等，只有用户明确要求时才更新。

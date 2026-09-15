# P4-R6 — Video Backend Differential / Windows Backend Freeze

> **性质：** 技术选型与实证轮。  
> **目标：** 不因“当前已经写了 Media Foundation”而直接继承结论；使用真实 Live Photo 视频，对 Media Foundation、minimal libav*、必要时 hybrid 进行 differential，冻结 P5 的 Windows video backend。

---

## 1. 为什么这一轮必须独立

视频是 P4 中仍明确需要“比赛”的主要 backend 决策。

候选：

```text
A. Windows Media Foundation
B. minimal FFmpeg libav* libraries
C. hybrid per-capability backend
```

禁止：

```text
ffmpeg.exe production subprocess
```

当前已有 MF implementation 只能算候选 A 的现状基础，不是自动获胜理由。

---

## 2. 必须保持的 ownership 边界

无论最后选择谁：

```text
LivePhotoBox project-owned:
ISO-BMFF / MOV / MP4 structural truth
vendor/protocol metadata semantics
container mutation correctness
preservation decision

Video backend:
codec decode/encode/transcode
必要的通用媒体能力
```

如果 project-owned ISO-BMFF engine 已能可靠做 passthrough/remux：

> 不允许仅因为引入 libav* 就强制换掉 project-owned container truth。

---

## 3. Benchmark Corpus

尽量覆盖项目真实来源：

```text
Apple H264 MOV
Apple HEVC MOV
Google / Xiaomi MP4
OPPO / OnePlus
vivo
Samsung
Huawei / Honor
```

并覆盖可获得的：

```text
audio / no-audio
rotation
10-bit
HDR/color metadata
odd dimensions
VFR / timing variants
不同 profile / level
```

如果某项真实样本缺失，应明确 coverage gap；不能把 synthetic 结果当成真实设备兼容性。

---

## 4. 比较维度

决策顺序遵守总纲，不允许“只看速度”。

至少比较：

```text
1 correctness
2 real-device compatibility
3 preservation / color / HDR / timing
4 stability / malformed / failure behavior
5 output quality
6 performance / hardware acceleration
7 maintainability
8 portability
9 license / distribution
10 package delta
11 dependency duplication
```

具体观察：

```text
output structural validity
codec profile / level
audio mapping
rotation
duration / timing
VFR behavior
color / HDR metadata
CPU
GPU
elapsed time
memory
cancel behavior
failure diagnostics
software fallback
binary/package delta
maintenance burden
```

---

## 5. Hardware / Software Backend Identity

如果 MF 或 libav 路径会选择：

```text
hardware encoder
software encoder
fallback
```

必须可诊断：

```text
实际选择了什么
为什么选择
fallback 是否发生
fallback 是否改变质量/profile/HDR/preservation
```

不能出现：

```text
硬件失败
→ 静默换软件/换参数
→ output semantics 改了
→ 仍报告“同样 preserved”
```

---

## 6. minimal libav* 的规则

如果为了比较引入 libav*，必须尽量以：

```text
library integration
minimal feature build
```

形式评估。

不能为了 benchmark 方便就把完整 FFmpeg CLI/runtime universe 直接视为正式方案。

候选 B 的 package delta 必须是真实可比较的最小方案，而不是一个人为膨胀的 full build。

---

## 7. 决策输出

本轮最终必须冻结一个明确结论：

### 可能 1

```text
WindowsVideoBackend = Media Foundation
```

### 可能 2

```text
WindowsVideoBackend = minimal libav*
```

### 可能 3

```text
Hybrid
- capability X → MF
- capability Y → libav*
- project-owned remux → existing ISO-BMFF engine
```

Hybrid 完全允许，但不能变成“哪个失败就随便试另一个”的不可诊断 fallback tree。

每种 capability 的 owner 必须明确。

---

## 8. 本轮应完成的结果

- 可重复的 video differential harness/evidence；
- 真实样本矩阵；
- candidate build/runtime/package delta；
- correctness/preservation/quality/performance 对比；
- hardware/software/fallback 行为；
- final decision record；
- P5 使用哪个 video backend 已冻结；
- 未获选候选不应无理由进入 production package；
- external `ffmpeg.exe` 不成为 production runtime。

---

## 9. 验收标准

全部满足才可进入 R7：

1. 至少 MF 与实际可行的 minimal libav* / approved alternative 有公平比较，或有充分证据证明某候选无法满足门槛；
2. 使用真实 Live Photo 视频，而不是只用 synthetic；
3. output structural validity 有独立验证；
4. timing/audio/rotation/profile 等关键语义有对比；
5. HDR/color/10-bit 对适用样本有证据；
6. hardware/software path 与 fallback 可诊断；
7. performance 数据不是唯一决策依据；
8. package/runtime delta 有实际数字或可信 baseline；
9. project-owned ISO-BMFF truth 没被 generic backend 取代；
10. final Windows P5 backend 已明确冻结；
11. hybrid 若被选中，capability ownership 清楚；
12. 未获选 dependency/runtime 不无理由留在正式包；
13. production 无 ffmpeg.exe/ffprobe.exe subprocess；
14. 决策证据足以让后续 P5 不重新开技术选型会。

---

## 10. 以下情况不得通过

任一出现即 `R6 BLOCKED`：

- “当前 MF 已经能跑，所以继续 MF”；
- 只用一两个简单 MP4 benchmark；
- 只看 FPS/耗时，不看 correctness/preservation；
- libav 候选用 full FFmpeg CLI，而 MF 用精简 native，导致不公平 package 比较；
- 产品自身 probe 读回自身 output 就宣告正确；
- rotation/audio/timing/HDR 被忽略；
- hardware fallback 改变质量却无记录；
- real-device/sample compatibility 证据不足却声称全面胜出；
- 最后仍然说“P5 再决定视频用谁”。

---

## 11. 验证与测试

### 每个候选至少验证

```text
probe
passthrough/remux where applicable
transcode
audio
rotation
timing
cancel/failure
hardware/software
malformed/unsupported input
```

### Independent validation

至少从以下类型中取得独立证据：

```text
independent media parser/decoder
reference structural inspection
真实设备/系统导入播放（当此项是决策依据时）
```

外部 CLI 可用于 benchmark/独立验证，但不得出现在 production code path。

本轮通过的核心含义：

> Windows 视频 backend 已经由证据选定，P5 只实现 Converter contract，不再重新讨论 MF 还是 FFmpeg。

# P4-Audit — Fresh Independent Architecture / Correctness Audit

> **角色：** Fresh Auditor。  
> **性质：** 独立、默认只读。  
> **目标：** 不相信 Implementer 报告，从 P4 Roadmap → production code → data flow → build/dependency/runtime → tests 重新反向判断 P4 是否真的形成稳定地基。

---

## 1. Auditor 不相信什么

不能把以下内容直接当 acceptance evidence：

```text
Implementer 说完成了
commit message
closeout report
测试名字
README
“全部 PASS”文字
旧 AI 审计结论
```

它们只能作为定位线索。

最终证据来自：

```text
当前 production code
真实调用链
实际 CMake/dependency graph
实际 runtime/package
真实 test scope
RealSamples
独立验证
```

---

## 2. 审计主线

Auditor 应从 P4 原始 17 项 Exit Gate 出发，逐项反查。

### Architecture

确认：

```text
portable protocol/container truth
        ↕
Media Backend
Platform Backend
```

是真实 data flow，不只是目录名。

重点检查：

- Windows API 是否仍污染 protocol authority；
- codec library 是否成为 vendor truth；
- public ABI 是否泄漏 platform/C++ type；
- C# 是否重新承担 result-affecting pixel/data-plane logic；
- legacy production fallback 是否复活。

### Platform / Transaction

确认：

- filesystem/publish 是否真的收口；
- destructive path 是否仍有绕过 canonical authority/transaction 的 lower-level入口；
- exact-object/identity/rollback semantics 是否保留；
- 是否存在第二套 publish primitive。

### Build / Dependency

确认：

- CMake 是否真是 canonical；
- vcpkg/approved dependency path 是否可复现；
- test/product 是否加载正确 Native artifact；
- dependency feature/runtime 是否和文档一致；
- build graph 是否真正分出 portable core / Windows backend / media backend。

### Media Backend

分别检查：

```text
JPEG
HEIC/HDR
Video
```

不能只看“库已经链接”。

必须检查实际 product data flow 是否经过 approved backend，以及 fallback/unsupported 行为。

---

## 3. Portability 审计的正确尺度

Auditor 不应要求：

```text
Linux 已经能发布
Web 已经能跑
所有 Windows API 都消失
```

真正要判断的是：

> 本可平台无关的 protocol/container/neutral truth 是否仍被 Windows implementation detail 锁死。

允许 Windows-specific：

```text
filesystem backend
atomic publish
最终获选的视频 backend
有明确证据保留的 WIC capability
UI layer
```

禁止“为了跨平台形式主义”把合法 Windows 优势判成 blocker。

---

## 4. Preservation / RealSample 审计

重点检查：

```text
JPEG metadata/HDR preservation claim
HEIC auxiliary/GainMap ownership
HDR/SDR degradation reporting
video rotation/timing/audio/color
P1–P3 regression
```

任何 `Preserved` / compatibility claim 都要问：

```text
证据是什么？
是 RealSample 吗？
是 independent validation 吗？
还是产品自己读自己？
```

---

## 5. Audit 输出要求

每一项至少给：

| 项目 | 代码/数据流证据 | 测试/样本证据 | 状态 | 严重性 | Blocker |
|---|---|---|---|---|---|

并最终给：

```text
READY FOR VERIFICATION
```

或：

```text
REJECT — BLOCKERS REMAIN
```

本地 Auditor 不宣布：

```text
P4 ACCEPT / COMPLETED
```

---

## 6. 必须判为 blocker 的典型情况

- P4 Exit Gate 任一关键项实际上未实现；
- CMake 文档说 canonical，但正式 build/test 仍依赖旧 project；
- portable_core 仍直接链接 Win32/WIC/MF；
- P1–P3 destructive safety 在 P4 重构后退化；
- backend abstraction 存在，但业务仍绕过它直接调用 WIC/MF/library；
- libheif 被当作 vendor protocol authority；
- RealSample coverage 被 synthetic 替代；
- HDR/GainMap degradation 被标成 preserved；
- video backend freeze 没有公平 differential；
- external media CLI 仍在 production；
- package 带有无法解释用途的重复 codec/runtime；
- fallback tree 不可诊断；
- public C ABI 泄漏 Windows/C++ implementation type；
- 为了测试绿灯降低断言或跳过失败类别。

---

## 7. Auditor 的验证方式

Auditor 可以运行少量高价值复核：

```text
source/static scan
call-chain tracing
CMake/dependency graph inspection
public export inspection
selected targeted tests
selected RealSample replay
package/runtime scan
portable-core link/build inspection
```

但不负责“帮 Implementer 把问题修好”。

发现 blocker：

```text
→ 报告
→ 返回对应 R2–R7
→ 修复后重新 fresh audit
```

Audit clear 的含义只有：

> 架构与代码审查层面没有发现阻止 P4 进入正式 Verifier 的问题。

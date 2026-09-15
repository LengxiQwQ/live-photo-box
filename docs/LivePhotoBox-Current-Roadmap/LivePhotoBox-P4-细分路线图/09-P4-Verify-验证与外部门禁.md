# P4-Verify — Fresh Verification / External Gate Preparation

> **角色：** Fresh Verifier。  
> **性质：** 只验证，不修 production source。  
> **目标：** 用实际 clean build、targeted tests、RealSamples、independent validation、package/runtime evidence 证明 P4 当前 HEAD 可进入 External Chief Gate。

---

## 1. Verifier 与 Auditor 的区别

Auditor 主要回答：

> 架构和生产代码看起来是否真的满足 P4？

Verifier 回答：

> 在当前 HEAD、当前构建和真实样本上，它实际上是否能被重复证明？

Verifier 不应在失败后顺手修改 production source。

失败：

```text
→ FAIL
→ 返回 Implementer
→ 修复
→ 再 fresh verify
```

---

## 2. 验证前置

必须确认：

- 当前 HEAD 与待验收实现一致；
- reconcile/state/task 没有 stale；
- build/test 不会加载旧 Native artifact；
- RealSample 路径真实可用；
- test filter/scope 确实包含所声称的类别；
- R6 冻结的视频 backend 与当前 production build 一致；
- Audit 已无 blocker。

---

## 3. Build Verification

至少实际执行并保留结果：

```text
clean CMake configure
clean Windows x64 Native build
required Debug/Release build
portable_core independent build
dependency restore/reconfigure proof
C ABI/runtime smoke
```

重点验证：

```text
artifact identity
public exports
version
backend/runtime capability info
test/product DLL loading path
```

---

## 4. Targeted Regression Verification

按照影响范围验证：

### P1

```text
Inspector truth
protocol/container recognition
malformed/adversarial relevant cases
```

### P2

```text
exact extraction
artifact identity
transaction
rollback
RealSamples
```

### P3

```text
destructive authority
stale/replay/TOCTOU protection
real filesystem transaction
preservation
post-clean verification
RealSamples
```

P4 不能以“只是基础设施”作为跳过 P1–P3 回归的理由。

---

## 5. P4 Foundation Verification

### Platform

```text
canonical filesystem/publish path
real failure behavior
UTF-8/path boundary
no public Windows ABI leakage
```

### JPEG

```text
real decode
encode
lossless transform
orientation
metadata/preservation evidence
independent decode/inspection
```

### HEIC/HDR

```text
real primary decode/encode
auxiliary/GainMap foundation
10-bit/HDR where applicable
no silent SDR degradation
independent structural/decode evidence
```

### Video

```text
frozen backend actually used
representative real videos
audio/rotation/timing
HDR/color where applicable
hardware/software identity
failure/cancel
independent validation
```

---

## 6. Package / Runtime Verification

实际检查最终 Windows package/runtime：

```text
Native DLL/dependencies
codec binaries/plugins
duplicate runtime
unused runtime
external CLI absence
package footprint
dependency versions
```

所有进入包的 native binary/feature 都应能对应 R7 purpose table。

---

## 7. Portable-Core Verification

不能只看 target 名字。

实际验证：

```text
portable_core can build independently
does not link Win32 filesystem backend
does not link WIC
does not link Media Foundation
does not duplicate protocol sources
```

如果做了 optional Clang/Emscripten smoke，也应确认它只是 compile pollution proof，不被宣传成产品支持。

---

## 8. P4 17 项 Exit Gate 最终矩阵

Verifier 必须重新逐项给状态。

建议状态只用：

```text
PASS
FAIL
NOT RUN
BLOCKED
```

任何必要项为：

```text
FAIL
NOT RUN
BLOCKED
```

都不能输出 ready。

不能用：

```text
“应该没问题”
“大部分通过”
“理论可行”
```

替代真实验证。

---

## 9. 以下情况必须 FAIL

- clean build 依赖机器旧缓存/手工安装；
- product/test 实际加载 stale Native DLL；
- RealSample 要求项没跑到；
- 测试 filter 把失败类别排除了；
- 只有 mock，没有要求的真实 filesystem/media evidence；
- 独立验证缺失；
- package 仍含 production external media CLI；
- backend runtime identity 与冻结决策不一致；
- portable_core 实际链接 Windows media/platform backend；
- P1–P3 targeted regression 失败；
- 任何 P4 Exit Gate 没有真实 evidence；
- 为通过验证临时修改 production code 或降低 test assertion。

---

## 10. Verifier 最终输出

若全部通过，只能输出：

```text
P4 VERIFICATION: READY FOR EXTERNAL CHIEF GATE
```

并附：

- 当前 HEAD/构建身份；
- build matrix；
- targeted test matrix；
- RealSample matrix；
- independent validation；
- package/runtime matrix；
- 17 项 Exit Gate matrix；
- residual non-blocking risks。

若有任一 blocker：

```text
P4 VERIFICATION: FAIL
```

列出：

```text
Blocker
Affected gate
Evidence
Required owner/sub-round
```

**Verifier 无权宣布 P4 completed。**

---

## 11. External Chief Gate

最终由当前用户 / External Chief Gate 独立决定：

```text
ACCEPT
或
REJECT
```

只有明确 `ACCEPT` 后：

```text
P4 → completed
P5 → eligible
```

但即使 P4 ACCEPT，也**不自动开始 P5 施工**。

这保持了项目现有 AI 治理规则：

```text
Implementer builds
Auditor challenges
Verifier proves
External Gate decides
```

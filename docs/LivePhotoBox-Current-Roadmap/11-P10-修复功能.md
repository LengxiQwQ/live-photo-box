# P10 — Repair

> **Entry:** 主 Split/Merge/Writer 主线稳定。  
> **Goal:** 对已知、可证明的问题执行窄范围、可验证、默认非破坏式修复。  
> **Architecture role:** Diagnosis/mutation semantics portable；pixel/media processing 使用 P4/P5 backend；transaction/publish 使用 Platform Backend。

---

# 1. 定位

```text
Repair != Source Neutralization
Repair != Target Writer
Repair != universal corrupted-media recovery
```

可以复用：

```text
Inspector
parser/container engine
metadata engine
Media Backend
Target/Media Validator
transaction/publish infrastructure
```

---

# 2. Repair categories

当前/未来按真实产品需求：

```text
thumbnail-related cleanup/fix
rotation matrix correction
orientation correction
stretch/aspect metadata correction
known narrow metadata/container repair
known protocol identity repair
```

只处理：

```text
可诊断
修改范围明确
结果可重新验证
```

的问题。

---

# 3. Portable diagnosis

以下应属于 Portable Core：

```text
what is broken?
which structure/fact is invalid?
what exact mutation is authorized?
what preservation constraints apply?
what post-repair facts are expected?
```

不得依赖：

```text
Windows UI
Windows file extension association
WIC says it can open
Media Foundation says it can play
```

来决定“需不需要修”。

这些只能是辅助媒体 evidence。

---

# 4. Media repair backend

如果 Repair 需要：

```text
JPEG lossless rotation
JPEG reencode
HEIC encode/decode
video transcode
color transform
```

使用 P4/P5 已批准 backend。

禁止 P10 为一个 repair feature 重新引：

```text
jpegtran.exe
ffmpeg.exe
ImageMagick production path
another HEIC codec
```

除非先通过 dependency decision rule，并升级 P4 architecture record。

---

# 5. Non-destructive by Default

```text
Inspect
→ Diagnose
→ RepairPlan
→ temp/new output
→ validate
→ publish new file
```

默认不覆盖原图。

未来 overwrite：

```text
product explicit action
+
transaction safety
+
backup/rollback policy
```

Native primitive 不擅自覆盖用户源文件。

---

# 6. Validation

每类 repair 必须重新证明：

```text
target problem removed
container/media valid
non-target metadata preserved
source unchanged
repair outcome truthful
```

如果修复结果需要 target protocol conformance：

```text
Target Validator
```

如果只是 generic media：

```text
appropriate structural/media validator
```

---

# 7. Future platform rule

未来平台只要拥有相同：

```text
Portable diagnosis/mutation
+
required Media Backend
+
Platform publish
```

即可复用 Repair。

平台缺某 codec 时：

```text
capability = unsupported
```

而不是复制一份降级 repair。

---

# 8. Exit Criteria

- 每类 Repair 有明确 diagnosis；
- 修改范围窄；
- 不猜测；
- 默认不覆盖原文件；
- 使用既有 backend，不产生新的依赖孤岛；
- 修复产物重新验证；
- 失败无假成功结果。

全局执行规则见 `00`。

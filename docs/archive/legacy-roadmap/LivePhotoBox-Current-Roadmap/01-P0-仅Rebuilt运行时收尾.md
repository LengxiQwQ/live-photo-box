# P0 — Rebuilt-only Runtime Closeout

> **Status:** DONE / maintenance gate  
> **Goal:** 确保 production runtime 只有 Rebuilt 路径，不存在 Legacy product fallback。  
> **Global architecture:** 服从 `00-重构总纲-唯一执行路线.md`。

## Scope

- GUI / CLI / Core 的正式处理入口只走 Rebuilt contracts。
- 旧 backend selector、legacy runtime、silent fallback 退出生产路径。
- Production runtime 不依赖外部媒体 CLI 子进程。
- Native ABI / capability / cancellation / log / error 基础可用。
- 后续新增 platform/media backend 仍属于 Rebuilt，不得被误判为“恢复 Legacy”。

## Maintenance Gate

任何后续修改如果重新引入：

```text
Legacy product fallback
external media CLI runtime
hidden second protocol authority
automatic fallback that silently changes correctness/preservation
```

均视为 P0 regression。

## Cross-platform note

P0 不要求跨平台产品。  
未来新增 Windows/Portable/Web backend 时，只能挂在 V4.0 backend contracts 后，不允许重新出现第二条产品主线。

## Verification

后续回归持续证明：

```text
no legacy product fallback
no hidden external media subprocess
Native unavailable → explicit failure
cancellation works
last error maps correctly
capabilities match runtime
backend identity is diagnosable
```

全局执行规则见 `00`。

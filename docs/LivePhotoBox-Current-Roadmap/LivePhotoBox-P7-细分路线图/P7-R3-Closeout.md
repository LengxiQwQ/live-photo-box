# P7 R3 — Backend Truth 收口

2026-10-16：**CLOSED / PASS / DONE_FOR_R3**。

当前用户授权本地 Codex 负责 R3 的判断、实现、验证和独立审查组织，不联系
ChatGPT。最终候选完成限定回归与 fresh 只读独立审查，已停止在 R4/P8 之前。
P7 仍为 `in_progress`；本报告不是整个 P7 或产品发布验收。未 commit/push/tag/release。

## 验证结果

| 检查项 | 可复核证据 | 状态 | 严重性 | Blocker | 建议 |
|---|---|---|---|---|---|
| Native / Managed 构建 | run-1/commands：Native product/harness v5、probe/tests v8，直接退出 0 | PASS | 无 | 否 | 无 |
| 最终限定回归 | run-1/raw/aggregate：120 discovered = executed = passed；fail/skip/notExecuted/missing/unexpected=0；child=0 | PASS | 无 | 否 | 无 |
| 实际后端与不可用失败 | run-1/raw/probes：16 个 fresh child 场景，成功/失败均按各自预期，source identity 不变、无未解释 TEMP 文件或失败输出 | PASS | 无 | 否 | 无 |
| 独立输出验证 | 42 个独立工具命令退出 0；八份输出 stream/video decode；六份视频输出 audio decode；NCLX/VUI、HDR Main10、尺寸/时序等核对 | PASS | 无 | 否 | 保留证明范围 |
| 固定候选身份 | 318 source、22 probe runtime、102 test runtime、6 Native artifact，全部重算匹配 | PASS | 无 | 否 | 不改写封存内容 |
| 历史证据与样本 | R1 439、R2 2063 indexed payload 全部重算不变；17 缓存样本身份符合冻结清单 | PASS | 无 | 否 | 不代表每个样本都跑全流水线 |
| 环境与治理 | Doctor 0 error/0 warning；sync/verify/reconcile/diff-check 退出 0 | PASS | 无 | 否 | 无 |
| Fresh 独立收口 | `/root/r3_closure_review`，全程只读且未参与修改；PASS / DONE_FOR_R3 | PASS | 无 | 否 | R3 后停止 |

正式证据：`.ai-tmp/evidence/P7/P7-R3-backend-truth/run-1/`。
原始候选索引 `evidence-index.json` 保持封存，最终结论另存
`independent-review.json`、`independent-review.md` 与 `closeout-index.json`。

## 实际修复及影响

1. MF capability 原先直接声明 H264/HEVC 可用，现实际枚举并激活已批准的同步
   软件 NV12 encoder。COM/MF 初始化、无候选或无法激活报告 BackendUnavailable；
   当前源类型无法协商仍为 Unsupported。该查询只证明存在可激活候选，不承诺
   任意源都能编码，不启用硬件或新后端。
2. 图像 trusted ordinary inspection 与 HDR-stage catch 的不可用 failure
   stage/category 对齐；cleanup/output-validation 的优先级保持。
3. malformed/ambiguous ISO GainMap metadata 失败显式携带 Metadata stage，避免
   沿用来源检查的默认 Read stage；没有放宽接受条件。
4. 修复旧测试手工夹具混入生产工作区、哈希读流未释放和错误输出目录断言。
   原始夹具使用测试专用生命周期，真实转换输出仍使用生产 MediaWorkspace。
   malformed 四种原 policy 组合要求上游精确拒绝，并实际执行普通转换 guard；
   合法 GainMap binding 对照实际要求 FallbackAllowed=true、FallbackOccurred=false。

生产修改限于一份 managed image converter 和四份 Native 内部源码/头文件；
公开 `livephotobox_native.h` 未改。另有三份测试及四份离线验证工具文件。
没有新增生产能力、fallback policy、运行/打包依赖或安装开发工具。
保留用户及 R1/R2 原有工作区改动，不重写历史失败结果。

## 后端和证明边界

- 六项 runtime capability 对应 libjpeg-turbo、libheif、项目 remux、MF software
  H264/HEVC 和 minimal-libav HDR/Main10。实际请求、ExecutionRecord、输出及运行
  文件身份均有对应证据；图像记录自身只声明 Native owner，codec identity 还
  结合 runtime identities、精确 DLL 和实际 dispatch 源码核对。
- 缺失/损坏 Native 和 sidecar 是真实隔离部署/load failure，没有修改系统 codec。
  不声称制造了所有 MF 系统初始化故障。HDR-stage entrypoint 缺失分支的修复属于
  静态分类一致性核查；真实 image child 覆盖可达的普通检查入口。
- HDR 输出为 HEVC Main10 / yuv420p10le，有效 CICP `(9,18,9,full)`；MF 两路线为
  `(12,1,6,full)` 与 `(5,6,6,full)`。独立 NCLX/VUI 核对区分 unspecified 值和缺失
  字段，不凭字符串命中猜结构。音频重编码的有效性不等于 payload 字节保留。
- hardware encoder failure 为 `NOT_APPLICABLE_CURRENT_CAPABILITY`。MF SDR 与
  minimal-libav HDR/Main10 属于不同批准请求域，不伪造可互换 backend differential。
- Unit/反射分类、Synthetic/Derived、原始 RealSamples 和独立 FFmpeg 验证保持区分。
  q8 的 skip=0，未用过滤排除 required RealSamples。

## 精确绑定及失败历史

- HEAD：`47df638cbae0c957904348cbf30f63af1e1c564f`；branch：`master`。
- Roadmap revision：`317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7`。
- Evidence index SHA256：`86197A7A4D118B133B4F69E8969B843A853C630D0E63473552CC340FF99252F8`。
- Candidate-v8 SHA256：`5D8BABEF58B09DF5CDFE30DE214CA894DD8A682EE12483A074E06E0A724FF357`。
- Native-v5 binding：`52E6937548B1CF96129DF37D9189910FB641F73767A839D729DBDC1CB266FA35`。
- Native DLL：`D24DF0D9265628C259E14F2271D8D8416366DA22EC91E3157509043BF06B34AF`。
- Sidecar DLL：`3563E836BFC5786FE4CD5459E922BFE03E90E386F1F6D7DD3A0C2EB7D561BA94`。

诊断 qualification-v2=88/119、q3=111/119 的原始 TRX/日志/summary 已保存在
run-1/diagnostics。q7=120/120 后独立审查纠正无输出断言，最终 q8 再次120/120。
全部 scratch 失败保留，只有当前 final q8/p8 用于收口；run-1 为 fresh create-only。
额度中断后重新核对了762正式payload、候选及样本身份，记录在resume-verification。

R4–R7 及 P7 External Chief Gate 尚未执行；不自动开始下一单元。

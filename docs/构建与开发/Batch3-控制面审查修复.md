# Batch 3：控制面接口与遗留废口审查修复

本轮在整批只读审查后，由用户授权自行评估、修复，并在完成后停止。范围仍为 Batch 3；未进入 Batch 4。

## 决策与实际修改

两项已确认问题都需要修复，采用局部修改，不扩建媒体保留体系。

1. **High：旧 Core 合成入口绕过媒体与目标校验。** 保留 `LivePhotoMergeService.WriteLivePhotoAsync` / `WriteLivePhotoRebuiltAsync` 的高层兼容签名，将其转接到现有 `LivePhotoMergeRunnerService`，统一执行来源检查、中性化、通用转换、结构化 Writer、目标校验及安全发布。保留显式目标路径、覆盖策略、封面时间、取消和测试观察边界。无完整资产契约的旧 raw XMP / raw Huawei 写入入口，在访问路径前返回明确的 `AuthorityViolation` 拒绝。伪图片与假视频不再产生新输出或覆盖已有目标。
2. **Medium：批量归档部分失败却仍报告成功。** `MergeCommand` 移动前预检一组来源的全部目标冲突；新增独立的 `AfterAction` 文件结果和 `afterFailed` 汇总。媒体转换成功仍保留其成品回执，归档失败则命令退出 1，取消退出 130。目录创建、无效归档路径、缺失来源及实际 I/O 异常被纳入后处理结果。回收操作同时检查 Windows 的 aborted 标志，不能只看返回码。

已知冲突在移动第一份文件前阻止。移动中才发生的共享锁、权限或其他 I/O 错误，可能仍产生部分移动；结果明确记录每份文件的 moved / recycled / retained / unknown 状态及原因。不以按路径盲目回滚冒险覆盖并发出现的其他文件，不宣称双文件归档具有原子性。

**无需修复的部分：** `NativeMethods.Media.cs` 未确认新的 ABI 缺陷；`NativeAppleMebxWriter.cs` 没有现存调用方，生产 Native authority gate 已阻止其裸 mutation。本轮不为清理未使用包装器扩大范围，更不恢复 C 级私有 mebx/编辑历史保留。没有修改这些文件、Native ABI、生产依赖、保留策略或目标范围。

## 验证

- 正式 Debug Native 构建状态校验与 CLI 构建通过，0 error；Core 全量重新编译出现 `WorkspaceOwnershipRegistry.cs` 三条既有 nullable warning，本轮未修改该文件。受改文件没有新增编译警告。
- 定向 Core 回归最终 60 项不同用例通过，CLI 回归 26 项通过，共 86 项，0 fail / 0 skip。包含正常兼容写入、Google/Samsung/Huawei 真实来源、来源别名防护、持锁与外来目标、提交前取消、发布后失败回滚、无效媒体拒绝、归档图片/视频重名、归档路径为文件和真实共享锁下的部分移动报告。
- 旧事务用例改用已有可解码 fixture；继续验证发布前完整 stage、输出与 stage 的长度/哈希一致，以及视频 mdat 负载一致。协议断言按实际协议身份、Huawei 实际视频范围与尾标核对，允许合法重封装，不要求旧 XML 前缀或整个容器逐字节不变。有效保护断言没有删除或放宽成任意成功。
- 首轮 5 项失败来自新用例的异常类型/目录隔离和上述旧断言。修正后第一次局部重测有 4 项被历史证据的 `CreateNew` 文件名冲突阻断；保留旧证据，使用同一任务下的 `core-transactions/recheck` 重测 4 项通过，不把证据写入失败改记成产品成功。
- 旧漏洞探针重跑：raw 入口明确拒绝，高层入口实际检查无效视频并拒绝，两者均不发布文件。
- 两组正式 CLI 实际成品：Apple JPEG 双文件 → Google V2，分别验证视频归档重名和正常归档。冲突返回 1 / afterFailed=1，两个源文件保持原位与原哈希，外来归档文件保持原字节；成功返回 0 / afterFailed=0，源文件在归档目录保持原哈希。两组成品与 Batch 2 已独立检查通过的对应成品逐字节一致，复用其合法结构、解码、音画同步、方向和封面证据。

所有逐次命令、TRX、原始失败、探针、CLI JSON 和候选哈希位于 `.ai-tmp/workspace/P9/target-writers/R8/batch3-review/`，汇总见 `fix-validation-summary.json`。

## 停点与未验证范围

本批两项问题已修复并通过上述定向验证。Batch 1 / Batch 2 原有候选文件哈希保持一致。未运行全来源×目标矩阵、完整测试套件、手机验证、回收站真实操作或修复后的独立角色复审；本轮自查不冒充独立复核。不提交、不推送，停在 Batch 3 等待后续指令。

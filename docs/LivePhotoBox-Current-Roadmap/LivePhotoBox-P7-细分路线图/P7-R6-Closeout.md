# P7-R6 Integrated Evidence 综合收口

2026-10-19：**CLOSED / PASS / DONE_FOR_R6**。按当前用户授权采用LOCAL_OWNER，由主代理统筹并编写综合报告，fresh只读独立审查通过。已停在R6；P7仍in_progress，R7 Audit→Verify→External Chief Gate未启动，P8未进入，没有Git发布。

## 综合结果

| 检查 | 实际结果与范围 |
|---|---|
| R1–R5证据完整度 | 6381个基础payload和84个收口引用重新计算大小与SHA256，全部匹配；历史失败与sealed原字节保留 |
| 当前bounded P7 aggregate | Core608/608、CLI2/2，0fail/skip/notExecuted/missing/unexpected，直接退出0；全部缺口测试方法在最终聚合再次执行 |
| 最小缺口补测 | 测试fixture/生产authority66/66；HEIF图/manifest及13条真实样本Cleaner路线42/42；浅TEMP remux预期故障点1/1 |
| 独立正确性 | 当前产物byte/structure bridge217项、tail/pairing15场景通过；真实Apple HEIF的source/final辅助图、owner、extent和payload由独立parser逐项对账 |
| 当前身份 | 681项源码/资产前后稳定；17个required私有样本按manifest前后匹配；实际Core/Native/sidecar/harness与CLI运行时hash稳定；最终TEMP文件残留0 |
| Windows portable与依赖 | 当前source/static library/exe绑定的Windows smoke通过；实际部署Native/sidecar PE依赖合规，69个clean C导出；无新增生产依赖 |
| 独立收口 | fresh本地只读GPT-6.1-sol/medium，PASS / DONE_FOR_R6；同HEAD/branch/workspace/Roadmap/candidate/index绑定 |

## 九项退出标准映射

| 标准 | 证据支持与边界 |
|---|---|
| 无已知silent corruption | R1结构/metadata、R3独立decode、R4独立媒体与metadata/timing/HDR证据，当前聚合和独立HEIF对账；限已批准P7管线和有界语料 |
| malformed不crash | R1十二类攻击及当前R1/Inspector/HEIF/转换拒绝回归；有限确定性语料 |
| ambiguous不猜 | ownership/namespace/protocol/pairing/range歧义拒绝；CP-002为准确rollback，不写成clean成功 |
| transaction/failure安全 | R2真实故障和未变化mutation/rollback实现，当前P7R2与CLI编排回归；六条VHDX相关历史rows明确复用、不计作本次执行 |
| batch/large无明显泄漏 | R4 53轮/1806.329秒、quiet/postload及大文件证据；有限观察，不宣称Private回启动基线或无限稳定 |
| backend fallback可解释 | R3历史与R4当前获批HEVC/libav truth、实际输出和当前Runtime/VideoConverter；无隐藏fallback，旧mf-hevc结论不当作当前能力 |
| Portable Core边界未回退 | R5静态/编译证据与本次当前Windows smoke、ABI回归；Linux smoke不代表产品支持 |
| 依赖图无未批准扩张 | R5固定依赖/零生产媒体CLI/codec职责，加本次实际PE导入/导出；无新runtime dependency |
| regression corpus可重复 | 17个缓存原样本、固定derived fixtures、精确selector/discovery/TRX和source/runtime/input身份、真实命令及退出码 |

每项均有可复核证据支持；这不是R7整体外部ACCEPT。当前方法与历史交集为R1 131/146、R2 91/216、R3 118/120、R4 387/387，交集不能相加；其余逐条复用或superseded原因已入详细索引。

## 修改与历史差异

R6仅修改八个测试文件：将手工fixture与生产MediaWorkspace隔离；真实样本Extract/Clean测试改用生产Inspector-issued plan和Extractor-issued cleanup authority；保持拒绝、保留、不可变、精确输出和清理断言。旧假媒体manifest测试改为真实Apple样本及独立图/字节核验，ExifTool离线副本使用ASCII名。R6没有修改生产源码、Contract、ABI、backend或依赖，也没有安装新工具。工作区原有R5四个Native修改及其他用户改动保留。

首轮聚合与中间资格测试的失败均如实保留；最后一次608项聚合在同一候选、浅固定TEMP下完整通过。未放宽断言、删除测试或把未执行写成通过。

R5旧状态/review记B8B38…，当前实际根为5A9A2…。10个payload按实际根验证；旧根关系缺乏原字节证明，未推断或改写历史。本次fresh R6审查直接绑定当前内容和新Windows smoke/PE证据。R2的ExportSourcePreparation只有R2-current直接源码等价，未虚构R4中间snapshot。

## 证据与剩余边界

正式记录：`.ai-tmp/evidence/P7/P7-R6-integrated-evidence/run-1/`。完整综合报告在`integration/integrated-report.md`，九项机器映射在`integration/exit-criteria-map.json`，逐项历史复用在`integration/historical-reuse-classification.json`，最终结论以`closeout-index.json`和`independent-review.md`为准。

基础证据索引SHA256：`F2F8B7237714595C732DD27587A3EF0CE24CA977437D4A004E5BDCDFE4A8AD60`，1525个payload；基础索引的READY状态保留原字节，最终closed状态通过追加closeout-index表达。

[发布前问题清单](../../发布前问题清单.md)中的RELEASE-001/002 GUI/Original路径仍未关闭；当前测试不冒充全GUI端到端、Linux/Web支持或全产品发布验收。R7必须等待新的明确授权，本次不开展其Audit、Verify或External Chief Gate。

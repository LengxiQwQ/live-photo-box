> 现行执行方式（2026-10-26）：每个主聊天只负责当前R，完成真实验证、fresh Review/另一fresh Verify和白话报告后提交推送，再由新开的主Codex聊天完整接任下一个R，不能用子代理代替接班。普通工程问题自主解决，不再逐R重复请示；实际功能/支持能力、方向及重要工具/安全/生产依赖决定仍须用户处理。验收标准不变。

> 产品范围澄清（2026-10-25）：当前用户明确Apple双文件、旧vivo双文件按拆分场景单独安排，本轮只做合成，不新增两种合成选项；拆分入口见 ../拆分功能-独立后续清单.md。旧“待问是否增加Apple/旧vivo合成”的文字已由本条作否定决定，保留原文作历史，不能自动启用。
> 当前用户授权更新（2026-10-25）：连续自主推进整个P9，不再逐R请示。普通内部接口/ABI与工程选择由主代理处理；实际功能/支持能力变化、项目方向或重要工具/安全/生产依赖决定先说明并询问。Fusion继续延期。旧逐R等待及普通接口等待文字记录历史，被本条覆盖；真实验收、设备与独立门禁不豁免，不自动进入P10或发布Git。
# P9 唯一进度 TODO

2026-10-26，LOCAL_OWNER。**R0、R1、R2、R3 技术 COMPLETE；R4 尚未开始，须先完成远程 CI 修复实跑门禁；Fusion/R6 延期。** R3 正式506项托管+10项Native、零跳过，fresh Review及另一fresh Verify通过；146条实际RealSamples回归，57份独立媒体完整解码。设备、产品切换和P9整体仍未完成。详见12与13。

## 本轮核查规划

- [x] 读AGENTS/项目技能、当前state/前置TASK与P8最终收口、正式P9/总纲/Neutral、全部11份协议事实及发布前问题。
- [x] 核对HEAD `2f93c529`、本地origin/master、起始clean、P8complete/active_task=null、reconcile通过。
- [x] 核产品typed边界、7target/14profile、现有Writer/Validator/publish缺口；区分源码与实际执行证据。
- [x] 建立R0–R9目标、顺序/依赖、范围、验证、独立门禁、设备需求和停止条件。
- [x] 用户确认开始，排除Fusion，改为每R报告→用户确认→下一R；已同步长期计划和state/TASK，当前止于R0。

## R0 范围、证据与环境基线

- [x] 冻结14行target/profile状态、生产路由/旧旁路缺口、负责R与完成条件：R0/target-profile-baseline.json；更细版本/HDR/auxiliary行在对应实现前补齐。
- [x] 14profile完整登记，其中Fusion2项用户延期，本轮6target/12profile；2项既有HEIC明确Unsupported、HuaweiHEVC冲突单列，不伪造支持。Apple/旧vivo新增选项不因设备可用自动启用。
- [x] 用户已提供身边小米/OPPO/Apple/vivo小于X200、其余非Huawei可后期商场；具体机型/OS/相册/导入执行前冻结，均NOT_RUN，Huawei无渠道不豁免验收。
- [x] 冻结冲突及负责R/实施前停止条件：HuaweiHEVC→R4、vivoSDR/GainMap→R7、SamsungSEF/pointer→R5、Apple保留/新增输出→受影响R1或单列决定。R0完成的是识别与约束，未把未知事实写成已解决。
- [x] 形成当前源码59项raw身份与59项换行桥、469runtime/54manifest输入、17原样本/缓存及authority身份、复用限制/设备与决策表；runner未执行/未资格化明确留R1按实际设计冻结，不虚构产品PASS。fresh R0review抽核全部59当前源码、8关键运行文件与authority条目通过。
- [x] R0 fresh只读独立复核：PASS_FOR_R0_BASELINE_ONLY_WAIT_USER_BEFORE_R1；白话报告后停止，R1尚未开始。

## R1 共享结构与安全

- [x] 用户授权独立Writer ABI12→13、旧任意修改入口按完整权限拒绝、R1共享基础与各家接线分工；2026-10-26新增逐R提交推送与新主聊天完整接班。
- [x] Native plan/POD、实际身份/hash/manifest、只读输入锁定、单次使用、回调/并发/销毁和publisher身份连续性设计经fresh只读审查后实施。
- [x] 完成R1必要结构观察/边界检查，复用已有JPEG/EXIF/XMP/ISO-BMFF等parser；HDR/aux/HEIF及超出当前新API子集明确拒绝，后续厂商接线前扩展，未削减现有产品支持。
- [x] malformed/duplicate/shadow/overflow、身份替换/replay、实际share-lock、取消/并发/释放、新publisher8项及旧合成事务32项验证；真实故障与注入中断后的实际回滚分开标注。
- [x] 旧可达任意修改入口/测试辅助代码安全收口；保留历史算法的62项原Assert。Native与Harness分别构建、实际导出和部署身份核对；配套ABI2项与旧入口9项通过。
- [x] 最终有界188项托管+2项Native全通过、零跳过，727份正式payload封存，fresh Review→另一fresh Verify PASS；R1仅共享基础完成，无厂商生产切换声明。结论08，接班09。

## R2 独立Validator

- [x] fresh close/reopen、独立observed事实/规则与四维结果；最小GoogleV2 JPEG MP4/MOV实验链实际运行，不信Writer或SourceInspector最终分类。其他族/变体随对应R补齐，无全族声明。
- [x] 具体Validator强制接入新生产链发布门禁，同一对象/字节连续性及发布后检查通过，调用方不能给always-pass verdict；现有GUI/CLI尚未切换，待R8完整生命周期。
- [x] 当前单文件范围的wrongtarget、结构/重开/字节替换/取消/真实权限/清理反例通过；未启用多成员/配对范围明确拒绝，不声称已通过双文件配对。正式390/390 managed、3/3 Native、106条真实样本回归，949payload，fresh Review及另一fresh Verify PASS；见10及11。

## R3–R7 每族完整生命周期

- [x] R3技术：GoogleV1/V2、OPPO结构化Writer+Validator，Xiaomi兼容分行；RELEASE-004精确原图五个适用JPEGprofile、无XMP/duplicate/malformed/普通metadata及MPF未知字节保留、辅助身份/事务/独立证据；fresh Review及另一fresh Verify通过。设备及产品切换仍R8，RELEASE-004不整体关闭。
- [ ] 用户插入维护门禁：进入R4前修复并验证远程CI。CodeQL历史失败D卷失联，CI-only修复已推送，精确最终R3提交的实际Build/CodeQL通过与回执待完成；不能仅凭workflow改动称已修复。
- [ ] R4：Huawei/Honor合法JPEG/HEIC、LIVE_与完整视频、封面/扩展保留；HEVC冲突按决定处理；不代签RELEASE-001。
- [ ] R5：SamsungJPEG/HEIC、XMP+SEF双层规则、mpv2/mpv3/relocation，独立保留与反例。
- **R6：DEFERRED_BY_CURRENT_USER**。Fusion暂不做、不计本轮交付、不勾成通过；Apple旧public utility实际旁路审查归R1，新Apple输出另行决定。
- [ ] R7：vivoX300UserComment/HDR与GainMap唯一ownership，SDR决策；旧双文件边界，若纳入则独立完成生命周期。
- [ ] 各族均有synthetic/mutation、实际RealSample、独立媒体/保留、适用真实失败与fresh技术复核；未测设备仍待R8。

## R8 逐目标设备与切换

- [ ] 冻结可执行设备手册与样本包，绑定产物/设备/OS/相册/传输身份。
- [ ] 确认范围内各目标完成真实导入/查看/播放及适用封面/方向/音频/HDR；未验证variant如实标记。
- [ ] 每目标严格按新Writer→Validator→自动/样本→外部证据→设备→切换→移除旧实现，无自动双轨fallback。
- [ ] 受影响GUI/CLI结果/history/保留分类实际集成，复用未受影响P8编排证据；RELEASE-004满足全部关闭条件后更新清单。

## R9 最终收口

- [ ] 正式P9各条款/每targetvariant证据矩阵齐全，同一最终候选有界aggregate通过，零必需skip；无全产品suite。
- [ ] fresh只读Review通过，另一fresh独立Verify通过；必要用户/外部门禁到位。
- [ ] doctor/必要sync/verify/reconcile/diff检查；最终长期结论/限制/遗留归属、state/TASK真实登记。
- [ ] 满足所有条件后P9complete，停P10之前；Git提交推送按现行逐R授权，tag/release不自动授权。

如果新修改只影响某些证据，只回退对应复选项并写明原因/下一动作；不全表清零，不改旧失败为通过。详细命令/逐样本记录留同任务workspace/evidence。

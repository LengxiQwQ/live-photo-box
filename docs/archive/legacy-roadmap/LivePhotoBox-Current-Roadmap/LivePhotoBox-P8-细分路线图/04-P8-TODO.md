# P8 实时 to-do list

状态基准：2026-10-25，LOCAL_OWNER。**P8 COMPLETE，R0–R7全部收口，active TASK已清除，停止P9前。** 最终五项GUI实操、fresh独立增量Review及另一freshVerify均通过。详细结论见 [07最终收口结论](07-P8-最终收口结论.md)。已测且仍适用的证据复用，旧失败与旧索引保留；发布遗留未因此关闭。

最新执行授权：当前用户随后明确同意GPT-6 Luna(max)按主代理冻结手册执行剩余GUI检查，并说“可以开始”。现由单一GUI子代理执行，主代理复核与处理异常；原“用户亲自操作/不再agentGUI”仅是此前时间点安排，已被本次明确授权替代。五项检查内容不变，未测项仍不得填PASS。工作与恢复入口为 `.ai-tmp/workspace/P8/product-foundation/R7/gui-final/frozen-manual.md`。

## 顺序与准入

- [x] R0：基线/复用/缺口与环境盘点完成，17项当前源码与已有candidate匹配；R0台账明确下一步R1/R2。未重跑测试。
- [x] R1：typed实际产品入口覆盖核查、未知设置guard与GUI编译完成；真实GUI状态交互交叉验收仍在R5，不能据此跳过。
- [x] R2：discovery/Neutral/Writer边界和生产旁路审计完成；新增11行覆盖按9+1+1成功证据关联，失败attempt保留，详见R2/closeout.md。
- [x] R3：queue/pause/cancel/progress/输出归属/error实现与17行新增覆盖完成；准确分组与复用见R3/closeout.md，GUI实操交叉项仍在R5。
- [x] R4：真实能力/缺MF受控x264实现/ExecutionRecord与必要独立复核完成；真实缺MF实测按用户决定移至RELEASE-003。
- [x] R5：CLI/Core semantic history、实际六路CLI及五项最终GUI检查完成；授权Luna执行机械步骤，主代理补最后时序敏感暂停观察并复核。
- [x] R6：八项标准技术证据与九域映射、有界aggregate完成；用户GUI待办单列，不计成已通过。
- [x] R7：fresh技术Review/Verify及最终GUI增量freshReview、另一freshVerify均通过，P8完成，active TASK清除，停止P9前。

## 已完成子项（直接复用）

- [x] 起始HEAD fce33271、P7complete/交接三入口hash、reconcile核对。
- [x] immutable TargetProtocol/OutputProfile与typed Split格式；兼容矩阵/媒体映射单一来源。
- [x] GUI Split/Merge、CLI Merge及CLI Split全部三个调用点接typed Core。
- [x] 非法选择在GUI运行状态/输出创建前受控拒绝；runner空profile/预取消不创建目录。
- [x] 初始Neutral+profile→existingWriter适配边界与原transaction调用。
- [x] GUI Split/Merge、CLI Merge不再删除用户共享output/Temp；CLI三项sentinel及runner原归属/取消证据。
- [x] 初始Core44/44、CLI18/18、最终CLI单文件补测18/18零fail/skip，原始结果保留。
- [x] 实际CLI四variant/八输出、输入hash不变、四视频独立probe。
- [x] Core/CLI/WinUI及迁移工具构建、90项Native/build/input身份复用桥。
- [x] 初始fresh只读review及CLI delta补审PASS_INITIAL_SLICE_ONLY；不是P8acceptance。

## 逐项完成记录（原施工待办）

- [x] R0生成当前生产路径/coverage/reuse/gap机器可读台账；标明每个已完成项适用范围。记录：workspace/P8/product-foundation/R0/baseline-and-coverage.json。
- [x] R1核旧设置/别名/非法组合源码及guard、编译；GUI实际状态在R5执行一次，不重复现有测试。
- [x] R2核所有discovery/prepare/Writer可达路径与未使用BatchRunner隔离，明确P9替换点；修复vivo发现顺序与显式普通媒体准备并验证。
- [x] R3核TaskStartInterval实际行为、pause/resume/cancel回调、completed计数与worker退场。
- [x] R3核显式OutputFilePath与OverwriteExisting的authority/期望语义；已有证据不充分时补准确生产路径。
- [x] R3核模板/相对目录/部分失败；GUI源动作只作用于本轮成功项，实际GUI交互在R5补证。
- [x] R4冻结MF状态分类、Native加载边界、capability contract、受控route与诊断局部设计。
- [x] R4实现实际能力查询；静态profile选择与可运行能力分离。
- [x] R4实现真实MF缺失时的受控H264/x264；普通失败不触发隐式fallback。
- [x] R4完成正常MF及sidecar缺失/身份/unsupported反例等本机受影响验证；不得用注入冒充真实缺MF环境。
- [x] 当前用户2026-10-23明确将真实缺MF环境实测移至发布前RELEASE-003；该实测NOT_RUN / DEFERRED_BY_CURRENT_USER，未计为通过，发布前关闭条件已登记。
- [x] R4验证受影响音频/时间/MEBX/metadata/preservation、cancel/cleanup；fresh只读高风险复核。
- [x] R5贯通semantic outcome/history、GUI/CLIbackend与fallback诊断；源码和CLI/Core证据通过，最终界面另验。
- [x] R5最终GUI五项确认完成；原始截图/树差异与未观察到稳定暂停的旧尝试保留，最终八项暂停/停止及历史重启证据经独立Review/Verify通过。
- [x] R5按实际触达范围登记RELEASE-001/002；当前Native调用链迁移的源码结论已补充，精确历史样本及Original缓存/时间轴/导出仍未验，两问题未关闭，不启动编辑导出或自动进入P9。
- [x] R6冻结最终command/filter/discovery，资格检查及复用适用性完成。
- [x] R6最终有界aggregate：7Core+2CLI通过、0fail/skip；八项exit/九域技术映射完成，GUI待确认明确登记。
- [x] R7fresh独立技术Review通过：PASS_FOR_CLI_CORE_TECHNICAL_HANDOFF / FINAL_GUI_USER_MANUAL_PENDING。
- [x] R7独立freshVerify通过：READY_FOR_USER_GUI_CONFIRMATION；读取原始证据、未重跑产品测试。
- [x] R7技术交接时doctor/sync/verify/reconcile、docs与state/TASK登记完成；P8仍in_progress，activeTASK保留等待剩余GUI及独立增量收口，停止P9前。2026-10-25后台doctor/reconcile再次通过，不是重跑产品测试。

## 不在本次施工范围

- P9结构化Writer/TargetValidator逐厂商替换和真实目标设备一致性；auxv完整实现等后续债务。
- 没有明确需求就实现EXIF复制、启用旧BatchRunner或新增生产依赖。
- P0–P7重复全阶段campaign、无理由30minsoak、全产品suite、Git发布、P9/P10施工。

若证据因新修改失效，只取消勾选受影响子项并写原因/下一动作，不能全部清零重来。未来R进度按真实结果逐项更新，不用预测PASS填满待办。

历史安排（已被后续明确委派GUI执行的授权替代）：2026-10-24 用户更新执行顺序：共享逻辑先全部通过CLI/Core验证和打磨；确需GUI的最终检查由用户操作。此前真实GUI证据按原候选与未受影响范围复用；最终显示修复不能凭build算实操通过。测试应用已退出，原用户设置已按SHA256还原。原始决定和准确界面证据见 `.ai-tmp/workspace/P8/product-foundation/R5/current-user-cli-first.json` 与 `R5/gui/observed-closeout.md`。CLI/Core完成后交付可执行的人工GUI清单；不以本更新降低媒体、事务、独立审查或真实缺MF发布前要求。




最终登记：2026-10-25，最终Review SHA6CB1A74F...、Verify SHA6C86DB58...均PASS；准确身份及run-1/run-2索引见07。原技术交接时的GUI待办文字仅记录当时范围，现由最终增量证据补齐。RELEASE001–004保留，真实缺MF环境NOT_RUN/DEFERRED，不进入P9、不发布Git。

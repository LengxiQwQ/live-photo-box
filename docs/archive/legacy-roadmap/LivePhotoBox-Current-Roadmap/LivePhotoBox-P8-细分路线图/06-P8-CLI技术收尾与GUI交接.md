# 当前状态更新（2026-10-25）

P8 COMPLETE。原CLI技术交接后的五项GUI实操及独立增量Review/Verify均完成，active TASK已清除，停止P9前。见 [07最终收口结论](07-P8-最终收口结论.md)。以下是保留的技术交接时点记录，原待测项不代表当前状态。

# P8 CLI/Core 技术收尾与 GUI 交接

2026-10-24，LOCAL_OWNER。**共享逻辑与CLI技术验证完成，fresh独立Review和另一fresh独立Verify均通过，可以交给用户确认最终GUI。P8仍为in_progress，activeTASK保留，不把GUI未测项或整阶段写成完成。**

## 已完成的产品骨架

- Split/Merge的GUI、CLI单项/批量/全格式入口使用typed Core契约，经过Inspector/Neutral准备和现有Writer adapter或拆分事务发布；媒体事实与正确性仍由Native负责。
- 统一semantic outcome贯通请求格式、实际提交路径、准备阶段保留/降级、实际执行backend、失败阶段/分类及取消；最终目标保留保持Unknown，不能从发布成功推断目标结构合法。
- 操作历史按版本化记录直接落盘，独立于临时日志队列；失败/取消无虚构输出，非法success shape不会覆盖旧合法记录，读取有保留和大小边界，历史路径不要求当前文件仍存在。
- Windows能力查询与受控缺MF H264/x264实现、本机受影响验证及独立保留证据已完成；真实缺组件系统按用户决定单列发布前RELEASE-003。
- 修复两轮任务间旧终态阻挡新Pending、累计计数、仅本轮成功源动作、未知格式guard，以及动态拆分按钮/暂停硬件标签/新任务页脚等显示问题。最后这些显示改动已编译和源码审查，用户实际界面确认仍待完成。

## 验证结论与范围

| 范围 | 结论 | 说明 |
|---|---|---|
| 原有有效P8及P0–P7证据 | 按身份与实际影响复用 | 未重跑历史整阶段或全产品suite；R4全部12个相关源码及Native/sidecar身份未变 |
| 新semantic outcome | 当前13个结果/分类场景分批覆盖通过 | 包含旧绿项复用、修正的合法Split样本、新增严格Writer拒绝回归、2个诊断边界；不是一次13项同跑，旧失败保留 |
| 最终历史schema/磁盘行为 | 18项通过，0skip | 包括畸形成功记录、去重、截断/新版本、保留范围与历史路径语义 |
| CLI结果投影 | 8项通过 | 与真实媒体执行分开记录 |
| 真实CLI六种生产入口与负例 | 六条原记录按适用分支复用，诊断失败的负例单独修复复测 | 原七进程FAIL_STOP未重写；21个原outcome与21条历史一一对应。全格式Merge为10项已发布、2项明确HEIC XMP Unsupported，不是全格式支持 |
| 新Split四格式/八产物 | 独立probe/解码8项通过 | 不冒充Merge目标结构一致性或独立全保留证明；保留要求复用对应未变Native证据 |
| 最终有界接口回归 | Core7+CLI2通过，0fail/skip | 只补R5新结果接口实际影响的旧队列/混合结果/取消调用边界 |
| 最终构建 | CLI0warning/0error；GUI720warning/0error | GUI构建和源码审查不是最终界面实操；既有分析器/PRI警告完整留存 |
| 独立Review / Verify | 均通过CLI/Core技术交接 | 独立复核源码、原始TRX/命令/日志/产物及复用适用性，未重跑产品测试或GUI |

运行明细、失败attempt、命令/TRX、逐产物身份与原始截图均留在 `.ai-tmp/workspace/P8/product-foundation/`。create-only正式快照为 `.ai-tmp/evidence/P8/product-foundation/run-1/evidence-index-v2.json`，SHA256 `656FC0453B791E20DC5585503713A06F98CFC1820A53760CEE7492650C7A54BB`；988项原始载荷，v1保留，v2只增补实际CLI程序集副本。独立报告分别是同workspace的 `R7/final-independent-review.md` 与 `R7/final-independent-verification.md`。

## 给用户的GUI确认

按 [05人工GUI确认清单](05-P8-人工GUI确认清单.md) 检查动态开始/停止、暂停提示、新Pending页脚、结果详情与历史恢复。只需要确认界面层，不重复已通过的媒体和业务逻辑测试。测试候选为当前Release x64 2.2.2.0，主程序 `LivePhotoBox/bin/x64/Release/net9.0-windows10.0.19041.0/win-x64/Live Photo Box.exe`；避免误用旧商店安装版。

最终product/test source digest：`90F887C9B7EF8C37A860162FDA3B9D75B96C404122F9238EC7DB9AAC720B8E7C`；Core DLL SHA256 `5ADEF0099DE7C7345511A0CD30ED775923A6B62AFBB861BADF78C703ECB25F40`。GUI DLL、主程序、resources.pri等身份在R6最终runtime清单。没有更换生产依赖或安装新开发工具；测试launcher alias已移除，生成MSIX中未包含该alias。测试应用已退出，原用户设置已逐字节恢复，并在最终CLI后核对未变。

收到GUI结果后，只处理真实受影响界面项；若候选与依赖未变，既有CLI/Core及独立证据继续复用。用户GUI确认通过、必要修复闭环后再登记P8complete并清activeTASK，不自动开始P9。

## 仍然开放的后续事项

- RELEASE-001：精确历史荣耀源未取得，不能用另一个缓存荣耀样本替代其播放/布局验收。
- RELEASE-002：Original读取当前已走计划式Native提取；缓存FullFramePath、实际时间轴provider及编辑预览/导出尚未验证，问题未关闭。这些不自动变成本次编辑导出施工。
- RELEASE-003：真实缺MF隔离Windows验证为NOT_RUN / DEFERRED_BY_CURRENT_USER，发布前用VM/测试机完成。
- RELEASE-004：旧JPEG Writer对已有合法非live XMP的输入可能生成重复标准packet。Merge已经提交后严格Split会拒绝，不能说已回滚或目标结构通过；归P9结构化Writer/TargetValidator，仍是发布阻塞。

未进入P9、未执行Git提交/推送/标签/发布，也未声称整产品可发布。上述四项的具体影响、下一步和关闭条件以 `docs/发布前问题清单.md` 为准。

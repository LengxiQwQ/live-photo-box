# Batch 2：原生目标写入器审查与局部修复

本轮按用户要求先完成整批审查，再统一修复已确认问题。范围为 `target_writer.cpp`、`target_validator_v2.cpp`、`google_v2_writer.cpp`、`samsung_target.cpp`、`isobmff_mebx_transplant.cpp`；没有进入 Batch 3。

## 结论与修改边界

确认并修复两项 High 内存边界问题：

- `target_validator_v2.cpp` 跳过不要求携带的私有辅助项后，仍以原始观察下标写入只有两个槽位的 ABI 结果。改为独立连续下标，写入前检查容量，完成后核对数量。保留原有语义、身份、范围、哈希及协议拒绝条件。
- `isobmff_mebx_transplant.cpp` 在任意头部投影不一致时，固定从偏移 96/100 读取宽高；mdhd 投影只有 44 字节。删除这些无条件读取和共享可变诊断缓冲区，返回固定且准确的头部差异说明；头部不一致仍拒绝。

其余三个受审文件未确认需要修改的问题。本轮没有调整公开 Contract/ABI、生产依赖、默认保留策略或协议范围，没有扩建私有信息保留系统。C 级数据按既有规则剥离仍合规。

## 验证与局限

- 正式 Debug Native 与 CLI 构建通过，零警告、零错误。
- 在现有 Native 测试中补入公开结果缓冲区哨兵检查，覆盖跳过两个私有项后写入 Original、以及 Original 哈希不匹配仍拒绝的情况；补入真实 MOV 的视频/音频 mdhd 创建时间和 handler 名称四种差异检查。两个测试程序使用 `/W4 /WX` 编译通过，修复前分别失败、修复后通过；本轮直接编译执行这两个程序，未运行整个 CTest 套件。
- Huawei、Samsung、Google/通用结构化写入定向托管回归最终 98 项不同用例全部通过，无跳过。首轮 18 项缺少测试证据输出目录，补齐环境后重跑对应测试，保留首轮失败及重跑记录。
- 正式 CLI 生成 Apple JPEG → Google V2、小米 JPEG → Huawei v6、小米 JPEG → Samsung mpv3 三组实际成品。独立离线检查覆盖主图/视频解码、方向、声音内容与同步、封面、基础 EXIF、来源哈希及实际目标结构。Samsung 另核对原生 GainMap/SEF 位置和版本，Huawei 另从视频 `com.openharmony.covertime` 标签核对封面时间。
- 旧通用检查脚本未读取 Huawei 封面标签，并错误地将严格 Google V2 的辅助项布局套到 Samsung 原生布局。原始失败不改写；另存协议专用复核结果。未修改产品以迎合旧脚本。
- 未运行全来源×目标矩阵、手机验证或修复后的独立角色复审；本次局部修复通过不等于设备兼容、R8 整体完成或可正式发布。Batch 1 原有候选文件哈希保持一致。

逐次命令、日志、TRX、探针和 CLI 产物位于 `.ai-tmp/workspace/P9/target-writers/R8/batch2-review/`。本轮停在 Batch 2，后续批次等待用户决定。

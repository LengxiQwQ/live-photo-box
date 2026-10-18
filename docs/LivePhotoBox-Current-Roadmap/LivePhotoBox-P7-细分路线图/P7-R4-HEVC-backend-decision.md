# HEVC backend 决策与验证结论

用户已授权重开P4/P5原HEVC freeze；该变更经P7 R4完成受影响验证和fresh独立收口。下表为当前执行前分派，不是失败后的重试树。

| 请求 | 唯一backend |
|---|---|
| Copy / ISO-BMFF remux | 现有Native结构与事务路径 |
| 任一端为HEVC的受支持视频重编码 | 已分发的minimal-libav C++ sidecar |
| HDR/10-bit preservation-required重编码 | 同一sidecar，按实际格式能力与原Contract验证或明确拒绝 |
| 普通SDR H264→H264 | MF software |

不调用ffmpeg.exe作为生产后端，不增加第二套FFmpeg backend，不依赖系统HEVC扩展提供所迁移能力，无隐藏fallback，未实施Worker或系统codec修复。C#编排、Native数据面及公开ABI边界保持。现有GPL、专利与发布渠道义务仍适用，本记录不增加法律或发布批准。

修复覆盖AVIO buffer释放、x2658+10bit精确API、bit depth/CICP与色度位置、支持的HDR SEI和时间戳映射、多MEBX、静态metadata/crop、源身份与事务发布；NeutralMediaService正确释放借给Cleaner使用的CleanupPlan。MF正常对照的时间戳修正只在严格帧序证明成立时执行。

最终受影响387项零跳过；视频矩阵11项包含10条成功路线和1条应拒绝路线，另有5类失败行为；扩展格式14项包含12成功和2个12-bit拒绝。独立校验视频、音频、色彩/HDR、方向与时间轴、结构及清理。普通图片BestEffort转码不能冒充全metadata保真。Root扩展格式文件是合成测试输入，播放器兼容性不是本次格式保留验证的承诺。

资源曲线后段趋稳，quiet约61秒Private279.62→279.34MiB，未见逐操作handle/thread增长；自然边界后段斜率仍为正，已披露。独立审查仅接受所要求的有限R4观察，未以forcedGC或任意新增内存阈值通过。目前不需要为本次已修复问题引入Worker，更广泛崩溃隔离是另行架构议题。

详细实验、历史失败、逐项记录、原始输出位于本地 .ai-tmp/evidence/P7/P7-R4-large-batch-soak/，汇总进度见[阶段进度](P7-阶段进度与后续边界.md)。后续不自动扩大到全部视频codec或进入R5。

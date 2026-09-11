/*
 * EditDocumentMapper.cs
 *
 * 负责从底层 Native / SourceMediaFacts 适配创建 EditDocument 领域模型的轻量映射器。
 * 遵循架构规则：
 * 1. 纯内存映射逻辑，严格禁止访问文件系统（无 File.Exists 等 I/O 探测）。
 * 2. 外部确定 pairState 后传入，不自主发现/探测媒体事实。
 * 3. 集中收口 Native SourceProtocol 到 UI LivePhotoProtocolType 的契约映射。
 */

using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Models;

/// <summary>
/// 将底层的 Native SourceMediaFacts 适配映射为 EditDocument 领域事实模型。
/// </summary>
public static class EditDocumentMapper
{
    /// <summary>
    /// 将 Native SourceProtocol 映射为 UI 展示层 LivePhotoProtocolType。
    /// </summary>
    public static LivePhotoProtocolType MapSourceProtocol(SourceProtocol protocol) => protocol switch
    {
        SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
        SourceProtocol.GoogleMicroVideoV1 => LivePhotoProtocolType.GoogleV1,
        SourceProtocol.GoogleMotionPhotoV2 => LivePhotoProtocolType.GoogleV2,
        SourceProtocol.OppoLivePhoto => LivePhotoProtocolType.OPPO,
        SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => LivePhotoProtocolType.Vivo,
        SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => LivePhotoProtocolType.Samsung,
        SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => LivePhotoProtocolType.Huawei,
        _ => LivePhotoProtocolType.Unknown
    };

    /// <summary>
    /// 从外部已探测的 Native SourceMediaFacts 与基础信息适配创建文档。
    /// 注意：本方法只做内存事实赋值映射，不触发任何文件解析、I/O 或 Native 调用。
    /// pairState 必须由调用方在外部确定后传入。
    /// </summary>
    public static EditDocument FromInspectedFacts(
        string primaryPath,
        string? motionPath,
        LivePhotoType livePhotoType,
        EditPairState pairState,
        SourceMediaFacts facts,
        EditMetadataFacts? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var protocol = MapSourceProtocol(facts.Protocol);
        bool isLive = facts.Protocol != SourceProtocol.NonLive && facts.Protocol != SourceProtocol.Unknown;

        // 尺寸与图像容器
        uint width = facts.PrimaryImage?.Width ?? 0;
        uint height = facts.PrimaryImage?.Height ?? 0;
        ImageContainer imgContainer = facts.PrimaryImage?.Container ?? ImageContainer.Unknown;

        // 动态视频事实
        VideoFacts? vf = facts.MotionVideo;
        double duration = vf?.DurationSeconds ?? 0.0;
        double fps = vf?.Fps ?? 0.0;
        VideoContainer vidContainer = vf?.Container ?? VideoContainer.Unknown;
        VideoCodec vidCodec = vf?.Codec ?? VideoCodec.Unknown;
        bool hasAudio = vf?.HasAudio ?? false;
        int rotation = vf?.RotationDegrees ?? 0;
        long offset = vf?.ByteOffset ?? 0;
        long length = vf?.ByteLength ?? 0;

        if (width == 0 && vf != null) width = vf.Width;
        if (height == 0 && vf != null) height = vf.Height;

        // 封面状态
        EditKeyPhotoInfo? keyPhoto = null;
        if (facts.Timing != null && (facts.Timing.CoverFrameIndex >= 0 || facts.Timing.CoverTimestampUs > 0))
        {
            keyPhoto = new EditKeyPhotoInfo
            {
                FrameIndex = facts.Timing.CoverFrameIndex,
                TimestampUs = facts.Timing.CoverTimestampUs,
                TimestampSeconds = facts.Timing.CoverTimestampUs > 0 ? facts.Timing.CoverTimestampUs / 1_000_000.0 : 0.0,
                IsOriginal = true
            };
        }

        EditMediaKind mediaKind = isLive
            ? EditMediaKind.LivePhoto
            : (vf != null && vf.IsPresent && (facts.PrimaryImage == null || !facts.PrimaryImage.IsPresent))
                ? EditMediaKind.Video
                : EditMediaKind.Photo;

        uint vidWidth = (vf != null && vf.Width > 0) ? vf.Width : (mediaKind == EditMediaKind.Video ? width : 0);
        uint vidHeight = (vf != null && vf.Height > 0) ? vf.Height : (mediaKind == EditMediaKind.Video ? height : 0);

        return new EditDocument
        {
            PrimaryPath = primaryPath,
            MotionPath = motionPath,
            MediaKind = mediaKind,
            LivePhotoType = livePhotoType,
            Protocol = protocol,
            SourceProtocol = facts.Protocol,
            PairState = pairState,
            MotionVideoByteOffset = offset,
            MotionVideoByteLength = length,
            Width = width,
            Height = height,
            VideoWidth = vidWidth,
            VideoHeight = vidHeight,
            DurationSeconds = duration,
            FrameRate = fps,
            ImageContainer = imgContainer,
            VideoContainer = vidContainer,
            VideoCodec = vidCodec,
            HasAudio = hasAudio,
            RotationDegrees = rotation,
            OriginalKeyPhoto = keyPhoto,
            CurrentKeyPhoto = keyPhoto,
            Metadata = metadata ?? new()
        };
    }
}

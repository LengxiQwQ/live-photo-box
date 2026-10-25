using System;
using System.Collections.Generic;
using System.Linq;

namespace LivePhotoBox.Media.Models;

// Product semantics, deliberately independent of UI/settings ordinals and source vendors.
public enum TargetProtocol
{
    MotionPhotoFusion,
    GoogleMicroVideoV1,
    GoogleMotionPhotoV2,
    OppoLivePhoto,
    VivoLivePhoto,
    SamsungMotionPhoto,
    HuaweiMovingPhoto
}

public enum OutputMediaFormat
{
    JpegMp4,
    JpegMov,
    HeicMp4,
    HeicMov,
    HeicMp4Hevc
}

/// <summary>A validated target selection. P9 can replace writers without changing product callers.</summary>
public sealed record OutputProfile
{
    public TargetProtocol TargetProtocol { get; }
    public OutputMediaFormat Format { get; }
    public MediaFormatRequirement MediaRequirement { get; }

    private OutputProfile(TargetProtocol targetProtocol, OutputMediaFormat format)
    {
        TargetProtocol = targetProtocol;
        Format = format;
        MediaRequirement = format switch
        {
            OutputMediaFormat.JpegMp4 => Requirement(ImageContainer.Jpeg, VideoContainer.Mp4),
            OutputMediaFormat.JpegMov => Requirement(ImageContainer.Jpeg, VideoContainer.Mov),
            OutputMediaFormat.HeicMp4 => Requirement(ImageContainer.Heic, VideoContainer.Mp4),
            OutputMediaFormat.HeicMov => Requirement(ImageContainer.Heic, VideoContainer.Mov),
            OutputMediaFormat.HeicMp4Hevc => Requirement(ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Hevc),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public static OutputProfile Create(TargetProtocol targetProtocol, OutputMediaFormat format)
    {
        if (!IsSupported(targetProtocol, format))
            throw new ArgumentException($"Output format {format} is not available for target {targetProtocol}.");
        return new OutputProfile(targetProtocol, format);
    }

    public static bool IsSupported(TargetProtocol target, OutputMediaFormat format) => target switch
    {
        TargetProtocol.MotionPhotoFusion or TargetProtocol.GoogleMicroVideoV1 =>
            format is OutputMediaFormat.JpegMp4 or OutputMediaFormat.JpegMov,
        TargetProtocol.GoogleMotionPhotoV2 =>
            format is OutputMediaFormat.JpegMp4 or OutputMediaFormat.JpegMov or OutputMediaFormat.HeicMov,
        TargetProtocol.OppoLivePhoto or TargetProtocol.VivoLivePhoto => format == OutputMediaFormat.JpegMp4,
        TargetProtocol.SamsungMotionPhoto => format is OutputMediaFormat.JpegMp4 or OutputMediaFormat.HeicMp4,
        TargetProtocol.HuaweiMovingPhoto =>
            format is OutputMediaFormat.JpegMp4 or OutputMediaFormat.HeicMp4 or OutputMediaFormat.HeicMp4Hevc,
        _ => false
    };

    public static IReadOnlyList<OutputProfile> GetSupportedProfiles() =>
        Enum.GetValues<TargetProtocol>().SelectMany(target => Enum.GetValues<OutputMediaFormat>()
            .Where(format => IsSupported(target, format)).Select(format => Create(target, format))).ToArray();

    private static MediaFormatRequirement Requirement(ImageContainer image, VideoContainer video,
        VideoCodec codec = VideoCodec.Copy) => new()
        { ImageContainer = image, VideoContainer = video, VideoCodec = codec };
}

/// <summary>Protocol-free split selection. Target writing belongs to Merge/P9.</summary>
public enum SplitMediaFormat { KeepSource, JpegMov, HeicMov, JpegMp4 }

public static class SplitMediaRequirements
{
    public static MediaFormatRequirement Get(SplitMediaFormat format) => format switch
    {
        SplitMediaFormat.KeepSource => new() { ImageContainer = ImageContainer.Unknown,
            VideoContainer = VideoContainer.Unknown, VideoCodec = VideoCodec.Copy },
        SplitMediaFormat.JpegMov => new() { ImageContainer = ImageContainer.Jpeg,
            VideoContainer = VideoContainer.Mov, VideoCodec = VideoCodec.Hevc },
        SplitMediaFormat.HeicMov => new() { ImageContainer = ImageContainer.Heic,
            VideoContainer = VideoContainer.Mov, VideoCodec = VideoCodec.Hevc },
        SplitMediaFormat.JpegMp4 => new() { ImageContainer = ImageContainer.Jpeg,
            VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.H264 },
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown split media format.")
    };
}

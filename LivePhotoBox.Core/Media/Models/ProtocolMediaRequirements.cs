using System;
using System.Collections.Generic;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Models;

/// <summary>
/// Strongly-typed media format requirement for an output format or operation.
/// Decouples generic media converters from vendor protocol internals.
/// </summary>
public sealed record MediaFormatRequirement
{
    public required ImageContainer ImageContainer { get; init; }
    public required VideoContainer VideoContainer { get; init; }
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Copy;
    public int? TargetFps { get; init; }
    public bool KeepSourceIfSame { get; init; } = true;
}

/// <summary>
/// Maps protocol compatibility matrix indices to typed MediaFormatRequirements.
/// </summary>
public static class ProtocolMediaRequirements
{
    /// <summary>
    /// Gets the media requirement for a merge protocol index and format index.
    /// Format indices: 0=JPG_MP4, 1=JPG_MOV, 2=HEIC_MP4, 3=HEIC_MOV, 4=HEIC_MP4_H265.
    /// </summary>
    public static MediaFormatRequirement GetMergeRequirement(int protocolIndex, int formatIndex)
    {
        if (!ProtocolFormatMatrix.IsAvailable(protocolIndex, formatIndex))
        {
            throw new ArgumentException(
                $"Merge format index {formatIndex} is not available for protocol index {protocolIndex}.");
        }

        return formatIndex switch
        {
            ProtocolFormatMatrix.FormatJpgMp4 => new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Jpeg,
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Copy
            },
            ProtocolFormatMatrix.FormatJpgMov => new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Jpeg,
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Copy
            },
            ProtocolFormatMatrix.FormatHeicMp4 => new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Copy
            },
            ProtocolFormatMatrix.FormatHeicMov => new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Copy
            },
            ProtocolFormatMatrix.FormatHeicMp4H265 => new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Hevc
            },
            _ => throw new ArgumentOutOfRangeException(nameof(formatIndex), formatIndex, "Unknown format index.")
        };
    }

    /// <summary>
    /// Gets the media requirement for a split protocol and output format index.
    /// Split format indices: 0=keep, 1=jpg+mov, 2=heic+mov, 3=jpg+mp4.
    /// </summary>
    public static MediaFormatRequirement GetSplitRequirement(int splitProtocolIndex, int splitFormatIndex)
    {
        if (!ProtocolFormatMatrix.IsSplitAvailable(splitProtocolIndex, splitFormatIndex))
        {
            throw new ArgumentException(
                $"Split format index {splitFormatIndex} is not available for split protocol index {splitProtocolIndex}.");
        }

        return splitFormatIndex switch
        {
            ProtocolFormatMatrix.SplitFormatKeep => new MediaFormatRequirement // keep
            {
                ImageContainer = ImageContainer.Unknown,
                VideoContainer = VideoContainer.Unknown,
                VideoCodec = VideoCodec.Copy,
                KeepSourceIfSame = true
            },
            ProtocolFormatMatrix.SplitFormatJpgMov => new MediaFormatRequirement // jpg+mov
            {
                ImageContainer = ImageContainer.Jpeg,
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Hevc
            },
            ProtocolFormatMatrix.SplitFormatHeicMov => new MediaFormatRequirement // heic+mov
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Hevc
            },
            ProtocolFormatMatrix.SplitFormatJpgMp4 => new MediaFormatRequirement // jpg+mp4
            {
                ImageContainer = ImageContainer.Jpeg,
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.H264
            },
            _ => throw new ArgumentOutOfRangeException(nameof(splitFormatIndex), splitFormatIndex, "Unknown split format index.")
        };
    }
}

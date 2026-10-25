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
        return ProductSelectionAdapter.Merge(protocolIndex, formatIndex).MediaRequirement;
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

        return SplitMediaRequirements.Get(ProductSelectionAdapter.Split(0, splitFormatIndex));
    }
}

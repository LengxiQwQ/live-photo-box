using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Compatibility adapter for persisted Windows dropdowns and existing CLI aliases.
/// Ordinals stop here; product orchestration consumes typed selections.
/// </summary>
public static class ProductSelectionAdapter
{
    public static TargetProtocol MergeTarget(int index) => index switch
    {
        0 => TargetProtocol.MotionPhotoFusion,
        1 => TargetProtocol.GoogleMicroVideoV1,
        2 => TargetProtocol.GoogleMotionPhotoV2,
        3 => TargetProtocol.OppoLivePhoto,
        4 => TargetProtocol.VivoLivePhoto,
        5 => TargetProtocol.SamsungMotionPhoto,
        6 => TargetProtocol.HuaweiMovingPhoto,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Unknown target selection.")
    };

    public static OutputMediaFormat MergeFormat(int index) => index switch
    {
        0 => OutputMediaFormat.JpegMp4,
        1 => OutputMediaFormat.JpegMov,
        2 => OutputMediaFormat.HeicMp4,
        3 => OutputMediaFormat.HeicMov,
        4 => OutputMediaFormat.HeicMp4Hevc,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Unknown media selection.")
    };

    public static OutputProfile Merge(int protocolIndex, int formatIndex) =>
        OutputProfile.Create(MergeTarget(protocolIndex), MergeFormat(formatIndex));

    public static SplitMediaFormat Split(int protocolIndex, int formatIndex)
    {
        if (protocolIndex != ProtocolFormatMatrix.SplitProtocolNone)
            throw new NotSupportedException("Rebuilt split exports protocol-free neutral media only; use protocol 'none'.");
        return formatIndex switch
        {
            0 => SplitMediaFormat.KeepSource,
            1 => SplitMediaFormat.JpegMov,
            2 => SplitMediaFormat.HeicMov,
            3 => SplitMediaFormat.JpegMp4,
            _ => throw new ArgumentOutOfRangeException(nameof(formatIndex), formatIndex, "Unknown split media selection.")
        };
    }

    // Only the temporary P8 writer adapter uses this reverse mapping. P9 replaces it.
    internal static int WriterProtocol(TargetProtocol target) => target switch
    {
        TargetProtocol.MotionPhotoFusion => 0,
        TargetProtocol.GoogleMicroVideoV1 => 1,
        TargetProtocol.GoogleMotionPhotoV2 => 2,
        TargetProtocol.OppoLivePhoto => 3,
        TargetProtocol.VivoLivePhoto => 4,
        TargetProtocol.SamsungMotionPhoto => 5,
        TargetProtocol.HuaweiMovingPhoto => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    internal static int WriterFormat(OutputMediaFormat format) => format switch
    {
        OutputMediaFormat.JpegMp4 => 0,
        OutputMediaFormat.JpegMov => 1,
        OutputMediaFormat.HeicMp4 => 2,
        OutputMediaFormat.HeicMov => 3,
        OutputMediaFormat.HeicMp4Hevc => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

using System;
using System.IO;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Compatibility facade for callers that still need a synchronous protocol
/// filter. Recognition is delegated entirely to the Native-backed source
/// inspector; this type deliberately contains no byte, XMP, or filename
/// heuristics.
/// </summary>
public static class LivePhotoProtocolDetector
{
    public static LivePhotoProtocolType Detect(
        string filePath,
        LivePhotoType livePhotoType,
        string? contentIdentifier = null,
        string? xmpText = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) ||
            livePhotoType == LivePhotoType.None || livePhotoType == LivePhotoType.DualFile)
            return LivePhotoProtocolType.Unknown;

        try
        {
            var facts = new SourceInspector().InspectAsync(filePath).GetAwaiter().GetResult();
            return Map(facts.Protocol);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceInspectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Scan($"Native protocol inspection failed for '{Path.GetFileName(filePath)}': {ex.Message}", LogLevel.Warning);
            throw;
        }
    }

    internal static LivePhotoProtocolType Map(SourceProtocol protocol) => protocol switch
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
}

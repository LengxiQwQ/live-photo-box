using System;

namespace LivePhotoBox.Media.Models;

public sealed record ImageConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required ImageContainer TargetContainer { get; init; }
    public required string TargetDirectory { get; init; }
    public int Quality { get; init; } = 92;
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
    public ImageConversionSourceFacts? TrustedSourceFacts { get; init; }
    /// <summary>Required to enter the explicit R3 semantic HDR/GainMap route.</summary>
    public ImageHdrGainMapSourceBinding? TrustedHdrGainMapBinding { get; init; }
    /// <summary>Explicit vendor-neutral target representation for an R3 semantic route.</summary>
    public ImageHdrGainMapTargetSemantic TargetHdrGainMapSemantic { get; init; } = ImageHdrGainMapTargetSemantic.Unknown;
    /// <summary>HDR-specific output authority; AllowDiscard does not imply SDR degradation permission.</summary>
    public HdrOutputPolicy HdrOutputPolicy { get; init; } = HdrOutputPolicy.PreserveHdr;
    public ImageTransformKind Transform { get; init; } = ImageTransformKind.None;
}

public sealed record ImageExecutionRecord
{
    public required ImageContainer InputContainer { get; init; }
    public required ImageContainer OutputContainer { get; init; }
    public bool PixelReencoded { get; init; }
    public bool MetadataCopied { get; init; }
    public PreservationOutcome PreservationOutcome { get; init; }
    public TimeSpan Duration { get; init; }
    public ConversionExecutionTruth Truth { get; init; } = new();
}

public sealed record ImageConversionResult
{
    public bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required ImageExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

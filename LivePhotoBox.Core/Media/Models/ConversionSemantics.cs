using System;

namespace LivePhotoBox.Media.Models;

// Backend-neutral conversion truth. Provider names remain diagnostic text only.
public enum ConversionOperationKind { Unknown, Passthrough, ContainerRemux, LosslessTransform, LosslessTranscode, LossyReencode, ColorTransform, HdrGainMapConversion, DegradedOutput, Unsupported }
public enum ConversionCapability { Unknown, ImageCodec, ImageLosslessTransform, HdrGainMapConversion, VideoRemux, VideoTranscodeSdr, VideoTranscodeHdr10Bit, ColorTransform }
public enum ConversionFailureStage { None, InvalidRequest, TrustedFactsMismatch, SourceInspection, PolicyRejection, BackendUnavailable, BackendExecution, OutputValidation, Cleanup }
public enum ConversionFailureCategory { None, InvalidRequest, Unsupported, TrustedFactsMismatch, SourceInspection, PolicyRejection, BackendUnavailable, BackendFailure, OutputMissing, OutputValidation, CleanupFailure }
public enum ConversionFactsOrigin { ArtifactDeclaration, CallerTrustedNeutralFacts, CompatibilityInspectionOrProbe }
public enum ConversionComponentOutcome { NotEvaluated, NotApplicable, Preserved, Reencoded, Degraded, Lost }
public enum HdrOutputPolicy { PreserveHdr, AllowSdrDegradation }

/// <summary>
/// Vendor-neutral target representation for an explicit semantic GainMap
/// conversion. This is independent of the source vendor and Native ABI values.
/// </summary>
public enum ImageHdrGainMapTargetSemantic
{
    Unknown = 0,
    JpegIsoGainMap = 1,
    HeicGainMapAuxiliary = 2
}

/// <summary>
/// Backend-neutral physical JPEG transform requested by an image conversion.
/// The numeric values deliberately do not expose Native ABI transform constants.
/// </summary>
public enum ImageTransformKind
{
    None = 0,
    Rotate90,
    Rotate180,
    Rotate270,
    FlipHorizontal,
    FlipVertical,
    Transpose,
    Transverse
}

public sealed record ImageConversionSourceFacts
{
    public required ImageContainer Container { get; init; }
    public ImageCodec Codec { get; init; } = ImageCodec.Unknown;
    public bool HasGainMap { get; init; }
    public bool HasAuxiliaryMedia { get; init; }
}

public sealed record VideoConversionSourceFacts
{
    public required VideoContainer Container { get; init; }
    public VideoCodec Codec { get; init; } = VideoCodec.Unknown;
    public bool HasAudio { get; init; }
    public int RotationDegrees { get; init; }
    public double DurationSeconds { get; init; }
}

public sealed record ConversionExecutionTruth
{
    public ConversionOperationKind RequestedOperationKind { get; init; }
    public ConversionOperationKind ActualOperationKind { get; init; }
    public ConversionCapability SelectedCapability { get; init; }
    public ConversionCapability ActualCapability { get; init; }
    public ConversionFactsOrigin FactsOrigin { get; init; }
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
    public bool FallbackAllowed { get; init; }
    public bool FallbackOccurred { get; init; }
    public string? FallbackReason { get; init; }
    public string BackendName { get; init; } = string.Empty;
    public string BackendVersion { get; init; } = string.Empty;
    public string HardwareMode { get; init; } = "Unknown";
    public string InputProfile { get; init; } = "Unknown";
    public string OutputProfile { get; init; } = "Unknown";
    public ConversionComponentOutcome Pixel { get; init; }
    public ConversionComponentOutcome Metadata { get; init; }
    public ConversionComponentOutcome ColorIcc { get; init; }
    public ConversionComponentOutcome HdrGainMap { get; init; }
    public ConversionComponentOutcome Auxiliary { get; init; }
    public ConversionComponentOutcome Audio { get; init; }
    public ConversionComponentOutcome Timing { get; init; }
    public ConversionComponentOutcome Orientation { get; init; }
    public PreservationOutcome PreservationOutcome { get; init; }
    public ConversionFailureStage FailureStage { get; init; }
    public ConversionFailureCategory FailureCategory { get; init; }
}

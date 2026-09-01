using System;

namespace LivePhotoBox.Media.Models;

public sealed record ImageConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required ImageContainer TargetContainer { get; init; }
    public required string TargetDirectory { get; init; }
    public int Quality { get; init; } = 92;
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
}

public sealed record ImageExecutionRecord
{
    public required ImageContainer InputContainer { get; init; }
    public required ImageContainer OutputContainer { get; init; }
    public bool PixelReencoded { get; init; }
    public bool MetadataCopied { get; init; }
    public PreservationOutcome PreservationOutcome { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record ImageConversionResult
{
    public bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required ImageExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

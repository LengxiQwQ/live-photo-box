using System;

namespace LivePhotoBox.Media.Models;

public sealed record VideoConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required VideoContainer TargetContainer { get; init; }
    public required VideoCodec TargetCodec { get; init; }
    public required string TargetDirectory { get; init; }
    public string? OutputFileName { get; init; }
}

public sealed record VideoExecutionRecord
{
    public required VideoContainer InputContainer { get; init; }
    public required VideoContainer OutputContainer { get; init; }
    public required VideoCodec InputCodec { get; init; }
    public required VideoCodec RequestedCodec { get; init; }
    public required VideoCodec OutputCodec { get; init; }
    public string? SelectedEncoder { get; init; }
    public bool RemuxUsed { get; init; }
    public bool HardwareFallbackOccurred { get; init; }
    public bool AudioPreserved { get; init; }
    public bool RotationPreserved { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record VideoConversionResult
{
    public required bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required VideoExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

using System;

namespace LivePhotoBox.Media.Models;

public enum VideoBackend
{
    Unknown = 0,
    ProjectIsoBmffRemux = 1,
    WindowsMediaFoundation = 2,
    MinimalLibav = 3
}

public enum VideoHardwareMode
{
    NotApplicable = 0,
    SoftwareForced = 1,
    Unknown = 2
}

public sealed record VideoConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required VideoContainer TargetContainer { get; init; }
    public required VideoCodec TargetCodec { get; init; }
    public required string TargetDirectory { get; init; }
    public int Crf { get; init; } = 23;
    public int TargetFps { get; init; } = 0;
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
    public VideoConversionSourceFacts? TrustedSourceFacts { get; init; }
}

public sealed record VideoExecutionRecord
{
    public required VideoContainer InputContainer { get; init; }
    public required VideoCodec InputCodec { get; init; }
    public required VideoContainer RequestedContainer { get; init; }
    public required VideoCodec RequestedCodec { get; init; }
    public required VideoContainer OutputContainer { get; init; }
    public required VideoCodec OutputCodec { get; init; }
    public bool RemuxUsed { get; init; }
    public VideoBackend Backend { get; init; } = VideoBackend.Unknown;
    public VideoHardwareMode HardwareMode { get; init; } = VideoHardwareMode.Unknown;
    public string SelectedEncoder { get; init; } = string.Empty;
    public bool HardwareFallbackOccurred { get; init; }
    public string HardwareFallbackReason { get; init; } = string.Empty;
    public bool AudioPreserved { get; init; }
    public bool RotationPreserved { get; init; }
    public TimeSpan Duration { get; init; }
    public VideoMetadataPreservationEvidence? MetadataPreservation { get; init; }
    public VideoCompleteRemuxEvidence? CompleteRemuxProof { get; init; }
    public ConversionExecutionTruth Truth { get; init; } = new();
}

/// <summary>Versioned original Copy-remux before/after transport proof.
/// No codec transform; permitted container/address/name mappings only.</summary>
public sealed record VideoCompleteRemuxEvidence(ulong SourceLength, ulong OutputLength,
    string SourceSha256, string OutputSha256)
{
    public int ProofVersion => 2;
    public string ProofKind => "OriginalCopyRemux";
}

/// <summary>Native pre-publication comparison of scoped static metadata assets.
/// Digests bind static boxes and canonical timed-header metadata projections;
/// target-owned IDs/timebase/duration, coded A/V and file layout are excluded.</summary>
public sealed record VideoMetadataPreservationEvidence
{
    public required PreservationPolicy Policy { get; init; }
    public required uint SourceBoxCount { get; init; }
    public required uint VerifiedBoxCount { get; init; }
    public required uint OpaqueBoxCount { get; init; }
    public required uint AudioBoxCount { get; init; }
    public required string SourceSha256 { get; init; }
    public required string OutputSha256 { get; init; }
}

public sealed record VideoConversionResult
{
    public bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required VideoExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

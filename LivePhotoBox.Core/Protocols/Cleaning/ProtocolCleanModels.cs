using System;
using System.Collections.Generic;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Strongly-typed request to clean source protocol-specific markers from extracted media artifacts.
/// </summary>
public sealed record ProtocolCleanRequest
{
    public required SourceMediaFacts SourceFacts { get; init; }
    public required ExtractedMediaBundle ExtractedBundle { get; init; }
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
}

/// <summary>
/// Description of a specific vendor protocol marker or feature that was removed during cleaning.
/// </summary>
public sealed record RemovedProtocolFact
{
    public required string ProtocolName { get; init; }
    public required string Component { get; init; }
    public required string Description { get; init; }
    public ProtocolFactKind Kind { get; init; } = ProtocolFactKind.Removed;
}

public enum ProtocolFactKind
{
    Removed,
    Extracted
}

/// <summary>
/// Result of source protocol cleaning operation.
/// </summary>
public sealed record ProtocolCleanResult
{
    public bool Success { get; init; }
    public MediaArtifact? CleanedImage { get; init; }
    public MediaArtifact? CleanedVideo { get; init; }
    public MediaArtifact? CleanedGainMap { get; init; }
    public IReadOnlyList<RemovedProtocolFact> RemovedFacts { get; init; } = [];
    public PreservationOutcome PreservationOutcome { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Manifest describing an individual neutral media artifact.
/// </summary>
public sealed record NeutralArtifactManifest
{
    public required string Role { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public long ByteLength { get; init; }
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;
    public PreservationOutcome PreservationOutcome { get; init; }
}

/// <summary>
/// NeutralMediaBundle: Immutable, fully cleaned and format-aligned bundle of media artifacts.
/// The bundle retains provenance of the source media facts, but the artifacts themselves
/// contain zero source Live/Motion Photo vendor dependencies.
/// </summary>
public sealed record NeutralMediaBundle
{
    public required MediaArtifact PrimaryImage { get; init; }
    public MediaArtifact? MotionVideo { get; init; }
    public MediaArtifact? GainMap { get; init; }
    public required SourceMediaFacts SourceProvenance { get; init; }
    public required IReadOnlyList<RemovedProtocolFact> RemovedProtocolFacts { get; init; }
    public required IReadOnlyList<NeutralArtifactManifest> Manifest { get; init; }
    public TimingFacts Timing { get; init; } = new();
}

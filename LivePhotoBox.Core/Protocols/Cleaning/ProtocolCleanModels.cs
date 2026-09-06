using System;
using System.Collections.Generic;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Strongly-typed request to clean source protocol-specific markers from extracted media artifacts.
/// The single trusted authority is ExtractedBundle; SourceFacts is derived directly from the bundle.
/// </summary>
public sealed record ProtocolCleanRequest
{
    public required ExtractedMediaBundle ExtractedBundle { get; init; }

    public SourceMediaFacts SourceFacts => ExtractedBundle.SourceFacts;

    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
}

public enum ProtocolFactKind
{
    Removed,
    Extracted
}

/// <summary>
/// Description of a specific vendor protocol marker or feature that was removed during cleaning.
/// Contains both human-readable descriptions and machine-verifiable authorization IDs.
/// </summary>
public sealed record RemovedProtocolFact
{
    public required string ProtocolName { get; init; }
    public required string Component { get; init; }
    public required string Description { get; init; }
    public ProtocolFactKind Kind { get; init; } = ProtocolFactKind.Removed;

    public string? ResidueId { get; init; }
    public MediaArtifactKind? ArtifactRole { get; init; }
    public ResidueStructureKind? StructureKind { get; init; }
    public string? Operation { get; init; }
    public string? BeforeFingerprint { get; init; }
    public string? AfterStatus { get; init; }
}

/// <summary>
/// An individual planned cleanup mutation derived from P1-authorized residues.
/// </summary>
public sealed record PlannedCleanupAction
{
    public required string ResidueId { get; init; }
    public required SourceProtocol OwnerProtocol { get; init; }
    public required MediaArtifactKind ArtifactRole { get; init; }
    public required ResidueStructureKind StructureKind { get; init; }
    public required string Selector { get; init; }
    public string? ExpectedSemantic { get; init; }
    public ResidueRemovalMode RemovalMode { get; init; } = ResidueRemovalMode.Delete;
    public string? ExpectedFingerprint { get; init; }
    public bool IsMandatory { get; init; } = true;
}

public sealed record PlannedArtifactTarget
{
    public required MediaArtifactKind Role { get; init; }
    public required long ExpectedByteLength { get; init; }
    public required string ExpectedSha256 { get; init; }
}

/// <summary>
/// Immutable plan generated and validated before any destructive mutations occur.
/// </summary>
public sealed record ProtocolCleanupPlan
{
    public required SourceProtocol Protocol { get; init; }
    public required IReadOnlyList<PlannedCleanupAction> Actions { get; init; }
    public required IReadOnlyList<PlannedArtifactTarget> ArtifactTargets { get; init; }
    public long PrimaryArtifactLength => PrimaryTarget?.ExpectedByteLength ?? 0;
    public string PrimaryArtifactSha256 => PrimaryTarget?.ExpectedSha256 ?? "";
    public long? SecondaryArtifactLength => VideoTarget?.ExpectedByteLength;
    public string? SecondaryArtifactSha256 => VideoTarget?.ExpectedSha256;
    public PlannedArtifactTarget? PrimaryTarget => System.Linq.Enumerable.FirstOrDefault(ArtifactTargets, t => t.Role == MediaArtifactKind.PrimaryImage);
    public PlannedArtifactTarget? VideoTarget => System.Linq.Enumerable.FirstOrDefault(ArtifactTargets, t => t.Role == MediaArtifactKind.MotionVideo);
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum PreservationCheckStatus
{
    VerifiedPreserved,
    SemanticallyPreserved,
    IntentionallyRemovedProtocolData,
    NotApplicable,
    UnableToVerify,
    Failed
}

public sealed record PreservationReportItem
{
    public required string Name { get; init; }
    public required PreservationCheckStatus Status { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// Auditable evidence verifying that non-protocol metadata and media samples remained preserved.
/// </summary>
public sealed record PreservationReport
{
    public required PreservationOutcome OverallOutcome { get; init; }
    public required IReadOnlyList<PreservationReportItem> Items { get; init; }
    public string? Summary { get; init; }
}

/// <summary>
/// Immutable snapshot of media artifact bytes, lengths, hashes, and extracted metadata baseline
/// frozen before any destructive mutations or staging begin.
/// </summary>
public sealed record PreservationBaseline
{
    public required byte[] PrimaryImageBytes { get; init; }
    public required long PrimaryImageLength { get; init; }
    public required string PrimaryImageSha256 { get; init; }
    public ImageContainer PrimaryImageContainer { get; init; }
    public required string PrimaryImagePath { get; init; }

    public SourceProtocol Protocol { get; init; }

    public byte[]? MotionVideoBytes { get; init; }
    public long? MotionVideoLength { get; init; }
    public string? MotionVideoSha256 { get; init; }
    public VideoContainer MotionVideoContainer { get; init; }
    public string? MotionVideoPath { get; init; }
    public string? PreMdatSha { get; init; }

    public byte[]? GainMapBytes { get; init; }
    public long? GainMapLength { get; init; }
    public string? GainMapSha256 { get; init; }

    public string? PreCodestreamSha { get; init; }
    public MetadataPreservationVerifier.TiffMetadata? PreTiff { get; init; }
    public byte[]? PreIcc { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum CleanerFailureCategory
{
    None,
    // Authority / Precondition
    FactsNotConfirmed,
    CleanupAuthorizationMissing,
    ArtifactChangedSinceExtraction,
    UnsupportedProtocol,
    AmbiguousProtocol,
    ArtifactFactMismatch,
    // Cleaning
    AuthorizedResidueNotFound,
    AuthorizedResidueAmbiguous,
    StructureChanged,
    RemovalWouldTouchUnknownData,
    StructuredRewriteFailed,
    // Preservation / Validation
    UnexpectedMetadataChange,
    MediaPayloadChanged,
    GainMapChanged,
    HdrMetadataChanged,
    MediaInvalid,
    ProtocolStillDetected,
    // Environment
    OutputCreateFailed,
    DiskFull,
    AccessDenied,
    LockedFile,
    InvalidPath,
    Cancelled,
    // Transaction
    PublishFailed,
    RollbackFailed
}

public enum CleanerFailureStage
{
    Preflight,
    ArtifactVerification,
    Authorization,
    Planning,
    Staging,
    PreservationDiff,
    MediaValidation,
    PostCleanInspection,
    Commit,
    Rollback
}

public class CleanerException : Exception
{
    public CleanerFailureCategory Category { get; }
    public CleanerFailureStage Stage { get; }
    public SourceProtocol Protocol { get; }
    public MediaArtifactKind? ArtifactRole { get; }

    public CleanerException(
        CleanerFailureCategory category,
        CleanerFailureStage stage,
        SourceProtocol protocol,
        string message,
        MediaArtifactKind? artifactRole = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        Stage = stage;
        Protocol = protocol;
        ArtifactRole = artifactRole;
    }
}

public enum CleanerTransactionState
{
    Initial,
    Staging,
    Validated,
    Committing,
    Committed,
    RollingBack,
    RolledBack,
    RollbackFailed
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
    public PreservationReport? PreservationReport { get; init; }
    public ProtocolCleanupPlan? CleanupPlan { get; init; }
    public CleanerFailureCategory? FailureCategory { get; init; }
    public CleanerFailureStage? FailureStage { get; init; }
    public CleanerTransactionState? TransactionState { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}

public enum GainMapRepresentation
{
    None,
    Embedded,
    Detached,
    Both
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
    public GainMapRepresentation GainMapRepresentation { get; init; } = GainMapRepresentation.None;
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
    public GainMapRepresentation GainMapRepresentation { get; init; } = GainMapRepresentation.None;
    public required SourceMediaFacts SourceProvenance { get; init; }
    public required IReadOnlyList<RemovedProtocolFact> RemovedProtocolFacts { get; init; }
    public required IReadOnlyList<NeutralArtifactManifest> Manifest { get; init; }
    public TimingFacts Timing { get; init; } = new();
}

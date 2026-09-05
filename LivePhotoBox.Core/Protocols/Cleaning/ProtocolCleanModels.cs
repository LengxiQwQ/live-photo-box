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
    public required MediaArtifactKind ArtifactRole { get; init; }
    public required ResidueStructureKind StructureKind { get; init; }
    public required string Selector { get; init; }
    public string? ExpectedSemantic { get; init; }
    public ResidueRemovalMode RemovalMode { get; init; } = ResidueRemovalMode.Delete;
    public string? ExpectedFingerprint { get; init; }
    public bool IsMandatory { get; init; } = true;
}

/// <summary>
/// Immutable plan generated and validated before any destructive mutations occur.
/// </summary>
public sealed record ProtocolCleanupPlan
{
    public required SourceProtocol Protocol { get; init; }
    public required IReadOnlyList<PlannedCleanupAction> Actions { get; init; }
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

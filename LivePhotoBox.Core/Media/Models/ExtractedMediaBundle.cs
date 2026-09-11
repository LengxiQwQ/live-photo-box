namespace LivePhotoBox.Media.Models;

using System.Collections.Generic;
using LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Bundle of extracted media artifacts in an isolated transaction workspace.
/// </summary>
public sealed record ExtractedMediaBundle
{
    public required MediaArtifact PrimaryImage { get; init; }
    public MediaArtifact? MotionVideo { get; init; }
    public MediaArtifact? GainMap { get; init; }
    /// <summary>
    /// Optional full source-container copy required by a same-container
    /// cleaner.  This is deliberately separate from <see cref="PrimaryImage"/>
    /// and is never a semantic media artifact.
    /// </summary>
    public MediaArtifact? CleanupSource { get; init; }
    public required SourceMediaFacts SourceFacts { get; init; }
    public IReadOnlyList<RemovedProtocolFact> ExtractedProtocolFacts { get; init; } = [];
    public IReadOnlyList<AuxiliaryMediaDescriptor> AuxiliaryMedia { get; init; } = [];
    public IReadOnlyList<PreservationCarrier> PreservationCarriers { get; init; } = [];

    /// <summary>
    /// Native cleanup-plan authority issued from the P2 extraction record
    /// before commit (when this bundle was produced by the Native extractor).
    /// It carries the transaction-owned artifact identities the Cleaner must
    /// verify against before any destructive mutation.  Null when the bundle
    /// was built without a Native trust chain (tests must supply their own
    /// authority through <see cref="ProtocolCleanRequest.CleanupPlan"/>).
    /// </summary>
    public CleanupPlan? CleanupPlan { get; init; }
}

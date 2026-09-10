using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

/// <summary>
/// Inspector-confirmed semantic auxiliary media.  A descriptor is not
/// itself a file: an embedded item may legitimately have no materialized
/// artifact while still remaining available to the P2-to-P3 handoff.
/// </summary>
public sealed record AuxiliaryMediaDescriptor
{
    public MediaArtifactKind ArtifactRole { get; init; } = MediaArtifactKind.AuxiliaryItem;
    public required string StableIdentity { get; init; }
    public required string Semantic { get; init; }
    public required string OwnerIdentity { get; init; }
    public required string Relationship { get; init; }
    public int SourceIndex { get; init; }
    public long SourceOffset { get; init; }
    public long SourceLength { get; init; }
    public required string SourceSha256 { get; init; }
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public AuxiliaryCodec Codec { get; init; } = AuxiliaryCodec.Unknown;
    public AuxiliaryRepresentation Representation { get; init; }
    public AuxiliaryOwnership Ownership { get; init; }
    public string ItemType { get; init; } = string.Empty;
    public bool GraphComplete { get; init; }
    public IReadOnlyList<HeifDependencyFacts> Dependencies { get; init; } = [];
    public MediaArtifact? MaterializedArtifact { get; init; }
    public PreservationOutcome PreservationOutcome { get; init; } = PreservationOutcome.Preserved;
}

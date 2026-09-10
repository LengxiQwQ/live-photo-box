namespace LivePhotoBox.Media.Models;

/// <summary>
/// Typed preservation handoff for source-container bytes which are not a
/// semantic media asset.  Carriers are descriptive evidence, not writer
/// append instructions.
/// </summary>
public sealed record PreservationCarrier
{
    public MediaArtifactKind ArtifactRole { get; init; } = MediaArtifactKind.PrimaryImage;
    public required string StableIdentity { get; init; }
    public required string Semantic { get; init; }
    public required string OwnerIdentity { get; init; }
    public required string Relationship { get; init; }
    public required string SourceSha256 { get; init; }
    public PreservationCarrierKind Kind { get; init; }
    public int SourceIndex { get; init; }
    public long SourceOffset { get; init; }
    public long SourceLength { get; init; }
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public AuxiliaryCodec Codec { get; init; } = AuxiliaryCodec.Unknown;
    public AuxiliaryRepresentation Representation { get; init; } = AuxiliaryRepresentation.Embedded;
    public AuxiliaryOwnership Ownership { get; init; } = AuxiliaryOwnership.Primary;
    public PreservationOutcome Outcome { get; init; } = PreservationOutcome.Preserved;
}

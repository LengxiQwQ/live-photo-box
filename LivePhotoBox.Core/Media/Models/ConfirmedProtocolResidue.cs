using System;
using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

public enum ResidueStructureKind
{
    XmpProperty,
    XmpContainerItem,
    ExifMakerNoteTag,
    QuickTimeMdtaKey,
    QuickTimeMetadataTrack,
    IsoBmffBox,
    HeifItem,
    HeifProperty,
    SefEntry,
    ProtocolTailRange,
    UuidBox
}

public enum CoordinateSpace
{
    OriginalSourceRange,
    ExtractedArtifactRange,
    StructuredSelector
}

public enum ResidueRemovalMode
{
    Delete,
    ZeroFill,
    RebuildContainer
}

/// <summary>
/// Machine-consumable cleanup authorization explicitly granted by Source Inspector evidence.
/// Cleaner may only modify structures that match an authorized residue.
/// </summary>
public sealed record ConfirmedProtocolResidue
{
    public required string Id { get; init; }
    public required SourceProtocol OwnerProtocol { get; init; }
    public required MediaArtifactKind ArtifactRole { get; init; }
    public required ResidueStructureKind StructureKind { get; init; }
    public required string Selector { get; init; }
    public string? ExpectedSemantic { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public CoordinateSpace CoordinateSpace { get; init; } = CoordinateSpace.StructuredSelector;
    public ResidueRemovalMode RemovalMode { get; init; } = ResidueRemovalMode.Delete;
    public bool RequiredAfterExtraction { get; init; } = true;
}

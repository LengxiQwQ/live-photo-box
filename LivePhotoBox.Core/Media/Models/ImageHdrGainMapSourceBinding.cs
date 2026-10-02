namespace LivePhotoBox.Media.Models;

/// <summary>
/// Inspector-derived authority binding for an explicit HDR/GainMap conversion.
/// It binds the exact cleaned source object and GainMap graph entry; a boolean
/// claim that a file “has GainMap” is deliberately insufficient.
/// </summary>
public sealed class ImageHdrGainMapSourceBinding
{
    internal ImageHdrGainMapSourceBinding(
        string sourcePath,
        string sourceSha256,
        string primaryImageSha256,
        WindowsFileIdentity sourceFileIdentity,
        ImageContainer sourceContainer,
        string stableIdentity,
        string inspectedStableIdentity,
        string semantic,
        string ownerIdentity,
        string inspectedOwnerIdentity,
        string relationship,
        string gainMapSha256,
        uint itemId,
        uint auxiliaryIndex,
        long byteOffset,
        long byteLength,
        long sourceByteOffset,
        long sourceByteLength,
        AuxiliaryRepresentation representation,
        AuxiliaryOwnership ownership,
        MediaArtifactKind ownerArtifactRole)
    {
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        PrimaryImageSha256 = primaryImageSha256;
        SourceFileIdentity = sourceFileIdentity;
        SourceContainer = sourceContainer;
        StableIdentity = stableIdentity;
        InspectedStableIdentity = inspectedStableIdentity;
        Semantic = semantic;
        OwnerIdentity = ownerIdentity;
        InspectedOwnerIdentity = inspectedOwnerIdentity;
        Relationship = relationship;
        GainMapSha256 = gainMapSha256;
        ItemId = itemId;
        AuxiliaryIndex = auxiliaryIndex;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        SourceByteOffset = sourceByteOffset;
        SourceByteLength = sourceByteLength;
        Representation = representation;
        Ownership = ownership;
        OwnerArtifactRole = ownerArtifactRole;
    }

    public string SourcePath { get; }
    public string SourceSha256 { get; }
    public string PrimaryImageSha256 { get; }
    public WindowsFileIdentity SourceFileIdentity { get; }
    public ImageContainer SourceContainer { get; }
    public string StableIdentity { get; }
    /// <summary>Identity assigned by the freshly inspected neutral container after any authorized reassembly.</summary>
    public string InspectedStableIdentity { get; }
    public string Semantic { get; }
    public string OwnerIdentity { get; }
    /// <summary>Owner assigned by the freshly inspected neutral container after any authorized reassembly.</summary>
    public string InspectedOwnerIdentity { get; }
    public string Relationship { get; }
    public string GainMapSha256 { get; }
    public uint ItemId { get; }
    public uint AuxiliaryIndex { get; }
    public long ByteOffset { get; }
    public long ByteLength { get; }
    public long SourceByteOffset { get; }
    public long SourceByteLength { get; }
    public AuxiliaryRepresentation Representation { get; }
    public AuxiliaryOwnership Ownership { get; }
    public MediaArtifactKind OwnerArtifactRole { get; }
}

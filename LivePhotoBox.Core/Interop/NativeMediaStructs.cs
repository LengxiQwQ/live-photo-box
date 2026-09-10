using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMediaRange
{
    public ulong Offset;
    public ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public uint Width;
    public uint Height;
    public NativeMediaRange FileRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public int Codec;
    public uint Width;
    public uint Height;
    public int RotationDegrees;
    public double DurationSeconds;
    public double Fps;
    public int HasAudio;
    public NativeMediaRange FileRange;
    public int SourceIndex;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativeGainMapItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public int Representation;
    public int Ownership;
    public int OwnerArtifactRole;
    public uint AuxiliaryIndex;
    public uint ItemId;
    public NativeMediaRange FileRange;
    public unsafe fixed byte Relationship[64];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativeAuxiliaryItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public int Representation;
    public int Ownership;
    public uint ItemId;
    public NativeMediaRange FileRange;
    public fixed byte Relationship[64];
    public int Codec;
    public int SourceIndex;
    public fixed byte Sha256[32];
    public fixed byte StableIdentity[96];
    public fixed byte OwnerIdentity[96];
    public fixed byte Semantic[64];
    public fixed byte ItemType[8];
    public uint GraphFlags;
    public uint DependencyCount;
    public fixed uint DependencyItemIds[64];
    public fixed ulong DependencyOffsets[64];
    public fixed ulong DependencyLengths[64];
    public fixed byte DependencyItemTypes[64 * 8];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativePreservationCarrierFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Kind;
    public int SourceIndex;
    public int ArtifactRole;
    public int Container;
    public int Codec;
    public NativeMediaRange FileRange;
    public fixed byte Sha256[32];
    public fixed byte StableIdentity[96];
    public fixed byte OwnerIdentity[96];
    public fixed byte Relationship[96];
    public fixed byte Semantic[96];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTimingFacts
{
    public uint StructSize;
    public long CoverTimestampUs;
    public long PrimaryTimestampUs;
    public int CoverFrameIndex;
    public int TotalFrames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSourceMediaFacts
{
    public uint StructSize;
    public int Protocol;
    public NativeImageItemFacts PrimaryImage;
    public NativeVideoItemFacts MotionVideo;
    public NativeGainMapItemFacts GainMap;
    public NativeTimingFacts Timing;
    public NativeMediaRange ProtocolTailRange;
    public fixed byte PairingIdentifier[128];
    public fixed byte PrimarySha256[32];
    public fixed byte SecondarySha256[32];
    public int HasSecondarySource;
    public uint AuxiliaryCount;
    public NativeAuxiliaryItemFacts Auxiliary0;
    public NativeAuxiliaryItemFacts Auxiliary1;
    public NativeAuxiliaryItemFacts Auxiliary2;
    public NativeAuxiliaryItemFacts Auxiliary3;
    public NativeAuxiliaryItemFacts Auxiliary4;
    public NativeAuxiliaryItemFacts Auxiliary5;
    public NativeAuxiliaryItemFacts Auxiliary6;
    public NativeAuxiliaryItemFacts Auxiliary7;
    public uint PreservationCarrierCount;
    public NativePreservationCarrierFacts Carrier0;
    public NativePreservationCarrierFacts Carrier1;
    public NativePreservationCarrierFacts Carrier2;
    public NativePreservationCarrierFacts Carrier3;
    public NativePreservationCarrierFacts Carrier4;
    public NativePreservationCarrierFacts Carrier5;
    public NativePreservationCarrierFacts Carrier6;
    public NativePreservationCarrierFacts Carrier7;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeExtractionOutput
{
    public uint StructSize;
    public uint AuxiliaryIndex;
    public nint OutputPath;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeConfirmedResidue
{
    public uint StructSize;
    public fixed byte ResidueId[64];
    public int OwnerProtocol;
    public int ArtifactRole;
    public int StructureKind;
    public fixed byte Selector[128];
    public fixed byte ExpectedSemantic[64];
    public fixed byte ExpectedFingerprint[64];
    public int CoordinateSpace;
    public int RemovalMode;
    public int RequiredAfterExtraction;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCleanupAction
{
    public uint StructSize;
    public fixed byte ResidueId[64];
    public int OwnerProtocol;
    public int ArtifactRole;
    public int StructureKind;
    public fixed byte Selector[128];
    public fixed byte ExpectedSemantic[64];
    public fixed byte ExpectedFingerprint[64];
    public int CoordinateSpace;
    public int RemovalMode;
    public int IsMandatory;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCleanupArtifactBinding
{
    public uint StructSize;
    public int ArtifactRole;
    public ulong ExpectedLength;
    public fixed byte ExpectedSha256[32];
    public int HasExpectedSha256;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeRemovedProtocolFact
{
    public uint StructSize;
    public fixed byte ProtocolName[64];
    public fixed byte Component[64];
    public fixed byte Description[128];
    public fixed byte ResidueId[64];
    public int ArtifactRole;
    public int StructureKind;
    public fixed byte Operation[64];
    public fixed byte BeforeFingerprint[64];
    public fixed byte AfterStatus[64];
}

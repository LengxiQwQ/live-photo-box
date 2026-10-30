using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeFileIdentity = LivePhotoBox.Interop.NativeCleanService.NativeFileIdentity;

namespace LivePhotoBox.Interop;

// Versioned ABI transport only. These caller expectations do not confer authority;
// Native must observe, pin and validate actual objects before issuing a plan.
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetWriterArtifactV1
{
    public uint StructSize, Version;
    public uint Role, Container, VideoCodec, Reserved;
    public NativeFileIdentity ExpectedIdentity;
    public fixed byte Sha256[32];
    public fixed byte PathUtf8[1024];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetWriterRequestV1
{
    public uint StructSize, Version;
    public uint TargetProfile, TargetVersion, MediaFormat;
    public uint ArtifactCount, Reserved0, Reserved1;
    public nint Artifacts;
    public long CoverTimestampUs;
    public uint CoverTimestampPresent, Reserved2;
    public fixed byte StagingDirectoryUtf8[1024];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetWriterOutputV1
{
    public uint StructSize, Version;
    public uint ArtifactRole, ArtifactIndex, TargetProfile, TargetVersion;
    public ulong Generation, StageId, ByteLength;
    public NativeFileIdentity Identity;
    public fixed byte Sha256[32];
    public fixed byte RequestFingerprint[32];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetWriterStatusV1
{
    public uint StructSize, Version, State, FailureCategory;
    public uint TargetProfile, OutputCount;
    public ulong Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetAuxiliaryV2
{
    public uint StructSize, Version, Representation, SelectorKind, SelectorId, OwnerIndex;
    public ulong EmbeddedOffset, ByteLength;
    public fixed byte Sha256[32];
    public fixed byte StableIdentity[128], OwnerIdentity[128], Semantic[32], Relationship[96];
    public NativeTargetWriterArtifactV1 Materialized;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetWriterRequestV2
{
    public uint StructSize, Version;
    public NativeTargetWriterRequestV1 Base;
    public long PrimaryTimestampUs;
    public uint PrimaryTimestampPresent, AuxiliaryCount;
    public nint Auxiliaries;
    public fixed byte PrimaryIdentity[128];
    public ulong Reserved;
}

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_prepare_v4")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterPrepareV4(nint context,NativeTargetWriterRequestV4* request,out nint plan);
    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_prepare_v3")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterPrepareV3(nint context, NativeTargetWriterRequestV3* request, out nint plan);
    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_prepare_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterPrepareV2(nint context,
        NativeTargetWriterRequestV2* request, out nint plan);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_prepare_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterPrepare(nint context,
        NativeTargetWriterRequestV1* request, out nint plan);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_stage_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TargetWriterStage(nint context, nint plan);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_get_outputs_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterGetOutputs(nint context, nint plan,
        NativeTargetWriterOutputV1* outputs, uint capacity, out uint requiredCount,
        ref NativeTargetWriterStatusV1 status);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_read_stage_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetWriterReadStage(nint context, nint plan,
        ulong stageId, ulong offset, byte* output, uint capacity, out uint bytesRead);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_writer_release_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TargetWriterRelease(nint context, nint plan);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetWriterRequestV3
{
    public uint StructSize, Version;
    public NativeTargetWriterRequestV2 Base;
    public ulong CompoundPrimaryToken, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetOrdinaryMetadataV1
{
    public uint StructSize,Version,Kind,OwnerIndex,Flags,Reserved;
    public fixed byte StableIdentity[128],OwnerIdentity[128],Semantic[32],Relationship[96];
    public NativeTargetWriterArtifactV1 Materialized;
}
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetWriterRequestV4
{
    public uint StructSize,Version;
    public NativeTargetWriterRequestV3 Base;
    public uint MetadataCount,Reserved;
    public nint Metadata;
    public ulong ReservedTail;
}

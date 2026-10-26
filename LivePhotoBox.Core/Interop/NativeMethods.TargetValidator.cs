using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetReaderV1
{
    public uint StructSize, Version;
    public ulong ByteLength;
    public nint User, ReadAt;
    public ulong Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetValidationRequestV1
{
    public uint StructSize, Version, TargetProfile, TargetVersion, MediaFormat;
    public uint MemberCount, MemberIndex, ArtifactRole;
    public long CoverTimestampUs;
    public uint CoverTimestampPresent, ExpectedVideoCodec, ExpectedAuxiliaryCount, Flags;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetValidationResultV1
{
    public uint StructSize, Version, StructuralStatus, GenericMediaStatus;
    public uint PreservationStatus, DeviceStatus, FailureCategory, FailureStage;
    public uint ObservedTargetProfile, ObservedTargetVersion, ObservedMediaFormat, ObservedVideoCodec;
    public ulong ObservedByteLength, PrimaryOffset, PrimaryLength, VideoOffset, VideoLength;
    public long ObservedCoverTimestampUs;
    public uint CoverEncoding, ImageWidth, ImageHeight, ImageOrientation;
    public uint VideoWidth, VideoHeight, VideoOrientation, ObservedAuxiliaryCount, RuleId, Reserved;
    public ulong FailureByteOffset;
    public fixed byte ObservedSha256[32];

    internal string GetSha256()
    {
        fixed (byte* hash = ObservedSha256)
            return Convert.ToHexString(new ReadOnlySpan<byte>(hash, 32));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetAuxiliaryExpectationV2
{
    public uint StructSize, Version, Semantic, Container;
    public ulong ByteLength;
    public fixed byte Sha256[32];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetValidationRequestV2
{
    public uint StructSize, Version;
    public NativeTargetValidationRequestV1 Base;
    public long PrimaryTimestampUs;
    public uint PrimaryTimestampPresent, AuxiliaryCount;
    public nint Auxiliaries;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeTargetAuxiliaryObservedV2
{
    public uint Semantic, Container, SelectorKind, SelectorId;
    public ulong ByteOffset, ByteLength;
    public fixed byte Sha256[32];
    internal string GetSha256()
    {
        fixed (byte* hash = Sha256) return Convert.ToHexString(new ReadOnlySpan<byte>(hash, 32));
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeTargetValidationResultV2
{
    public uint StructSize, Version;
    public NativeTargetValidationResultV1 Base;
    public long ObservedPrimaryTimestampUs;
    public uint PrimaryTimestampEncoding, AuxiliaryCount;
    public NativeTargetAuxiliaryObservedV2 Auxiliary0, Auxiliary1;
}

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_target_validate_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetValidateV2(nint context,
        NativeTargetValidationRequestV2* request, NativeTargetReaderV1* reader,
        NativeTargetValidationResultV2* result);

    [LibraryImport(LibraryName, EntryPoint = "lpb_target_validate_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetValidate(nint context,
        NativeTargetValidationRequestV1* request, NativeTargetReaderV1* reader,
        NativeTargetValidationResultV1* result);
}

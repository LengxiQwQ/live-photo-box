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

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_target_validate_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TargetValidate(nint context,
        NativeTargetValidationRequestV1* request, NativeTargetReaderV1* reader,
        NativeTargetValidationResultV1* result);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeUnspecifiedJpegOrientationRequestV1
{
    public uint StructSize,ApiVersion,Handling,Reserved;
    public NativeCleanService.NativeFileIdentity ExpectedIdentity;
    public fixed byte SourceSha256[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeImageConversionRequestV2
{
    public uint StructSize,ApiVersion,SourceContainer,TargetContainer;
    public int Quality;
    public uint OrientationPolicy;
    public NativeCleanService.NativeFileIdentity ExpectedIdentity;
    public fixed byte SourceSha256[32];
    public uint Reserved0,Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeImageConversionProofV1
{
    public uint StructSize,ApiVersion,ProofKind,ProofFlags;
    public fixed byte SourceSha256[32];
    public fixed byte OutputSha256[32];
    public fixed byte MappedSourceMetadataSha256[32];
    public fixed byte OutputMetadataSha256[32];
    public ulong SourceLength,OutputLength;
    public uint SourceOrientation,OutputOrientation,Operation;
    public uint MetadataStatus,IccStatus,OrientationStatus;
    public uint SourceWidth,SourceHeight,OutputWidth,OutputHeight;
    public uint MetadataCount,Reserved;
    public NativeCleanService.NativeFileIdentity OutputIdentity;
}

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName,EntryPoint="lpb_resolve_unspecified_jpeg_orientation_v1",StringMarshalling=StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]
    internal static partial NativeResult ResolveUnspecifiedJpegOrientationV1(nint context,string sourcePath,string outputPath,
        in NativeUnspecifiedJpegOrientationRequestV1 request,ref NativeImageConversionProofV1 proof);
    [LibraryImport(LibraryName,EntryPoint="lpb_convert_image_v2",StringMarshalling=StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]
    internal static partial NativeResult ConvertImageV2(nint context,string sourcePath,string outputPath,
        in NativeImageConversionRequestV2 request,ref NativeImageConversionProofV1 proof);

    [LibraryImport(LibraryName,EntryPoint="lpb_get_hdr_image_conversion_proof_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs=[typeof(CallConvCdecl)])]
    internal static partial NativeResult GetHdrImageConversionProofV1(nint context,ulong transactionToken,
        ref NativeImageConversionProofV1 proof);
}

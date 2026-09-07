using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_media", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult InspectMedia(
        nint context,
        string primaryPath,
        string? secondaryPath,
        ref NativeSourceMediaFacts outFacts);

    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_media_with_residues", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult InspectMediaWithResidues(
        nint context,
        string primaryPath,
        string? secondaryPath,
        ref NativeSourceMediaFacts outFacts,
        NativeConfirmedResidue* outResidues,
        nuint residuesCapacity,
        out nuint outResiduesCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_extract_media", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ExtractMedia(
        nint context,
        string primaryPath,
        string? secondaryPath,
        in NativeSourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath);

    [LibraryImport(LibraryName, EntryPoint = "lpb_probe_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ProbeVideo(
        nint context,
        string videoPath,
        ref NativeVideoItemFacts outVideoFacts);

    [LibraryImport(LibraryName, EntryPoint = "lpb_remux_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult RemuxVideo(
        nint context,
        string inputVideoPath,
        string outputVideoPath,
        int targetContainer);

    [LibraryImport(LibraryName, EntryPoint = "lpb_convert_image", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ConvertImage(
        nint context,
        string inputImagePath,
        string outputImagePath,
        int targetContainer,
        int quality,
        out int outReencoded);

    [LibraryImport(LibraryName, EntryPoint = "lpb_transcode_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TranscodeVideo(
        nint context,
        string inputVideoPath,
        string outputVideoPath,
        int targetContainer,
        int targetCodec,
        int crf,
        byte* outEncoderUsed,
        nuint encoderBufLen);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_extractor_fault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TestSetExtractorFault(
        nint context,
        NativeExtractorFault fault,
        int targetArtifact,
        ulong triggerAfterBytes,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_cleaner_snapshot_hook")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TestSetCleanerSnapshotHook(
        nint context,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_sha256_buffer")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TestSha256Buffer(
        byte* data,
        nuint length,
        byte* outHash);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_sha256_file", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TestSha256File(
        nint fileHandle,
        byte* outHash);

    [LibraryImport(LibraryName, EntryPoint = "lpb_reassemble_jpeg_gainmap", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult lpb_reassemble_jpeg_gainmap(
        nint context,
        string primaryJpegPath,
        string gainmapJpegPath,
        string outputPath);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_capture_preservation_observation", ExactSpelling = true)]
    internal static extern NativeResult lpb_capture_preservation_observation(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mediaPath,
        int protocolHint,
        int containerHint,
        ref NativePreservationObservation outObservation);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativePreservationObservation
{
    public uint StructSize;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ImageCodestreamSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExifIfd0NonPtrSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExifExifIfdSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DatetimeOriginal;
    public ushort Orientation;
    public ushort Pad0;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string GpsSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string IccSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string MakernoteNonliveSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string XmpNonprotocolSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExtendedXmpSha256;
    public uint HeicPrimaryItemId;
    public uint HeicAuxItemId;
    public uint HeicAuxFromItemId;
    public uint HeicAuxToItemId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string HeicAuxItemSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string HeicAuxType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string VideoMdatSha256;
}

[Flags]
internal enum NativeExtractorFault
{
    None = 0,
    DiskFull = 1,
    WriteFail = 2,
    PublishFail = 3,
    ShortRead = 4,
    FlushDiskFull = 5,
    FlushWriteFail = 6,
    CleanupFail = 0x80
}

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
}

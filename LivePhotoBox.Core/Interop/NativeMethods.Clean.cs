using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_clean_source_protocol", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult CleanSourceProtocol(
        nint context,
        in NativeSourceMediaFacts facts,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
        string? outputVideoPath,
        NativeRemovedProtocolFact* outFacts,
        nuint factsCapacity,
        out nuint outFactsCount);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_clean_source_protocol_with_plan", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult CleanSourceProtocolWithPlan(
        nint context,
        in NativeSourceMediaFacts facts,
        NativeCleanupAction* actions,
        nuint actionCount,
        NativeCleanupArtifactBinding* targets,
        nuint targetCount,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
        string? outputVideoPath,
        NativeRemovedProtocolFact* outFacts,
        nuint factsCapacity,
        out nuint outFactsCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_clean_source_protocol_with_plan_and_cleanup_source", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult CleanSourceProtocolWithPlanAndCleanupSource(
        nint context,
        in NativeSourceMediaFacts facts,
        NativeCleanupAction* actions,
        nuint actionCount,
        NativeCleanupArtifactBinding* targets,
        nuint targetCount,
        string inputImagePath,
        string? inputVideoPath,
        string cleanupSourcePath,
        NativeCleanupArtifactBinding* cleanupSourceTarget,
        string outputImagePath,
        string? outputVideoPath,
        NativeRemovedProtocolFact* outFacts,
        nuint factsCapacity,
        out nuint outFactsCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_issue_cleanup_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult IssueCleanupPlan(
        nint context,
        nint extractionPlan,
        ulong generation,
        out nint outPlan,
        out ulong outGeneration);

    [LibraryImport(LibraryName, EntryPoint = "lpb_claim_cleanup_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult ClaimCleanupPlan(
        nint context,
        nint plan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_finish_cleanup_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult FinishCleanupPlan(
        nint context,
        nint plan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_release_cleanup_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult ReleaseCleanupPlan(
        nint context,
        nint plan);

    [LibraryImport(LibraryName, EntryPoint = "lpb_clean_source_protocol_with_cleanup_plan", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult CleanSourceProtocolWithCleanupPlan(
        nint context,
        nint plan,
        ulong generation,
        string inputImagePath,
        string? inputVideoPath,
        string? cleanupSourcePath,
        string outputImagePath,
        string? outputVideoPath,
        NativeRemovedProtocolFact* outFacts,
        nuint factsCapacity,
        out nuint outFactsCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_clean_get_staged_outputs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint CleanGetStagedOutputs(
        nint context,
        nint records,
        nuint capacity);
}

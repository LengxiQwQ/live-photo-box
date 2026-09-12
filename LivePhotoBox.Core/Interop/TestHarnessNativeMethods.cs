using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

/// <summary>
/// P/Invoke surface for the test-harness build of LivePhotoBox.Native
/// (LivePhotoBox.Native.TestHarness.dll).  Only used for cleanup-plan
/// operations on plans that were issued by the harness (test contexts);
/// production never references this library.
/// </summary>
internal static partial class TestHarnessNativeMethods
{
    internal const string LibraryName = "LivePhotoBox.Native.TestHarness";

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

    [LibraryImport(LibraryName, EntryPoint = "lpb_create_context")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CreateContext(nint options, out nint context);

    [LibraryImport(LibraryName, EntryPoint = "lpb_destroy_context")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyContext(nint context);

    [LibraryImport(LibraryName, EntryPoint = "lpb_get_last_error")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult GetLastError(
        nint context,
        nint utf8Buffer,
        nuint bufferSize,
        out nuint requiredSize);

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

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_cancel_callback")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TestSetCancelCallback(
        nint context,
        nint callback,
        nint userData);

    // Test-only publish race seam: when 1, the harness build swaps the owned
    // temp object for a foreign one at the same temp pathname immediately
    // before the FIRST publish whose artifact role equals targetArtifactRole.
    // Publishes for any other artifact role run normally and never consume the
    // seam, so a test can target exactly one sink (e.g. MotionVideo for the
    // MP4 sink) without an earlier PrimaryImage publish consuming it first.
    // Pass CleanerPublishFaultAnyRole (-1) for legacy any-role behavior.
    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_cleaner_publish_fault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult SetCleanerPublishFault(
        nint context,
        int swapTempSourceBeforePublish,
        int targetArtifactRole);

    // Harness-only proof of where the one-shot publish race seam actually
    // fired: returns the artifact role that consumed it and how many times it
    // fired (-1 / CleanerPublishFaultAnyRole when it never fired).  Tests
    // assert these values so a test whose name claims one sink cannot
    // silently exercise another.
    [LibraryImport(LibraryName, EntryPoint = "lpb_test_get_cleaner_publish_fault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult GetCleanerPublishFault(
        nint context,
        out int lastTriggeredArtifactRole,
        out int triggerCount);
}

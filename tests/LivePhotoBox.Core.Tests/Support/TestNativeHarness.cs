using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;

namespace LivePhotoBox.Core.Tests.Support;

internal static unsafe partial class TestNativeMethods
{
    internal const string LibraryName = "LivePhotoBox.Native.TestHarness";

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

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_extractor_fault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult SetExtractorFault(
        nint context,
        NativeExtractorFault fault,
        int targetArtifact,
        ulong triggerAfterBytes,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_set_cleaner_snapshot_hook")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult SetCleanerSnapshotHook(
        nint context,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_sha256_buffer")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult Sha256Buffer(
        byte* data,
        nuint length,
        byte* outHash);

    [LibraryImport(LibraryName, EntryPoint = "lpb_test_sha256_file", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult Sha256File(
        nint fileHandle,
        byte* outHash);

    [DllImport(LibraryName, EntryPoint = "lpb_test_get_context_id", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong GetContextId(nint context);

    [DllImport(LibraryName, EntryPoint = "lpb_test_get_plan_accounting", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult GetPlanAccounting(
        nint context,
        out NativePlanAccounting accounting);

    [DllImport(LibraryName, EntryPoint = "lpb_test_get_destroyed_plan_accounting", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult GetDestroyedPlanAccounting(
        ulong contextId,
        out NativePlanAccounting accounting);

    [DllImport(LibraryName, EntryPoint = "lpb_test_probe_destroyed_plan", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ProbeDestroyedPlan(ulong contextId, ulong planToken);

    [DllImport(LibraryName, EntryPoint = "lpb_test_extract_media_from_facts", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ExtractMediaFromFacts(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primaryPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondaryPath,
        in NativeSourceMediaFacts facts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputImagePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputVideoPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputGainmapPath);

    [DllImport(LibraryName, EntryPoint = "lpb_test_extract_media_from_facts_outputs", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ExtractMediaFromFactsWithOutputs(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primaryPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondaryPath,
        in NativeSourceMediaFacts facts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputImagePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputVideoPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputGainmapPath,
        NativeExtractionOutput* auxiliaryOutputs,
        nuint auxiliaryOutputCount);

    [DllImport(LibraryName, EntryPoint = "lpb_test_get_extraction_plan_record_address", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern nuint GetExtractionPlanRecordAddress(nint context, nint plan);

    [DllImport(LibraryName, EntryPoint = "lpb_inspect_media_with_plan", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult InspectMediaWithPlan(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primaryPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondaryPath,
        ref NativeSourceMediaFacts facts,
        out nint plan,
        NativeConfirmedResidue* residues,
        nuint residueCapacity,
        out nuint residueCount,
        out ulong generation);

    [DllImport(LibraryName, EntryPoint = "lpb_release_extraction_plan", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ReleaseExtractionPlan(nint context, nint plan);

    [DllImport("LivePhotoBox.Native", EntryPoint = "lpb_extract_media", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ExtractMediaLegacy(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primaryPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondaryPath,
        in NativeSourceMediaFacts facts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputImagePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputVideoPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputGainmapPath);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlanAccounting
{
    public ulong ContextId;
    public ulong Issued;
    public ulong Claimed;
    public ulong Consumed;
    public ulong Released;
    public ulong LivePlanCount;
    public ulong RegistryRecordCount;
    public ulong ReleasedOnContextDestroy;
    public uint ContextDestroyed;
    public uint Reserved;
}

internal static unsafe class TestNativeHarness
{
    internal static void SetExtractorFault(
        NativeContext context,
        NativeExtractorFault fault,
        int targetArtifact = 0,
        ulong triggerAfterBytes = 0,
        nint callback = 0,
        nint userData = 0)
    {
        using NativeContextLease lease = context.AcquireOperationLease(allowDisposeRequested: true);
        SetExtractorFault(lease.Handle, fault, targetArtifact, triggerAfterBytes, callback, userData);
    }

    internal static void SetExtractorFault(
        TestNativeContext context,
        NativeExtractorFault fault,
        int targetArtifact = 0,
        ulong triggerAfterBytes = 0,
        nint callback = 0,
        nint userData = 0) =>
        SetExtractorFault(context.Handle, fault, targetArtifact, triggerAfterBytes, callback, userData);

    internal static void ConfigureCleanerSnapshotHook(NativeContext context, nint callback)
    {
        using NativeContextLease lease = context.AcquireOperationLease(allowDisposeRequested: true);
        NativeResult result = TestNativeMethods.SetCleanerSnapshotHook(lease.Handle, callback, nint.Zero);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException($"Failed to configure Native cleaner snapshot hook: {result}");
        }
    }

    internal static NativeResult Sha256Buffer(byte* data, nuint length, byte* outHash) =>
        TestNativeMethods.Sha256Buffer(data, length, outHash);

    internal static NativeResult Sha256File(nint fileHandle, byte* outHash) =>
        TestNativeMethods.Sha256File(fileHandle, outHash);

    internal static ulong GetContextId(nint context) =>
        TestNativeMethods.GetContextId(context);

    internal static NativePlanAccounting GetLivePlanAccounting(nint context)
    {
        NativeResult result = TestNativeMethods.GetPlanAccounting(context, out NativePlanAccounting accounting);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException($"Failed to read live Native plan accounting: {result}");
        }
        return accounting;
    }

    internal static NativePlanAccounting GetDestroyedPlanAccounting(ulong contextId)
    {
        NativeResult result = TestNativeMethods.GetDestroyedPlanAccounting(contextId, out NativePlanAccounting accounting);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException($"Failed to read destroyed Native plan accounting: {result}");
        }
        return accounting;
    }

    internal static NativeResult ProbeDestroyedPlan(ulong contextId, ulong planToken) =>
        TestNativeMethods.ProbeDestroyedPlan(contextId, planToken);

    private static void SetExtractorFault(
        nint context,
        NativeExtractorFault fault,
        int targetArtifact,
        ulong triggerAfterBytes,
        nint callback,
        nint userData)
    {
        NativeResult result = TestNativeMethods.SetExtractorFault(
            context, fault, targetArtifact, triggerAfterBytes, callback, userData);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException($"Failed to configure Native test harness: {result}");
        }
    }
}

internal sealed unsafe class TestNativeContext : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private GCHandle _selfHandle;
    private nint _contextHandle;
    private int _disposed;

    private TestNativeContext(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _selfHandle = GCHandle.Alloc(this);

        delegate* unmanaged[Cdecl]<nint, int> cancelFunc = &CheckCancelledCallback;
        var options = new NativeContextOptions
        {
            StructSize = checked((uint)sizeof(NativeContextOptions)),
            AbiVersion = NativeRuntime.SupportedAbiVersion,
            CancelCallback = (nint)cancelFunc,
            UserData = GCHandle.ToIntPtr(_selfHandle)
        };

        NativeResult result = TestNativeMethods.CreateContext((nint)(&options), out _contextHandle);
        if (result != NativeResult.Ok || _contextHandle == nint.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException($"Failed to create Native test harness context: {result}");
        }
    }

    internal nint Handle => _contextHandle;

    internal static TestNativeContext Create(CancellationToken cancellationToken = default) =>
        new(cancellationToken);

    internal (nint Token, ulong Generation, nuint RecordAddress) InspectPlanToken(
        string primaryPath,
        string? secondaryPath)
    {
        NativeSourceMediaFacts facts = new()
        {
            StructSize = checked((uint)sizeof(NativeSourceMediaFacts)),
            PrimaryImage = new NativeImageItemFacts { StructSize = checked((uint)sizeof(NativeImageItemFacts)) },
            MotionVideo = new NativeVideoItemFacts { StructSize = checked((uint)sizeof(NativeVideoItemFacts)) },
            GainMap = new NativeGainMapItemFacts { StructSize = checked((uint)sizeof(NativeGainMapItemFacts)) },
            Timing = new NativeTimingFacts { StructSize = checked((uint)sizeof(NativeTimingFacts)) }
        };
        Span<NativeConfirmedResidue> residues = stackalloc NativeConfirmedResidue[64];
        fixed (NativeConfirmedResidue* pResidues = residues)
        {
            for (int i = 0; i < residues.Length; i++)
                pResidues[i].StructSize = checked((uint)sizeof(NativeConfirmedResidue));

            NativeResult result = TestNativeMethods.InspectMediaWithPlan(
                _contextHandle,
                primaryPath,
                secondaryPath,
                ref facts,
                out nint plan,
                pResidues,
                (nuint)residues.Length,
                out _,
                out ulong generation);
            if (result != NativeResult.Ok)
            {
                ThrowIfFailed(result);
            }

            nuint recordAddress = TestNativeMethods.GetExtractionPlanRecordAddress(_contextHandle, plan);
            return (plan, generation, recordAddress);
        }
    }

    internal void ReleasePlan(nint plan) =>
        _ = TestNativeMethods.ReleaseExtractionPlan(_contextHandle, plan);

    internal NativeResult ExtractMediaFromFacts(
        string primaryPath,
        string? secondaryPath,
        in NativeSourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath) =>
        TestNativeMethods.ExtractMediaFromFacts(
            _contextHandle,
            primaryPath,
            secondaryPath,
            in facts,
            outputImagePath,
            outputVideoPath,
            outputGainmapPath);

    internal NativeResult ExtractMediaFromFacts(
        string primaryPath,
        string? secondaryPath,
        in NativeSourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        IReadOnlyList<NativeMediaService.NativeAuxiliaryOutputBinding> auxiliaryOutputs)
    {
        ArgumentNullException.ThrowIfNull(auxiliaryOutputs);
        if (auxiliaryOutputs.Count == 0)
        {
            return ExtractMediaFromFacts(
                primaryPath,
                secondaryPath,
                in facts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath);
        }

        NativeExtractionOutput* nativeOutputs = stackalloc NativeExtractionOutput[auxiliaryOutputs.Count];
        var allocatedPaths = new List<nint>(auxiliaryOutputs.Count);
        try
        {
            for (int i = 0; i < auxiliaryOutputs.Count; i++)
            {
                NativeMediaService.NativeAuxiliaryOutputBinding binding = auxiliaryOutputs[i]
                    ?? throw new ArgumentException("Auxiliary extraction output binding cannot be null.", nameof(auxiliaryOutputs));
                if (string.IsNullOrWhiteSpace(binding.Path))
                {
                    throw new ArgumentException("Auxiliary extraction output path cannot be empty.", nameof(auxiliaryOutputs));
                }

                nativeOutputs[i] = new NativeExtractionOutput
                {
                    StructSize = checked((uint)sizeof(NativeExtractionOutput)),
                    AuxiliaryIndex = binding.AuxiliaryIndex,
                    OutputPath = Marshal.StringToCoTaskMemUTF8(binding.Path)
                };
                allocatedPaths.Add(nativeOutputs[i].OutputPath);
            }

            return TestNativeMethods.ExtractMediaFromFactsWithOutputs(
                _contextHandle,
                primaryPath,
                secondaryPath,
                in facts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath,
                nativeOutputs,
                (nuint)auxiliaryOutputs.Count);
        }
        finally
        {
            foreach (nint allocatedPath in allocatedPaths)
            {
                Marshal.FreeCoTaskMem(allocatedPath);
            }
        }
    }

    internal string? GetLastError()
    {
        if (_contextHandle == nint.Zero) return null;

        Span<byte> buffer = stackalloc byte[1024];
        fixed (byte* pBuffer = buffer)
        {
            NativeResult result = TestNativeMethods.GetLastError(
                _contextHandle,
                (nint)pBuffer,
                (nuint)buffer.Length,
                out nuint required);
            if (result == NativeResult.Ok && required > 0)
            {
                int length = 0;
                while (length < buffer.Length && buffer[length] != 0) length++;
                return Encoding.UTF8.GetString(buffer[..length]);
            }
        }
        return null;
    }

    internal void ThrowIfFailed(NativeResult result)
    {
        if (result == NativeResult.Ok) return;

        string message = GetLastError() ?? $"Native test harness failed with result {result}.";
        if (result == NativeResult.Cancelled || message.StartsWith("[Cancelled]", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException(message, _cancellationToken);
        }
        if (result == NativeResult.AuthorityViolation)
        {
            throw new ExtractionException(ExtractionFailureCategory.AuthorityViolation, message);
        }
        if (result == NativeResult.PlanReplayed)
        {
            throw new ExtractionException(ExtractionFailureCategory.PlanReplay, message);
        }
        if (result == NativeResult.SourceChanged || message.StartsWith("[SourceChanged]", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(ExtractionFailureCategory.SourceChanged, message);
        }

        if (message.StartsWith("[CleanupFailed]", StringComparison.OrdinalIgnoreCase))
        {
            ExtractionFailureCategory? originalCategory = null;
            Exception? originalException = null;
            if (message.Contains("Original failure: [Cancelled]", StringComparison.OrdinalIgnoreCase))
            {
                originalCategory = ExtractionFailureCategory.Cancelled;
                originalException = new OperationCanceledException(message, _cancellationToken);
            }
            else if (message.Contains("Original failure: [OutputWriteFailed]", StringComparison.OrdinalIgnoreCase))
            {
                originalCategory = ExtractionFailureCategory.OutputWriteFailed;
                originalException = new ExtractionException(originalCategory.Value, message);
            }
            else if (message.Contains("Original failure: [OutputPublishFailed]", StringComparison.OrdinalIgnoreCase))
            {
                originalCategory = ExtractionFailureCategory.OutputPublishFailed;
                originalException = new ExtractionException(originalCategory.Value, message);
            }
            else if (message.Contains("Original failure: [DiskFull]", StringComparison.OrdinalIgnoreCase))
            {
                originalCategory = ExtractionFailureCategory.DiskFull;
                originalException = new ExtractionException(originalCategory.Value, message);
            }
            else if (message.Contains("Original failure: [SourceRangeUnreadable]", StringComparison.OrdinalIgnoreCase))
            {
                originalCategory = ExtractionFailureCategory.SourceRangeUnreadable;
                originalException = new ExtractionException(originalCategory.Value, message);
            }

            throw new ExtractionException(
                ExtractionFailureCategory.CleanupFailed,
                message,
                innerException: originalException,
                originalCategory: originalCategory);
        }

        ExtractionFailureCategory category = message switch
        {
            _ when message.StartsWith("[InvalidFacts]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.InvalidFacts,
            _ when message.StartsWith("[UnsupportedLayout]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.UnsupportedLayout,
            _ when message.StartsWith("[InvalidAlias]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.InvalidAlias,
            _ when message.StartsWith("[SourceRangeUnreadable]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.SourceRangeUnreadable,
            _ when message.StartsWith("[DiskFull]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.DiskFull,
            _ when message.StartsWith("[OutputWriteFailed]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.OutputWriteFailed,
            _ when message.StartsWith("[OutputPublishFailed]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.OutputPublishFailed,
            _ when message.StartsWith("[CleanupFailed]", StringComparison.OrdinalIgnoreCase) => ExtractionFailureCategory.CleanupFailed,
            _ => result == NativeResult.InvalidArgument
                ? ExtractionFailureCategory.InvalidFacts
                : ExtractionFailureCategory.InternalError
        };
        throw new ExtractionException(category, message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CheckCancelledCallback(nint userData)
    {
        if (userData == nint.Zero) return 0;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            return handle.IsAllocated && handle.Target is TestNativeContext context &&
                context._cancellationToken.IsCancellationRequested ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        nint context = Interlocked.Exchange(ref _contextHandle, nint.Zero);
        if (context != nint.Zero)
        {
            TestNativeMethods.DestroyContext(context);
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}

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

    [DllImport(LibraryName, EntryPoint = "lpb_test_extract_media_from_facts", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern NativeResult ExtractMediaFromFacts(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primaryPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondaryPath,
        in NativeSourceMediaFacts facts,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputImagePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputVideoPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputGainmapPath);

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

    internal void SetExtractorFault(
        NativeExtractorFault fault,
        int targetArtifact = 0,
        ulong triggerAfterBytes = 0,
        nint callback = 0,
        nint userData = 0)
    {
        NativeResult result = TestNativeMethods.SetExtractorFault(
            _contextHandle, fault, targetArtifact, triggerAfterBytes, callback, userData);
        if (result != NativeResult.Ok)
        {
            throw new InvalidOperationException($"Failed to configure Native test harness: {result}");
        }
    }

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

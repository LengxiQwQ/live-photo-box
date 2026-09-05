using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeContextOptions
{
    public uint StructSize;
    public uint AbiVersion;
    public nint LogCallback;
    public nint CancelCallback;
    public nint UserData;
}

/// <summary>
/// Safe managed wrapper for native lpb_context lifecycle, cancellation callbacks, and error retrieval.
/// </summary>
internal sealed class NativeContext : IDisposable
{
    private readonly CancellationToken _cancellationToken;
    private GCHandle _selfHandle;
    private nint _contextHandle;
    private bool _disposed;

    private NativeContext(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _selfHandle = GCHandle.Alloc(this);

        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int> cancelFunc = &CheckCancelledCallback;
            var options = new NativeContextOptions
            {
                StructSize = (uint)sizeof(NativeContextOptions),
                AbiVersion = NativeRuntime.SupportedAbiVersion,
                LogCallback = nint.Zero,
                CancelCallback = (nint)cancelFunc,
                UserData = GCHandle.ToIntPtr(_selfHandle)
            };

            NativeResult res = NativeMethods.CreateContext((nint)(&options), out _contextHandle);
            if (res != NativeResult.Ok || _contextHandle == nint.Zero)
            {
                _selfHandle.Free();
                throw new InvalidOperationException($"Failed to create native context: {res}");
            }
        }
    }

    public nint Handle => _contextHandle;

    internal void SetExtractorFault(NativeExtractorFault fault, int targetArtifact = 0, ulong triggerAfterBytes = 0, nint callback = 0, nint userData = 0)
    {
        NativeMethods.TestSetExtractorFault(_contextHandle, fault, targetArtifact, triggerAfterBytes, callback, userData);
    }

    public static NativeContext Create(CancellationToken cancellationToken = default)
    {
        return new NativeContext(cancellationToken);
    }

    public string? GetLastError()
    {
        if (_contextHandle == nint.Zero) return null;
        Span<byte> buf = stackalloc byte[512];
        unsafe
        {
            fixed (byte* pBuf = buf)
            {
                NativeResult res = NativeMethods.GetLastError(_contextHandle, (nint)pBuf, (nuint)buf.Length, out nuint required);
                if (res == NativeResult.Ok && required > 0)
                {
                    int len = 0;
                    while (len < (int)required && buf[len] != 0) len++;
                    return Encoding.UTF8.GetString(buf[..len]);
                }
            }
        }
        return null;
    }

    public void ThrowIfFailed(NativeResult res)
    {
        if (res == NativeResult.Ok) return;

        string? msg = GetLastError();
        if (!string.IsNullOrWhiteSpace(msg))
        {
            if (msg.StartsWith("[CleanupFailed]", StringComparison.OrdinalIgnoreCase))
            {
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory? origCat = null;
                Exception? inner = null;
                if (msg.Contains("Original failure: [DiskFull]", StringComparison.OrdinalIgnoreCase))
                {
                    origCat = LivePhotoBox.Media.Extraction.ExtractionFailureCategory.DiskFull;
                    inner = new LivePhotoBox.Media.Extraction.ExtractionException(origCat.Value, "Disk full occurred prior to rollback failure.");
                }
                else if (msg.Contains("Original failure: [OutputWriteFailed]", StringComparison.OrdinalIgnoreCase))
                {
                    origCat = LivePhotoBox.Media.Extraction.ExtractionFailureCategory.OutputWriteFailed;
                    inner = new LivePhotoBox.Media.Extraction.ExtractionException(origCat.Value, "Output write failed prior to rollback failure.");
                }
                else if (msg.Contains("Original failure: [OutputPublishFailed]", StringComparison.OrdinalIgnoreCase))
                {
                    origCat = LivePhotoBox.Media.Extraction.ExtractionFailureCategory.OutputPublishFailed;
                    inner = new LivePhotoBox.Media.Extraction.ExtractionException(origCat.Value, "Output publish failed prior to rollback failure.");
                }
                else if (msg.Contains("Original failure: [SourceRangeUnreadable]", StringComparison.OrdinalIgnoreCase))
                {
                    origCat = LivePhotoBox.Media.Extraction.ExtractionFailureCategory.SourceRangeUnreadable;
                    inner = new LivePhotoBox.Media.Extraction.ExtractionException(origCat.Value, "Source range became unreadable prior to rollback failure.");
                }
                else if (msg.Contains("Original failure: [Cancelled]", StringComparison.OrdinalIgnoreCase))
                {
                    origCat = LivePhotoBox.Media.Extraction.ExtractionFailureCategory.Cancelled;
                    inner = new OperationCanceledException("Operation was cancelled prior to rollback failure.", _cancellationToken);
                }

                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.CleanupFailed,
                    msg,
                    innerException: inner,
                    originalCategory: origCat);
            }

            if (res == NativeResult.Cancelled || _cancellationToken.IsCancellationRequested || msg.StartsWith("[Cancelled]", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException(msg, _cancellationToken);
            }

            if (msg.StartsWith("[DiskFull]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.DiskFull, msg);
            if (msg.StartsWith("[SourceRangeUnreadable]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.SourceRangeUnreadable, msg);
            if (msg.StartsWith("[OutputWriteFailed]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.OutputWriteFailed, msg);
            if (msg.StartsWith("[OutputPublishFailed]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.OutputPublishFailed, msg);
            if (msg.StartsWith("[UnsupportedLayout]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.UnsupportedLayout, msg);
            if (msg.StartsWith("[InvalidFacts]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts, msg);
            if (msg.StartsWith("[InvalidAlias]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidAlias, msg);
            if (msg.StartsWith("[SourceChanged]", StringComparison.OrdinalIgnoreCase))
                throw new LivePhotoBox.Media.Extraction.ExtractionException(LivePhotoBox.Media.Extraction.ExtractionFailureCategory.SourceChanged, msg);
        }

        if (res == NativeResult.Cancelled || _cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(_cancellationToken);
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg)
            ? $"Native media operation failed with result: {res}"
            : $"Native media operation failed ({res}): {msg}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CheckCancelledCallback(nint userData)
    {
        if (userData == nint.Zero) return 0;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.IsAllocated && handle.Target is NativeContext ctx)
            {
                return ctx._cancellationToken.IsCancellationRequested ? 1 : 0;
            }
        }
        catch
        {
            return 0;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_contextHandle != nint.Zero)
        {
            NativeMethods.DestroyContext(_contextHandle);
            _contextHandle = nint.Zero;
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}

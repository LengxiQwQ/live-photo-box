using System;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Managed wrapper over the Native opaque cleanup-plan authority (P3).
///
/// A cleanup plan is the ONLY way production code may request a destructive
/// protocol mutation.  It is issued natively from a trusted Inspector-created
/// extraction plan (the P2 record owns the published artifact identities
/// captured while the transaction held the handles); Managed DTOs never carry
/// destructive authority.  Single-use semantics (claim / consume / release,
/// cross-context and stale-generation rejection, replay rejection, fail-closed
/// behavior) are enforced inside LivePhotoBox.Native on the plan record.
///
/// This wrapper borrows the owning NativeContext; it does not dispose it
/// (the caller keeps the extraction plan / context alive through cleaning).
/// </summary>
public sealed class CleanupPlan : IDisposable
{
    private readonly object _gate = new();
    private readonly NativeContext? _context;
    private readonly nint _contextHandle;
    private readonly bool _ownsContextLease;
    private readonly bool _useHarnessLibrary;
    private nint _nativeHandle;
    private readonly ulong _generation;
    private bool _disposed;

    internal CleanupPlan(
        NativeContext? context,
        nint contextHandle,
        nint nativeHandle,
        ulong generation,
        bool ownsContextLease,
        bool useHarnessLibrary)
    {
        _context = context;
        _contextHandle = contextHandle;
        if (nativeHandle != nint.Zero)
        {
            if (generation == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }
        }
        _nativeHandle = nativeHandle;
        _generation = generation;
        _ownsContextLease = ownsContextLease;
        _useHarnessLibrary = useHarnessLibrary;
    }

    /// <summary>The opaque token passed to Native; it is never dereferenced.</summary>
    internal nint NativeHandle
    {
        get
        {
            lock (_gate)
            {
                return _nativeHandle;
            }
        }
    }

    internal ulong Generation => _generation;

    /// <summary>True when the plan is bound to the test-harness Native build.</summary>
    internal bool UseHarnessLibrary => _useHarnessLibrary;

    internal bool IsFake => _nativeHandle == nint.Zero;

    /// <summary>
    /// Claims this plan (single-use) and returns an attempt lease that owns the
    /// Native claim plus a context operation lease until disposed.
    /// </summary>
    internal CleanupPlanAttempt BeginCleanupAttempt(CancellationToken cancellationToken = default)
    {
        NativeContext? context;
        NativeContextLease contextLease;
        nint handle;

        lock (_gate)
        {
            if (_disposed || _nativeHandle == nint.Zero)
            {
                throw new CleanerException(
                    CleanerFailureCategory.NativeAuthorityViolation,
                    CleanerFailureStage.Authorization,
                    SourceProtocol.Unknown,
                    "The Native cleanup plan has already been released, consumed, or was never issued.");
            }

            context = _context;
            handle = _nativeHandle;
            contextLease = _ownsContextLease
                ? context!.AcquireOperationLease()
                : new NativeContextLease(null!, _contextHandle);
        }

        try
        {
            NativeResult result = _useHarnessLibrary
                ? TestHarnessNativeMethods.ClaimCleanupPlan(contextLease.Handle, handle, _generation)
                : NativeMethods.ClaimCleanupPlan(contextLease.Handle, handle, _generation);
            if (result != NativeResult.Ok)
            {
                string? message = ReadLastError(contextLease.Handle, _useHarnessLibrary);
                throw new CleanerException(
                    result == NativeResult.PlanReplayed
                        ? CleanerFailureCategory.CleanupAuthorizationMissing
                        : CleanerFailureCategory.NativeAuthorityViolation,
                    CleanerFailureStage.Authorization,
                    SourceProtocol.Unknown,
                    message ?? $"Native cleanup-plan claim failed with {result}.");
            }

            return new CleanupPlanAttempt(this, contextLease, handle, _generation);
        }
        catch
        {
            contextLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Issues a cleanup plan from a trusted Inspector-created extraction plan.
    /// Must be called while the extraction record still owns the published
    /// artifact identities (i.e. before commit).
    /// </summary>
    internal static CleanupPlan IssueFrom(ExtractionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        NativeContext? context = plan.Context
            ?? throw new CleanerException(
                CleanerFailureCategory.NativeAuthorityViolation,
                CleanerFailureStage.Authorization,
                SourceProtocol.Unknown,
                "A Native cleanup plan can only be issued from a Native-backed extraction plan; fake plans cannot authorize cleaning.");

        using NativeContextLease lease = context.AcquireOperationLease();
        NativeResult result = NativeMethods.IssueCleanupPlan(
            lease.Handle,
            plan.NativeHandle,
            plan.Generation,
            out nint planHandle,
            out ulong planGeneration);
        if (result != NativeResult.Ok)
        {
            string? message = ReadLastError(lease.Handle);
            throw new CleanerException(
                result == NativeResult.PlanReplayed
                    ? CleanerFailureCategory.CleanupAuthorizationMissing
                    : CleanerFailureCategory.NativeAuthorityViolation,
                CleanerFailureStage.Authorization,
                SourceProtocol.Unknown,
                message ?? $"Native cleanup-plan issuance failed with {result}.");
        }

        return new CleanupPlan(context, context.Handle, planHandle, planGeneration,
            ownsContextLease: true, useHarnessLibrary: false);
    }

    /// <summary>
    /// Wraps a test-harness-issued cleanup plan bound to a harness context.
    /// Test-only: the harness build of Native is the only issuer that accepts
    /// caller-supplied facts, and the corresponding clean call is dispatched to
    /// the harness library by <see cref="UseHarnessLibrary"/>.
    /// </summary>
    internal static CleanupPlan CreateForTests(nint contextHandle, nint nativeHandle, ulong generation)
        => new(null, contextHandle, nativeHandle, generation,
            ownsContextLease: false, useHarnessLibrary: true);

    /// <summary>
    /// Test-only authority placeholder for orchestration fakes.  It carries no
    /// Native token and cannot enter any Native path: the attempt it produces
    /// is claim-less and the fake clean invoker ignores it.
    /// </summary>
    internal static CleanupPlan CreateFake()
        => new(null, nint.Zero, nint.Zero, 0,
            ownsContextLease: false, useHarnessLibrary: false);

    private static string? ReadLastError(nint contextHandle, bool useHarnessLibrary = false)
    {
        try
        {
            Span<byte> buf = stackalloc byte[512];
            unsafe
            {
                fixed (byte* pBuf = buf)
                {
                    nuint required = 0;
                    NativeResult res = useHarnessLibrary
                        ? TestHarnessNativeMethods.GetLastError(contextHandle, (nint)pBuf, (nuint)buf.Length, out required)
                        : NativeMethods.GetLastError(contextHandle, (nint)pBuf, (nuint)buf.Length, out required);
                    if (res == NativeResult.Ok && required > 0)
                    {
                        int len = 0;
                        while (len < (int)required && buf[len] != 0) len++;
                        return System.Text.Encoding.UTF8.GetString(buf[..len]);
                    }
                }
            }
        }
        catch
        {
            // Best effort.
        }
        return null;
    }

    public void Dispose()
    {
        NativeContext? context;
        nint handle;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            context = _context;
            handle = _nativeHandle;
            _nativeHandle = nint.Zero;
        }

        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            NativeContextLease lease = _ownsContextLease
                ? context!.AcquireOperationLease(allowDisposeRequested: true)
                : new NativeContextLease(null!, _contextHandle);
            using (lease)
            {
                NativeResult result = _useHarnessLibrary
                    ? TestHarnessNativeMethods.ReleaseCleanupPlan(lease.Handle, handle)
                    : NativeMethods.ReleaseCleanupPlan(lease.Handle, handle);
                _ = result;
            }
        }
        catch (ObjectDisposedException)
        {
            // The borrowed context may already be in deferred destruction;
            // Native destroys any remaining plan tombstones fail-closed.
        }
    }
}

/// <summary>
/// A single claimed use of a Native cleanup plan.  Disposing the attempt
/// finishes (consumes) the native claim and releases the context lease.
/// </summary>
internal sealed class CleanupPlanAttempt : IDisposable
{
    private readonly CleanupPlan _plan;
    private NativeContextLease _contextLease;
    private readonly nint _nativeHandle;
    private readonly ulong _generation;
    private int _disposed;

    internal CleanupPlanAttempt(
        CleanupPlan plan,
        NativeContextLease contextLease,
        nint nativeHandle,
        ulong generation)
    {
        _plan = plan;
        _contextLease = contextLease;
        _nativeHandle = nativeHandle;
        _generation = generation;
    }

    internal NativeContextLease ContextLease => _contextLease;
    internal nint NativeHandle => _nativeHandle;
    internal ulong Generation => _generation;
    internal bool UseHarnessLibrary => _plan.UseHarnessLibrary;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            NativeResult result = UseHarnessLibrary
                ? TestHarnessNativeMethods.FinishCleanupPlan(_contextLease.Handle, _nativeHandle, _generation)
                : NativeMethods.FinishCleanupPlan(_contextLease.Handle, _nativeHandle, _generation);
            _ = result;
        }
        catch
        {
            // Finish is idempotent cleanup and must not mask the operation
            // result or revive a plan.
        }
        finally
        {
            _contextLease.Dispose();
        }
    }
}

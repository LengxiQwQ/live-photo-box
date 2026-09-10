using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Opaque authority returned by the source Inspector for one extraction.
/// The native handle is an unpredictable registry token, never an address of
/// the authoritative record. Diagnostic facts are a detached deep snapshot.
/// </summary>
public sealed class ExtractionPlan : IDisposable
{
    private readonly object _gate = new();
    private readonly NativeContext? _context;
    private readonly ulong _generation;
    private readonly SourceMediaFacts _facts;
    private nint _nativeHandle;
    private bool _disposed;

    internal ExtractionPlan(NativeContext context, nint nativeHandle, ulong generation, SourceMediaFacts facts)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (nativeHandle == nint.Zero) throw new ArgumentException("An opaque Native extraction plan token is required.", nameof(nativeHandle));
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        _nativeHandle = nativeHandle;
        _generation = generation;
        _facts = CloneFacts(facts ?? throw new ArgumentNullException(nameof(facts)));
    }

    private ExtractionPlan(SourceMediaFacts facts)
    {
        _facts = CloneFacts(facts ?? throw new ArgumentNullException(nameof(facts)));
    }

    /// <summary>Detached facts for diagnostics and managed orchestration only.</summary>
    internal SourceMediaFacts Facts => _facts;

    internal NativeContext? Context => _context;

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

    /// <summary>
    /// Claims this plan before cancellation, path/range validation, workspace
    /// allocation, or hashing. The returned attempt owns both the Native
    /// claim and a context operation lease until it is disposed.
    /// </summary>
    internal ExtractionPlanAttempt BeginExtractionAttempt(CancellationToken cancellationToken = default)
    {
        NativeContext context;
        NativeContextLease contextLease;
        nint handle;

        lock (_gate)
        {
            if (_disposed || _context is null || _nativeHandle == nint.Zero)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.PlanReplay,
                    "The extraction plan has already been released or consumed.");
            }

            context = _context;
            handle = _nativeHandle;
            contextLease = context.AcquireOperationLease();
        }

        try
        {
            NativeResult result = NativeMethods.ClaimExtractionPlan(contextLease.Handle, handle, _generation);
            if (result != NativeResult.Ok)
            {
                context.ThrowIfFailed(result);
            }

            context.BindOperationCancellation(cancellationToken);
            return new ExtractionPlanAttempt(this, context, contextLease, handle, CloneFacts(_facts));
        }
        catch
        {
            contextLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Test-only plan used by orchestration fakes. It has no Native authority
    /// and therefore cannot enter the production extraction path.
    /// </summary>
    internal static ExtractionPlan CreateForTests(SourceMediaFacts facts) => new(facts);

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

        if (context is null || handle == nint.Zero)
        {
            return;
        }

        try
        {
            using NativeContextLease lease = context.AcquireOperationLease(allowDisposeRequested: true);
            _ = NativeMethods.ReleaseExtractionPlan(lease.Handle, handle);
        }
        catch (ObjectDisposedException)
        {
            // The context may already be in deferred destruction; cleanup is
            // idempotent and no managed result should be masked.
        }
        finally
        {
            context.Dispose();
        }
    }

    internal static SourceMediaFacts CloneFacts(SourceMediaFacts source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            PrimaryImage = source.PrimaryImage with { },
            MotionVideo = source.MotionVideo is null ? null : source.MotionVideo with { },
            GainMap = source.GainMap is null ? null : source.GainMap with { },
            AuxiliaryItems = new List<AuxiliaryMediaFacts>((source.AuxiliaryItems ?? []).Select(item => item with
            {
                Dependencies = new List<HeifDependencyFacts>(
                    (item.Dependencies ?? []).Select(dependency => dependency with { }))
            })),
            Timing = source.Timing with { },
            ConfirmedResidues = new List<ConfirmedProtocolResidue>(source.ConfirmedResidues ?? []),
            PreservationCarriers = new List<PreservationCarrier>((source.PreservationCarriers ?? []).Select(carrier => carrier with { }))
        };
    }
}

internal sealed class ExtractionPlanAttempt : IDisposable
{
    private readonly ExtractionPlan _plan;
    private readonly NativeContext _context;
    private readonly NativeContextLease _contextLease;
    private readonly nint _nativeHandle;
    private int _disposed;

    internal ExtractionPlanAttempt(
        ExtractionPlan plan,
        NativeContext context,
        NativeContextLease contextLease,
        nint nativeHandle,
        SourceMediaFacts facts)
    {
        _plan = plan;
        _context = context;
        _contextLease = contextLease;
        _nativeHandle = nativeHandle;
        Facts = facts;
    }

    internal NativeContext Context => _context;
    internal NativeContextLease ContextLease => _contextLease;
    internal nint NativeHandle => _nativeHandle;
    internal ulong Generation => _plan.Generation;
    internal SourceMediaFacts Facts { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // Native extraction normally leaves a consumed tombstone. This
            // call also consumes attempts that failed in managed preflight or
            // cancellation before Native extraction was entered.
            _ = NativeMethods.FinishExtractionPlan(
                _contextLease.Handle,
                _nativeHandle,
                Generation);
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

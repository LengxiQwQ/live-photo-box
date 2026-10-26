using System;
using System.IO;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

// Expectations constrain actual freshly observed bytes. They are not observed facts
// and do not confer Writer or Cleaner mutation authority.
internal sealed record TargetValidationExpectations(
    uint TargetProfile, uint MediaFormat, uint VideoCodec, long? CoverTimestampUs)
{
    internal uint? ImageWidth { get; init; }
    internal uint? ImageHeight { get; init; }
    internal uint? ImageOrientation { get; init; }
    internal uint? VideoWidth { get; init; }
    internal uint? VideoHeight { get; init; }
    internal uint? VideoOrientation { get; init; }
}

internal sealed class TargetValidationException(string message) : IOException(message);

internal sealed record TargetValidationReport(NativeResult InvocationResult,
    NativeTargetValidationResultV1 Observed, string Sha256)
{
    internal bool CanPublish => InvocationResult == NativeResult.Ok &&
        Observed.StructuralStatus == 1 && Observed.GenericMediaStatus == 1 &&
        Observed.PreservationStatus == 0 && Observed.DeviceStatus == 0;
}

internal static class NativeTargetValidator
{
    // This hook observes the actual reopened window; it cannot provide a verdict.
    internal static AsyncLocal<Action<WindowsSplitOutputPublisher.TargetPublicationStage, FileStream>?> ReaderOpenedForTests { get; } = new();
    internal static AsyncLocal<Action?> ReadCallbackForTests { get; } = new();
    internal static TargetValidationReport ValidateStage(
        WindowsSplitOutputPublisher.TargetPublicationStage stage,
        TargetValidationExpectations expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(expected);
        using FileStream reader = stage.OpenRead();
        ReaderOpenedForTests.Value?.Invoke(stage, reader);
        TargetValidationReport report = Observe(reader, expected, token);
        RequirePass(report, expected);
        if (report.Observed.ObservedByteLength != (ulong)stage.ByteLength ||
            !string.Equals(report.Sha256, stage.Sha256, StringComparison.Ordinal))
            throw new TargetValidationException("Fresh target observations do not match the sealed publication bytes.");
        return report;
    }

    // Read-only entry also permits independently constructed artifacts to exercise
    // the exact observer. Publication always uses ValidateStage's guarded reopen.
    internal static unsafe TargetValidationReport Observe(FileStream reader,
        TargetValidationExpectations expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        using var context = NativeContext.Create(token);
        using var operation = context.AcquireOperationLease();
        var state = new ReaderState(reader.SafeFileHandle, token);
        GCHandle user = default;
        bool retained = false;
        try
        {
            reader.SafeFileHandle.DangerousAddRef(ref retained);
            user = GCHandle.Alloc(state);
            var source = new NativeTargetReaderV1
            {
                StructSize = (uint)sizeof(NativeTargetReaderV1), Version = 1,
                ByteLength = checked((ulong)RandomAccess.GetLength(reader.SafeFileHandle)),
                User = GCHandle.ToIntPtr(user),
                ReadAt = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte*, uint, uint*, NativeResult>)&ReadAt
            };
            var request = new NativeTargetValidationRequestV1
            {
                StructSize = (uint)sizeof(NativeTargetValidationRequestV1), Version = 1,
                TargetProfile = expected.TargetProfile, TargetVersion = 1,
                MediaFormat = expected.MediaFormat, MemberCount = 1,
                ExpectedVideoCodec = expected.VideoCodec,
                CoverTimestampPresent = expected.CoverTimestampUs.HasValue ? 1u : 0,
                CoverTimestampUs = expected.CoverTimestampUs ?? 0
            };
            var observed = new NativeTargetValidationResultV1
            { StructSize = (uint)sizeof(NativeTargetValidationResultV1), Version = 1 };
            NativeResult result = NativeMethods.TargetValidate(operation.Handle, &request, &source, &observed);
            token.ThrowIfCancellationRequested();
            if (state.Failure is OperationCanceledException cancellation) throw cancellation;
            if (state.Failure is not null)
                throw new IOException("Reading the fresh target artifact failed.", state.Failure);
            if (result == NativeResult.Cancelled)
                throw new OperationCanceledException("Native target validation was cancelled.", token);
            return new TargetValidationReport(result, observed, observed.GetSha256());
        }
        finally
        {
            if (user.IsAllocated) user.Free();
            if (retained) reader.SafeFileHandle.DangerousRelease();
            GC.KeepAlive(reader);
        }
    }

    internal static void RequirePass(TargetValidationReport report, TargetValidationExpectations expected)
    {
        var facts = report.Observed;
        if (!report.CanPublish || facts.ObservedTargetProfile != expected.TargetProfile ||
            facts.ObservedTargetVersion != 1 || facts.ObservedMediaFormat != expected.MediaFormat ||
            facts.ObservedVideoCodec != expected.VideoCodec || facts.ObservedAuxiliaryCount != 0 ||
            (expected.CoverTimestampUs is long cover &&
                (facts.CoverEncoding != 1 || facts.ObservedCoverTimestampUs != cover)) ||
            (!expected.CoverTimestampUs.HasValue && facts.CoverEncoding != 2) ||
            !Matches(expected.ImageWidth, facts.ImageWidth) || !Matches(expected.ImageHeight, facts.ImageHeight) ||
            !Matches(expected.ImageOrientation, facts.ImageOrientation) ||
            !Matches(expected.VideoWidth, facts.VideoWidth) || !Matches(expected.VideoHeight, facts.VideoHeight) ||
            !Matches(expected.VideoOrientation, facts.VideoOrientation))
            throw new TargetValidationException($"Target validation rejected publication: invocation={report.InvocationResult}, " +
                $"structure={facts.StructuralStatus}, media={facts.GenericMediaStatus}, " +
                $"category={facts.FailureCategory}, stage={facts.FailureStage}, rule={facts.RuleId}.");
    }

    private static bool Matches(uint? expected, uint actual) => !expected.HasValue || expected.Value == actual;

    private sealed class ReaderState(SafeFileHandle handle, CancellationToken token)
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal CancellationToken Token { get; } = token;
        internal Exception? Failure { get; set; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe NativeResult ReadAt(nint user, ulong offset, byte* destination,
        uint count, uint* bytesRead)
    {
        if (bytesRead == null) return NativeResult.InvalidArgument;
        *bytesRead = 0;
        ReaderState? state = null;
        try
        {
            if (user == 0 || (count != 0 && destination == null) || count > int.MaxValue || offset > long.MaxValue)
                return NativeResult.InvalidArgument;
            state = (ReaderState)GCHandle.FromIntPtr(user).Target!;
            state.Token.ThrowIfCancellationRequested();
            ReadCallbackForTests.Value?.Invoke();
            var target = new Span<byte>(destination, checked((int)count));
            int total = 0;
            while (total < target.Length)
            {
                state.Token.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(state.Handle, target[total..], checked((long)offset + total));
                if (read == 0) throw new EndOfStreamException("Fresh target reader returned fewer bytes than requested.");
                total += read;
            }
            *bytesRead = count;
            return NativeResult.Ok;
        }
        catch (Exception exception)
        {
            if (state is not null) state.Failure ??= exception;
            return exception is OperationCanceledException ? NativeResult.Cancelled : NativeResult.InternalError;
        }
    }
}

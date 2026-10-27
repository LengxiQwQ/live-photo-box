using System;
using System.IO;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Linq;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

// Expectations constrain actual freshly observed bytes. They are not observed facts
// and do not confer Writer or Cleaner mutation authority.
internal sealed record TargetValidationExpectations(
    uint TargetProfile, uint MediaFormat, uint VideoCodec, long? CoverTimestampUs)
{
    internal bool UseV2 { get; init; }
    internal uint TargetVersion { get; init; } = 1;
    internal NativeCompoundPrimaryObservation? Compound { get; init; }
    internal long? PrimaryTimestampUs { get; init; }
    internal IReadOnlyList<TargetAuxiliaryExpectation> Auxiliaries { get; init; } = [];
    internal uint? ImageWidth { get; init; }
    internal uint? ImageHeight { get; init; }
    internal uint? ImageOrientation { get; init; }
    internal uint? VideoWidth { get; init; }
    internal uint? VideoHeight { get; init; }
    internal uint? VideoOrientation { get; init; }
    internal int? ImageRotationDegrees { get; init; }
    internal int? VideoRotationDegrees { get; init; }
    internal NeutralReflection? ImageReflection { get; init; }
    internal NeutralReflection? VideoReflection { get; init; }
}

internal sealed record TargetAuxiliaryExpectation(uint Semantic, uint Container, ulong ByteLength, string Sha256);

internal sealed class TargetValidationException(string message) : IOException(message);

internal sealed record TargetValidationReport(NativeResult InvocationResult,
    NativeTargetValidationResultV1 Observed, string Sha256)
{
    internal NativeTargetValidationResultV2? ObservedV2 { get; init; }
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
                TargetProfile = expected.TargetProfile, TargetVersion = expected.TargetVersion,
                MediaFormat = expected.MediaFormat, MemberCount = 1,
                ExpectedVideoCodec = expected.VideoCodec,
                CoverTimestampPresent = expected.CoverTimestampUs.HasValue ? 1u : 0,
                CoverTimestampUs = expected.CoverTimestampUs ?? 0
            };
            var observed = new NativeTargetValidationResultV1
            { StructSize = (uint)sizeof(NativeTargetValidationResultV1), Version = 1 };
            NativeTargetValidationResultV2? actualV2 = null;
            NativeResult result;
            if (expected.UseV2)
            {
                if (expected.Auxiliaries.Count > 2) throw new InvalidDataException("The R3 request admits at most two unique supported auxiliary assets.");
                NativeTargetAuxiliaryExpectationV2* auxiliaries = stackalloc NativeTargetAuxiliaryExpectationV2[2];
                for (int i = 0; i < expected.Auxiliaries.Count; i++)
                {
                    var row = expected.Auxiliaries[i];
                    byte[] digest = Convert.FromHexString(row.Sha256);
                    if (digest.Length != 32 || row.ByteLength == 0 || row.Semantic is not (1 or 2 or 3 or 4 or 5))
                        throw new InvalidDataException("Target auxiliary expectation is incomplete.");
                    auxiliaries[i] = new NativeTargetAuxiliaryExpectationV2
                    {
                        StructSize = (uint)sizeof(NativeTargetAuxiliaryExpectationV2), Version = 2,
                        Semantic = row.Semantic, Container = row.Container, ByteLength = row.ByteLength
                    };
                    for (int j = 0; j < 32; j++) auxiliaries[i].Sha256[j] = digest[j];
                }
                request.ExpectedAuxiliaryCount = (uint)expected.Auxiliaries.Count;
                var requestV2 = new NativeTargetValidationRequestV2
                {
                    StructSize = (uint)sizeof(NativeTargetValidationRequestV2), Version = 2, Base = request,
                    PrimaryTimestampPresent = expected.PrimaryTimestampUs.HasValue ? 1u : 0,
                    PrimaryTimestampUs = expected.PrimaryTimestampUs ?? 0,
                    AuxiliaryCount = (uint)expected.Auxiliaries.Count,
                    Auxiliaries = expected.Auxiliaries.Count == 0 ? 0 : (nint)auxiliaries
                };
                var observedV2 = new NativeTargetValidationResultV2
                { StructSize = (uint)sizeof(NativeTargetValidationResultV2), Version = 2 };
                if (expected.Compound is { } compound)
                {
                    var requestV3 = new NativeTargetValidationRequestV3
                    { StructSize = (uint)sizeof(NativeTargetValidationRequestV3), Version = 3, Base = requestV2, Compound = compound };
                    result = NativeMethods.TargetValidateV3(operation.Handle, &requestV3, &source, &observedV2);
                }
                else result = NativeMethods.TargetValidateV2(operation.Handle, &requestV2, &source, &observedV2);
                observed = observedV2.Base;
                actualV2 = observedV2;
            }
            else
            {
                if (expected.PrimaryTimestampUs.HasValue || expected.Auxiliaries.Count != 0)
                    throw new InvalidDataException("Version one cannot drop version two semantic expectations.");
                result = NativeMethods.TargetValidate(operation.Handle, &request, &source, &observed);
            }
            token.ThrowIfCancellationRequested();
            if (state.Failure is OperationCanceledException cancellation) throw cancellation;
            if (state.Failure is not null)
                throw new IOException("Reading the fresh target artifact failed.", state.Failure);
            if (result == NativeResult.Cancelled)
                throw new OperationCanceledException("Native target validation was cancelled.", token);
            return new TargetValidationReport(result, observed, observed.GetSha256()) { ObservedV2 = actualV2 };
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
            facts.ObservedTargetVersion != expected.TargetVersion || facts.ObservedMediaFormat != expected.MediaFormat ||
            facts.ObservedVideoCodec != expected.VideoCodec || facts.ObservedAuxiliaryCount != expected.Auxiliaries.Count ||
            (expected.CoverTimestampUs is long cover &&
                (facts.CoverEncoding != 1 || facts.ObservedCoverTimestampUs != cover)) ||
            (!expected.CoverTimestampUs.HasValue && (expected.UseV2 ? facts.CoverEncoding is not (2 or 3) : facts.CoverEncoding != 2)) ||
            !Matches(expected.ImageWidth, facts.ImageWidth) || !Matches(expected.ImageHeight, facts.ImageHeight) ||
            !Matches(expected.ImageOrientation, facts.ImageOrientation) ||
            !MatchesOrientation(expected.ImageRotationDegrees, expected.ImageReflection, facts.ImageOrientation) ||
            !Matches(expected.VideoWidth, facts.VideoWidth) || !Matches(expected.VideoHeight, facts.VideoHeight) ||
            !Matches(expected.VideoOrientation, facts.VideoOrientation) ||
            !MatchesOrientation(expected.VideoRotationDegrees, expected.VideoReflection, facts.VideoOrientation))
            throw new TargetValidationException($"Target validation rejected publication: invocation={report.InvocationResult}, " +
                $"structure={facts.StructuralStatus}, media={facts.GenericMediaStatus}, " +
                $"category={facts.FailureCategory}, stage={facts.FailureStage}, rule={facts.RuleId}.");
        if (expected.UseV2)
        {
            if (report.ObservedV2 is not { } actual || actual.StructSize != Marshal.SizeOf<NativeTargetValidationResultV2>() ||
                actual.Version != 2 || actual.AuxiliaryCount != expected.Auxiliaries.Count ||
                (expected.PrimaryTimestampUs.HasValue
                    ? actual.PrimaryTimestampEncoding != 1 || actual.ObservedPrimaryTimestampUs != expected.PrimaryTimestampUs.Value
                    : actual.PrimaryTimestampEncoding is not (0 or 2 or 3)))
                throw new TargetValidationException("Fresh version two target timing or auxiliary observations differ from expectations.");
            NativeTargetAuxiliaryObservedV2[] rows = [actual.Auxiliary0, actual.Auxiliary1];
            if (expected.Auxiliaries.Select(row => row.Semantic).Distinct().Count() != expected.Auxiliaries.Count)
                throw new TargetValidationException("Duplicate target auxiliary semantic expectations.");
            for (int i = 0; i < expected.Auxiliaries.Count; i++)
            {
                TargetAuxiliaryExpectation row = expected.Auxiliaries[i];
                var matches = rows.Take((int)actual.AuxiliaryCount).Where(value => value.Semantic == row.Semantic).ToArray();
                if (matches.Length != 1 || matches[0].Container != row.Container || matches[0].ByteLength != row.ByteLength ||
                    !string.Equals(matches[0].GetSha256(), row.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TargetValidationException("Fresh target auxiliary bytes differ from their Neutral semantic asset.");
            }
        }
        else if (report.ObservedV2.HasValue)
            throw new TargetValidationException("Unexpected version two observation for a version one request.");
    }

    private static bool Matches(uint? expected, uint actual) => !expected.HasValue || expected.Value == actual;

    private static bool MatchesOrientation(int? rotation, NeutralReflection? reflection, uint actual)
    {
        if (!rotation.HasValue && !reflection.HasValue) return true;
        // Same canonical rotation + horizontal-reflection decomposition as
        // preservation_observation.cpp normalize_exif_orientation; matrices in
        // inspect_isobmff_reader use this identical EXIF 1..8 ordering.
        (int Rotation, NeutralReflection Reflection)? observed = actual switch
        {
            1 => (0, NeutralReflection.None), 2 => (0, NeutralReflection.Horizontal),
            3 => (180, NeutralReflection.None), 4 => (180, NeutralReflection.Horizontal),
            5 => (90, NeutralReflection.Horizontal), 6 => (90, NeutralReflection.None),
            7 => (270, NeutralReflection.Horizontal), 8 => (270, NeutralReflection.None),
            _ => null
        };
        if (observed is not { } value || reflection == NeutralReflection.Unknown ||
            (rotation.HasValue && rotation.Value is not (0 or 90 or 180 or 270))) return false;
        // Vertical reflection is the equivalent horizontal reflection plus a
        // half turn. Unknown channels stay unconstrained; no facts are created.
        int? canonicalRotation = rotation.HasValue
            ? (rotation.Value + (reflection == NeutralReflection.Vertical ? 180 : 0)) % 360 : null;
        NeutralReflection? canonicalReflection = reflection == NeutralReflection.Vertical
            ? NeutralReflection.Horizontal : reflection;
        return (!canonicalRotation.HasValue || value.Rotation == canonicalRotation.Value) &&
            (!canonicalReflection.HasValue || value.Reflection == canonicalReflection.Value);
    }

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

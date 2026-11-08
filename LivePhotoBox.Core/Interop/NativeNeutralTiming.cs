using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeNeutralTimingObservationV1
{
    internal uint StructSize;
    internal uint PresenceFlags;
    internal int SourceEvidence;
    internal int FinalApplicability;
    internal long CoverTimestampUs;
    internal long PrimaryTimestampUs;
    internal int CoverFrameIndex;
    internal int TotalFrames;
    internal long FinalDurationUs;
    internal fixed byte PrimarySha256[32];
    internal fixed byte SecondarySha256[32];
    internal fixed byte FinalVideoSha256[32];
}

/// <summary>Maps a Native, identity-bound final timeline observation; never parses media bytes.</summary>
internal static class NativeNeutralTiming
{
    internal sealed record TimingAttempt(NeutralTimingSemantics? Timing, Exception? Failure)
    {
        public static implicit operator TimingAttempt(NeutralTimingSemantics timing) => new(timing, null);
    }
    internal static Task<TimingAttempt> TryObserveAsync(string primary, string? secondary, MediaArtifact video,
        SourceMediaFacts expected, CancellationToken token = default) =>
        Task.Run(() => TryObserve(primary, secondary, video, expected, token), CancellationToken.None);
    internal static Task<TimingAttempt> TryObserveCompositionAsync(string primary, string selectedVideo, MediaArtifact video,
        SourceMediaFacts expected, CancellationToken token) =>
        Task.Run(() => TryObserve(primary, selectedVideo, video, expected, token, true), CancellationToken.None);

    internal static async Task<NeutralTimingSemantics> ObserveAsync(string primaryPath, string? secondaryPath,
        MediaArtifact finalVideo, SourceMediaFacts expectedSource, CancellationToken cancellationToken = default)
    {
        var attempt = await TryObserveAsync(primaryPath, secondaryPath, finalVideo, expectedSource, cancellationToken).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Timing!;
    }
    internal static async Task<NeutralTimingSemantics> ObserveCompositionAsync(string primary, string selectedVideo,
        MediaArtifact finalVideo, SourceMediaFacts expected, CancellationToken token)
    {
        var attempt = await TryObserveCompositionAsync(primary, selectedVideo, finalVideo, expected, token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Timing!;
    }

    private static unsafe TimingAttempt TryObserve(string primaryPath, string? secondaryPath,
        MediaArtifact finalVideo, SourceMediaFacts expectedSource, CancellationToken cancellationToken, bool composition = false)
    {
        if (cancellationToken.IsCancellationRequested) return new(null, new OperationCanceledException(cancellationToken));
        if (Marshal.SizeOf<NativeNeutralTimingObservationV1>() != 144 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.CoverTimestampUs)).ToInt32() != 16 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.FinalDurationUs)).ToInt32() != 40 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.PrimarySha256)).ToInt32() != 48)
            throw new InvalidOperationException("Neutral timing observation POD does not match Native ABI v12.");
        using var context = NativeContext.Create(cancellationToken);
        var observation = new NativeNeutralTimingObservationV1 { StructSize = 144 };
        NativeResult result = composition
            ? ObserveCompositionNative(context.Handle, primaryPath, secondaryPath, finalVideo.Path, ref observation)
            : ObserveNative(context.Handle, primaryPath, secondaryPath, finalVideo.Path, ref observation);
        if (result != NativeResult.Ok) return new(null, context.CreateFailure(result));
        if (cancellationToken.IsCancellationRequested) return new(null, new OperationCanceledException(cancellationToken));

        if (observation.StructSize != 144 || (observation.PresenceFlags & ~63u) != 0 ||
            observation.SourceEvidence is < 0 or > 3 || observation.FinalApplicability is < 0 or > 3)
            return new(null, new InvalidDataException("Native neutral timing observation is incompatible or malformed."));
        if (HashFailure(observation.PrimarySha256, expectedSource.PrimarySha256, "source primary") is { } primaryHashFailure) return new(null, primaryHashFailure);
        if (HashFailure(observation.FinalVideoSha256, finalVideo.Sha256, "final motion video") is { } finalHashFailure) return new(null, finalHashFailure);
        bool hasSecondary = (observation.PresenceFlags & 32) != 0;
        if (hasSecondary != !string.IsNullOrEmpty(secondaryPath))
            return new(null, new InvalidDataException("Native timing source companion identity is inconsistent."));
        if (hasSecondary && HashFailure(observation.SecondarySha256, expectedSource.SecondarySha256, "source companion") is { } companionHashFailure) return new(null, companionHashFailure);

        long? duration = (observation.PresenceFlags & 16) != 0 ? observation.FinalDurationUs : null;
        int? frames = (observation.PresenceFlags & 8) != 0 ? observation.TotalFrames : null;
        if (duration is <= 0 || frames is <= 0)
            return new(null, new InvalidDataException("Final timeline duration or frame count is invalid."));
        bool hasCover = (observation.PresenceFlags & 1) != 0;
        bool hasPrimary = (observation.PresenceFlags & 2) != 0;
        bool hasCoverFrame = (observation.PresenceFlags & 4) != 0;
        bool applicable = observation.FinalApplicability is 1 or 2;
        if ((hasCover || hasPrimary || hasCoverFrame) &&
            (!applicable || observation.SourceEvidence is not (1 or 2) || !duration.HasValue || !frames.HasValue))
            return new(null, new InvalidDataException("Native timing values have no applicable final-timeline evidence."));
        if ((hasCover && (observation.CoverTimestampUs < 0 || observation.CoverTimestampUs >= duration)) ||
            (hasPrimary && (observation.PrimaryTimestampUs < 0 || observation.PrimaryTimestampUs >= duration)) ||
            (hasCoverFrame && (!hasCover || observation.CoverFrameIndex < 0 || observation.CoverFrameIndex >= frames)))
            return new(null, new InvalidDataException("Native timing position is outside the final video timeline."));
        if (applicable && (!hasCover || observation.SourceEvidence is not (1 or 2)))
            return new(null, new InvalidDataException("Native timing applicability has no verified source cover fact."));
        if (observation.FinalApplicability == 3 && observation.SourceEvidence != 3)
            return new(null, new InvalidDataException("NotApplicable timing requires an explicitly absent source cover."));

        NeutralEvidenceState evidence = observation.FinalApplicability switch
        {
            1 => observation.SourceEvidence == 2 ? NeutralEvidenceState.Verified : NeutralEvidenceState.VerifiedPreserved,
            2 => NeutralEvidenceState.VerifiedConverted,
            3 => NeutralEvidenceState.NotApplicable,
            _ => NeutralEvidenceState.Unknown
        };
        byte* finalHash = stackalloc byte[32];
        var firstResult = ObserveFirstNative(context.Handle, finalVideo.Path, out long first, finalHash);
        if (firstResult != NativeResult.Ok) return new(null, context.CreateFailure(firstResult));
        if (cancellationToken.IsCancellationRequested) return new(null, new OperationCanceledException(cancellationToken));
        if (HashFailure(finalHash, finalVideo.Sha256, "first-frame final video") is { } firstHashFailure) return new(null, firstHashFailure);
        if (first < 0 || duration is not long end || first >= end)
            return new(null, new InvalidDataException("Final first-frame timestamp is outside its timeline."));
        return new NeutralTimingSemantics
        {
            FirstValidFrameTimestampUs = first,
            CoverTimestampUs = hasCover ? observation.CoverTimestampUs : null,
            PrimaryTimestampUs = hasPrimary ? observation.PrimaryTimestampUs : null,
            CoverFrameIndex = hasCoverFrame ? observation.CoverFrameIndex : null,
            TotalFrames = frames,
            DurationUs = duration,
            EvidenceState = evidence
        };
    }

    private static unsafe Exception? HashFailure(byte* observed, string? expected, string role)
    {
        string actual = Convert.ToHexString(new ReadOnlySpan<byte>(observed, 32));
        if (expected is not { Length: 64 } ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return new InvalidDataException("Timing observation does not identify the inspected " + role + ".");
        return null;
    }

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_observe_final_first_frame_v1", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static unsafe extern NativeResult ObserveFirstNative(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string video, out long timestampUs, byte* sha256);

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_observe_composition_timing_v1", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern NativeResult ObserveCompositionNative(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string video, ref NativeNeutralTimingObservationV1 observation);

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_observe_neutral_timing_v1", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern NativeResult ObserveNative(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string video,
        ref NativeNeutralTimingObservationV1 observation);
}

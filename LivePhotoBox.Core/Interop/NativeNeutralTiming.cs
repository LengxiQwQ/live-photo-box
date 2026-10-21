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
    internal static Task<NeutralTimingSemantics> ObserveAsync(string primaryPath, string? secondaryPath,
        MediaArtifact finalVideo, SourceMediaFacts expectedSource, CancellationToken cancellationToken = default) =>
        Task.Run(() => Observe(primaryPath, secondaryPath, finalVideo, expectedSource, cancellationToken), cancellationToken);

    private static unsafe NeutralTimingSemantics Observe(string primaryPath, string? secondaryPath,
        MediaArtifact finalVideo, SourceMediaFacts expectedSource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Marshal.SizeOf<NativeNeutralTimingObservationV1>() != 144 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.CoverTimestampUs)).ToInt32() != 16 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.FinalDurationUs)).ToInt32() != 40 ||
            Marshal.OffsetOf<NativeNeutralTimingObservationV1>(nameof(NativeNeutralTimingObservationV1.PrimarySha256)).ToInt32() != 48)
            throw new InvalidOperationException("Neutral timing observation POD does not match Native ABI v12.");
        using var context = NativeContext.Create(cancellationToken);
        var observation = new NativeNeutralTimingObservationV1 { StructSize = 144 };
        context.ThrowIfFailed(ObserveNative(context.Handle, primaryPath, secondaryPath, finalVideo.Path, ref observation));
        cancellationToken.ThrowIfCancellationRequested();

        if (observation.StructSize != 144 || (observation.PresenceFlags & ~63u) != 0 ||
            observation.SourceEvidence is < 0 or > 3 || observation.FinalApplicability is < 0 or > 3)
            throw new InvalidDataException("Native neutral timing observation is incompatible or malformed.");
        RequireHash(observation.PrimarySha256, expectedSource.PrimarySha256, "source primary");
        RequireHash(observation.FinalVideoSha256, finalVideo.Sha256, "final motion video");
        bool hasSecondary = (observation.PresenceFlags & 32) != 0;
        if (hasSecondary != !string.IsNullOrEmpty(secondaryPath))
            throw new InvalidDataException("Native timing source companion identity is inconsistent.");
        if (hasSecondary) RequireHash(observation.SecondarySha256, expectedSource.SecondarySha256, "source companion");

        long? duration = (observation.PresenceFlags & 16) != 0 ? observation.FinalDurationUs : null;
        int? frames = (observation.PresenceFlags & 8) != 0 ? observation.TotalFrames : null;
        if (duration is <= 0 || frames is <= 0)
            throw new InvalidDataException("Final timeline duration or frame count is invalid.");
        bool hasCover = (observation.PresenceFlags & 1) != 0;
        bool hasPrimary = (observation.PresenceFlags & 2) != 0;
        bool hasCoverFrame = (observation.PresenceFlags & 4) != 0;
        bool applicable = observation.FinalApplicability is 1 or 2;
        if ((hasCover || hasPrimary || hasCoverFrame) &&
            (!applicable || observation.SourceEvidence is not (1 or 2) || !duration.HasValue || !frames.HasValue))
            throw new InvalidDataException("Native timing values have no applicable final-timeline evidence.");
        if ((hasCover && (observation.CoverTimestampUs < 0 || observation.CoverTimestampUs >= duration)) ||
            (hasPrimary && (observation.PrimaryTimestampUs < 0 || observation.PrimaryTimestampUs >= duration)) ||
            (hasCoverFrame && (!hasCover || observation.CoverFrameIndex < 0 || observation.CoverFrameIndex >= frames)))
            throw new InvalidDataException("Native timing position is outside the final video timeline.");
        if (applicable && (!hasCover || observation.SourceEvidence is not (1 or 2)))
            throw new InvalidDataException("Native timing applicability has no verified source cover fact.");
        if (observation.FinalApplicability == 3 && observation.SourceEvidence != 3)
            throw new InvalidDataException("NotApplicable timing requires an explicitly absent source cover.");

        NeutralEvidenceState evidence = observation.FinalApplicability switch
        {
            1 => observation.SourceEvidence == 2 ? NeutralEvidenceState.Verified : NeutralEvidenceState.VerifiedPreserved,
            2 => NeutralEvidenceState.VerifiedConverted,
            3 => NeutralEvidenceState.NotApplicable,
            _ => NeutralEvidenceState.Unknown
        };
        return new NeutralTimingSemantics
        {
            CoverTimestampUs = hasCover ? observation.CoverTimestampUs : null,
            PrimaryTimestampUs = hasPrimary ? observation.PrimaryTimestampUs : null,
            CoverFrameIndex = hasCoverFrame ? observation.CoverFrameIndex : null,
            TotalFrames = frames,
            DurationUs = duration,
            EvidenceState = evidence
        };
    }

    private static unsafe void RequireHash(byte* observed, string? expected, string role)
    {
        string actual = Convert.ToHexString(new ReadOnlySpan<byte>(observed, 32));
        if (expected is not { Length: 64 } ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Timing observation does not identify the inspected " + role + ".");
    }

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_observe_neutral_timing_v1", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern NativeResult ObserveNative(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string video,
        ref NativeNeutralTimingObservationV1 observation);
}

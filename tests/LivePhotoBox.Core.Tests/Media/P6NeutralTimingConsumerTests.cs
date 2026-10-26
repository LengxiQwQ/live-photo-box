using System;
using System.IO;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P6NeutralTimingConsumerTests
{
    [Theory]
    [InlineData(NeutralEvidenceState.Verified, 0L)]
    [InlineData(NeutralEvidenceState.VerifiedPreserved, 1_234_567L)]
    [InlineData(NeutralEvidenceState.VerifiedConverted, 2_345_678L)]
    public void P6_ResolveNeutralCoverTimestamp_UsesLegalVerifiedCover(
        NeutralEvidenceState evidenceState,
        long coverTimestampUs)
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = evidenceState,
            CoverTimestampUs = coverTimestampUs,
            DurationUs = 3_000_000
        };

        Assert.Equal(
            coverTimestampUs,
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs: null));
    }

    [Fact]
    public void P6_ResolveNeutralCoverTimestamp_UnknownWithoutExplicitRequestRejectsSourceTimestamp()
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = NeutralEvidenceState.Unknown,
            CoverTimestampUs = 0,
            DurationUs = 1_000_000
        };

        Assert.Throws<InvalidDataException>(() =>
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs: null));
    }

    [Fact]
    public void P6_ResolveNeutralCoverTimestamp_NotApplicableWithoutSourceCoverUsesOpeningFrame()
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = NeutralEvidenceState.NotApplicable,
            DurationUs = 1_000_000
        };

        Assert.Equal(
            0,
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void P6_ResolveNeutralCoverTimestamp_NotApplicableRequiresVerifiedPositiveDuration(long? duration)
    {
        var timing = new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.NotApplicable, DurationUs = duration };
        Assert.Throws<InvalidDataException>(() => LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, null));
    }

    [Fact]
    public void P6_ResolveNeutralCoverTimestamp_UsesLegalExplicitRequestWithUnknownSourceTiming()
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = NeutralEvidenceState.Unknown,
            DurationUs = 1_000_000
        };

        Assert.Equal(
            250_000,
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs: 250_000));
    }

    [Theory]
    [InlineData(-1L, 1_000_000L)]
    [InlineData(1_000_000L, 1_000_000L)]
    [InlineData(0L, null)]
    public void P6_ResolveNeutralCoverTimestamp_InvalidExplicitRequestRejects(
        long requestedTimestampUs,
        long? durationUs)
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = NeutralEvidenceState.Unknown,
            DurationUs = durationUs
        };

        Assert.Throws<InvalidDataException>(() =>
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs));
    }

    [Theory]
    [InlineData(-1L, 1_000_000L)]
    [InlineData(1_000_000L, 1_000_000L)]
    [InlineData(0L, null)]
    public void P6_ResolveNeutralCoverTimestamp_InvalidVerifiedCoverRejects(
        long coverTimestampUs,
        long? durationUs)
    {
        NeutralTimingSemantics timing = new()
        {
            EvidenceState = NeutralEvidenceState.Verified,
            CoverTimestampUs = coverTimestampUs,
            DurationUs = durationUs
        };

        Assert.Throws<InvalidDataException>(() =>
            LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(timing, requestedTimestampUs: null));
    }

    [Fact]
    public void P6_Runner_ConsumesNeutralTimingSemanticsInsteadOfOriginalBundleTiming()
    {
        string runnerPath = Path.Combine(
            FindRepositoryRoot(),
            "LivePhotoBox.Core",
            "Services",
            "LivePhotoMergeRunnerService.cs");
        string source = File.ReadAllText(runnerPath);
        const string methodStart =
            "private static async Task<ProductOperationOutcome> ProcessSinglePairRebuiltAsync(";
        const string methodEnd = "internal static long ResolveNeutralCoverTimestamp(";
        int start = source.IndexOf(methodStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "The rebuilt merge consumer method was not found.");
        int end = source.IndexOf(methodEnd, start, StringComparison.Ordinal);
        Assert.True(end > start, "The rebuilt merge consumer method boundary was not found.");
        string consumer = source[start..end];

        Assert.Contains("bundle.Semantics.Timing", consumer, StringComparison.Ordinal);
        Assert.Contains("options.KeyPhotoTimestampUs", consumer, StringComparison.Ordinal);
        Assert.Contains("ResolveNeutralCoverTimestamp(", consumer, StringComparison.Ordinal);
        Assert.Contains("presentationTimestampUs,", consumer, StringComparison.Ordinal);
        Assert.DoesNotContain("bundle.Timing", consumer, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string runnerPath = Path.Combine(
                current.FullName,
                "LivePhotoBox.Core",
                "Services",
                "LivePhotoMergeRunnerService.cs");
            if (File.Exists(runnerPath))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Live Photo Box repository root.");
    }
}

using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class DualFileTargetTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(200000L)]
    public async Task MinimumApplePairPublishesValidMovieAndCleansItsOnlyCid(long cover)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(MinimumApplePairPublishesValidMovieAndCleansItsOnlyCid));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, cover: cover, mov: true);
        string image = Path.Combine(folder.Path, "target.jpg");
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.AppleLivePhoto, OutputMediaFormat.JpegMov), image,
            folder.Path, false, CancellationToken.None);
        Assert.NotNull(receipt.VideoPath);
        var facts = await new SourceInspector().InspectAsync(image, receipt.VideoPath);
        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);
        Assert.Equal(cover, facts.Timing.CoverTimestampUs);
        var videoFacts = await new SourceInspector().InspectAsync(receipt.VideoPath!);
        Assert.Equal(facts.PairingIdentifier, videoFacts.PairingIdentifier);
        Assert.Equal(cover, videoFacts.Timing.CoverTimestampUs);
        using var workspace = new MediaWorkspace();
        var neutral = await new NeutralMediaService().CreateNeutralBundleAsync(image, receipt.VideoPath, workspace,
            SplitMediaRequirements.Get(SplitMediaFormat.KeepSource), PreservationPolicy.BestEffort);
        Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(neutral.PrimaryImage.Path)).Protocol);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.AppleLivePhoto, OutputMediaFormat.JpegMov)]
    [InlineData(TargetProtocol.AppleLivePhoto, OutputMediaFormat.HeicMov)]
    [InlineData(TargetProtocol.VivoLegacyDualFile, OutputMediaFormat.JpegMp4)]
    public async Task CompletePairIsPublishedAndCanReturnToNeutral(TargetProtocol target, OutputMediaFormat format)
    {
        string source = TestSampleResolver.ResolveSample("红米老款-GV1.JPG");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        using var folder = new P9TargetTestInputs.Folder(nameof(CompletePairIsPublishedAndCanReturnToNeutral) + format + target);
        var profile = OutputProfile.Create(target, format);
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source, "", "pair", 1,
            new LivePhotoMergeRunOptions { OutputProfile = profile, OutputDirectory = folder.Path }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        Assert.Equal(2, outcome.CommittedOutputs.Count);
        string image = Assert.Single(outcome.CommittedOutputs, x => x.Role == "PrimaryImage").Path;
        string video = Assert.Single(outcome.CommittedOutputs, x => x.Role == "MotionVideo").Path;
        var facts = await new SourceInspector().InspectAsync(image, video);
        Assert.Equal(target == TargetProtocol.AppleLivePhoto ? SourceProtocol.AppleLivePhoto : SourceProtocol.VivoLegacyDualFile, facts.Protocol);
        Assert.False(string.IsNullOrWhiteSpace(facts.PairingIdentifier));
        if (target == TargetProtocol.AppleLivePhoto)
        {
            // CoCreateGuid emits version 4; raw Windows memory hex swaps this nibble.
            Assert.True(Guid.TryParseExact(facts.PairingIdentifier, "D", out _));
            Assert.Equal('4', facts.PairingIdentifier![14]);
            Assert.Contains(char.ToLowerInvariant(facts.PairingIdentifier[19]), "89ab");
        }

        using var workspace = new MediaWorkspace();
        var neutral = await new NeutralMediaService().CreateNeutralBundleAsync(image, video, workspace,
            SplitMediaRequirements.Get(SplitMediaFormat.KeepSource), PreservationPolicy.BestEffort);
        Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(neutral.PrimaryImage.Path)).Protocol);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
    }

    [Fact]
    public void MissingNativePlanCannotForgePairValidation()
    {
        using var context = NativeContext.Create(CancellationToken.None);
        using var operation = context.AcquireOperationLease();
        Assert.Throws<TargetValidationException>(() => NativeTargetPairProof.Observe(operation.Handle, 0));
    }

    [Fact]
    public async Task SecondMemberRealCollisionRollsBackFirstAndKeepsForeignFile()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(SecondMemberRealCollisionRollsBackFirstAndKeepsForeignFile));
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        string image = Path.Combine(folder.Path, "target.jpg"), video = Path.Combine(folder.Path, "target.MP4");
        var previous = WindowsSplitOutputPublisher.TestHook.Value;
        bool triggered = false;
        WindowsSplitOutputPublisher.TestHook.Value = (trace, _) =>
        {
            if (!triggered && trace.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication &&
                string.Equals(trace.FinalPath, video, StringComparison.OrdinalIgnoreCase))
            { File.WriteAllText(video, "foreign companion"); triggered = true; }
            return Task.CompletedTask;
        };
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle,
                OutputProfile.Create(TargetProtocol.VivoLegacyDualFile, OutputMediaFormat.JpegMp4), image,
                folder.Path, false, CancellationToken.None));
            Assert.True(triggered);
            Assert.False(File.Exists(image));
            Assert.Equal("foreign companion", File.ReadAllText(video));
            Assert.Empty(Directory.GetFiles(folder.Path, "*.stage"));
        }
        finally { WindowsSplitOutputPublisher.TestHook.Value = previous; }
    }

    [Fact]
    public async Task LockedCompanionDoesNotChangeEitherExistingTarget()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(LockedCompanionDoesNotChangeEitherExistingTarget));
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        string image = Path.Combine(folder.Path, "target.jpg"), video = Path.Combine(folder.Path, "target.MP4");
        File.WriteAllText(image, "existing image"); File.WriteAllText(video, "existing video");
        using var locked = File.Open(video, FileMode.Open, FileAccess.Read, FileShare.None);
        var failure = await Record.ExceptionAsync(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.VivoLegacyDualFile, OutputMediaFormat.JpegMp4), image,
            folder.Path, true, CancellationToken.None));
        var lockedFailure = Assert.IsType<System.ComponentModel.Win32Exception>(failure);
        Assert.Contains(lockedFailure.NativeErrorCode, new[] { 32, 33 });
        Assert.Equal("existing image", File.ReadAllText(image));
        locked.Dispose();
        Assert.Equal("existing video", File.ReadAllText(video));
    }
}

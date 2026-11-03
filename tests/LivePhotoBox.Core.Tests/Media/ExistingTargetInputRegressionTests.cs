using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExistingTargetInputRegressionTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("三星.jpg")]
    [InlineData("三星.heic")]
    [InlineData("一加-改了封面照片.jpg")]
    public async Task PreparedAuxiliaryGraphCanReachCrossTargetWithoutChangingSource(string name)
    {
        string source = TestSampleResolver.ResolveSample(name);
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace, profile.MediaRequirement, PreservationPolicy.BestEffort);
        string? evidence = Environment.GetEnvironmentVariable("LPB_STEP3_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(new { bundle.AuxiliaryMedia, bundle.GainMap, bundle.Manifest }, new JsonSerializerOptions { WriteIndented = true }));
        }
        await StructuredTargetWriterService.WriteAsync(bundle, profile, Path.Combine(workspace.RootDirectory, "target.jpg"), workspace.RootDirectory, false, CancellationToken.None);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
    }
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("红米老款-GV1.JPG")]
    [InlineData("三星.jpg")]
    [InlineData("三星.heic")]
    [InlineData("华为-Mate80.jpg")]
    public async Task VivoTargetKeepsBoundPrimaryPayloadsAfterReflow(string name)
    {
        string source = TestSampleResolver.ResolveSample(name);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.VivoLivePhoto, OutputMediaFormat.JpegMp4);
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source, "", "target", 1,
            new LivePhotoMergeRunOptions { OutputProfile = profile, OutputDirectory = workspace.RootDirectory }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        Assert.Single(outcome.CommittedOutputs);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV")]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV")]
    public async Task HuaweiPreparesPcmAudioWithoutLosingValidatedAdditionalTracks(string image, string video)
    {
        using var workspace = new MediaWorkspace();
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(TestSampleResolver.ResolveSample(image), TestSampleResolver.ResolveSample(video), "target", 1,
            new LivePhotoMergeRunOptions { OutputProfile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4), OutputDirectory = workspace.RootDirectory }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        Assert.Single(outcome.CommittedOutputs);
        Assert.NotNull(outcome.PreparationExecution);
        Assert.Contains(outcome.PreparationExecution.Videos, v => v.AudioTranscodedToAac);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.SamsungMotionPhoto)]
    [InlineData(TargetProtocol.HuaweiMovingPhoto)]
    public async Task DetachedEditHistoryDoesNotBlockBestEffortMainMedia(TargetProtocol target)
    {
        string source = TestSampleResolver.ResolveSample("一加-改了封面照片.jpg");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        using var workspace = new MediaWorkspace();
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source, "", "target", 1,
            new LivePhotoMergeRunOptions { OutputProfile = OutputProfile.Create(target, OutputMediaFormat.JpegMp4), OutputDirectory = workspace.RootDirectory }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        Assert.Single(outcome.CommittedOutputs);
        Assert.Contains(outcome.NotCarried, item => item.Item == "Original");
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(1u, OutputMediaFormat.HeicMp4)]
    [InlineData(2u, OutputMediaFormat.HeicMp4)]
    [InlineData(1u, OutputMediaFormat.HeicMp4Hevc)]
    [InlineData(2u, OutputMediaFormat.HeicMp4Hevc)]
    public async Task HuaweiHeicEnvelopeRoundTripsThroughInspectorExtractorAndCleaner(uint variant, OutputMediaFormat format)
    {
        using var workspace = new Support.TestFixtureWorkspace();
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(TestSampleResolver.ResolveSample("华为Mate80.heic"), "", "target", 1,
            new LivePhotoMergeRunOptions
            {
                OutputProfile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, format), OutputDirectory = workspace.RootDirectory,
                StructuredOptions = new() { HuaweiVariant = (HuaweiTargetVariant)variant }
            }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        string output = Assert.Single(outcome.CommittedOutputs).Path;
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
        var facts = await new SourceInspector().InspectAsync(output);
        Assert.Equal(variant == 1 ? SourceProtocol.HuaweiMovingPhoto : SourceProtocol.HonorMovingPhoto, facts.Protocol);
        var split = await LivePhotoSplitService.SplitAsync(output, Path.Combine(workspace.RootDirectory, "split"), SplitMediaFormat.KeepSource, CancellationToken.None);
        Assert.True(split.Outcome!.IsSuccess, split.Outcome.Details);
        Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(split.ImageOutputPath)).Protocol);
        Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(split.VideoOutputPath)).Protocol);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))));
    }

}

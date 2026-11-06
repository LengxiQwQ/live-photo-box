using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class HuaweiRepairTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CancelAfterMovieStaging_CleansOwnedIntermediatesAndRetainsSource()
    {
        string source = TestSampleResolver.ResolveSample("华为Mate80.heic");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        string root;
        using (var workspace = new MediaWorkspace())
        using (var cancellation = new CancellationTokenSource())
        {
            root = workspace.RootDirectory;
            string output = workspace.AllocateFilePath("huawei", ".heic");
            Task<HuaweiRepairObservation> repair = NativeHuaweiRepair.RunAsync(source, output, 8, cancellation.Token);
            string movie = Path.Combine(root, "huawei-source.mp4");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(movie) && !repair.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repair.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(File.Exists(movie), "Cancellation must exercise a registered intermediate, not just pre-start cancellation.");
            Assert.False(File.Exists(output));
        }
        Assert.False(Directory.Exists(root));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task IndependentPreviewRepairRetainsUnknownMetadataAndEntireHuaweiMovie(bool selectVideo)
    {
        using var workspace = new TestFixtureWorkspace();
        string sample = TestSampleResolver.ResolveSample("华为Mate80.heic");
        var facts = await NativeMediaService.InspectMediaAsync(sample, null, default);
        byte[] real = File.ReadAllBytes(sample);
        byte[] jpeg = P9TargetTestInputs.Image;
        byte[] opaque = System.Text.Encoding.UTF8.GetBytes("Unfamiliar vendor note: retain these exact bytes.");
        static byte[] Segment(byte marker, byte[] value) => new byte[] {255, marker,
            (byte)((value.Length + 2) >> 8), (byte)(value.Length + 2)}.Concat(value).ToArray();
        byte[] exif = {69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,2,0,0,0,0,0,0,0};
        byte[] input = jpeg[..2].Concat(Segment(0xef, opaque)).Concat(Segment(0xe1, exif)).Concat(Segment(0xe2, jpeg))
            .Concat(jpeg[2..]).Concat(real.AsSpan((int)facts.MotionVideo!.ByteOffset).ToArray()).ToArray();
        string path = Path.Combine(workspace.RootDirectory, "huawei-preview.jpg");
        File.WriteAllBytes(path, input);
        var result = await LivePhotoRepairService.RepairImageFileAsync(path, Path.Combine(workspace.RootDirectory, "out"),
            true, false, new RepairOptions { FixVideoRotation = selectVideo }, default);
        Assert.Equal("success", result.Status);
        Assert.NotNull(result.Image);
        Assert.DoesNotContain("_isolated", result.Image!);
        byte[] output = File.ReadAllBytes(result.Image!);
        Assert.True(output.AsSpan().IndexOf(opaque) >= 0);
        var after = await NativeMediaService.InspectMediaAsync(result.Image!, null, default);
        if (!selectVideo) Assert.Equal(SHA256.HashData(real.AsSpan((int)facts.MotionVideo.ByteOffset)),
            SHA256.HashData(output.AsSpan((int)after.MotionVideo!.ByteOffset)));
        else Assert.Equal(0u, (await NativeHuaweiRepair.RunAsync(result.Image!, null, 8, default)).Changed);
        var checkedImage = await NativeHuaweiRepair.RunAsync(result.Image!, null, 3, default);
        Assert.Equal(1u, checkedImage.ImageOrientation);
        Assert.Equal(0u, checkedImage.Changed);
        Assert.Equal(input, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("华为Mate80.heic")]
    [InlineData("华为-Mate80.jpg")]
    [Trait("Category", "RealSamples")]
    public async Task HuaweiRecognizedOutsideDefaultScope_OriginalAndEmbeddedMovieRetained(string name)
    {
        using var workspace = new TestFixtureWorkspace();
        string path = Path.Combine(workspace.RootDirectory, name);
        File.Copy(TestSampleResolver.ResolveSample(name), path);
        byte[] original = File.ReadAllBytes(path);
        var skipped = await LivePhotoRepairService.AnalyzeFileAsync(path, default, defaultDevicesOnly: true);
        Assert.Equal(RepairIssueType.NonApple, skipped.IssueType);
        var analysis = await LivePhotoRepairService.AnalyzeFileAsync(path);
        Assert.Equal(RepairIssueType.NeedsRebuild, analysis.IssueType);
        Assert.Equal(90, analysis.VideoRotationAngle);
        Assert.DoesNotContain("codestream cannot be verified", analysis.IssueDescription);
        string output = Path.Combine(workspace.RootDirectory, "out");
        var defaultResult = await LivePhotoRepairService.RepairImageFileAsync(path, output, false, false, new(), default);
        Assert.Equal("skipped", defaultResult.Status);
        Assert.False(Directory.Exists(output));
        var limited = await LivePhotoRepairService.RepairImageFileAsync(path, output, true, false, new(), default);
        Assert.Equal("success", limited.Status);
        Assert.NotNull(limited.Image);
        var fixedVideo = await NativeHuaweiRepair.RunAsync(limited.Image!, null, 15, default);
        Assert.True(fixedVideo.Supported, fixedVideo.Details);
        Assert.Equal(0u, fixedVideo.Changed);
        Assert.Equal(0u, fixedVideo.Pending);
        var repeat = await LivePhotoRepairService.RepairImageFileAsync(limited.Image!, output, true, false, new(), default);
        Assert.Equal("healthy", repeat.Status);
        // Photo-only selection is explicit: no video repair or HDR conversion.
        var result = await LivePhotoRepairService.RepairImageFileAsync(path, output, true, false,
            new RepairOptions { FixVideoRotation = false }, default, copyPerfect: true);
        Assert.Equal("copied", result.Status);
        Assert.NotNull(result.Image);
        var before = await NativeMediaService.InspectMediaAsync(path, null, default);
        var after = await NativeMediaService.InspectMediaAsync(result.Image!, null, default);
        Assert.Equal(before.Protocol, after.Protocol);
        Assert.Equal(before.Timing.CoverTimestampUs, after.Timing.CoverTimestampUs);
        Assert.Equal(before.MotionVideo!.ByteLength, after.MotionVideo!.ByteLength);
        byte[] repaired = File.ReadAllBytes(result.Image!);
        Assert.Equal(SHA256.HashData(original.AsSpan((int)before.MotionVideo.ByteOffset)),
            SHA256.HashData(repaired.AsSpan((int)after.MotionVideo.ByteOffset)));
        Assert.Equal(original, File.ReadAllBytes(path));
        var again = await NativeHuaweiRepair.RunAsync(result.Image!, null, 6, default);
        Assert.True(again.Supported, again.Details);
        Assert.Equal(0u, again.Changed);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task DamagedHuaweiTrailerIsAnItemResult_NotAnUnhandledException()
    {
        using var workspace = new TestFixtureWorkspace();
        string path = Path.Combine(workspace.RootDirectory, "broken.heic");
        byte[] bytes = File.ReadAllBytes(TestSampleResolver.ResolveSample("华为Mate80.heic"));
        bytes[^15] = (byte)'X';
        File.WriteAllBytes(path, bytes);
        var analysis = await LivePhotoRepairService.AnalyzeFileAsync(path);
        Assert.Equal(RepairIssueType.Error, analysis.IssueType);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}

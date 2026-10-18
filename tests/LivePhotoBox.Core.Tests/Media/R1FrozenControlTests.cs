using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

/// <summary>Unmodified controls for every frozen source, with real pair semantics.</summary>
public sealed class R1FrozenControlTests
{
    [Theory]
    [Trait("P7", "R1-RealSample-Control")]
    [Trait("Category", "RealSamples")]
    [InlineData("oppo.jpg", null, SourceProtocol.OppoLivePhoto)]
    [InlineData("vivo.jpg", null, SourceProtocol.VivoLivePhoto)]
    [InlineData("一加.jpg", null, SourceProtocol.OppoLivePhoto)]
    [InlineData("一加-改了封面照片.jpg", null, SourceProtocol.OppoLivePhoto)]
    [InlineData("三星.jpg", null, SourceProtocol.SamsungMotionPhotoJpeg)]
    [InlineData("三星.heic", null, SourceProtocol.SamsungMotionPhotoHeic)]
    [InlineData("华为-Mate80.jpg", null, SourceProtocol.HuaweiMovingPhoto)]
    [InlineData("华为Mate80.heic", null, SourceProtocol.HuaweiMovingPhoto)]
    [InlineData("荣耀.jpg", null, SourceProtocol.HonorMovingPhoto)]
    [InlineData("小米.jpg", null, SourceProtocol.GoogleMotionPhotoV2)]
    [InlineData("红米老款-GV1.JPG", null, SourceProtocol.GoogleMicroVideoV1)]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV", SourceProtocol.AppleLivePhoto)]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV", SourceProtocol.AppleLivePhoto)]
    [InlineData("vivo双文件.jpg", "vivo双文件.mp4", SourceProtocol.VivoLegacyDualFile)]
    public async Task Inspect_FrozenOriginalSource_WithActualPairing_PreservesIdentity(
        string primaryName, string? secondaryName, SourceProtocol expectedProtocol)
    {
        string primary = TestSampleResolver.ResolveSample(primaryName);
        string? secondary = secondaryName is null ? null : TestSampleResolver.ResolveSample(secondaryName);
        string primaryBefore = await HashAsync(primary);
        string? secondaryBefore = secondary is null ? null : await HashAsync(secondary);
        using var inspected = await new SourceInspector().InspectWithPlanAsync(primary, secondary);
        SourceMediaFacts facts = inspected.Facts;
        Assert.Equal(expectedProtocol, facts.Protocol);
        Assert.True(facts.PrimaryImage.IsPresent);
        Assert.InRange(facts.PrimaryImage.ByteLength, 1, new FileInfo(primary).Length);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.MotionVideo.IsPresent);
        Assert.InRange(facts.MotionVideo.ByteLength, 1, new FileInfo(secondary ?? primary).Length);
        string primaryAfter = await HashAsync(primary);
        string? secondaryAfter = secondary is null ? null : await HashAsync(secondary);
        Assert.Equal(primaryBefore, primaryAfter);
        Assert.Equal(secondaryBefore, secondaryAfter);

        string? root = Environment.GetEnvironmentVariable("LPB_R1_FROZEN_CONTROL_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            Directory.CreateDirectory(root);
            await using var output = new FileStream(Path.Combine(root, primaryName + ".json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(output, new
            {
                evidenceClass = "RealSample", primaryName, secondaryName,
                primaryBefore, primaryAfter, secondaryBefore, secondaryAfter,
                expectedProtocol = expectedProtocol.ToString(), actualProtocol = facts.Protocol.ToString(),
                primaryRange = new { facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength },
                motionRange = new { facts.MotionVideo.ByteOffset, facts.MotionVideo.ByteLength },
                inspectorIssuedPlan = inspected.ExtractionPlan is not null,
                operation = "Inspector only: unchanged legal controls, not extraction/clean/conversion acceptance"
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}

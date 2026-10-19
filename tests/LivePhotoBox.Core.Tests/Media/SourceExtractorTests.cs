using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceExtractorTests
{
    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    [Theory]
    [InlineData("oppo.jpg")]
    [InlineData("vivo.jpg")]
    [InlineData("三星.jpg")]
    [InlineData("华为-Mate80.jpg")]
    [InlineData("小米.jpg")]
    [InlineData("红米老款-GV1.JPG")]
    [Trait("Category", "RealSamples")]
    public async Task Extract_SingleFileSamples_ExtractsValidImageAndVideo(string sampleName)
    {
        string sample = ResolveSample(sampleName);
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        using var inspected = await inspector.InspectWithPlanAsync(sample);
        var facts = inspected.Facts;

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(inspected.ExtractionPlan, sample, null, workspace);
        using var cleanupAuthority = bundle.CleanupPlan;

        Assert.NotNull(bundle.PrimaryImage);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.True(bundle.PrimaryImage.ByteLength > 0);

        byte[] imgHeader = new byte[12];
        using (var fs = File.OpenRead(bundle.PrimaryImage.Path))
        {
            fs.ReadExactly(imgHeader, 0, 12);
        }
        Assert.Equal(0xFF, imgHeader[0]);
        Assert.Equal(0xD8, imgHeader[1]);

        if (facts.MotionVideo != null && facts.MotionVideo.IsPresent)
        {
            Assert.NotNull(bundle.MotionVideo);
            Assert.True(File.Exists(bundle.MotionVideo.Path));
            Assert.True(bundle.MotionVideo.ByteLength > 0);

            byte[] vidHeader = new byte[12];
            using (var fs = File.OpenRead(bundle.MotionVideo.Path))
            {
                fs.ReadExactly(vidHeader, 0, 12);
            }
            Assert.Equal((byte)'f', vidHeader[4]);
            Assert.Equal((byte)'t', vidHeader[5]);
            Assert.Equal((byte)'y', vidHeader[6]);
            Assert.Equal((byte)'p', vidHeader[7]);
        }

        if (sampleName == "vivo.jpg")
        {
            Assert.NotNull(bundle.GainMap);
            Assert.True(File.Exists(bundle.GainMap.Path));
            byte[] gmHeader = new byte[2];
            using (var fs = File.OpenRead(bundle.GainMap.Path))
            {
                fs.ReadExactly(gmHeader, 0, 2);
            }
            Assert.Equal(0xFF, gmHeader[0]);
            Assert.Equal(0xD8, gmHeader[1]);
        }

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Extract_AppleDualFile_ExtractsValidArtifacts()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string mov = ResolveSample("苹果双文件.MOV");
        string imgBeforeSha = await ComputeSha256Async(img);
        string movBeforeSha = await ComputeSha256Async(mov);

        var inspector = new SourceInspector();
        using var inspected = await inspector.InspectWithPlanAsync(img, mov);

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(inspected.ExtractionPlan, img, mov, workspace);
        using var cleanupAuthority = bundle.CleanupPlan;

        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.True(File.Exists(bundle.MotionVideo.Path));

        string imgAfterSha = await ComputeSha256Async(img);
        string movAfterSha = await ComputeSha256Async(mov);
        Assert.Equal(imgBeforeSha, imgAfterSha);
        Assert.Equal(movBeforeSha, movAfterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task LivePhotoVideoExtractor_ExtractsAndCommitsReadableMotionVideoWithoutLeakingRollbackHandle()
    {
        string sample = ResolveSample("oppo.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        using var inspected = await new SourceInspector().InspectWithPlanAsync(sample);
        Assert.NotNull(inspected.Facts.MotionVideo);

        string? extractedPath = await LivePhotoBox.Services.LivePhotoVideoExtractor.ExtractVideoAutoAsync(sample, 0);
        try
        {
            Assert.NotNull(extractedPath);
            Assert.True(File.Exists(extractedPath));
            Assert.Equal(inspected.Facts.MotionVideo!.ByteLength, new FileInfo(extractedPath!).Length);

            // Prove that CommitExtractionOutputs closed the Native rollback handle
            // so the caller can open the extracted preview video with exclusive access.
            using (var exclusiveStream = new FileStream(extractedPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(inspected.Facts.MotionVideo.ByteLength, exclusiveStream.Length);
            }
        }
        finally
        {
            if (extractedPath is not null && File.Exists(extractedPath))
            {
                File.Delete(extractedPath);
            }
        }

        Assert.Equal(beforeSha, await ComputeSha256Async(sample));
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorRealSampleTests
{
    private static string ResolveSample(string fileName)
    {
        string[] candidates = [
            Path.Combine(AppContext.BaseDirectory, "samples", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "designs", "各个机型测试", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "designs", "各个机型测试", fileName),
            Path.Combine(AppContext.BaseDirectory, "designs", "各个机型测试", fileName)
        ];
        foreach (var c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException($"Sample not found: {fileName}");
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ComputeSliceSha256Async(string filePath, long offset, long length)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    [Theory]
    [InlineData("oppo.jpg")]
    [InlineData("vivo.jpg")]
    [InlineData("一加-改了封面照片.jpg")]
    [InlineData("一加.jpg")]
    [InlineData("三星.heic")]
    [InlineData("三星.jpg")]
    [InlineData("华为-Mate80.jpg")]
    [InlineData("华为Mate80.heic")]
    [InlineData("小米.jpg")]
    [InlineData("红米老款-GV1.JPG")]
    [InlineData("荣耀.jpg")]
    [Trait("Category", "RealSamples")]
    public async Task Extract_SingleFileRealSamples_ByteExactAndClean(string fileName)
    {
        string samplePath = ResolveSample(fileName);
        string beforeSha = await ComputeFileSha256Async(samplePath);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(samplePath);

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // 1. Source Immutability
        string afterSha = await ComputeFileSha256Async(samplePath);
        Assert.Equal(beforeSha, afterSha);

        // 2. Primary Image Validation & Byte Exactness
        Assert.NotNull(bundle.PrimaryImage);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.Equal(facts.PrimaryImage.ByteLength, bundle.PrimaryImage.ByteLength);
        string oracleImgSha = await ComputeSliceSha256Async(samplePath, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        Assert.Equal(oracleImgSha, bundle.PrimaryImage.Sha256);

        byte[] imgHeader = new byte[12];
        using (var fs = File.OpenRead(bundle.PrimaryImage.Path))
        {
            fs.ReadExactly(imgHeader, 0, 12);
        }
        if (facts.PrimaryImage.Container == ImageContainer.Jpeg)
        {
            Assert.Equal(0xFF, imgHeader[0]);
            Assert.Equal(0xD8, imgHeader[1]);
        }
        else if (facts.PrimaryImage.Container == ImageContainer.Heic)
        {
            Assert.Equal((byte)'f', imgHeader[4]);
            Assert.Equal((byte)'t', imgHeader[5]);
            Assert.Equal((byte)'y', imgHeader[6]);
            Assert.Equal((byte)'p', imgHeader[7]);
        }

        await AssertImageDecodableAndDimensionsMatchAsync(bundle.PrimaryImage.Path, facts.PrimaryImage);

        // 3. Motion Video Validation & Byte Exactness
        int expectedArtifactCount = 1;
        if (facts.MotionVideo is { IsPresent: true } vidFacts)
        {
            expectedArtifactCount++;
            Assert.NotNull(bundle.MotionVideo);
            Assert.True(File.Exists(bundle.MotionVideo.Path));
            Assert.Equal(vidFacts.ByteLength, bundle.MotionVideo.ByteLength);
            string oracleVidSha = await ComputeSliceSha256Async(samplePath, vidFacts.ByteOffset, vidFacts.ByteLength);
            Assert.Equal(oracleVidSha, bundle.MotionVideo.Sha256);

            byte[] vidHeader = new byte[12];
            using (var fs = File.OpenRead(bundle.MotionVideo.Path))
            {
                fs.ReadExactly(vidHeader, 0, 12);
            }
            Assert.Equal((byte)'f', vidHeader[4]);
            Assert.Equal((byte)'t', vidHeader[5]);
            Assert.Equal((byte)'y', vidHeader[6]);
            Assert.Equal((byte)'p', vidHeader[7]);

            await AssertVideoProbedValidAsync(bundle.MotionVideo.Path, vidFacts);
        }

        // 4. GainMap Validation & Byte Exactness (e.g. vivo.jpg)
        if (facts.GainMap is { IsPresent: true } gmFacts)
        {
            expectedArtifactCount++;
            Assert.NotNull(bundle.GainMap);
            Assert.True(File.Exists(bundle.GainMap.Path));
            Assert.Equal(gmFacts.ByteLength, bundle.GainMap.ByteLength);
            string oracleGmSha = await ComputeSliceSha256Async(samplePath, gmFacts.ByteOffset, gmFacts.ByteLength);
            Assert.Equal(oracleGmSha, bundle.GainMap.Sha256);

            byte[] gmHeader = new byte[2];
            using (var fs = File.OpenRead(bundle.GainMap.Path))
            {
                fs.ReadExactly(gmHeader, 0, 2);
            }
            if (gmFacts.Container == ImageContainer.Jpeg)
            {
                Assert.Equal(0xFF, gmHeader[0]);
                Assert.Equal(0xD8, gmHeader[1]);
            }

            await AssertImageDecodableAndDimensionsMatchAsync(bundle.GainMap.Path, new ImageFacts
            {
                Container = gmFacts.Container,
                ByteOffset = gmFacts.ByteOffset,
                ByteLength = gmFacts.ByteLength,
                IsPresent = true
            });
        }

        // 5. Workspace Cleanliness
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Equal(expectedArtifactCount, workspaceFiles.Length);
        Assert.DoesNotContain(workspaceFiles, f => Path.GetFileName(f).Contains("tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("vivo双文件.jpg", "vivo双文件.mp4")]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV")]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV")]
    [Trait("Category", "RealSamples")]
    public async Task Extract_DualFileRealSamples_ByteExactAndClean(string primaryFileName, string secondaryFileName)
    {
        string primaryPath = ResolveSample(primaryFileName);
        string secondaryPath = ResolveSample(secondaryFileName);

        string primaryBeforeSha = await ComputeFileSha256Async(primaryPath);
        string secondaryBeforeSha = await ComputeFileSha256Async(secondaryPath);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(primaryPath, secondaryPath);

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, primaryPath, secondaryPath, workspace);

        // 1. Source Immutability
        string primaryAfterSha = await ComputeFileSha256Async(primaryPath);
        string secondaryAfterSha = await ComputeFileSha256Async(secondaryPath);
        Assert.Equal(primaryBeforeSha, primaryAfterSha);
        Assert.Equal(secondaryBeforeSha, secondaryAfterSha);

        // 2. Primary Image Validation & Byte Exactness
        Assert.NotNull(bundle.PrimaryImage);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.Equal(facts.PrimaryImage.ByteLength, bundle.PrimaryImage.ByteLength);
        string oracleImgSha = await ComputeSliceSha256Async(primaryPath, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        Assert.Equal(oracleImgSha, bundle.PrimaryImage.Sha256);

        // 3. Motion Video Validation & Byte Exactness
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.Equal(facts.MotionVideo!.ByteLength, bundle.MotionVideo.ByteLength);

        string videoSourcePath = facts.MotionVideo.SourceIndex == 1 ? secondaryPath : primaryPath;
        string oracleVidSha = await ComputeSliceSha256Async(videoSourcePath, facts.MotionVideo.ByteOffset, facts.MotionVideo.ByteLength);
        Assert.Equal(oracleVidSha, bundle.MotionVideo.Sha256);

        byte[] vidHeader = new byte[12];
        using (var fs = File.OpenRead(bundle.MotionVideo.Path))
        {
            fs.ReadExactly(vidHeader, 0, 12);
        }
        Assert.Equal((byte)'f', vidHeader[4]);
        Assert.Equal((byte)'t', vidHeader[5]);
        Assert.Equal((byte)'y', vidHeader[6]);
        Assert.Equal((byte)'p', vidHeader[7]);

        await AssertImageDecodableAndDimensionsMatchAsync(bundle.PrimaryImage.Path, facts.PrimaryImage);
        await AssertVideoProbedValidAsync(bundle.MotionVideo.Path, facts.MotionVideo!);

        // 4. Workspace Cleanliness
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Equal(2, workspaceFiles.Length);
        Assert.DoesNotContain(workspaceFiles, f => Path.GetFileName(f).Contains("tmp", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertImageDecodableAndDimensionsMatchAsync(string imagePath, ImageFacts facts)
    {
        Assert.True(File.Exists(imagePath));
        Assert.True(new FileInfo(imagePath).Length > 0);

        try
        {
            using var fileStream = File.OpenRead(imagePath);
            using var mem = new MemoryStream();
            await fileStream.CopyToAsync(mem);
            mem.Position = 0;
            using var randomStream = mem.AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomStream);
            Assert.True(decoder.PixelWidth > 0);
            Assert.True(decoder.PixelHeight > 0);
            if (facts.Width > 0 && facts.Height > 0)
            {
                bool dimsMatch = (decoder.PixelWidth == facts.Width && decoder.PixelHeight == facts.Height) ||
                                 (decoder.PixelWidth == facts.Height && decoder.PixelHeight == facts.Width);
                Assert.True(dimsMatch, $"Decoded dimensions {decoder.PixelWidth}x{decoder.PixelHeight} do not match facts {facts.Width}x{facts.Height}");
            }
        }
        catch (COMException) when (facts.Container == ImageContainer.Heic)
        {
            // System without HEVC/HEIC codec installed; container and facts checked
            Assert.True(facts.Width > 0 && facts.Height > 0);
        }
    }

    private static async Task AssertVideoProbedValidAsync(string videoPath, VideoFacts expectedFacts)
    {
        Assert.True(File.Exists(videoPath));
        Assert.True(new FileInfo(videoPath).Length > 0);

        var probed = await NativeMediaService.ProbeVideoAsync(videoPath);
        Assert.True(probed.IsPresent);
        Assert.True(probed.Width > 0, $"Probed video width must be > 0, got {probed.Width}");
        Assert.True(probed.Height > 0, $"Probed video height must be > 0, got {probed.Height}");
        Assert.True(probed.DurationSeconds > 0, $"Probed video duration must be > 0, got {probed.DurationSeconds}");
        if (expectedFacts.Width > 0 && expectedFacts.Height > 0)
        {
            Assert.Equal(expectedFacts.Width, probed.Width);
            Assert.Equal(expectedFacts.Height, probed.Height);
        }
    }
}

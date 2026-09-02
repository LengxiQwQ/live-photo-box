using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class SourceProtocolCleanerTests
{
    private static string ResolveSample(string filename)
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "designs", "各个机型测试", filename);
            if (File.Exists(candidate)) return candidate;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Sample file '{filename}' not found.");
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static async Task RunSampleCleanAndVerifyAsync(
        string primarySampleName,
        string? secondarySampleName,
        SourceProtocol expectedInitialProtocol)
    {
        string primaryPath = ResolveSample(primarySampleName);
        string? secondaryPath = secondarySampleName != null ? ResolveSample(secondarySampleName) : null;

        string primaryShaBefore = ComputeSha256(primaryPath);
        string? secondaryShaBefore = secondaryPath != null ? ComputeSha256(secondaryPath) : null;

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        // 1. Inspect
        var facts = await inspector.InspectAsync(primaryPath, secondaryPath);
        Assert.Equal(expectedInitialProtocol, facts.Protocol);

        // 2. Extract
        var extracted = await extractor.ExtractAsync(facts, primaryPath, secondaryPath, workspace);
        Assert.NotNull(extracted.PrimaryImage);
        Assert.True(File.Exists(extracted.PrimaryImage.Path));

        // 3. Clean
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        Assert.True(File.Exists(cleanResult.CleanedImage.Path));
        // A source protocol may be removed by range extraction (for example
        // Huawei's 60-byte trailer) before the media cleaner runs.  Keep the
        // two responsibilities explicit and assert the combined audit trail.
        Assert.NotEmpty(extracted.ExtractedProtocolFacts.Concat(cleanResult.RemovedFacts));

        // 4. Source Immutability Assertion
        Assert.Equal(primaryShaBefore, ComputeSha256(primaryPath));
        if (secondaryPath != null)
        {
            Assert.Equal(secondaryShaBefore, ComputeSha256(secondaryPath));
        }

        // 5. Re-Inspection of Cleaned Media: Must be NonLive
        string cleanedImgPath = cleanResult.CleanedImage.Path;
        string? cleanedVidPath = cleanResult.CleanedVideo?.Path;

        var recheckFacts = await inspector.InspectAsync(cleanedImgPath, secondaryPath != null ? cleanedVidPath : null);
        Assert.Equal(SourceProtocol.NonLive, recheckFacts.Protocol);

        // 6. Idempotency test: Cleaning already cleaned media produces NonLive output without error
        using var secondWorkspace = new MediaWorkspace();
        var secondExtracted = await extractor.ExtractAsync(recheckFacts, cleanedImgPath, secondaryPath != null ? cleanedVidPath : null, secondWorkspace);
        var secondCleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = recheckFacts,
            ExtractedBundle = secondExtracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, secondWorkspace);

        Assert.True(secondCleanResult.Success, secondCleanResult.ErrorMessage);
        Assert.NotNull(secondCleanResult.CleanedImage);
        Assert.True(File.Exists(secondCleanResult.CleanedImage.Path));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Apple_StripsMakerNoteAndMebx()
    {
        await RunSampleCleanAndVerifyAsync("苹果双文件.HEIC", "苹果双文件.MOV", SourceProtocol.AppleLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_GoogleV1_StripsMicroVideo()
    {
        await RunSampleCleanAndVerifyAsync("红米老款-GV1.JPG", null, SourceProtocol.GoogleMicroVideoV1);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_GoogleV2_Xiaomi_StripsMotionPhoto()
    {
        await RunSampleCleanAndVerifyAsync("小米.jpg", null, SourceProtocol.GoogleMotionPhotoV2);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Oppo_StripsOLivePhoto()
    {
        await RunSampleCleanAndVerifyAsync("oppo.jpg", null, SourceProtocol.OppoLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_VivoX300_StripsVMotionPhoto()
    {
        await RunSampleCleanAndVerifyAsync("vivo.jpg", null, SourceProtocol.VivoLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_VivoLegacyDual_StripsAlbumTailAndMp4Keys()
    {
        await RunSampleCleanAndVerifyAsync("vivo双文件.jpg", "vivo双文件.mp4", SourceProtocol.VivoLegacyDualFile);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungJpeg_StripsSefTrailer()
    {
        await RunSampleCleanAndVerifyAsync("三星.jpg", null, SourceProtocol.SamsungMotionPhotoJpeg);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungHeic_StripsMpvd()
    {
        await RunSampleCleanAndVerifyAsync("三星.heic", null, SourceProtocol.SamsungMotionPhotoHeic);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungHeic_StripsMotionPhotoXmpWithoutDroppingHdrDirectory()
    {
        string primaryPath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(primaryPath);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoHeic, facts.Protocol);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        string cleanedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanResult.CleanedImage!.Path));
        Assert.DoesNotContain("GCamera:MotionPhoto", cleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Semantic=\"MotionPhoto\"", cleanedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sefd", cleanedText, StringComparison.Ordinal);
        Assert.Contains("Semantic=\"GainMap\"", cleanedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_HuaweiJpeg_StripsLiveTail()
    {
        await RunSampleCleanAndVerifyAsync("华为-Mate80.jpg", null, SourceProtocol.HuaweiMovingPhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_HuaweiHeic_StripsLiveTail()
    {
        await RunSampleCleanAndVerifyAsync("华为Mate80.heic", null, SourceProtocol.HuaweiMovingPhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Honor_StripsMovingPhoto()
    {
        await RunSampleCleanAndVerifyAsync("荣耀.jpg", null, SourceProtocol.HonorMovingPhoto);
    }
}

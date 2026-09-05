using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class CleanerTrustChainTests
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

    [Fact]
    public async Task Clean_FailsWhenSourceFactsIsMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = null!,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.FactsNotConfirmed, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    public async Task Clean_FailsWhenProtocolIsUnknown()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.Unknown,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.UnsupportedProtocol, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }


    [Fact]
    public void CleanRequest_GuaranteesSourceFactsDerivedSolelyFromExtractedBundle()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.SamsungMotionPhotoJpeg,
            PrimarySha256 = "SHA_AAA",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = "test.jpg",
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "SHA_AAA"
            }
        };

        var request = new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        };

        Assert.Same(bundle.SourceFacts, request.SourceFacts);
    }

    [Fact]
    public async Task Clean_FailsWhenPrimaryImageFileMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string nonExistentPath = Path.Combine(workspace.RootDirectory, "non-existent.jpg");

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.OppoLivePhoto,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = nonExistentPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 100,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactFactMismatch, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    public async Task Clean_FailsWhenDeclaredMotionVideoFileMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        string nonExistentVid = Path.Combine(workspace.RootDirectory, "non-existent.mp4");

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.VivoLegacyDualFile,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true },
            MotionVideo = new VideoFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true, SourceIndex = 1 }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            },
            MotionVideo = new MediaArtifact
            {
                Path = nonExistentVid,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                ByteLength = 100,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactFactMismatch, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenPrimaryImageByteLengthChangedSinceExtraction()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Tamper declared byte length
        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage! with { ByteLength = extracted.PrimaryImage.ByteLength + 10 }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenPrimaryImageSha256ChangedSinceExtraction()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Tamper declared SHA-256
        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage! with { Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenMotionVideoSha256ChangedSinceExtraction()
    {
        string sampleImg = ResolveSample("苹果双文件.HEIC");
        string sampleMov = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(sampleImg, sampleMov);
        var extracted = await extractor.ExtractAsync(facts, sampleImg, sampleMov, workspace);
        Assert.NotNull(extracted.MotionVideo);

        // Tamper declared video SHA-256
        var tamperedBundle = extracted with
        {
            MotionVideo = extracted.MotionVideo! with { Sha256 = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    public async Task Clean_NonLiveSourceBypassesMutationsVerbatim()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("normal-img", ".jpg");
        byte[] imgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };
        await File.WriteAllBytesAsync(imgPath, imgBytes);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(imgBytes));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimarySha256 = imgSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = imgBytes.Length, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = imgBytes.Length,
                Sha256 = imgSha
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.True(result.Success);
        Assert.NotNull(result.CleanedImage);
        Assert.Equal(imgSha, result.CleanedImage.Sha256);
        Assert.Equal(imgBytes.Length, result.CleanedImage.ByteLength);
        Assert.Empty(result.RemovedFacts);
        Assert.NotNull(result.CleanupPlan);
        Assert.Empty(result.CleanupPlan.Actions);
        Assert.Equal(PreservationOutcome.Preserved, result.PreservationOutcome);
    }
}

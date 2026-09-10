using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Core.Tests.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NeutralMediaServiceTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_Apple_ExtractsCleansAndProducesValidBundle()
    {
        string primary = ResolveSample("苹果双文件.HEIC");
        string secondary = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, secondary, workspace);

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
        Assert.Equal(VideoContainer.Mov, bundle.MotionVideo.VideoContainer);
        Assert.NotEmpty(bundle.RemovedProtocolFacts);
        Assert.NotEmpty(bundle.Manifest);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_WithFormatConversion_ConvertsCorrectly()
    {
        string primary = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, null, workspace, new MediaFormatRequirement
        {
            ImageContainer = ImageContainer.Heic,
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Copy
        });

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(VideoContainer.Mov, bundle.MotionVideo.VideoContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_ReencodedVideo_IsNotReportedLossless()
    {
        string primary = ResolveSample("vivo双文件.jpg");
        string secondary = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, secondary, workspace, new MediaFormatRequirement
        {
            ImageContainer = ImageContainer.Unknown,
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.Hevc
        });

        var videoManifest = Assert.Single(bundle.Manifest, x => x.Role == "MotionVideo");
        Assert.Equal(PreservationOutcome.Reencoded, videoManifest.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_XiaomiWithGainMap_PreservesGainMapInBundle()
    {
        string primary = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, null, workspace);

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.NotNull(bundle.GainMap);
        Assert.True(File.Exists(bundle.GainMap.Path));

        byte[] primaryBytes = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        byte[] gainMapBytes = await File.ReadAllBytesAsync(bundle.GainMap!.Path);
        int jpegCount = 0;
        for (int i = 0; i + 1 < primaryBytes.Length; i++)
        {
            if (primaryBytes[i] == 0xFF && primaryBytes[i + 1] == 0xD8)
                jpegCount++;
        }

        Assert.True(jpegCount >= 2, "Neutral JPEG must retain the primary and GainMap JPEG payloads.");
        Assert.True(primaryBytes.AsSpan().IndexOf(gainMapBytes) >= 0,
            "Neutral primary must contain the exact GainMap bytes consumed by final reassembly.");

        // Downstream ownership contract: must be unambiguously declared as Embedded
        Assert.Equal(GainMapRepresentation.Embedded, bundle.GainMapRepresentation);
        var primaryManifest = Assert.Single(bundle.Manifest, x => x.Role == "PrimaryImage");
        Assert.Equal(GainMapRepresentation.Embedded, primaryManifest.GainMapRepresentation);
        var gainMapManifest = Assert.Single(bundle.Manifest, x => x.Role == "GainMap");
        Assert.Equal(GainMapRepresentation.Embedded, gainMapManifest.GainMapRepresentation);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(gainMapBytes)), gainMapManifest.Sha256);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_NonGainMap_HasNoneGainMapRepresentation()
    {
        string primary = ResolveSample("苹果-双文件.JPG");
        string video = ResolveSample("苹果-双文件.MOV");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, video, workspace);

        Assert.NotNull(bundle);
        Assert.Null(bundle.GainMap);
        Assert.Equal(GainMapRepresentation.None, bundle.GainMapRepresentation);

        var primaryManifest = Assert.Single(bundle.Manifest, x => x.Role == "PrimaryImage");
        Assert.Equal(GainMapRepresentation.None, primaryManifest.GainMapRepresentation);
    }

    [Fact]
    public async Task CreateNeutralBundle_FinalGainMapConsumeRejectsReplacementAfterPreservation()
    {
        using var workspace = new MediaWorkspace();
        string primaryPath = workspace.AllocateFilePath("final-consume-primary", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1Jpeg(primaryPath);

        string gainMapPath = workspace.AllocateFilePath("final-consume-gainmap", ".jpg");
        byte[] gainMapA =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
        ];
        byte[] gainMapB = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x04, 0x42, 0x00, 0xFF, 0xD9];
        await File.WriteAllBytesAsync(gainMapPath, gainMapA);

        string primarySha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(primaryPath)));
        string gainMapSha = Convert.ToHexString(SHA256.HashData(gainMapA));
        var primaryArtifact = new MediaArtifact
        {
            Path = primaryPath,
            Kind = MediaArtifactKind.PrimaryImage,
            MimeType = "image/jpeg",
            ImageContainer = ImageContainer.Jpeg,
            ByteLength = new FileInfo(primaryPath).Length,
            Sha256 = primarySha
        };
        var gainMapArtifact = new MediaArtifact
        {
            Path = gainMapPath,
            Kind = MediaArtifactKind.GainMap,
            MimeType = "image/jpeg",
            ImageContainer = ImageContainer.Jpeg,
            ByteLength = gainMapA.Length,
            Sha256 = gainMapSha
        };
        var sourceFacts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimarySha256 = primarySha,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteLength = new FileInfo(primaryPath).Length
            }
        };
        var extracted = new ExtractedMediaBundle
        {
            PrimaryImage = primaryArtifact,
            GainMap = gainMapArtifact,
            SourceFacts = sourceFacts
        };

        var cleaner = new PreservationThenReplaceCleaner(gainMapB);
        var service = new NeutralMediaService(
            inspector: new NonLiveAfterFirstInspection(),
            extractor: new FixedExtractor(extracted),
            cleaner: cleaner);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateNeutralBundleAsync(
            primaryPath, null, workspace, preservationPolicy: PreservationPolicy.BestEffort));

        Assert.True(cleaner.PreservationPassed);
        Assert.Equal(gainMapB, await File.ReadAllBytesAsync(gainMapPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.RootDirectory),
            path => Path.GetFileName(path).StartsWith("neutral-img-gainmap", StringComparison.Ordinal));
    }

    private sealed class NonLiveAfterFirstInspection : ISourceInspector
    {
        private int _calls;

        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            bool first = Interlocked.Increment(ref _calls) == 1;
            return Task.FromResult(new SourceMediaFacts
            {
                Protocol = first ? SourceProtocol.GoogleMicroVideoV1 : SourceProtocol.NonLive,
                PrimarySha256 = string.Empty,
                PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg }
            });
        }

        public async Task<InspectedSource> InspectWithPlanAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            SourceMediaFacts facts = await InspectAsync(primaryPath, secondaryPath, cancellationToken);
            return InspectedSource.CreateForTests(facts);
        }
    }

    private sealed class FixedExtractor(ExtractedMediaBundle bundle) : ISourceExtractor
    {
        public Task<ExtractedMediaBundle> ExtractAsync(
            ExtractionPlan plan,
            string primaryPath,
            string? secondaryPath,
            IMediaWorkspace workspace,
            CancellationToken cancellationToken = default) => Task.FromResult(bundle);
    }

    private sealed class PreservationThenReplaceCleaner(byte[] replacement) : ISourceProtocolCleaner
    {
        public bool PreservationPassed { get; private set; }

        public async Task<ProtocolCleanResult> CleanAsync(
            ProtocolCleanRequest request,
            IMediaWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            var baseline = await MetadataPreservationVerifier.CaptureBaselineAsync(
                request.ExtractedBundle, cancellationToken);
            var report = await MetadataPreservationVerifier.VerifyAgainstBaselineAsync(
                baseline,
                request.ExtractedBundle.PrimaryImage.Path,
                stagedVideoPath: null,
                stagedGainMapPath: request.ExtractedBundle.GainMap!.Path,
                cancellationToken);
            PreservationPassed = report.OverallOutcome == PreservationOutcome.Preserved;
            Assert.True(PreservationPassed, report.Summary);

            await File.WriteAllBytesAsync(request.ExtractedBundle.GainMap.Path, replacement, cancellationToken);
            return new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = request.ExtractedBundle.PrimaryImage,
                CleanedGainMap = request.ExtractedBundle.GainMap,
                GainMapExpectedSha256 = request.ExtractedBundle.GainMap.Sha256,
                PreservationOutcome = report.OverallOutcome
            };
        }
    }
}

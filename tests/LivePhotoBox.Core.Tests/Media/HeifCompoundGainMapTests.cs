using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Core.Tests.Support;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class HeifCompoundGainMapTests
{
    private const string AppleGainMapUrn = "urn:com:apple:photo:2020:aux:hdrgainmap";
    private const string SamsungGainMapUrn = "urn:com:samsung:photo:2024:aux:hdrgainmap";

    [Fact]
    [Trait("Category", "Extractor")]
    public unsafe void SyntheticValidGridDimgGraph_ReportsCompleteDependenciesAndPrimaryOwnership()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 2, AppleGainMapUrn),
            new W3SyntheticItem(3, "hvc1", 6, 4),
            new W3SyntheticItem(4, "hvc1", 10, 4)
        ],
        [
            new W3SyntheticReference("dimg", 2, [3, 4]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        (NativeResult result, string? error, NativeAuxiliaryItemFacts[] items) = W3NativeHeif.Enumerate(input);

        Assert.Equal(NativeResult.Ok, result);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        NativeAuxiliaryItemFacts auxiliary = Assert.Single(items);
        Assert.Equal(2u, auxiliary.ItemId);
        Assert.Equal((int)AuxiliaryOwnership.Primary, auxiliary.Ownership);
        Assert.Equal(1u, auxiliary.GraphFlags & 1u); // complete
        Assert.Equal(2u, auxiliary.DependencyCount);
        Assert.Equal(3u, auxiliary.DependencyItemIds[0]);
        Assert.Equal(4u, auxiliary.DependencyItemIds[1]);
        Assert.Equal("grid", ReadFixed(auxiliary.ItemType, 8));
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticMissingTile_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 2, AppleGainMapUrn)
        ],
        [
            new W3SyntheticReference("dimg", 2, [99]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        AssertRejected(input, "missing");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticDuplicateDependency_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 2, AppleGainMapUrn),
            new W3SyntheticItem(3, "hvc1", 6, 4)
        ],
        [
            new W3SyntheticReference("dimg", 2, [3, 3]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        AssertRejected(input, "duplicate");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticDependencyCycle_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 2, AppleGainMapUrn),
            new W3SyntheticItem(3, "grid", 6, 2)
        ],
        [
            new W3SyntheticReference("dimg", 2, [3]),
            new W3SyntheticReference("dimg", 3, [2]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        AssertRejected(input, "cycle");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticOverlappingExtent_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 4, AppleGainMapUrn),
            new W3SyntheticItem(3, "hvc1", 6, 4)
        ],
        [
            new W3SyntheticReference("dimg", 2, [3]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        AssertRejected(input, "overlap");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticShadowRelationship_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "grid", 4, 2, AppleGainMapUrn),
            new W3SyntheticItem(3, "grid", 6, 2, AppleGainMapUrn)
        ],
        [
            new W3SyntheticReference("auxl", 2, [1]),
            new W3SyntheticReference("auxl", 3, [1])
        ]);

        AssertRejected(input, "shadow");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void SyntheticUnknownDerivedType_FailsClosed()
    {
        byte[] input = W3HeifSyntheticBuilder.Build(
        [
            new W3SyntheticItem(1, "hvc1", 0, 4),
            new W3SyntheticItem(2, "zzzz", 4, 2, AppleGainMapUrn),
            new W3SyntheticItem(3, "hvc1", 6, 4)
        ],
        [
            new W3SyntheticReference("dimg", 2, [3]),
            new W3SyntheticReference("auxl", 2, [1])
        ]);

        AssertRejected(input, "unsupported");
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task AppleHeicGridGainMap_RemainsEmbeddedPrimaryOwnedWithoutPseudoFile()
    {
        W3SampleSnapshot heic = await W3SampleEvidence.CacheAsync("苹果双文件.HEIC");
        W3SampleSnapshot mov = await W3SampleEvidence.CacheAsync("苹果双文件.MOV");
        using var workspace = new W3EvidenceWorkspace("apple-grid-gainmap");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(heic.CachePath, mov.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        AuxiliaryMediaFacts auxiliary = Assert.Single(facts.AuxiliaryItems, item => item.Semantic == "GainMap");
        W3IndependentHeifGraph independent = W3IndependentHeifParser.Parse(await File.ReadAllBytesAsync(heic.CachePath));
        W3IndependentHeifAuxiliary independentAuxiliary = Assert.Single(independent.Auxiliaries,
            item => item.Relationship == AppleGainMapUrn);
        Assert.Equal(AppleGainMapUrn, auxiliary.Relationship);
        Assert.Equal(AuxiliaryRepresentation.Embedded, auxiliary.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, auxiliary.Ownership);
        Assert.Equal("primary:0", auxiliary.OwnerIdentity);
        Assert.Equal("grid", auxiliary.ItemType);
        Assert.True(auxiliary.GraphComplete);
        Assert.Equal(12, auxiliary.Dependencies.Count);
        Assert.Equal(8, auxiliary.ByteLength);
        Assert.Equal("grid", independentAuxiliary.ItemType);
        Assert.Equal(12, independentAuxiliary.Dependencies.Count);

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            heic.CachePath,
            mov.CachePath,
            workspace);
        AuxiliaryMediaDescriptor descriptor = Assert.Single(bundle.AuxiliaryMedia, item => item.Semantic == "GainMap");
        Assert.Equal(AuxiliaryRepresentation.Embedded, descriptor.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, descriptor.Ownership);
        Assert.Equal("primary:0", descriptor.OwnerIdentity);
        Assert.Null(descriptor.MaterializedArtifact);
        Assert.Null(bundle.GainMap);
        Assert.DoesNotContain(Directory.EnumerateFiles(workspace.RootDirectory), path =>
            Path.GetFileName(path).Contains("gainmap", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("aux-", StringComparison.OrdinalIgnoreCase));

        await WriteRealSampleEvidenceAsync(workspace, heic, mov, facts, bundle, "apple", auxiliary);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task SamsungHeicGridGainMap_UsesGenericRelationshipWithoutPseudoFile()
    {
        W3SampleSnapshot heic = await W3SampleEvidence.CacheAsync("三星.heic");
        using var workspace = new W3EvidenceWorkspace("samsung-grid-gainmap");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(heic.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        AuxiliaryMediaFacts auxiliary = Assert.Single(facts.AuxiliaryItems, item => item.Semantic == "GainMap");
        W3IndependentHeifGraph independent = W3IndependentHeifParser.Parse(await File.ReadAllBytesAsync(heic.CachePath));
        W3IndependentHeifAuxiliary independentAuxiliary = Assert.Single(independent.Auxiliaries,
            item => item.Relationship == SamsungGainMapUrn);
        Assert.Equal(SamsungGainMapUrn, auxiliary.Relationship);
        Assert.Equal(AuxiliaryOwnership.Primary, auxiliary.Ownership);
        Assert.Equal("primary:0", auxiliary.OwnerIdentity);
        Assert.Equal("grid", auxiliary.ItemType);
        Assert.True(auxiliary.GraphComplete);
        Assert.Equal(4, auxiliary.Dependencies.Count);
        Assert.Equal("grid", independentAuxiliary.ItemType);
        Assert.Equal(4, independentAuxiliary.Dependencies.Count);

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            heic.CachePath,
            null,
            workspace);
        AuxiliaryMediaDescriptor descriptor = Assert.Single(bundle.AuxiliaryMedia, item => item.Semantic == "GainMap");
        Assert.Null(descriptor.MaterializedArtifact);
        Assert.Null(bundle.GainMap);
        Assert.DoesNotContain(Directory.EnumerateFiles(workspace.RootDirectory), path =>
            Path.GetFileName(path).Contains("gainmap", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("aux-", StringComparison.OrdinalIgnoreCase));

        await WriteRealSampleEvidenceAsync(workspace, heic, null, facts, bundle, "samsung", auxiliary);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task AppleEmbeddedGainMap_IsRepresentedOnceInNeutralManifest()
    {
        using var workspace = new W3EvidenceWorkspace("apple-neutral-manifest");
        string primaryPath = workspace.AllocateFilePath("primary", ".jpg");
        byte[] primaryBytes = [0xFF, 0xD8, 0xFF, 0xD9];
        await File.WriteAllBytesAsync(primaryPath, primaryBytes);
        string primarySha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(primaryBytes));
        string auxiliarySha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 0x01 }));
        var sourceFacts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimarySha256 = primarySha,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteLength = primaryBytes.Length
            }
        };
        var descriptor = new AuxiliaryMediaDescriptor
        {
            ArtifactRole = MediaArtifactKind.GainMap,
            StableIdentity = "heif:item:55",
            Semantic = "GainMap",
            OwnerIdentity = "primary:0",
            Relationship = SamsungGainMapUrn,
            SourceOffset = 8,
            SourceLength = 8,
            SourceSha256 = auxiliarySha,
            ImageContainer = ImageContainer.Heic,
            Codec = AuxiliaryCodec.Hevc,
            Representation = AuxiliaryRepresentation.Embedded,
            Ownership = AuxiliaryOwnership.Primary,
            ItemType = "grid",
            GraphComplete = true,
            Dependencies = [new HeifDependencyFacts { ItemId = 51, ItemType = "hvc1", ByteOffset = 1, ByteLength = 1 }]
        };
        var extracted = new ExtractedMediaBundle
        {
            PrimaryImage = new MediaArtifact
            {
                Path = primaryPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ImageCodec = ImageCodec.Jpeg,
                ByteLength = primaryBytes.Length,
                Sha256 = primarySha
            },
            SourceFacts = sourceFacts,
            AuxiliaryMedia = [descriptor]
        };

        NeutralMediaBundle bundle = await new NeutralMediaService(
            inspector: new FixedNonLiveInspector(sourceFacts),
            extractor: new FixedBundleExtractor(extracted),
            cleaner: new EmbeddedDescriptorCleaner())
            .CreateNeutralBundleAsync(
            primaryPath,
            null,
            workspace);

        Assert.Equal(GainMapRepresentation.Embedded, bundle.GainMapRepresentation);
        NeutralArtifactManifest gainMap = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");
        Assert.Equal(GainMapRepresentation.Embedded, gainMap.GainMapRepresentation);
        Assert.Equal(bundle.PrimaryImage.Path, gainMap.Path);
        Assert.DoesNotContain(bundle.Manifest, item =>
            item.Role == "GainMap" && item.Path != bundle.PrimaryImage.Path);
        Assert.Single(bundle.Manifest, item => item.StableIdentity == gainMap.StableIdentity);
    }

    private sealed class FixedNonLiveInspector(SourceMediaFacts facts) : ISourceInspector
    {
        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(facts);

        public Task<InspectedSource> InspectWithPlanAsync(
            string primaryPath,
            string? secondaryPath = null,
            System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(InspectedSource.CreateForTests(facts));
    }

    private sealed class FixedBundleExtractor(ExtractedMediaBundle bundle) : ISourceExtractor
    {
        public Task<ExtractedMediaBundle> ExtractAsync(
            ExtractionPlan plan,
            string primaryPath,
            string? secondaryPath,
            IMediaWorkspace workspace,
            System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(bundle);
    }

    private sealed class EmbeddedDescriptorCleaner : ISourceProtocolCleaner
    {
        public Task<ProtocolCleanResult> CleanAsync(
            ProtocolCleanRequest request,
            IMediaWorkspace workspace,
            System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = request.ExtractedBundle.PrimaryImage,
                AuxiliaryMedia = request.ExtractedBundle.AuxiliaryMedia,
                PreservationCarriers = request.ExtractedBundle.PreservationCarriers,
                PreservationOutcome = PreservationOutcome.Preserved
            });
    }

    private static void AssertRejected(byte[] input, string expectedDiagnostic)
    {
        (NativeResult result, string? error, NativeAuxiliaryItemFacts[] _) = W3NativeHeif.Enumerate(input);
        Assert.Equal(NativeResult.InvalidArgument, result);
        Assert.Contains(expectedDiagnostic, error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string ReadFixed(byte* value, int capacity)
    {
        int length = 0;
        while (length < capacity && value[length] != 0) length++;
        return System.Text.Encoding.ASCII.GetString(value, length);
    }

    private static async Task WriteRealSampleEvidenceAsync(
        W3EvidenceWorkspace workspace,
        W3SampleSnapshot heic,
        W3SampleSnapshot? mov,
        SourceMediaFacts facts,
        ExtractedMediaBundle bundle,
        string sampleKind,
        AuxiliaryMediaFacts auxiliary)
    {
        string originalAfter = await W3SampleEvidence.ComputeSha256Async(heic.OriginalPath);
        string cacheAfter = await W3SampleEvidence.ComputeSha256Async(heic.CachePath);
        W3IndependentHeifGraph independent = W3IndependentHeifParser.Parse(await File.ReadAllBytesAsync(heic.CachePath));
        string evidenceDirectory = Path.Combine(workspace.RootDirectory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "independent-heif-parser.json"),
            JsonSerializer.Serialize(independent, new JsonSerializerOptions { WriteIndented = true }));
        var evidence = new
        {
            schema = "p2-w3-heif-evidence-v1",
            sampleKind,
            heic = new
            {
                heic.FileName,
                heic.OriginalPath,
                heic.CachePath,
                beforeSha256 = heic.OriginalSha256,
                afterSha256 = originalAfter,
                cacheBeforeSha256 = heic.CacheSha256,
                cacheAfterSha256 = cacheAfter
            },
            secondary = mov == null ? null : new
            {
                mov.FileName,
                mov.OriginalPath,
                mov.CachePath,
                beforeSha256 = mov.OriginalSha256,
                afterSha256 = W3SampleEvidence.ComputeSha256Async(mov.OriginalPath).GetAwaiter().GetResult(),
                cacheBeforeSha256 = mov.CacheSha256,
                cacheAfterSha256 = W3SampleEvidence.ComputeSha256Async(mov.CachePath).GetAwaiter().GetResult()
            },
            inspector = new
            {
                facts.PrimarySha256,
                facts.SecondarySha256,
                auxiliary.ItemId,
                auxiliary.ItemType,
                auxiliary.Relationship,
                auxiliary.OwnerIdentity,
                auxiliary.Ownership,
                auxiliary.Representation,
                auxiliary.ByteOffset,
                auxiliary.ByteLength,
                auxiliary.Sha256,
                auxiliary.GraphComplete,
                dependencies = auxiliary.Dependencies.Select(dependency => new
                {
                    dependency.ItemId,
                    dependency.ItemType,
                    dependency.ByteOffset,
                    dependency.ByteLength
                })
            },
            independentParser = independent,
            outputs = Directory.EnumerateFiles(workspace.RootDirectory)
                .Select(path => new { path = Path.GetFileName(path), sha256 = W3SampleEvidence.ComputeSha256Async(path).GetAwaiter().GetResult() })
                .OrderBy(item => item.path, StringComparer.Ordinal)
                .ToArray(),
            descriptors = bundle.AuxiliaryMedia.Select(item => new
            {
                item.StableIdentity,
                item.Semantic,
                item.OwnerIdentity,
                item.Relationship,
                item.Representation,
                item.Ownership,
                materialized = item.MaterializedArtifact?.Path
            })
        };
        await File.WriteAllTextAsync(
            Path.Combine(workspace.RootDirectory, "heif-evidence.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Core.Tests.Support;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorW2RealSampleTests
{
    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task OppoOriginalAuxiliary_UsesGenericMaterializedChannelWithoutDuplication()
    {
        W2SampleSnapshot sample = await W2SampleEvidence.CacheAsync("一加-改了封面照片.jpg");
        string beforeCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string beforeDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);

        using var workspace = new W2EvidenceWorkspace("oppo-original");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            sample.CachePath,
            null,
            workspace);

        AuxiliaryMediaDescriptor original = Assert.Single(
            bundle.AuxiliaryMedia,
            descriptor => descriptor.Semantic == "Original");
        Assert.Equal(MediaArtifactKind.AuxiliaryItem, original.ArtifactRole);
        Assert.Equal(AuxiliaryRepresentation.Materialized, original.Representation);
        Assert.NotNull(original.MaterializedArtifact);
        Assert.Equal(MediaArtifactKind.AuxiliaryItem, original.MaterializedArtifact!.Kind);
        Assert.True(File.Exists(original.MaterializedArtifact.Path));
        Assert.Equal(original.SourceSha256, original.MaterializedArtifact.Sha256);
        Assert.Equal(3176955, original.SourceOffset);
        Assert.Equal(11355276 - 3176955, original.SourceLength);

        string independentlyHashedSlice = await W2IndependentMediaEvidence.ComputeSliceSha256Async(
            sample.CachePath,
            original.SourceOffset,
            original.SourceLength);
        Assert.Equal(independentlyHashedSlice, original.SourceSha256);
        AssertNoDuplicateSourceRelationships(facts, bundle.AuxiliaryMedia);

        string afterCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string afterDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);
        Assert.Equal(beforeCacheSha, afterCacheSha);
        Assert.Equal(beforeDesignSha, afterDesignSha);
        Assert.Equal(beforeCacheSha, facts.PrimarySha256, ignoreCase: true);

        await W2EvidenceWriter.WriteAsync(
            workspace,
            sample,
            facts,
            bundle,
            beforeCacheSha,
            afterCacheSha,
            new
            {
                channel = "generic-materialized-auxiliary",
                originalRangeSha256 = independentlyHashedSlice,
                outputDirectory = Directory.GetFiles(workspace.RootDirectory).Select(Path.GetFileName).OrderBy(x => x).ToArray()
            });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task SamsungJpeg_PreservesNonMotionSefEntriesAndIndexTrailerAsCarriers()
    {
        W2SampleSnapshot sample = await W2SampleEvidence.CacheAsync("三星.jpg");
        string beforeCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string beforeDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);

        using var workspace = new W2EvidenceWorkspace("samsung-jpeg-carriers");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        Assert.Equal(SourceProtocol.SamsungMotionPhotoJpeg, facts.Protocol);

        byte[] sourceBytes = await File.ReadAllBytesAsync(sample.CachePath);
        Assert.True(
            W2IndependentMediaEvidence.TryParseSamsungSef(
                sourceBytes,
                facts.PrimaryImage.ByteOffset + facts.PrimaryImage.ByteLength,
                out SamsungSefEvidence independentSef));

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            sample.CachePath,
            null,
            workspace);

        Assert.NotEmpty(bundle.PreservationCarriers);
        PreservationCarrier indexCarrier = Assert.Single(
            bundle.PreservationCarriers,
            carrier => carrier.StableIdentity == "samsung:sef:index-trailer");
        Assert.Equal(independentSef.SeffHeaderOffset, indexCarrier.SourceOffset);
        Assert.Equal(independentSef.IndexTrailerLength, indexCarrier.SourceLength);
        Assert.Equal(
            await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                sample.CachePath,
                indexCarrier.SourceOffset,
                indexCarrier.SourceLength),
            indexCarrier.SourceSha256);

        foreach (SamsungSefEntryEvidence entry in independentSef.PreservedEntries)
        {
            string identity = $"samsung:sef:entry:{entry.Marker}:{entry.Name}";
            PreservationCarrier carrier = Assert.Single(
                bundle.PreservationCarriers,
                candidate => candidate.StableIdentity == identity);
            Assert.Equal(entry.PayloadOffset, carrier.SourceOffset);
            Assert.Equal(entry.PayloadLength, carrier.SourceLength);
            Assert.Equal(
                await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                    sample.CachePath,
                    entry.PayloadOffset,
                    entry.PayloadLength),
                carrier.SourceSha256);
        }

        Assert.DoesNotContain(bundle.PreservationCarriers, carrier => carrier.Semantic == "GainMap");
        string afterCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string afterDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);
        Assert.Equal(beforeCacheSha, afterCacheSha);
        Assert.Equal(beforeDesignSha, afterDesignSha);

        await W2EvidenceWriter.WriteAsync(
            workspace,
            sample,
            facts,
            bundle,
            beforeCacheSha,
            afterCacheSha,
            independentSef);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task SamsungHeic_ExposesHdrGainMapRelationshipWithoutPseudoFile()
    {
        W2SampleSnapshot sample = await W2SampleEvidence.CacheAsync("三星.heic");
        string beforeCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string beforeDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);

        using var workspace = new W2EvidenceWorkspace("samsung-heic-embedded-aux");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        AuxiliaryMediaFacts samsungAuxiliary = Assert.Single(
            facts.AuxiliaryItems,
            item => item.Relationship == "urn:com:samsung:photo:2024:aux:hdrgainmap");
        Assert.Equal("GainMap", samsungAuxiliary.Semantic);
        Assert.Equal(AuxiliaryRepresentation.Embedded, samsungAuxiliary.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, samsungAuxiliary.Ownership);
        Assert.Equal("primary:0", samsungAuxiliary.OwnerIdentity);
        Assert.Equal("grid", samsungAuxiliary.ItemType);
        Assert.True(samsungAuxiliary.GraphComplete);
        Assert.Equal(4, samsungAuxiliary.Dependencies.Count);

        byte[] sourceBytes = await File.ReadAllBytesAsync(sample.CachePath);
        Assert.True(
            W2IndependentMediaEvidence.TryFindSamsungHdrGainMapAuxiliary(
                sourceBytes,
                out HeifAuxiliaryEvidence independentHeif));
        Assert.Equal("urn:com:samsung:photo:2024:aux:hdrgainmap", independentHeif.Relationship);
        Assert.True(independentHeif.HasAuxlReference);
        Assert.True(independentHeif.HasPrimaryItemBox);
        Assert.True(independentHeif.HasItemInfoBox);

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            sample.CachePath,
            null,
            workspace);
        AuxiliaryMediaDescriptor descriptor = Assert.Single(
            bundle.AuxiliaryMedia,
            item => item.Relationship == "urn:com:samsung:photo:2024:aux:hdrgainmap");
        Assert.Equal(MediaArtifactKind.GainMap, descriptor.ArtifactRole);
        Assert.Equal(AuxiliaryOwnership.Primary, descriptor.Ownership);
        Assert.Equal("primary:0", descriptor.OwnerIdentity);
        Assert.Null(descriptor.MaterializedArtifact);
        Assert.Null(bundle.GainMap);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.RootDirectory),
            path => Path.GetFileName(path).StartsWith("aux-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                sample.CachePath,
                descriptor.SourceOffset,
                descriptor.SourceLength),
            descriptor.SourceSha256);

        string afterCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string afterDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);
        Assert.Equal(beforeCacheSha, afterCacheSha);
        Assert.Equal(beforeDesignSha, afterDesignSha);

        await W2EvidenceWriter.WriteAsync(
            workspace,
            sample,
            facts,
            bundle,
            beforeCacheSha,
            afterCacheSha,
            independentHeif);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task VivoJpegGainMap_IsMaterializedByteExactOnce()
    {
        W2SampleSnapshot sample = await W2SampleEvidence.CacheAsync("vivo.jpg");
        string beforeCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string beforeDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);

        using var workspace = new W2EvidenceWorkspace("vivo-jpeg-gainmap");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.CachePath);
        SourceMediaFacts facts = inspected.Facts;
        Assert.NotNull(facts.GainMap);

        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            sample.CachePath,
            null,
            workspace);
        AuxiliaryMediaDescriptor gainMap = Assert.Single(
            bundle.AuxiliaryMedia,
            descriptor => descriptor.ArtifactRole == MediaArtifactKind.GainMap);
        Assert.NotNull(gainMap.MaterializedArtifact);
        Assert.NotNull(bundle.GainMap);
        Assert.Equal(bundle.GainMap!.Path, gainMap.MaterializedArtifact!.Path);
        Assert.Equal(gainMap.SourceSha256, gainMap.MaterializedArtifact.Sha256);
        Assert.Equal(facts.GainMap!.ByteOffset, gainMap.SourceOffset);
        Assert.Equal(facts.GainMap.ByteLength, gainMap.SourceLength);
        Assert.Equal(
            await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                sample.CachePath,
                gainMap.SourceOffset,
                gainMap.SourceLength),
            gainMap.SourceSha256);
        W2IndependentMediaEvidence.AssertJpegRange(
            await File.ReadAllBytesAsync(sample.CachePath),
            gainMap.SourceOffset,
            gainMap.SourceLength);
        Assert.Equal(
            1,
            Directory.EnumerateFiles(workspace.RootDirectory)
                .Count(path => Path.GetFileName(path).StartsWith("gainmap", StringComparison.OrdinalIgnoreCase)));
        Assert.Single(bundle.AuxiliaryMedia, item => item.Semantic == "GainMap");

        string afterCacheSha = await W2SampleEvidence.ComputeSha256Async(sample.CachePath);
        string afterDesignSha = await W2SampleEvidence.ComputeSha256Async(sample.OriginalPath);
        Assert.Equal(beforeCacheSha, afterCacheSha);
        Assert.Equal(beforeDesignSha, afterDesignSha);

        await W2EvidenceWriter.WriteAsync(
            workspace,
            sample,
            facts,
            bundle,
            beforeCacheSha,
            afterCacheSha,
            new
            {
                channel = "typed-jpeg-gainmap",
                sourceRangeSha256 = gainMap.SourceSha256
            });
    }

    private static void AssertNoDuplicateSourceRelationships(
        SourceMediaFacts facts,
        IReadOnlyList<AuxiliaryMediaDescriptor> descriptors)
    {
        var ranges = new HashSet<(int SourceIndex, long Offset, long Length)>();
        ranges.Add((0, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength));
        if (facts.MotionVideo is { IsPresent: true } video)
        {
            Assert.True(ranges.Add((video.SourceIndex, video.ByteOffset, video.ByteLength)));
        }
        if (facts.GainMap is { IsPresent: true } gainMap)
        {
            Assert.True(ranges.Add((0, gainMap.ByteOffset, gainMap.ByteLength)));
        }
        foreach (AuxiliaryMediaDescriptor descriptor in descriptors)
        {
            bool isTypedGainMap = facts.GainMap is { IsPresent: true } typedGainMap &&
                descriptor.ArtifactRole == MediaArtifactKind.GainMap &&
                descriptor.SourceIndex == 0 &&
                descriptor.SourceOffset == typedGainMap.ByteOffset &&
                descriptor.SourceLength == typedGainMap.ByteLength;
            if (!isTypedGainMap)
            {
                Assert.True(ranges.Add((descriptor.SourceIndex, descriptor.SourceOffset, descriptor.SourceLength)));
            }
        }
    }
}

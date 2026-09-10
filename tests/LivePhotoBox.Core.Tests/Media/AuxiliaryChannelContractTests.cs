using System;
using System.Collections.Generic;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed unsafe class AuxiliaryChannelContractTests
{
    [Fact]
    [Trait("Category", "Extractor")]
    public void MultipleAuxiliaryItems_PreserveIndependentIdentityAndRelationship()
    {
        SourceMediaFacts facts = CreateFacts(
            CreateAuxiliary(1, 4096, 128, "Thumbnail", "aux:thumbnail", "thumb-rel"),
            CreateAuxiliary(2, 4224, 128, "Depth", "aux:depth", "depth-rel"));

        NativeSourceMediaFacts native = NativeMediaService.MapToNativeFacts(facts);

        Assert.Equal(2u, native.AuxiliaryCount);
        Assert.Equal(0, native.Auxiliary0.SourceIndex);
        Assert.Equal(0, native.Auxiliary1.SourceIndex);
        Assert.Equal(1u, native.Auxiliary0.ItemId);
        Assert.Equal(2u, native.Auxiliary1.ItemId);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void DuplicateStableIdentity_FailsClosedBeforeNativeMapping()
    {
        SourceMediaFacts facts = CreateFacts(
            CreateAuxiliary(1, 4096, 128, "Thumbnail", "aux:duplicate", "thumb-rel"),
            CreateAuxiliary(2, 4224, 128, "Depth", "aux:duplicate", "depth-rel"));

        ExtractionException error = Assert.Throws<ExtractionException>(
            () => NativeMediaService.MapToNativeFacts(facts));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, error.Category);
        Assert.Contains("stable identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void ReorderedGainMapRelationship_WithoutRebindingIndex_FailsClosed()
    {
        AuxiliaryMediaFacts gainMap = CreateAuxiliary(
            7,
            8192,
            256,
            "GainMap",
            "aux:gainmap",
            "urn:test:gainmap") with
        {
            Representation = AuxiliaryRepresentation.Materialized
        };
        AuxiliaryMediaFacts thumbnail = CreateAuxiliary(
            8,
            8448,
            128,
            "Thumbnail",
            "aux:thumbnail",
            "thumb-rel");

        SourceMediaFacts facts = CreateFacts(gainMap, thumbnail) with
        {
            GainMap = new GainMapFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                Representation = AuxiliaryRepresentation.Materialized,
                Ownership = AuxiliaryOwnership.Primary,
                OwnerArtifactRole = MediaArtifactKind.PrimaryImage,
                AuxiliaryIndex = 0,
                ItemId = gainMap.ItemId,
                ByteOffset = gainMap.ByteOffset,
                ByteLength = gainMap.ByteLength,
                Relationship = gainMap.Relationship
            }
        };

        SourceMediaFacts reordered = facts with
        {
            AuxiliaryItems = new[] { thumbnail, gainMap }
        };

        ExtractionException error = Assert.Throws<ExtractionException>(
            () => NativeMediaService.MapToNativeFacts(reordered));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, error.Category);
        Assert.Contains("GainMap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void GainMapRepresentationMismatch_FailsClosed()
    {
        AuxiliaryMediaFacts gainMap = CreateAuxiliary(
            9,
            8192,
            256,
            "GainMap",
            "aux:gainmap-representation",
            "urn:test:gainmap") with
        {
            Representation = AuxiliaryRepresentation.Embedded
        };

        SourceMediaFacts facts = CreateFacts(gainMap) with
        {
            GainMap = new GainMapFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                Representation = AuxiliaryRepresentation.Materialized,
                Ownership = AuxiliaryOwnership.Primary,
                OwnerArtifactRole = MediaArtifactKind.PrimaryImage,
                AuxiliaryIndex = 0,
                ItemId = gainMap.ItemId,
                ByteOffset = gainMap.ByteOffset,
                ByteLength = gainMap.ByteLength,
                Relationship = gainMap.Relationship
            }
        };

        ExtractionException error = Assert.Throws<ExtractionException>(
            () => NativeMediaService.MapToNativeFacts(facts));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, error.Category);
        Assert.Contains("GainMap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void AuxiliaryOwnerMismatch_FailsClosed()
    {
        AuxiliaryMediaFacts invalid = CreateAuxiliary(
            3,
            4096,
            128,
            "Depth",
            "aux:depth",
            "depth-rel") with
        {
            Ownership = AuxiliaryOwnership.Auxiliary,
            OwnerIdentity = "primary:0"
        };

        ExtractionException error = Assert.Throws<ExtractionException>(
            () => NativeMediaService.MapToNativeFacts(CreateFacts(invalid)));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, error.Category);
        Assert.Contains("owner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void EmbeddedAndMaterializedDuplicateSourceRelationship_FailsClosed()
    {
        AuxiliaryMediaFacts embedded = CreateAuxiliary(
            4,
            4096,
            128,
            "Thumbnail",
            "aux:embedded-thumbnail",
            "thumbnail-rel");
        AuxiliaryMediaFacts materialized = embedded with
        {
            ItemId = 5,
            StableIdentity = "aux:materialized-thumbnail",
            Representation = AuxiliaryRepresentation.Materialized
        };

        ExtractionException error = Assert.Throws<ExtractionException>(
            () => NativeMediaService.MapToNativeFacts(CreateFacts(embedded, materialized)));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, error.Category);
        Assert.Contains("source range", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceMediaFacts CreateFacts(params AuxiliaryMediaFacts[] auxiliaryItems) =>
        new()
        {
            Protocol = SourceProtocol.NonLive,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = 0,
                ByteLength = 4096
            },
            AuxiliaryItems = auxiliaryItems,
            PrimarySha256 = new string('1', 64)
        };

    private static AuxiliaryMediaFacts CreateAuxiliary(
        uint itemId,
        long offset,
        long length,
        string semantic,
        string stableIdentity,
        string relationship) =>
        new()
        {
            IsPresent = true,
            Container = ImageContainer.Jpeg,
            Representation = AuxiliaryRepresentation.Embedded,
            Ownership = AuxiliaryOwnership.Primary,
            ItemId = itemId,
            ByteOffset = offset,
            ByteLength = length,
            Relationship = relationship,
            StableIdentity = stableIdentity,
            Semantic = semantic,
            OwnerIdentity = "primary:0",
            Sha256 = itemId.ToString("X").PadLeft(64, '0'),
            Codec = AuxiliaryCodec.Jpeg,
            SourceIndex = 0
        };
}

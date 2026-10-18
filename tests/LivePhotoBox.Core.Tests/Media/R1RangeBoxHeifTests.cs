using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class R1RangeBoxHeifTests
{
    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CRg001_PartialIlocOverlap_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("RG001", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CRg002_DistinctItemsShareExtent_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("RG002", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CRg003_ChildCrossesParent_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("RG003", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CRg004_GoogleV1ImageVideoOverlap_FailsProtocolMalformed() =>
        AssertInspectionFailureAsync("RG004", SourceInspectionStage.Protocol, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx001_Size32BelowHeader_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("BX001", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx002_Size32BeyondParent_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("BX002", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx003_TruncatedExtendedHeader_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("BX003", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx004_ExtendedSizeBelowHeader_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("BX004", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx005_FinalZeroSizeMdat_IsAccepted() =>
        AssertAcceptedGoogleV1Async("BX005");

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx006_ParentChildSizeConflict_FailsContainerMalformed() =>
        AssertInspectionFailureAsync("BX006", SourceInspectionStage.Container, SourceInspectionFailureCategory.Malformed);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx007_ValidExtendedSize_IsAccepted() =>
        AssertAcceptedGoogleV1Async("BX007");

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CBx008_ValidOrdinarySize32_IsAccepted() =>
        AssertAcceptedGoogleV1Async("BX008");

    [Fact]
    [Trait("P7", "R1-C")]
    public async Task Inspect_R1CHf001_AdjacentMultiExtent_IsAccepted()
    {
        R1RangeBoxHeifFixture fixture = R1RangeBoxHeifFixtureFactory.Load("HF001");
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        string donorBefore = R1RangeBoxHeifFixtureFactory.CaptureDonorHash(fixture);
        SourceMediaFacts? facts = null;
        string? errorType = null;
        string? errorMessage = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(fixture.Path);
            facts = inspected.Facts;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().FullName;
            errorMessage = ex.Message;
        }

        R1RangeBoxHeifFixtureFactory.AssertInputsUnchanged(fixture, before, donorBefore);
        var item = facts?.AuxiliaryItems.SingleOrDefault(candidate => candidate.ItemId == 55);
        R1RangeBoxHeifFixtureFactory.WriteResult(fixture, new
        {
            caseId = fixture.Id,
            fixtureSha256 = before,
            donorSha256 = donorBefore,
            errorType,
            errorMessage,
            protocol = facts?.Protocol.ToString(),
            auxiliaryItemId = item?.ItemId,
            auxiliaryOffset = item?.ByteOffset,
            auxiliaryLength = item?.ByteLength,
            auxiliarySha256 = item?.Sha256,
            auxiliaryType = item?.ItemType,
            sourceUnchanged = true
        });

        Assert.Null(errorType);
        Assert.NotNull(facts);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoHeic, facts.Protocol);
        Assert.NotNull(item);
        Assert.True(item.IsPresent);
        Assert.Equal(fixture.Semantic.GetProperty("byteOffset").GetInt64(), item.ByteOffset);
        Assert.Equal(fixture.Semantic.GetProperty("byteLength").GetInt64(), item.ByteLength);
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(
            fixture.Semantic.GetProperty("sha256").GetString(), item.Sha256));
    }

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CHf002_ValidConstructionMethod_IsUnsupported() =>
        AssertInspectionFailureAsync("HF002", SourceInspectionStage.Container, SourceInspectionFailureCategory.Unsupported);

    [Fact]
    [Trait("P7", "R1-C")]
    public Task Inspect_R1CHf003_ValidDataReferenceIndex_IsUnsupported() =>
        AssertInspectionFailureAsync("HF003", SourceInspectionStage.Container, SourceInspectionFailureCategory.Unsupported);

    [Fact]
    [Trait("P7", "R1-C")]
    public async Task Inspect_R1CHf004_ValidGraphWithoutOptionalItem_ReportsAbsence()
    {
        R1RangeBoxHeifFixture fixture = R1RangeBoxHeifFixtureFactory.Load("HF004");
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        bool exifFound = NativeHeifBoxParser.TryLocateExifItem(bytes, out long exifOffset, out long exifLength, out string? exifError);
        bool xmpFound = NativeHeifBoxParser.TryLocateXmpItem(bytes, out long xmpOffset, out long xmpLength, out string? xmpError);
        SourceMediaFacts? facts = null;
        SourceInspectionException? inspectionError = null;
        string? otherErrorType = null;
        string? otherErrorMessage = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(fixture.Path);
            facts = inspected.Facts;
        }
        catch (SourceInspectionException ex)
        {
            inspectionError = ex;
        }
        catch (Exception ex)
        {
            otherErrorType = ex.GetType().FullName;
            otherErrorMessage = ex.Message;
        }

        R1RangeBoxHeifFixtureFactory.AssertInputsUnchanged(fixture, before, null);
        R1RangeBoxHeifFixtureFactory.WriteResult(fixture, new
        {
            caseId = fixture.Id,
            fixtureSha256 = before,
            exifFound,
            exifOffset,
            exifLength,
            exifError,
            xmpFound,
            xmpOffset,
            xmpLength,
            xmpError,
            inspectorProtocol = facts?.Protocol.ToString(),
            inspectorStage = inspectionError?.Stage.ToString(),
            inspectorCategory = inspectionError?.Category.ToString(),
            inspectorError = inspectionError?.Message,
            otherErrorType,
            otherErrorMessage,
            primaryImagePresent = facts?.PrimaryImage.IsPresent,
            primaryImageContainer = facts?.PrimaryImage.Container.ToString(),
            primaryOffset = facts?.PrimaryImage.ByteOffset,
            primaryLength = facts?.PrimaryImage.ByteLength,
            motionVideoPresent = facts?.MotionVideo is { IsPresent: true },
            gainMapPresent = facts?.GainMap is { IsPresent: true },
            auxiliaryItemCount = facts?.AuxiliaryItems.Count,
            auxiliaryItems = facts?.AuxiliaryItems.Select(item => new
            {
                item.ItemId,
                item.ItemType,
                item.Semantic,
                item.Relationship,
                item.ByteOffset,
                item.ByteLength,
                item.Sha256
            }).ToArray(),
            sourceUnchanged = true
        });

        Assert.False(exifFound);
        Assert.False(xmpFound);
        Assert.Contains("Confirmed absent", exifError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Confirmed absent", xmpError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(inspectionError);
        Assert.Null(otherErrorType);
        Assert.NotNull(facts);
        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);
        Assert.True(facts.PrimaryImage.IsPresent);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.True(facts.PrimaryImage.ByteOffset >= 0);
        Assert.True(facts.PrimaryImage.ByteLength > 0);
        Assert.True(checked(facts.PrimaryImage.ByteOffset + facts.PrimaryImage.ByteLength) <= bytes.LongLength);
        Assert.True(facts.MotionVideo is null || !facts.MotionVideo.IsPresent);
        Assert.True(facts.GainMap is null || !facts.GainMap.IsPresent);
        Assert.Empty(facts.AuxiliaryItems);
    }

    [Fact]
    [Trait("P7", "R1-C")]
    public async Task Inspect_R1CHf005_MalformedGraphNeverReportsAbsence()
    {
        R1RangeBoxHeifFixture fixture = R1RangeBoxHeifFixtureFactory.Load("HF005");
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        bool exifFound = NativeHeifBoxParser.TryLocateExifItem(bytes, out _, out _, out string? exifError);
        bool xmpFound = NativeHeifBoxParser.TryLocateXmpItem(bytes, out _, out _, out string? xmpError);
        SourceInspectionException? inspectionError = null;
        string? otherErrorType = null;
        string? otherErrorMessage = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(fixture.Path);
        }
        catch (SourceInspectionException ex)
        {
            inspectionError = ex;
        }
        catch (Exception ex)
        {
            otherErrorType = ex.GetType().FullName;
            otherErrorMessage = ex.Message;
        }

        R1RangeBoxHeifFixtureFactory.AssertInputsUnchanged(fixture, before, null);
        R1RangeBoxHeifFixtureFactory.WriteResult(fixture, new
        {
            caseId = fixture.Id,
            fixtureSha256 = before,
            exifFound,
            exifError,
            xmpFound,
            xmpError,
            stage = inspectionError?.Stage.ToString(),
            category = inspectionError?.Category.ToString(),
            inspectionError = inspectionError?.Message,
            otherErrorType,
            otherErrorMessage,
            sourceUnchanged = true
        });

        Assert.False(exifFound);
        Assert.False(xmpFound);
        Assert.DoesNotContain("Confirmed absent", exifError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confirmed absent", xmpError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(otherErrorType);
        Assert.NotNull(inspectionError);
        Assert.Equal(SourceInspectionStage.Container, inspectionError.Stage);
        Assert.Equal(SourceInspectionFailureCategory.Malformed, inspectionError.Category);
        await AssertConversionRejectedAsync(fixture);
    }

    private static async Task AssertInspectionFailureAsync(
        string caseId,
        SourceInspectionStage expectedStage,
        SourceInspectionFailureCategory expectedCategory)
    {
        R1RangeBoxHeifFixture fixture = R1RangeBoxHeifFixtureFactory.Load(caseId);
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        string donorBefore = R1RangeBoxHeifFixtureFactory.CaptureDonorHash(fixture);
        SourceInspectionException? inspectionError = null;
        string? otherErrorType = null;
        string? otherErrorMessage = null;
        SourceMediaFacts? unexpectedFacts = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(fixture.Path);
            unexpectedFacts = inspected.Facts;
        }
        catch (SourceInspectionException ex)
        {
            inspectionError = ex;
        }
        catch (Exception ex)
        {
            otherErrorType = ex.GetType().FullName;
            otherErrorMessage = ex.Message;
        }

        R1RangeBoxHeifFixtureFactory.AssertInputsUnchanged(fixture, before, donorBefore);
        R1RangeBoxHeifFixtureFactory.WriteResult(fixture, new
        {
            caseId,
            fixtureSha256 = before,
            donorSha256 = donorBefore,
            stage = inspectionError?.Stage.ToString(),
            category = inspectionError?.Category.ToString(),
            inspectionError = inspectionError?.Message,
            otherErrorType,
            otherErrorMessage,
            unexpectedProtocol = unexpectedFacts?.Protocol.ToString(),
            sourceUnchanged = true
        });

        Assert.Null(otherErrorType);
        Assert.Null(unexpectedFacts);
        Assert.NotNull(inspectionError);
        Assert.Equal(expectedStage, inspectionError.Stage);
        Assert.Equal(expectedCategory, inspectionError.Category);
        Assert.Equal(expectedStage.ToString(), fixture.ExpectedStage);
        Assert.Equal(expectedCategory.ToString(), fixture.ExpectedCategory);
        await AssertConversionRejectedAsync(fixture);
    }

    private static async Task AssertConversionRejectedAsync(R1RangeBoxHeifFixture fixture)
    {
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        using var workspace = new MediaWorkspace();
        ImageContainer sourceContainer = Path.GetExtension(fixture.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            ? ImageContainer.Jpeg : ImageContainer.Heic;
        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = fixture.Path, Kind = MediaArtifactKind.PrimaryImage,
                MimeType = sourceContainer == ImageContainer.Jpeg ? "image/jpeg" : "image/heic",
                ImageContainer = sourceContainer, ByteLength = new FileInfo(fixture.Path).Length
            },
            TargetContainer = sourceContainer == ImageContainer.Jpeg ? ImageContainer.Heic : ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory
        });
        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionFailureStage.SourceInspection, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.SourceInspection, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.RootDirectory, "*", SearchOption.AllDirectories));
        string after = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        Assert.Equal(before, after);
        string? root = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_R1C_RESULTS");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string directory = Path.Combine(root, "conversion-cases");
            Directory.CreateDirectory(directory);
            await using var output = new FileStream(Path.Combine(directory, fixture.Id + ".json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(output, new
            {
                caseId = fixture.Id, sourceSha256Before = before, sourceSha256After = after,
                conversionInvoked = true, result.Success, outputArtifact = result.OutputArtifact?.Path,
                failureStage = result.ExecutionRecord.Truth.FailureStage.ToString(),
                failureCategory = result.ExecutionRecord.Truth.FailureCategory.ToString(),
                outputEntries = Directory.GetFileSystemEntries(workspace.RootDirectory, "*", SearchOption.AllDirectories)
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static async Task AssertAcceptedGoogleV1Async(string caseId)
    {
        R1RangeBoxHeifFixture fixture = R1RangeBoxHeifFixtureFactory.Load(caseId);
        string before = R1RangeBoxHeifFixtureFactory.HashFile(fixture.Path);
        string donorBefore = R1RangeBoxHeifFixtureFactory.CaptureDonorHash(fixture);
        SourceMediaFacts? facts = null;
        string? errorType = null;
        string? errorMessage = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(fixture.Path);
            facts = inspected.Facts;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().FullName;
            errorMessage = ex.Message;
        }

        R1RangeBoxHeifFixtureFactory.AssertInputsUnchanged(fixture, before, donorBefore);
        R1RangeBoxHeifFixtureFactory.WriteResult(fixture, new
        {
            caseId,
            fixtureSha256 = before,
            donorSha256 = donorBefore,
            errorType,
            errorMessage,
            protocol = facts?.Protocol.ToString(),
            primaryOffset = facts?.PrimaryImage.ByteOffset,
            primaryLength = facts?.PrimaryImage.ByteLength,
            motionOffset = facts?.MotionVideo?.ByteOffset,
            motionLength = facts?.MotionVideo?.ByteLength,
            sourceUnchanged = true
        });

        Assert.Null(errorType);
        Assert.NotNull(facts);
        Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);
        Assert.True(facts.MotionVideo is { IsPresent: true });
        Assert.Equal(fixture.Semantic.GetProperty("videoRangeStart").GetInt64(), facts.MotionVideo!.ByteOffset);
        Assert.Equal(fixture.Semantic.GetProperty("videoRangeLength").GetInt64(), facts.MotionVideo.ByteLength);
    }
}

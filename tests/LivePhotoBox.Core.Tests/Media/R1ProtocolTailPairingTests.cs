using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class R1ProtocolTailPairingTests
{
    private const string EvidenceDirectoryVariable = "LPB_PLAN137_EVIDENCE_DIR";
    private const string AppleIdentifier = "12345678-ABCD-1234-ABCD-1234567890AB";
    private const string VivoIdentifierTail = "vivo{\"com.android.camera.livephoto\":\"synthetic-vivo-id\"}cameralbum!";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITestOutputHelper _output;

    public R1ProtocolTailPairingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task SEFD1_CorruptFooterTotalSize_FailsClosed()
    {
        byte[] canonical = CreateSamsungSefBytes();
        byte[] derived = CorruptSamsungFooterTotalSize(CreateSamsungSefBytes(), out int offset, out uint before, out uint after);
        byte[] replay = CorruptSamsungFooterTotalSize(CreateSamsungSefBytes(), out int replayOffset, out uint replayBefore, out uint replayAfter);
        string mutation = $"SEFT.totalSize LE32 offset={offset} original={before} value={after}";
        Assert.Equal(offset, replayOffset);
        Assert.Equal(before, replayBefore);
        Assert.Equal(after, replayAfter);

        return AssertInspectionRejectedAsync(
            "SEF-D1",
            [new InputVariant("primary", ".jpg", canonical, derived, replay)],
            mutation,
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Protocol,
            NativeRuntime.SamsungJpegCapability,
            paths => new SourceInspector().InspectAsync(paths[0]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task SEFD2_SefDirectoryRangeOutsideFile_FailsClosed()
    {
        byte[] canonical = CreateSamsungSefBytes();
        byte[] derived = CorruptSamsungDirectoryOffset(CreateSamsungSefBytes(), out int offset, out uint before, out uint after);
        byte[] replay = CorruptSamsungDirectoryOffset(CreateSamsungSefBytes(), out int replayOffset, out uint replayBefore, out uint replayAfter);
        string mutation = $"SEFH.entry[0].payloadOffset LE32 offset={offset} original={before} value={after}";
        Assert.Equal(offset, replayOffset);
        Assert.Equal(before, replayBefore);
        Assert.Equal(after, replayAfter);

        return AssertInspectionRejectedAsync(
            "SEF-D2",
            [new InputVariant("primary", ".jpg", canonical, derived, replay)],
            mutation,
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Protocol,
            NativeRuntime.SamsungJpegCapability,
            paths => new SourceInspector().InspectAsync(paths[0]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task SEFD3_DuplicateMotionPhotoDirectoryEntry_FailsClosed()
    {
        byte[] canonical = CreateSamsungSefBytes();
        byte[] derived = DuplicateSamsungMotionEntry(CreateSamsungSefBytes(), out int entryOffset, out uint originalCount, out uint replacementCount, out uint originalTotalSize, out uint replacementTotalSize);
        byte[] replay = DuplicateSamsungMotionEntry(CreateSamsungSefBytes(), out int replayEntryOffset, out uint replayOriginalCount, out uint replayReplacementCount, out uint replayOriginalTotalSize, out uint replayReplacementTotalSize);
        Assert.Equal(entryOffset, replayEntryOffset);
        Assert.Equal(originalCount, replayOriginalCount);
        Assert.Equal(replacementCount, replayReplacementCount);
        Assert.Equal(originalTotalSize, replayOriginalTotalSize);
        Assert.Equal(replacementTotalSize, replayReplacementTotalSize);

        return AssertInspectionRejectedAsync(
            "SEF-D3",
            [new InputVariant("primary", ".jpg", canonical, derived, replay)],
            $"duplicate the 0x0A30 MotionPhoto_Data directory row at offset={entryOffset}; SEFH.entry_count {originalCount}->{replacementCount}; SEFT.totalSize {originalTotalSize}->{replacementTotalSize}",
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Protocol,
            NativeRuntime.SamsungJpegCapability,
            paths => new SourceInspector().InspectAsync(paths[0]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task SEFD4_OtherMarkerAndTrailingDirectoryBytes_FailsClosed()
    {
        byte[] canonical = CreateSamsungSefBytes();
        byte[] derived = InsertSamsungSefDirectoryTail(CreateSamsungSefBytes(), [0xDE, 0xAD, 0xBE, 0xEF], out int offset, out uint originalTotalSize, out uint replacementTotalSize);
        byte[] replay = InsertSamsungSefDirectoryTail(CreateSamsungSefBytes(), [0xDE, 0xAD, 0xBE, 0xEF], out int replayOffset, out uint replayOriginalTotalSize, out uint replayReplacementTotalSize);
        Assert.Equal(offset, replayOffset);
        Assert.Equal(originalTotalSize, replayOriginalTotalSize);
        Assert.Equal(replacementTotalSize, replayReplacementTotalSize);

        return AssertInspectionRejectedAsync(
            "SEF-D4",
            [new InputVariant("primary", ".jpg", canonical, derived, replay)],
            $"insert unowned trailing bytes DE AD BE EF after the SEFH entry table at offset={offset}, before SEFT; the canonical two-entry directory includes the non-live 0x0A01 DualCam_Data marker; totalSize {originalTotalSize}->{replacementTotalSize} while entry_count remains unchanged",
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Protocol,
            NativeRuntime.SamsungJpegCapability,
            paths => new SourceInspector().InspectAsync(paths[0]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public async Task SEFC1_ValidSamsungSef_ExtractsExactMotionPayload()
    {
        byte[] canonical = CreateSamsungSefBytes();
        byte[] replay = CreateSamsungSefBytes();
        Assert.Equal(canonical, replay);

        using var workspace = new MediaWorkspace();
        string input = WriteInput(workspace, "sef-c1", ".jpg", canonical);
        FileSnapshot[] before = await CaptureAsync([input]);
        SourceMediaFacts? facts = null;
        ExtractedMediaBundle? extracted = null;
        bool primaryExtractionMatches = false;
        byte[]? extractedVideo = null;
        byte[]? extractedPrimary = null;
        Exception? unexpected = null;
        try
        {
            using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(input);
            facts = inspected.Facts;
            extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan, input, secondaryPath: null, workspace);
            Assert.NotNull(extracted.MotionVideo);
            extractedVideo = await File.ReadAllBytesAsync(extracted.MotionVideo.Path);
            byte[] source = await File.ReadAllBytesAsync(input);
            extractedPrimary = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
            primaryExtractionMatches = extractedPrimary.AsSpan().SequenceEqual(source.AsSpan(
                checked((int)facts.PrimaryImage.ByteOffset), checked((int)facts.PrimaryImage.ByteLength)));
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        FileSnapshot[] after = await CaptureAsync([input]);
        byte[] expectedVideo = SyntheticProtocolFixtures.CreateMinimalMp4();
        byte[] sourceBytes = await File.ReadAllBytesAsync(input);
        byte[]? actualVideo = null;
        long videoOffset = -1;
        long videoLength = -1;
        if (facts?.MotionVideo is { IsPresent: true } video && video.ByteOffset >= 0 &&
            video.ByteLength > 0 && video.ByteOffset + video.ByteLength <= sourceBytes.LongLength)
        {
            videoOffset = video.ByteOffset;
            videoLength = video.ByteLength;
            actualVideo = sourceBytes.AsSpan(checked((int)video.ByteOffset), checked((int)video.ByteLength)).ToArray();
        }

        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        string[] declaredWorkspaceFiles = new[]
        {
            input, extracted?.PrimaryImage.Path, extracted?.MotionVideo?.Path,
            extracted?.CleanupSource?.Path, extracted?.GainMap?.Path
        }.Where(path => path is not null).Select(path => Path.GetFileName(path)!)
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string? retainedExtractedVideo = extractedVideo is null ? null : RetainInputVariant("SEF-C1",
            new InputVariant("extracted-motion", ".mp4", expectedVideo, extractedVideo, expectedVideo), "derived", extractedVideo);
        string? retainedExtractedPrimary = extractedPrimary is null ? null : RetainInputVariant("SEF-C1",
            new InputVariant("extracted-primary", ".jpg", extractedPrimary, extractedPrimary, extractedPrimary), "derived", extractedPrimary);
        byte[]? cleanupSource = extracted?.CleanupSource is { } carrier
            ? await File.ReadAllBytesAsync(carrier.Path) : null;
        string? retainedCleanupSource = cleanupSource is null ? null : RetainInputVariant("SEF-C1",
            new InputVariant("cleanup-source", ".jpg", canonical, cleanupSource, canonical), "derived", cleanupSource);
        extracted?.CleanupPlan?.Dispose();
        bool outputPublished = File.Exists(Path.Combine(workspace.RootDirectory, "SEF-C1-published.bin"));
        WriteEvidence("SEF-C1", new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId = "SEF-C1",
            evidenceClass = "Synthetic",
            mutationId = "none-valid-control",
            inputs = DescribeInputs("SEF-C1", [new InputVariant("primary", ".jpg", canonical, canonical, replay)], [input]),
            baseSha256 = Sha256(canonical),
            derivedSha256 = Sha256(canonical),
            replaySha256 = Sha256(replay),
            derivedEqualsReplay = canonical.AsSpan().SequenceEqual(replay),
            expectedProtocol = SourceProtocol.SamsungMotionPhotoJpeg,
            actualProtocol = facts?.Protocol,
            expectedVideoSha256 = Sha256(expectedVideo),
            actualVideoSha256 = actualVideo is null ? null : Sha256(actualVideo),
            extractedVideo = new
            {
                path = extracted?.MotionVideo is null ? null : Path.GetFileName(extracted.MotionVideo.Path),
                actualBytes = extractedVideo?.LongLength,
                actualSha256 = extractedVideo is null ? null : Sha256(extractedVideo),
                retainedPath = retainedExtractedVideo,
                inspectorIssuedPlanUsed = extracted is not null,
                primaryExtractionMatches
            },
            extractedPrimary = new
            {
                path = extracted is null ? null : Path.GetFileName(extracted.PrimaryImage.Path),
                retainedPath = retainedExtractedPrimary
            },
            extractedCleanupSource = cleanupSource is null ? null : new
            {
                path = Path.GetFileName(extracted!.CleanupSource!.Path),
                retainedPath = retainedCleanupSource
            },
            exactMotionPayload = actualVideo is not null && expectedVideo.AsSpan().SequenceEqual(actualVideo),
            videoOffset,
            videoLength,
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles,
            declaredWorkspaceFiles,
            outputPublished
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(facts);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoJpeg, facts.Protocol);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.MotionVideo.IsPresent);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
        Assert.NotNull(actualVideo);
        Assert.Equal(expectedVideo, actualVideo);
        Assert.NotNull(extractedVideo);
        Assert.Equal(expectedVideo, extractedVideo);
        Assert.True(primaryExtractionMatches);
        Assert.NotNull(extracted?.CleanupPlan);
        Assert.Equal(before, after);
        Assert.Equal(declaredWorkspaceFiles, workspaceFiles);
        Assert.False(outputPublished);
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task PAIRD1_ApplePairIdentifiersDisagree_RejectsAtPairing()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] canonicalVideo = CreateAppleMovBytes();
        byte[] derivedVideo = ReplaceAppleVideoIdentifier((byte[])canonicalVideo.Clone(), out int offset, out byte before, out byte after);
        byte[] replayVideo = ReplaceAppleVideoIdentifier(CreateAppleMovBytes(), out int replayOffset, out byte replayBefore, out byte replayAfter);
        string mutation = $"Apple ContentIdentifier value offset={offset + Encoding.ASCII.GetByteCount(AppleIdentifier) - 1} original={before:X2} value={after:X2}";
        Assert.Equal(offset, replayOffset);
        Assert.Equal(before, replayBefore);
        Assert.Equal(after, replayAfter);
        Assert.False(canonicalVideo.AsSpan().SequenceEqual(derivedVideo), "Canonical MOV bytes must differ from the mutated replay input.");
        Assert.True(derivedVideo.AsSpan().SequenceEqual(replayVideo), "The mutated MOV must replay byte-for-byte.");

        return AssertInspectionRejectedAsync(
            "PAIR-D1",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("secondary-video", ".mov", canonicalVideo, derivedVideo, replayVideo)
            ],
            mutation,
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Pairing,
            NativeRuntime.AppleCapability,
            paths => new SourceInspector().InspectAsync(paths[0], paths[1]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task PAIRD2_DuplicateAppleVideoCandidates_NeverSelectsFirstMatch()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] video = CreateAppleMovBytes();
        Assert.Equal(video, CreateAppleMovBytes());
        return AssertMatcherRejectedAsync(
            "PAIR-D2",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("candidate-video-a", ".mov", video, video, video),
                new InputVariant("candidate-video-b", ".mov", video, video, video)
            ],
            "Two distinct candidate paths expose the same Apple ContentIdentifier; candidate order is fixed and neither may win by first-match precedence.",
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Pairing,
            NativeRuntime.AppleCapability,
            paths => LivePhotoMetadataMatcher.MatchAsync([paths[0]], [paths[1], paths[2]]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task PAIRD3_ShadowMakerNoteOwner_FailsClosed()
    {
        byte[] canonical = CreateAppleJpegBytes();
        byte[] derived = CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleJpegWithShadowMakerNote, "pair-d3-derived", ".jpg");
        byte[] replay = CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleJpegWithShadowMakerNote, "pair-d3-replay", ".jpg");
        PairD3StructureProof structureProof = BuildPairD3StructureProof(canonical, derived, replay);
        WriteEvidence("PAIR-D3-STRUCTURE", structureProof);
        Assert.True(structureProof.StructurallyValid, JsonSerializer.Serialize(structureProof, EvidenceJsonOptions));

        return AssertInspectionRejectedAsync(
            "PAIR-D3",
            [new InputVariant("primary-image", ".jpg", canonical, derived, replay)],
            "duplicate the formal Apple Exif MakerNote owner while both entries point at the same Apple payload",
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Pairing,
            NativeRuntime.AppleCapability,
            paths => new SourceInspector().InspectAsync(paths[0]),
            structureProof);
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task PAIRC1_OneAppleImageAndVideo_MatchesUniquely()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] video = CreateAppleMovBytes();
        return AssertMatcherAcceptedAsync(
            "PAIR-C1",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("secondary-video", ".mov", video, video, video)
            ],
            "one unambiguous Apple ContentIdentifier pair",
            paths => LivePhotoMetadataMatcher.MatchAsync([paths[0]], [paths[1]]),
            MatchSource.ContentIdentifier,
            expectedProtocol: SourceProtocol.AppleLivePhoto);
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task XPROTOD1_VivoLegacyIdentity_IsNotAppleContentIdentifierOwnership()
    {
        byte[] image = CreateVivoLegacyImageBytes();
        byte[] video = CreateVivoLegacyVideoBytes();
        return AssertMatcherRejectedAsync(
            "XPROTO-D1",
            [
                new InputVariant("vivo-primary-image", ".jpg", image, image, image),
                new InputVariant("vivo-secondary-video", ".mp4", video, video, video)
            ],
            "offer the same Vivo legacy ID to generic Apple ContentIdentifier matching; generic matcher must reject the typed Vivo candidate rather than return an Apple pair",
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Pairing,
            NativeRuntime.VivoLegacyCapability,
            paths => LivePhotoMetadataMatcher.MatchAsync([paths[0]], [paths[1]]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task XPROTOD2_AppleAndVivoOwnersOnOneImage_FailsClosedWithoutPrecedence()
    {
        byte[] canonical = CreateAppleJpegBytes();
        byte[] derived = [.. canonical, .. Encoding.ASCII.GetBytes(VivoIdentifierTail)];
        byte[] replay = [.. CreateAppleJpegBytes(), .. Encoding.ASCII.GetBytes(VivoIdentifierTail)];
        return AssertInspectionRejectedAsync(
            "XPROTO-D2",
            [new InputVariant("primary-image", ".jpg", canonical, derived, replay)],
            "append a structurally recognized Vivo legacy identifier after JPEG EOI while retaining the formally owned Apple MakerNote ContentIdentifier",
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Pairing,
            NativeRuntime.VivoLegacyCapability,
            paths => new SourceInspector().InspectAsync(paths[0]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public async Task XPROTOC1_VivoSpecificMatcher_AcceptsValidLegacyPair()
    {
        byte[] image = CreateVivoLegacyImageBytes();
        byte[] video = CreateVivoLegacyVideoBytes();
        Assert.Equal(image, CreateVivoLegacyImageBytes());
        Assert.Equal(video, CreateVivoLegacyVideoBytes());

        using var workspace = new MediaWorkspace();
        string imagePath = WriteInput(workspace, "xproto-c1-image", ".jpg", image);
        string videoPath = WriteInput(workspace, "xproto-c1-video", ".mp4", video);
        string[] paths = [imagePath, videoPath];
        FileSnapshot[] before = await CaptureAsync(paths);
        MetadataMatchOutput? result = null;
        Exception? unexpected = null;
        try
        {
            result = LivePhotoMetadataMatcher.MatchVivo([imagePath], [videoPath]);
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        FileSnapshot[] after = await CaptureAsync(paths);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        bool outputPublished = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories).Length != paths.Length;
        WriteEvidence("XPROTO-C1", new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId = "XPROTO-C1",
            evidenceClass = "Synthetic",
            mutationId = "none-valid-vivo-control",
            inputs = DescribeInputs(
                "XPROTO-C1",
                [
                    new InputVariant("vivo-primary-image", ".jpg", image, image, image),
                    new InputVariant("vivo-secondary-video", ".mp4", video, video, video)
                ], paths),
            expectedProtocol = SourceProtocol.VivoLegacyDualFile,
            expectedMatcherSource = MatchSource.VivoLivePhoto,
            actualPairCount = result?.Pairs.Count,
            actualMatcherSource = result?.Pairs.SingleOrDefault()?.Source,
            remainingImages = result?.RemainingImages,
            remainingVideos = result?.RemainingVideos,
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles,
            outputPublished
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(result);
        Assert.Single(result.Pairs);
        Assert.Equal(MatchSource.VivoLivePhoto, result.Pairs[0].Source);
        Assert.Equal(imagePath, result.Pairs[0].ImagePath);
        Assert.Equal(videoPath, result.Pairs[0].VideoPath);
        Assert.Equal(0, result.RemainingImages);
        Assert.Equal(0, result.RemainingVideos);
        Assert.Equal(before, after);
        Assert.Equal(ExpectedWorkspaceNames(paths), workspaceFiles);
        Assert.False(outputPublished);
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task TAILD1_AppleMovTrailingGarbage_FailsExactContainerConsumption()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] canonicalVideo = CreateAppleMovBytes();
        byte[] derivedVideo = CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleCorruptDualMov, "tail-d1-derived", ".mov");
        byte[] replayVideo = CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleCorruptDualMov, "tail-d1-replay", ".mov");
        return AssertInspectionRejectedAsync(
            "TAIL-D1",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("secondary-video", ".mov", canonicalVideo, derivedVideo, replayVideo)
            ],
            "append deterministic bytes 01 02 03 04 after the last declared MOV box",
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Container,
            NativeRuntime.AppleCapability,
            paths => new SourceInspector().InspectAsync(paths[0], paths[1]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task TAILD2_AppleMovDeclaredBoxRangePastFile_FailsClosed()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] canonicalVideo = CreateAppleMovBytes();
        byte[] derivedVideo = IncreaseTopLevelBoxSize(CreateAppleMovBytes(), "moov", out int offset, out uint before, out uint after);
        byte[] replayVideo = IncreaseTopLevelBoxSize(CreateAppleMovBytes(), "moov", out int replayOffset, out uint replayBefore, out uint replayAfter);
        string mutation = $"top-level moov declared size BE32 offset={offset} original={before} value={after}";
        Assert.Equal(offset, replayOffset);
        Assert.Equal(before, replayBefore);
        Assert.Equal(after, replayAfter);

        return AssertInspectionRejectedAsync(
            "TAIL-D2",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("secondary-video", ".mov", canonicalVideo, derivedVideo, replayVideo)
            ],
            mutation,
            SourceInspectionFailureCategory.Malformed,
            SourceInspectionStage.Container,
            NativeRuntime.AppleCapability,
            paths => new SourceInspector().InspectAsync(paths[0], paths[1]));
    }

    [Fact]
    [Trait("P7", "R1-D")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task TAILC1_ValidApplePairWithoutCorruptTail_RemainsAccepted()
    {
        byte[] image = CreateAppleJpegBytes();
        byte[] video = CreateAppleMovBytes();
        return AssertInspectionAcceptedAsync(
            "TAIL-C1",
            [
                new InputVariant("primary-image", ".jpg", image, image, image),
                new InputVariant("secondary-video", ".mov", video, video, video)
            ],
            "same Apple MOV with exact declared-box consumption and no extra tail bytes",
            paths => new SourceInspector().InspectAsync(paths[0], paths[1]),
            SourceProtocol.AppleLivePhoto);
    }

    private async Task AssertInspectionRejectedAsync(
        string matrixId,
        IReadOnlyList<InputVariant> inputs,
        string mutation,
        SourceInspectionFailureCategory expectedCategory,
        SourceInspectionStage expectedStage,
        ulong expectedCapability,
        Func<string[], Task<SourceMediaFacts>> inspect,
        object? preInspectionEvidence = null)
    {
        Assert.NotEmpty(inputs);
        Assert.All(inputs, input => Assert.Equal(input.Derived, input.Replay));
        Assert.All(inputs, input => Assert.Equal(Sha256(input.Derived), Sha256(input.Replay)));

        using var workspace = new MediaWorkspace();
        string[] paths = inputs.Select(input => WriteInput(workspace, matrixId + "-" + input.Role, input.Extension, input.Derived)).ToArray();
        FileSnapshot[] before = await CaptureAsync(paths);
        SourceInspectionException? error = null;
        SourceMediaFacts? facts = null;
        Exception? unexpected = null;
        try
        {
            facts = await inspect(paths);
        }
        catch (SourceInspectionException ex)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        if (inputs.Count == 1 && inputs[0].Extension == ".jpg")
        {
            ImageConversionResult conversion = await new ImageConverter().ConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = paths[0], Kind = MediaArtifactKind.PrimaryImage, MimeType = "image/jpeg",
                    ImageContainer = ImageContainer.Jpeg, ByteLength = inputs[0].Derived.LongLength
                },
                TargetContainer = ImageContainer.Heic, TargetDirectory = workspace.RootDirectory
            });
            Assert.False(conversion.Success);
            Assert.Null(conversion.OutputArtifact);
            Assert.Equal(ConversionFailureStage.SourceInspection, conversion.ExecutionRecord.Truth.FailureStage);
            Assert.Equal(ConversionFailureCategory.SourceInspection, conversion.ExecutionRecord.Truth.FailureCategory);
            string? evidenceRoot = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                string conversionRoot = Path.Combine(evidenceRoot, "conversion-cases");
                Directory.CreateDirectory(conversionRoot);
                await using var output = new FileStream(Path.Combine(conversionRoot, matrixId + ".json"), FileMode.CreateNew);
                await JsonSerializer.SerializeAsync(output, new
                {
                    matrixId, conversionInvoked = true, conversion.Success,
                    failureStage = conversion.ExecutionRecord.Truth.FailureStage.ToString(),
                    failureCategory = conversion.ExecutionRecord.Truth.FailureCategory.ToString(),
                    outputArtifact = conversion.OutputArtifact?.Path,
                    workspaceFiles = GetWorkspaceFileNames(workspace)
                }, EvidenceJsonOptions);
            }
        }

        FileSnapshot[] after = await CaptureAsync(paths);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        bool outputPublished = File.Exists(Path.Combine(workspace.RootDirectory, matrixId + "-published.bin"));
        WriteEvidence(matrixId, new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId,
            evidenceClass = "Synthetic",
            mutation,
            inputs = DescribeInputs(matrixId, inputs, paths),
            preInspectionEvidence,
            expected = new { outcome = "Rejected", category = expectedCategory, stage = expectedStage, capability = expectedCapability },
            actual = new { outcome = error is not null ? "Rejected" : unexpected is not null ? "UnexpectedException" : "Accepted", category = error?.Category, stage = error?.Stage, capability = error?.Capability, protocol = facts?.Protocol, reason = error?.Message ?? unexpected?.GetType().FullName },
            pairReturned = false,
            guessedPairReturned = false,
            outputPublished,
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(error);
        Assert.Null(facts);
        Assert.Equal(expectedCategory, error.Category);
        Assert.Equal(expectedStage, error.Stage);
        Assert.Equal(expectedCapability, error.Capability);
        Assert.Equal(before, after);
        Assert.Equal(ExpectedWorkspaceNames(paths), workspaceFiles);
        Assert.False(outputPublished);
    }

    private async Task AssertMatcherRejectedAsync(
        string matrixId,
        IReadOnlyList<InputVariant> inputs,
        string mutation,
        SourceInspectionFailureCategory expectedCategory,
        SourceInspectionStage expectedStage,
        ulong expectedCapability,
        Func<string[], Task<MetadataMatchOutput>> match)
    {
        Assert.NotEmpty(inputs);
        Assert.All(inputs, input => Assert.Equal(input.Derived, input.Replay));
        Assert.All(inputs, input => Assert.Equal(Sha256(input.Derived), Sha256(input.Replay)));

        using var workspace = new MediaWorkspace();
        string[] paths = inputs.Select(input => WriteInput(workspace, matrixId + "-" + input.Role, input.Extension, input.Derived)).ToArray();
        FileSnapshot[] before = await CaptureAsync(paths);
        SourceInspectionException? error = null;
        MetadataMatchOutput? output = null;
        Exception? unexpected = null;
        try
        {
            output = await match(paths);
        }
        catch (SourceInspectionException ex)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        FileSnapshot[] after = await CaptureAsync(paths);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        bool outputPublished = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories).Length != paths.Length;
        WriteEvidence(matrixId, new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId,
            evidenceClass = "Synthetic",
            mutation,
            inputs = DescribeInputs(matrixId, inputs, paths),
            expected = new { outcome = "Rejected", category = expectedCategory, stage = expectedStage, capability = expectedCapability },
            actual = new { outcome = error is not null ? "Rejected" : unexpected is not null ? "UnexpectedException" : output is not null ? "ReturnedMatchOutput" : "NoResult", category = error?.Category, stage = error?.Stage, capability = error?.Capability, pairCount = output?.Pairs.Count, pairSources = output?.Pairs.Select(pair => pair.Source).ToArray(), reason = error?.Message ?? unexpected?.GetType().FullName },
            pairReturned = output is not null,
            guessedPairReturned = output?.Pairs.Count > 0,
            outputPublished,
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(error);
        Assert.Null(output);
        Assert.Equal(expectedCategory, error.Category);
        Assert.Equal(expectedStage, error.Stage);
        Assert.Equal(expectedCapability, error.Capability);
        Assert.Equal(before, after);
        Assert.Equal(ExpectedWorkspaceNames(paths), workspaceFiles);
        Assert.False(outputPublished);
    }

    private async Task AssertInspectionAcceptedAsync(
        string matrixId,
        IReadOnlyList<InputVariant> inputs,
        string mutation,
        Func<string[], Task<SourceMediaFacts>> inspect,
        SourceProtocol expectedProtocol)
    {
        Assert.All(inputs, input => Assert.Equal(input.Derived, input.Replay));
        using var workspace = new MediaWorkspace();
        string[] paths = inputs.Select(input => WriteInput(workspace, matrixId + "-" + input.Role, input.Extension, input.Derived)).ToArray();
        FileSnapshot[] before = await CaptureAsync(paths);
        SourceMediaFacts? facts = null;
        Exception? unexpected = null;
        try
        {
            facts = await inspect(paths);
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        FileSnapshot[] after = await CaptureAsync(paths);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        bool outputPublished = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories).Length != paths.Length;
        WriteEvidence(matrixId, new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId,
            evidenceClass = "Synthetic",
            mutation,
            inputs = DescribeInputs(matrixId, inputs, paths),
            expectedProtocol,
            actualProtocol = facts?.Protocol,
            expectedCategory = "none",
            actualCategory = "none",
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles,
            outputPublished,
            unexpectedException = unexpected?.GetType().FullName
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(facts);
        Assert.Equal(expectedProtocol, facts.Protocol);
        Assert.Equal(before, after);
        Assert.Equal(ExpectedWorkspaceNames(paths), workspaceFiles);
        Assert.False(outputPublished);
    }

    private async Task AssertMatcherAcceptedAsync(
        string matrixId,
        IReadOnlyList<InputVariant> inputs,
        string mutation,
        Func<string[], Task<MetadataMatchOutput>> match,
        MatchSource expectedSource,
        SourceProtocol expectedProtocol)
    {
        Assert.All(inputs, input => Assert.Equal(input.Derived, input.Replay));
        using var workspace = new MediaWorkspace();
        string[] paths = inputs.Select(input => WriteInput(workspace, matrixId + "-" + input.Role, input.Extension, input.Derived)).ToArray();
        FileSnapshot[] before = await CaptureAsync(paths);
        MetadataMatchOutput? output = null;
        Exception? unexpected = null;
        try
        {
            output = await match(paths);
        }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        FileSnapshot[] after = await CaptureAsync(paths);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);
        bool outputPublished = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories).Length != paths.Length;
        WriteEvidence(matrixId, new
        {
            schema = "PLAN137-R1D-Case-v1",
            matrixId,
            evidenceClass = "Synthetic",
            mutation,
            inputs = DescribeInputs(matrixId, inputs, paths),
            expectedProtocol,
            actualProtocol = output?.Pairs.SingleOrDefault()?.Source == expectedSource ? expectedProtocol : (SourceProtocol?)null,
            expectedMatcherSource = expectedSource,
            actualMatcherSources = output?.Pairs.Select(pair => pair.Source).ToArray(),
            pairCount = output?.Pairs.Count,
            remainingImages = output?.RemainingImages,
            remainingVideos = output?.RemainingVideos,
            sourceBefore = before,
            sourceAfter = after,
            workspaceFiles,
            outputPublished,
            unexpectedException = unexpected?.GetType().FullName
        });

        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        Assert.NotNull(output);
        Assert.Single(output.Pairs);
        Assert.Equal(expectedSource, output.Pairs[0].Source);
        Assert.Equal(paths[0], output.Pairs[0].ImagePath);
        Assert.Equal(paths[1], output.Pairs[0].VideoPath);
        Assert.Equal(0, output.RemainingImages);
        Assert.Equal(0, output.RemainingVideos);
        Assert.Equal(before, after);
        Assert.Equal(ExpectedWorkspaceNames(paths), workspaceFiles);
        Assert.False(outputPublished);
    }

    private static byte[] CreateSamsungSefBytes() =>
        CreateFixtureBytes(path => SyntheticProtocolFixtures.CreateSamsungSefJpeg(path), "plan136-sef", ".jpg");

    private static byte[] CreateAppleJpegBytes() =>
        CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleJpeg, "plan136-apple", ".jpg");

    private static byte[] CreateAppleMovBytes() =>
        CreateFixtureBytes(SyntheticProtocolFixtures.CreateAppleMov, "plan136-apple", ".mov");

    private static byte[] CreateVivoLegacyImageBytes() =>
        CreateFixtureBytes(SyntheticProtocolFixtures.CreateVivoLegacyDualJpeg, "plan136-vivo", ".jpg");

    private static byte[] CreateVivoLegacyVideoBytes() =>
        CreateFixtureBytes(SyntheticProtocolFixtures.CreateVivoLegacyDualMp4, "plan136-vivo", ".mp4");

    private static byte[] CreateFixtureBytes(Action<string> create, string prefix, string extension)
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath(prefix, extension);
        create(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] DuplicateSamsungMotionEntry(
        byte[] bytes,
        out int entryOffset,
        out uint originalCount,
        out uint replacementCount,
        out uint originalTotalSize,
        out uint replacementTotalSize)
    {
        SamsungSefDirectoryLayout layout = ReadSamsungSefDirectoryLayout(bytes);
        int motionEntryOffset = -1;
        for (int index = 0; index < layout.EntryCount; index++)
        {
            int candidate = checked(layout.DirectoryOffset + 12 + index * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(candidate + 2, 2)) != 0x0A30) continue;
            Assert.Equal(-1, motionEntryOffset);
            motionEntryOffset = candidate;
        }
        Assert.True(motionEntryOffset >= 0, "Canonical Samsung SEF directory must own one MotionPhoto_Data entry.");

        entryOffset = layout.FooterOffset;
        var result = new byte[checked(bytes.Length + 12)];
        bytes.AsSpan(0, entryOffset).CopyTo(result);
        bytes.AsSpan(motionEntryOffset, 12).CopyTo(result.AsSpan(entryOffset, 12));
        bytes.AsSpan(entryOffset).CopyTo(result.AsSpan(entryOffset + 12));

        originalCount = layout.EntryCount;
        replacementCount = checked(originalCount + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(layout.DirectoryOffset + 8, 4), replacementCount);
        int newFooterOffset = checked(layout.FooterOffset + 12);
        originalTotalSize = layout.TotalSize;
        replacementTotalSize = checked(originalTotalSize + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(newFooterOffset, 4), replacementTotalSize);
        return result;
    }

    private static byte[] InsertSamsungSefDirectoryTail(
        byte[] bytes,
        byte[] trailingBytes,
        out int offset,
        out uint originalTotalSize,
        out uint replacementTotalSize)
    {
        Assert.NotEmpty(trailingBytes);
        SamsungSefDirectoryLayout layout = ReadSamsungSefDirectoryLayout(bytes);
        offset = layout.FooterOffset;
        var result = new byte[checked(bytes.Length + trailingBytes.Length)];
        bytes.AsSpan(0, offset).CopyTo(result);
        trailingBytes.AsSpan().CopyTo(result.AsSpan(offset));
        bytes.AsSpan(offset).CopyTo(result.AsSpan(offset + trailingBytes.Length));

        originalTotalSize = layout.TotalSize;
        replacementTotalSize = checked(originalTotalSize + (uint)trailingBytes.Length);
        int newFooterOffset = checked(layout.FooterOffset + trailingBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(newFooterOffset, 4), replacementTotalSize);
        return result;
    }

    private static SamsungSefDirectoryLayout ReadSamsungSefDirectoryLayout(byte[] bytes)
    {
        Assert.True(bytes.Length >= 20, "Canonical Samsung SEF input must contain a bounded footer and directory.");
        int footerOffset = checked(bytes.Length - 8);
        Assert.Equal("SEFT", Encoding.ASCII.GetString(bytes, footerOffset + 4, 4));
        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footerOffset, 4));
        Assert.InRange(totalSize, 12u, checked((uint)footerOffset));
        int directoryOffset = checked(footerOffset - (int)totalSize);
        Assert.Equal("SEFH", Encoding.ASCII.GetString(bytes, directoryOffset, 4));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directoryOffset + 8, 4));
        Assert.True(entryCount <= int.MaxValue, "Canonical Samsung SEF entry count must fit the fixture bounds.");
        Assert.Equal(footerOffset, checked(directoryOffset + 12 + (int)entryCount * 12));
        return new SamsungSefDirectoryLayout(footerOffset, directoryOffset, entryCount, totalSize);
    }

    private static byte[] CorruptSamsungFooterTotalSize(byte[] bytes, out int offset, out uint original, out uint replacement)
    {
        Assert.True(bytes.Length >= 8);
        Assert.Equal("SEFT", Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        offset = bytes.Length - 8;
        original = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        replacement = checked(original + 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), replacement);
        return bytes;
    }

    private static byte[] CorruptSamsungDirectoryOffset(byte[] bytes, out int offset, out uint original, out uint replacement)
    {
        int header = bytes.AsSpan().IndexOf("SEFH"u8);
        Assert.True(header >= 0, "Canonical synthetic Samsung JPEG must contain an SEFH directory.");
        offset = checked(header + 12 + 4);
        original = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        replacement = uint.MaxValue;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), replacement);
        return bytes;
    }

    private static byte[] ReplaceAppleVideoIdentifier(byte[] bytes, out int offset, out byte original, out byte replacement)
    {
        byte[] needle = Encoding.ASCII.GetBytes(AppleIdentifier);
        offset = bytes.AsSpan().IndexOf(needle);
        Assert.True(offset >= 0, "Canonical Apple MOV must contain its structurally owned ContentIdentifier value.");
        Assert.Equal(-1, bytes.AsSpan(offset + needle.Length).IndexOf(needle));
        int changed = checked(offset + needle.Length - 1);
        original = bytes[changed];
        replacement = (byte)'C';
        Assert.Equal((byte)'B', original);
        bytes[changed] = replacement;
        return bytes;
    }

    private static byte[] IncreaseTopLevelBoxSize(byte[] bytes, string boxType, out int offset, out uint original, out uint replacement)
    {
        int position = 0;
        while (position + 8 <= bytes.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            string type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            if (type == boxType)
            {
                Assert.InRange(size, 8u, checked((uint)(bytes.Length - position)));
                offset = position;
                original = size;
                replacement = checked(size + 1);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), replacement);
                return bytes;
            }

            Assert.True(size >= 8 && size <= bytes.Length - position, "Synthetic MOV top-level box must be structurally bounded before mutation.");
            position = checked(position + (int)size);
        }

        throw new InvalidDataException($"Top-level {boxType} box was not found in the canonical MOV.");
    }

    private static string WriteInput(MediaWorkspace workspace, string prefix, string extension, byte[] bytes)
    {
        string path = workspace.AllocateFilePath(prefix, extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<FileSnapshot[]> CaptureAsync(IReadOnlyList<string> paths)
    {
        var snapshots = new FileSnapshot[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            byte[] bytes = await File.ReadAllBytesAsync(paths[i]);
            snapshots[i] = new FileSnapshot(Path.GetFileName(paths[i])!, bytes.LongLength, Sha256(bytes));
        }
        return snapshots;
    }

    private static string[] GetWorkspaceFileNames(MediaWorkspace workspace) =>
        Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspace.RootDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string[] ExpectedWorkspaceNames(IReadOnlyList<string> paths) =>
        paths.Select(path => Path.GetFileName(path)!).OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static object[] DescribeInputs(string matrixId, IReadOnlyList<InputVariant> inputs, IReadOnlyList<string> paths) =>
        inputs.Select((input, index) =>
        {
            string? canonicalPath = RetainInputVariant(matrixId, input, "canonical", input.Base);
            string? derivedPath = RetainInputVariant(matrixId, input, "derived", input.Derived);
            string? replayPath = RetainInputVariant(matrixId, input, "replay", input.Replay);
            return (object)new
            {
                role = input.Role,
                path = Path.GetFileName(paths[index]),
                baseBytes = input.Base.LongLength,
                baseSha256 = Sha256(input.Base),
                derivedBytes = input.Derived.LongLength,
                derivedSha256 = Sha256(input.Derived),
                replayBytes = input.Replay.LongLength,
                replaySha256 = Sha256(input.Replay),
                byteForByteReplay = input.Derived.AsSpan().SequenceEqual(input.Replay),
                retainedInputs = new
                {
                    canonical = canonicalPath,
                    derived = derivedPath,
                    replay = replayPath
                }
            };
        }).ToArray();

    private static string? RetainInputVariant(string matrixId, InputVariant input, string variant, byte[] bytes)
    {
        string? configuredRoot = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot)) return null;

        string root = Path.GetFullPath(configuredRoot);
        string inputRoot = Path.Combine(root, "inputs");
        Directory.CreateDirectory(inputRoot);

        string matrixToken = SanitizeEvidenceToken(matrixId);
        string roleToken = SanitizeEvidenceToken(input.Role);
        string variantToken = SanitizeEvidenceToken(variant);
        if (string.IsNullOrWhiteSpace(input.Extension) || input.Extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            input.Extension.Contains(Path.DirectorySeparatorChar) || input.Extension.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException($"Evidence input extension '{input.Extension}' is not a filename extension.");

        string filename = $"{matrixToken}-{roleToken}-{variantToken}-{Sha256(bytes)}{input.Extension}";
        string path = Path.Combine(inputRoot, filename);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"Retained evidence input exists with different bytes: {path}");
        }

        return Path.Combine("inputs", filename).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string SanitizeEvidenceToken(string value)
    {
        string token = new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidDataException("Evidence filename token is empty after sanitization.");
        return token;
    }

    private void WriteEvidence(string matrixId, object evidence)
    {
        string? root = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(root))
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, matrixId + ".json");
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        _output.WriteLine("PLAN137_CASE|" + matrixId + "|" + JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
    }

    private static PairD3StructureProof BuildPairD3StructureProof(byte[] canonical, byte[] derived, byte[] replay)
    {
        bool replayByteIdentical = derived.AsSpan().SequenceEqual(replay);
        bool replayHashIdentical = Sha256(derived) == Sha256(replay);
        try
        {
            AppleExifLayout canonicalLayout = ReadAppleExifLayoutIndependently(canonical);
            AppleExifLayout derivedLayout = ReadAppleExifLayoutIndependently(derived);
            bool canonicalHasOneMakerNote = canonicalLayout.MakerNotes.Count == 1;
            bool derivedHasTwoMakerNotes = derivedLayout.MakerNotes.Count == 2;
            bool canonicalExifCountIsOne = canonicalLayout.ExifIfdEntryCount == 1;
            bool derivedExifCountIsTwo = derivedLayout.ExifIfdEntryCount == 2;
            bool duplicateEntriesAreAdjacent = derivedHasTwoMakerNotes &&
                derivedLayout.MakerNotes[1].EntryOffset == derivedLayout.MakerNotes[0].EntryOffset + 12;
            bool duplicateTagsAndTypes = derivedHasTwoMakerNotes &&
                derivedLayout.MakerNotes.All(entry => entry.Tag == 0x927C && entry.Type == 7);
            bool duplicateCountsAndOffsets = derivedHasTwoMakerNotes &&
                derivedLayout.MakerNotes[0].ValueCount == derivedLayout.MakerNotes[1].ValueCount &&
                derivedLayout.MakerNotes[0].ValueOffset == derivedLayout.MakerNotes[1].ValueOffset;
            bool ownerPayloadMatchesCanonical = canonicalHasOneMakerNote && derivedHasTwoMakerNotes &&
                derivedLayout.MakerNotes.All(entry => entry.Payload.AsSpan().SequenceEqual(canonicalLayout.MakerNotes[0].Payload));
            bool appleSignaturePresent = derivedHasTwoMakerNotes && derivedLayout.MakerNotes.All(entry =>
                entry.Payload.AsSpan().StartsWith(Encoding.ASCII.GetBytes("Apple iOS\0\0\x01MM")));
            bool app1LengthDeltaIsTwelve = derivedLayout.App1SegmentLength == canonicalLayout.App1SegmentLength + 12;
            bool makerNoteOffsetsShiftedByTwelve = canonicalHasOneMakerNote && derivedHasTwoMakerNotes &&
                derivedLayout.MakerNotes[0].ValueOffset == canonicalLayout.MakerNotes[0].ValueOffset + 12 &&
                derivedLayout.MakerNotes[1].ValueOffset == canonicalLayout.MakerNotes[0].ValueOffset + 12;
            bool unrelatedBytesPreserved = canonicalHasOneMakerNote && derivedHasTwoMakerNotes &&
                AreUnrelatedPairD3BytesPreserved(canonical, derived, canonicalLayout);
            var owners = derivedLayout.MakerNotes.Select(entry => new PairD3MakerNoteOwner(
                entry.Tag,
                entry.Type,
                entry.ValueCount,
                entry.ValueOffset,
                entry.EntryOffset,
                Sha256(entry.Payload))).ToArray();

            bool structurallyValid = replayByteIdentical && replayHashIdentical && canonicalHasOneMakerNote &&
                derivedHasTwoMakerNotes && canonicalExifCountIsOne && derivedExifCountIsTwo &&
                derivedLayout.ExifIfdReachableThrough8769 && duplicateEntriesAreAdjacent && duplicateTagsAndTypes &&
                duplicateCountsAndOffsets && ownerPayloadMatchesCanonical && appleSignaturePresent &&
                app1LengthDeltaIsTwelve && makerNoteOffsetsShiftedByTwelve && unrelatedBytesPreserved;

            return new PairD3StructureProof(
                "PLAN137-PAIR-D3-IndependentStructure-v1",
                structurallyValid,
                canonical.LongLength,
                derived.LongLength,
                replay.LongLength,
                Sha256(canonical),
                Sha256(derived),
                Sha256(replay),
                replayByteIdentical,
                replayHashIdentical,
                derivedLayout.ExifIfdReachableThrough8769,
                canonicalLayout.ExifIfdEntryCount,
                derivedLayout.ExifIfdEntryCount,
                canonicalLayout.MakerNotes.Count,
                derivedLayout.MakerNotes.Count,
                owners,
                canonicalLayout.App1SegmentLength,
                derivedLayout.App1SegmentLength,
                duplicateEntriesAreAdjacent,
                duplicateTagsAndTypes,
                duplicateCountsAndOffsets,
                ownerPayloadMatchesCanonical,
                appleSignaturePresent,
                app1LengthDeltaIsTwelve,
                makerNoteOffsetsShiftedByTwelve,
                unrelatedBytesPreserved,
                null);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            return new PairD3StructureProof(
                "PLAN137-PAIR-D3-IndependentStructure-v1",
                false,
                canonical.LongLength,
                derived.LongLength,
                replay.LongLength,
                Sha256(canonical),
                Sha256(derived),
                Sha256(replay),
                replayByteIdentical,
                replayHashIdentical,
                false,
                0,
                0,
                0,
                0,
                [],
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                ex.GetType().FullName + ": " + ex.Message);
        }
    }

    private static AppleExifLayout ReadAppleExifLayoutIndependently(byte[] jpeg)
    {
        int markerOffset = FindExifApp1MarkerIndependently(jpeg);
        int app1LengthOffset = checked(markerOffset + 2);
        ushort app1Length = ReadBigEndianUInt16(jpeg, app1LengthOffset, jpeg.Length);
        int app1End = checked(app1LengthOffset + app1Length);
        if (app1Length < 8 || app1End > jpeg.Length)
            throw new InvalidDataException("Exif APP1 segment length is invalid.");

        int tiffStart = checked(markerOffset + 10);
        RequireRange(jpeg, tiffStart, 8, app1End, "TIFF header");
        if (jpeg[tiffStart] != (byte)'M' || jpeg[tiffStart + 1] != (byte)'M' ||
            ReadBigEndianUInt16(jpeg, tiffStart + 2, app1End) != 42)
            throw new InvalidDataException("Expected big-endian TIFF header.");

        uint ifd0RelativeOffset = ReadBigEndianUInt32(jpeg, tiffStart + 4, app1End);
        int ifd0CountOffset = ResolveTiffOffset(tiffStart, ifd0RelativeOffset, app1End, "IFD0");
        ushort ifd0Count = ReadBigEndianUInt16(jpeg, ifd0CountOffset, app1End);
        RequireRange(jpeg, checked(ifd0CountOffset + 2), checked((int)ifd0Count * 12 + 4), app1End, "IFD0 entries");

        uint exifIfdRelativeOffset = 0;
        int exifIfdPointers = 0;
        for (int i = 0; i < ifd0Count; i++)
        {
            int entryOffset = checked(ifd0CountOffset + 2 + i * 12);
            if (ReadBigEndianUInt16(jpeg, entryOffset, app1End) != 0x8769) continue;
            if (ReadBigEndianUInt16(jpeg, entryOffset + 2, app1End) != 4 ||
                ReadBigEndianUInt32(jpeg, entryOffset + 4, app1End) != 1)
                throw new InvalidDataException("IFD0 ExifIFD pointer has an unexpected type or count.");
            exifIfdRelativeOffset = ReadBigEndianUInt32(jpeg, entryOffset + 8, app1End);
            exifIfdPointers++;
        }

        if (exifIfdPointers != 1)
            throw new InvalidDataException("IFD0 does not contain exactly one ExifIFD pointer.");
        int exifIfdCountOffset = ResolveTiffOffset(tiffStart, exifIfdRelativeOffset, app1End, "ExifIFD");
        ushort exifEntryCount = ReadBigEndianUInt16(jpeg, exifIfdCountOffset, app1End);
        RequireRange(jpeg, checked(exifIfdCountOffset + 2), checked((int)exifEntryCount * 12 + 4), app1End, "ExifIFD entries");

        var makerNotes = new List<AppleMakerNoteEntry>();
        for (int i = 0; i < exifEntryCount; i++)
        {
            int entryOffset = checked(exifIfdCountOffset + 2 + i * 12);
            ushort tag = ReadBigEndianUInt16(jpeg, entryOffset, app1End);
            if (tag != 0x927C) continue;
            ushort type = ReadBigEndianUInt16(jpeg, entryOffset + 2, app1End);
            uint valueCount = ReadBigEndianUInt32(jpeg, entryOffset + 4, app1End);
            uint valueOffset = ReadBigEndianUInt32(jpeg, entryOffset + 8, app1End);
            if (type != 7 || valueCount > int.MaxValue)
                throw new InvalidDataException("MakerNote owner has an unexpected type or size.");
            int payloadOffset = ResolveTiffOffset(tiffStart, valueOffset, app1End, "MakerNote payload");
            RequireRange(jpeg, payloadOffset, checked((int)valueCount), app1End, "MakerNote payload");
            makerNotes.Add(new AppleMakerNoteEntry(
                entryOffset,
                tag,
                type,
                valueCount,
                valueOffset,
                jpeg.AsSpan(payloadOffset, checked((int)valueCount)).ToArray()));
        }

        return new AppleExifLayout(app1LengthOffset, app1Length, exifIfdCountOffset, exifEntryCount, true, makerNotes);
    }

    private static int FindExifApp1MarkerIndependently(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("JPEG SOI marker is missing.");

        int cursor = 2;
        while (cursor < jpeg.Length)
        {
            int markerOffset = cursor;
            if (jpeg[cursor++] != 0xFF)
                throw new InvalidDataException("JPEG marker stream is malformed.");
            while (cursor < jpeg.Length && jpeg[cursor] == 0xFF) cursor++;
            if (cursor >= jpeg.Length) break;
            byte marker = jpeg[cursor++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;

            ushort segmentLength = ReadBigEndianUInt16(jpeg, cursor, jpeg.Length);
            if (segmentLength < 2) throw new InvalidDataException("JPEG segment length is invalid.");
            int segmentEnd = checked(cursor + segmentLength);
            if (segmentEnd > jpeg.Length) throw new InvalidDataException("JPEG segment extends past the file.");
            int payloadOffset = checked(cursor + 2);
            if (marker == 0xE1 && segmentLength >= 8 &&
                jpeg.AsSpan(payloadOffset, 6).SequenceEqual(Encoding.ASCII.GetBytes("Exif\0\0")))
                return markerOffset;
            cursor = segmentEnd;
        }

        throw new InvalidDataException("JPEG has no Exif APP1 segment.");
    }

    private static bool AreUnrelatedPairD3BytesPreserved(byte[] canonical, byte[] derived, AppleExifLayout layout)
    {
        if (derived.Length != canonical.Length + 12 || layout.MakerNotes.Count != 1) return false;
        AppleMakerNoteEntry makerNote = layout.MakerNotes[0];
        int insertAt = checked(makerNote.EntryOffset + 12);
        var changedOffsets = new HashSet<int>
        {
            layout.App1SegmentLengthOffset,
            layout.App1SegmentLengthOffset + 1,
            layout.ExifIfdEntryCountOffset,
            layout.ExifIfdEntryCountOffset + 1,
            makerNote.EntryOffset + 8,
            makerNote.EntryOffset + 9,
            makerNote.EntryOffset + 10,
            makerNote.EntryOffset + 11
        };

        for (int sourceOffset = 0; sourceOffset < canonical.Length; sourceOffset++)
        {
            if (changedOffsets.Contains(sourceOffset)) continue;
            int derivedOffset = sourceOffset < insertAt ? sourceOffset : checked(sourceOffset + 12);
            if (canonical[sourceOffset] != derived[derivedOffset]) return false;
        }

        return true;
    }

    private static ushort ReadBigEndianUInt16(byte[] bytes, int offset, int limit)
    {
        RequireRange(bytes, offset, 2, limit, "16-bit field");
        return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset, int limit)
    {
        RequireRange(bytes, offset, 4, limit, "32-bit field");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    }

    private static int ResolveTiffOffset(int tiffStart, uint relativeOffset, int app1End, string field)
    {
        if (relativeOffset > int.MaxValue)
            throw new InvalidDataException($"{field} offset cannot be represented.");
        long absoluteOffset = (long)tiffStart + relativeOffset;
        if (absoluteOffset < tiffStart || absoluteOffset >= app1End)
            throw new InvalidDataException($"{field} offset is outside APP1.");
        return (int)absoluteOffset;
    }

    private static void RequireRange(byte[] bytes, int offset, int length, int limit, string field)
    {
        if (limit < 0 || limit > bytes.Length || offset < 0 || length < 0 || offset > limit - length)
            throw new InvalidDataException($"{field} is outside its container.");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record InputVariant(string Role, string Extension, byte[] Base, byte[] Derived, byte[] Replay);
    private sealed record FileSnapshot(string Path, long Bytes, string Sha256);
    private sealed record AppleExifLayout(
        int App1SegmentLengthOffset,
        ushort App1SegmentLength,
        int ExifIfdEntryCountOffset,
        ushort ExifIfdEntryCount,
        bool ExifIfdReachableThrough8769,
        IReadOnlyList<AppleMakerNoteEntry> MakerNotes);
    private sealed record AppleMakerNoteEntry(
        int EntryOffset,
        ushort Tag,
        ushort Type,
        uint ValueCount,
        uint ValueOffset,
        byte[] Payload);
    private sealed record PairD3MakerNoteOwner(
        ushort Tag,
        ushort Type,
        uint ValueCount,
        uint ValueOffset,
        int EntryOffset,
        string PayloadSha256);
    private sealed record PairD3StructureProof(
        string Schema,
        bool StructurallyValid,
        long CanonicalBytes,
        long DerivedBytes,
        long ReplayBytes,
        string CanonicalSha256,
        string DerivedSha256,
        string ReplaySha256,
        bool DerivedReplayByteIdentical,
        bool DerivedReplayHashIdentical,
        bool ExifIfdReachableThrough8769,
        int CanonicalExifIfdEntryCount,
        int DerivedExifIfdEntryCount,
        int CanonicalMakerNoteOwnerCount,
        int DerivedMakerNoteOwnerCount,
        IReadOnlyList<PairD3MakerNoteOwner> Owners,
        ushort? CanonicalApp1SegmentLength,
        ushort? DerivedApp1SegmentLength,
        bool DuplicateEntriesAreAdjacent,
        bool DuplicateTagsAndTypes,
        bool DuplicateCountsAndOffsets,
        bool OwnerPayloadMatchesCanonical,
        bool AppleSignaturePresent,
        bool App1LengthDeltaIsTwelve,
        bool MakerNoteOffsetsShiftedByTwelve,
        bool UnrelatedBytesPreserved,
        string? ParseError);
    private readonly record struct SamsungSefDirectoryLayout(int FooterOffset, int DirectoryOffset, uint EntryCount, uint TotalSize);
}

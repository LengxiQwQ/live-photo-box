using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Core.Tests.Protocols;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class R1ArithmeticMalformedInputTests
{
    private readonly ITestOutputHelper _output;

    public R1ArithmeticMalformedInputTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_ValidControlsForJpegMp4HeifAndSef_AreAccepted()
    {
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");
        byte[] mp4 = CreateGoogleV1JpegWithValidOffset();
        byte[] heif = CreateSamsungHeic();
        byte[] sef = CreateSamsungSefJpeg();

        await AssertInspectableAsync("control-jpeg", ".jpg", jpeg,
            SourceProtocol.NonLive, ImageContainer.Jpeg, expectsMotionVideo: false);
        await AssertInspectableAsync("control-mp4", ".jpg", mp4,
            SourceProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg, expectsMotionVideo: true);
        await AssertInspectableAsync("control-heif", ".heic", heif,
            SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic, expectsMotionVideo: true);
        await AssertInspectableAsync("control-sef", ".jpg", sef,
            SourceProtocol.SamsungMotionPhotoJpeg, ImageContainer.Jpeg, expectsMotionVideo: true);
    }

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample")]
    public async Task Inspect_R1AControl_FrozenGoogleV1RealSample_IsAccepted()
    {
        FrozenSample sample = await LoadFrozenSampleAsync("红米老款-GV1.JPG");
        SourceMediaFacts facts = await new SourceInspector().InspectAsync(sample.Path);
        string afterHash = await HashFileAsync(sample.Path);
        var record = new
        {
            schemaVersion = 1,
            caseId = "CONTROL-001",
            evidenceClass = "RealSample",
            canonicalPath = sample.Path,
            canonicalBytes = sample.Bytes,
            canonicalSha256 = sample.Sha256,
            sourceSha256After = afterHash,
            actualProtocol = facts.Protocol.ToString(),
            actualContainer = facts.PrimaryImage.Container.ToString(),
            actualVideoPresent = facts.MotionVideo?.IsPresent ?? false
        };
        await SaveCaseRecordAsync("CONTROL-001", record);
        _output.WriteLine(JsonSerializer.Serialize(record));

        Assert.Equal(sample.Sha256, afterHash);
        Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.MotionVideo.IsPresent);
    }

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1AIo001_GoogleV1MaximumOffset_FailsProtocolMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "IO-001", "红米老款-GV1.JPG", "GOOG-V1-RANGE-END",
            "GCamera:MicroVideoOffset=UINT64_MAX; offset+videoLength exceeds file",
            ImageContainer.Jpeg, SourceInspectionStage.Protocol, convertRequired: true,
            bytes => ReplaceJpegXmpAttribute(bytes, "GCamera:MicroVideoOffset", "18446744073709551615"));

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1AIo002_GoogleV2MaximumMotionLength_FailsProtocolMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "IO-002", "小米.jpg", "GOOG-V2-ITEM-LENGTH",
            "Item:Length=UINT64_MAX on the structurally identified MotionPhoto item",
            ImageContainer.Jpeg, SourceInspectionStage.Protocol, convertRequired: true,
            bytes => ReplaceMotionPhotoItemLength(bytes, "18446744073709551615"));

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1AIo003_HeifIlocMaximumExtentCount_FailsContainerMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "IO-003", "华为Mate80.heic", "HEIF-ILOC-COUNT-MUL",
            "first bounded iloc item extent_count=UINT16_MAX",
            ImageContainer.Heic, SourceInspectionStage.Container, convertRequired: true,
            SetFirstIlocExtentCountToMaximum);

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1AIo004_OnePlusNegativeMotionLength_FailsProtocolMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "IO-004", "一加.jpg", "OPPO-LENGTH-SIGNED-CAST",
            "Item:Length=-1 on the structurally identified MotionPhoto item",
            ImageContainer.Jpeg, SourceInspectionStage.Protocol, convertRequired: true,
            bytes => ReplaceMotionPhotoItemLength(bytes, "-1"));

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1ATr001_JpegApp1TruncatedAtDeclaredEnd_FailsContainerMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "TR-001", "小米.jpg", "JPEG-APP1-TRUNCATE",
            "physical input ends one byte before the structurally declared XMP APP1 end",
            ImageContainer.Jpeg, SourceInspectionStage.Container, convertRequired: true,
            TruncateBeforeJpegXmpApp1End);

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1ATr002_HeifIlocChildTruncatedAtDeclaredEnd_FailsContainerMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "TR-002", "华为Mate80.heic", "HEIF-META-CHILD-TRUNCATE",
            "physical input ends one byte before the bounded meta/iloc child end",
            ImageContainer.Heic, SourceInspectionStage.Container, convertRequired: true,
            TruncateBeforeHeifIlocEnd);

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1ATr003_AppleContentIdentifierDataAtomTruncated_FailsContainerMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "TR-003", "苹果双文件.MOV", "QT-CID-ATOM-TRUNCATE",
            "secondary MOV physical input ends one byte before the bounded ContentIdentifier data atom end",
            ImageContainer.Heic, SourceInspectionStage.Container, convertRequired: false,
            TruncateBeforeAppleContentIdentifierDataEnd,
            pairedPrimarySampleName: "苹果双文件.HEIC");

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1ATr004_SamsungSefMotionEntryTruncatedByOneByte_FailsProtocolMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "TR-004", "三星.jpg", "SEF-DIRECTORY-TRUNCATE",
            "bounded MotionPhoto_Data SEF index entry size decremented by one byte; SEFT footer retained",
            ImageContainer.Jpeg, SourceInspectionStage.Protocol, convertRequired: true,
            TruncateSamsungMotionEntryByOneByte);

    [Fact]
    [Trait("P7", "R1-A")]
    [Trait("Category", "P7-R1-A")]
    [Trait("EvidenceClass", "RealSample-derived Synthetic")]
    public Task Inspect_R1ATr005_HuaweiLiveDecimalFieldTruncatedByOneByte_FailsProtocolMalformed() =>
        AssertFrozenMalformedCaseAsync(
            "TR-005", "华为-Mate80.jpg", "HUAWEI-LIVE-TAIL-TRUNCATE",
            "final decimal byte of the fixed-width LIVE_ field removed and consumed by its required space padding",
            ImageContainer.Jpeg, SourceInspectionStage.Protocol, convertRequired: true,
            TruncateHuaweiLiveFieldByOneByte);

    [Theory]
    [InlineData("below-minimum", 1)]
    [InlineData("declared-past-eof", 0xFFFF)]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_JpegApp1LengthBoundary_FailsClosed(string caseId, int declaredLength)
    {
        await AssertMalformedAsync(
            caseId,
            ".jpg",
            CreateXmpJpeg,
            bytes =>
            {
                int marker = FindBytes(bytes, [0xFF, 0xE1]);
                Assert.True(marker >= 0, "Canonical fixture must contain the APP1 marker.");
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(marker + 2, 2), checked((ushort)declaredLength));
                return bytes;
            },
            $"APP1.Length={declaredLength}",
            SourceInspectionStage.Container);
    }

    [Theory]
    [InlineData("unsigned-64-overflow", "18446744073709551616")]
    [InlineData("unsigned-64-maximum-outside-file", "18446744073709551615")]
    [InlineData("negative", "-1")]
    [InlineData("non-decimal", "1.5")]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_GoogleV1MicroVideoOffsetInvalidDecimal_FailsClosed(string caseId, string offset)
    {
        await AssertMalformedAsync(
            caseId,
            ".jpg",
            CreateGoogleV1JpegWithValidOffset,
            bytes => ReplaceGoogleV1Offset(bytes, offset),
            $"GCamera:MicroVideoOffset={offset}",
            SourceInspectionStage.Protocol);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_Mp4TopLevelMdatSizePastEof_FailsClosed()
    {
        await AssertMalformedAsync(
            "mp4-mdat-size-past-eof",
            ".mp4",
            SyntheticProtocolFixtures.CreateMinimalMp4,
            bytes =>
            {
                int mdat = FindTopLevelBox(bytes, "mdat");
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(mdat, 4), uint.MaxValue);
                return bytes;
            },
            "top-level mdat size=0xFFFFFFFF",
            SourceInspectionStage.Container);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_Mp4StcoChunkOffsetOutsideFile_FailsClosed()
    {
        await AssertMalformedAsync(
            "mp4-stco-offset-outside-file",
            ".mp4",
            SyntheticProtocolFixtures.CreateMinimalMp4,
            bytes =>
            {
                int moov = FindTopLevelBox(bytes, "moov");
                int trak = FindChildBox(bytes, moov, "trak");
                int mdia = FindChildBox(bytes, trak, "mdia");
                int minf = FindChildBox(bytes, mdia, "minf");
                int stbl = FindChildBox(bytes, minf, "stbl");
                int stco = FindChildBox(bytes, stbl, "stco");
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stco + 12, 4)));
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(stco + 16, 4), uint.MaxValue);
                return bytes;
            },
            "stco.entry[0].chunk_offset=0xFFFFFFFF",
            SourceInspectionStage.Container);
    }

    [Theory]
    [InlineData("maximum-count", 0xFFFFFFFFu)]
    [InlineData("32-bit-product-wraps-to-existing-4-byte-entry", 0x40000001u)]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_Mp4StcoEntryCountTimesEntryWidthExceedsRemaining_FailsClosed(string caseId, uint entryCount)
    {
        await AssertMalformedAsync(
            $"mp4-stco-entry-count-times-width-{caseId}",
            ".mp4",
            SyntheticProtocolFixtures.CreateMinimalMp4,
            bytes =>
            {
                int moov = FindTopLevelBox(bytes, "moov");
                int trak = FindChildBox(bytes, moov, "trak");
                int mdia = FindChildBox(bytes, trak, "mdia");
                int minf = FindChildBox(bytes, mdia, "minf");
                int stbl = FindChildBox(bytes, minf, "stbl");
                int stco = FindChildBox(bytes, stbl, "stco");
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stco + 12, 4)));
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(stco + 12, 4), entryCount);
                return bytes;
            },
            $"stco.entry_count=0x{entryCount:X8} with 4-byte entries and one entry of available payload",
            SourceInspectionStage.Container);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_Mp4MdatPayloadTruncatedByOneByte_FailsClosed()
    {
        await AssertMalformedAsync(
            "mp4-mdat-payload-truncated-one-byte",
            ".mp4",
            SyntheticProtocolFixtures.CreateMinimalMp4,
            bytes =>
            {
                int mdat = FindTopLevelBox(bytes, "mdat");
                uint mdatSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdat, 4));
                int payloadEnd = checked(mdat + (int)mdatSize);
                Assert.True(payloadEnd > mdat + 8);
                return [.. bytes.AsSpan(0, payloadEnd - 1), .. bytes.AsSpan(payloadEnd)];
            },
            "remove final byte of declared mdat payload",
            SourceInspectionStage.Container);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_HeifMetaBoxSizePastEof_FailsClosed()
    {
        await AssertMalformedAsync(
            "heif-meta-size-past-eof",
            ".heic",
            CreateSamsungHeic,
            bytes =>
            {
                int meta = FindTopLevelBox(bytes, "meta");
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(meta, 4), uint.MaxValue);
                return bytes;
            },
            "top-level meta size=0xFFFFFFFF",
            SourceInspectionStage.Container);
    }

    [Theory]
    [InlineData("heif-iloc-offset-outside-file", 22, uint.MaxValue)]
    [InlineData("heif-iloc-length-outside-file", 26, uint.MaxValue)]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_HeifIlocExtentRangeOutsideFile_FailsClosed(string caseId, int ilocRelativeOffset, uint value)
    {
        await AssertMalformedAsync(
            caseId,
            ".heic",
            CreateSamsungHeic,
            bytes =>
            {
                int meta = FindTopLevelBox(bytes, "meta");
                int iloc = FindChildBox(bytes, meta, "iloc", fullBoxPrefixLength: 4);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(iloc + ilocRelativeOffset, 4), value);
                return bytes;
            },
            $"iloc.entry[0].{(ilocRelativeOffset == 22 ? "extent_offset" : "extent_length")}=0x{value:X8}",
            SourceInspectionStage.Container);
    }

    [Theory]
    [InlineData("sef-directory-offset-outside-region", 4, uint.MaxValue)]
    [InlineData("sef-directory-length-outside-region", 8, uint.MaxValue)]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_SefDirectoryPayloadRangeOutsideRegion_FailsClosed(string caseId, int entryRelativeOffset, uint value)
    {
        await AssertMalformedAsync(
            caseId,
            ".jpg",
            CreateSamsungSefJpeg,
            bytes =>
            {
                int entry = FindSefDirectoryEntry(bytes);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + entryRelativeOffset, 4), value);
                return bytes;
            },
            $"SEFH.entry[0].{(entryRelativeOffset == 4 ? "payload_offset" : "payload_length")}=0x{value:X8}",
            SourceInspectionStage.Protocol);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_SefDirectoryCountExceedsAvailableEntries_FailsClosed()
    {
        await AssertMalformedAsync(
            "sef-directory-count-exceeds-entries",
            ".jpg",
            CreateSamsungSefJpeg,
            bytes =>
            {
                int sefh = FindBytes(bytes, Encoding.ASCII.GetBytes("SEFH"));
                Assert.True(sefh >= 0, "Canonical SEF fixture must contain its directory header.");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sefh + 8, 4), uint.MaxValue);
                return bytes;
            },
            "SEFH.entry_count=0xFFFFFFFF",
            SourceInspectionStage.Protocol);
    }

    [Fact]
    [Trait("Category", "P7-R1-A")]
    public async Task Inspect_SefFooterTruncatedByOneByte_FailsClosed()
    {
        await AssertMalformedAsync(
            "sef-footer-truncated-one-byte",
            ".jpg",
            CreateSamsungSefJpeg,
            bytes => bytes[..^1],
            "remove final byte of SEFT footer",
            SourceInspectionStage.Pairing,
            SourceInspectionFailureCategory.Ambiguous);
    }

    private async Task AssertInspectableAsync(
        string caseId,
        string extension,
        byte[] bytes,
        SourceProtocol expectedProtocol,
        ImageContainer expectedImageContainer,
        bool expectsMotionVideo)
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath(caseId, extension);
        await File.WriteAllBytesAsync(path, bytes);
        string beforeHash = Hash(await File.ReadAllBytesAsync(path));

        var facts = await new SourceInspector().InspectAsync(path);

        Assert.Equal(beforeHash, Hash(await File.ReadAllBytesAsync(path)));
        Assert.Equal(new[] { Path.GetFullPath(path) }, Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(expectedProtocol, facts.Protocol);
        Assert.True(facts.PrimaryImage.IsPresent, $"Control {caseId} must report a primary image.");
        Assert.Equal(expectedImageContainer, facts.PrimaryImage.Container);
        Assert.Equal(0, facts.PrimaryImage.ByteOffset);
        Assert.InRange(facts.PrimaryImage.ByteLength, 1, bytes.LongLength);
        if (expectsMotionVideo)
        {
            Assert.NotNull(facts.MotionVideo);
            Assert.True(facts.MotionVideo.IsPresent, $"Control {caseId} must report a motion video.");
            Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
            Assert.InRange(facts.MotionVideo.ByteOffset, 0, bytes.LongLength - 1);
            Assert.InRange(facts.MotionVideo.ByteLength, 1, bytes.LongLength - facts.MotionVideo.ByteOffset);
        }
        else
        {
            Assert.True(facts.MotionVideo is null || !facts.MotionVideo.IsPresent,
                $"Control {caseId} must not report motion video.");
        }
        Assert.Equal(64, facts.PrimarySha256.Length);
        _output.WriteLine(
            $"R1A_CONTROL|caseId={caseId}|bytes={bytes.Length}|sha256={beforeHash}|protocol={facts.Protocol}|primaryPresent={facts.PrimaryImage.IsPresent}|primaryContainer={facts.PrimaryImage.Container}|primaryOffset={facts.PrimaryImage.ByteOffset}|primaryLength={facts.PrimaryImage.ByteLength}|videoPresent={facts.MotionVideo?.IsPresent ?? false}|videoContainer={facts.MotionVideo?.Container.ToString() ?? "None"}|videoOffset={facts.MotionVideo?.ByteOffset.ToString() ?? "None"}|videoLength={facts.MotionVideo?.ByteLength.ToString() ?? "None"}");
    }

    private async Task AssertMalformedAsync(
        string caseId,
        string extension,
        Func<byte[]> canonicalFactory,
        Func<byte[], byte[]> mutation,
        string mutationParameters,
        SourceInspectionStage expectedStage,
        SourceInspectionFailureCategory expectedCategory = SourceInspectionFailureCategory.Malformed)
    {
        byte[] canonicalFirst = canonicalFactory();
        byte[] canonicalReplay = canonicalFactory();
        string canonicalHash = Hash(canonicalFirst);
        Assert.Equal(canonicalHash, Hash(canonicalReplay));

        byte[] derivedFirst = mutation(canonicalFirst.ToArray());
        byte[] derivedReplay = mutation(canonicalReplay.ToArray());
        Assert.Equal(derivedFirst, derivedReplay);
        string derivedHash = Hash(derivedFirst);
        string replayHash = Hash(derivedReplay);
        Assert.NotEqual(canonicalHash, derivedHash);
        Assert.Equal(derivedHash, replayHash);
        string? evidenceRoot = Environment.GetEnvironmentVariable("P7_R1_R1A_WORKSPACE");
        string? evidenceDirectory = string.IsNullOrWhiteSpace(evidenceRoot)
            ? null : Path.Combine(Path.GetFullPath(evidenceRoot), "supplemental", caseId);
        if (evidenceDirectory is not null)
        {
            Directory.CreateDirectory(evidenceDirectory);
            await using (var canonical = new FileStream(Path.Combine(evidenceDirectory, "canonical" + extension), FileMode.CreateNew))
                await canonical.WriteAsync(canonicalFirst);
            await using (var derived = new FileStream(Path.Combine(evidenceDirectory, "derived" + extension), FileMode.CreateNew))
                await derived.WriteAsync(derivedFirst);
        }
        _output.WriteLine(
            $"R1A_CASE|caseId={caseId}|mutation={mutationParameters}|canonicalBytes={canonicalFirst.Length}|canonicalSha256={canonicalHash}|derivedBytes={derivedFirst.Length}|derivedSha256={derivedHash}|replayBytes={derivedReplay.Length}|replaySha256={replayHash}|expectedCategory={expectedCategory}|expectedStage={expectedStage}");

        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath(caseId, extension);
        await File.WriteAllBytesAsync(path, derivedFirst);
        string beforeHash = Hash(await File.ReadAllBytesAsync(path));
        string[] filesBefore = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SourceInspectionException exception = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(path));

        Assert.True(expectedCategory == exception.Category,
            $"Case {caseId} expected category {expectedCategory} but got {exception.Category} at {exception.Stage}: {exception.Reason}");
        Assert.True(expectedStage == exception.Stage,
            $"Case {caseId} expected stage {expectedStage} but got {exception.Stage} as {exception.Category}: {exception.Reason}");
        Assert.Equal(beforeHash, Hash(await File.ReadAllBytesAsync(path)));
        string[] filesAfter = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(filesBefore, filesAfter);

        bool conversionApplicable = extension is ".jpg" or ".heic";
        ImageConversionResult? conversion = null;
        if (conversionApplicable)
        {
            string outputDirectory = Path.Combine(workspace.RootDirectory, "output");
            Directory.CreateDirectory(outputDirectory);
            ImageContainer container = extension == ".jpg" ? ImageContainer.Jpeg : ImageContainer.Heic;
            conversion = await new ImageConverter().ConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = path, Kind = MediaArtifactKind.PrimaryImage,
                    MimeType = container == ImageContainer.Jpeg ? "image/jpeg" : "image/heic",
                    ImageContainer = container, ByteLength = derivedFirst.LongLength
                },
                TargetContainer = container == ImageContainer.Jpeg ? ImageContainer.Heic : ImageContainer.Jpeg,
                TargetDirectory = outputDirectory
            });
            Assert.False(conversion.Success);
            Assert.Null(conversion.OutputArtifact);
            Assert.Equal(ConversionFailureStage.SourceInspection, conversion.ExecutionRecord.Truth.FailureStage);
            Assert.Equal(ConversionFailureCategory.SourceInspection, conversion.ExecutionRecord.Truth.FailureCategory);
            Assert.Empty(Directory.GetFileSystemEntries(outputDirectory, "*", SearchOption.AllDirectories));
            Assert.Equal(filesBefore, Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray());
            Assert.Equal(beforeHash, Hash(await File.ReadAllBytesAsync(path)));
        }
        if (evidenceDirectory is not null)
        {
            var record = new
            {
                caseId, mutationParameters, evidenceClass = "Synthetic", extension,
                canonicalBytes = canonicalFirst.Length, canonicalSha256 = canonicalHash,
                derivedBytes = derivedFirst.Length, derivedSha256 = derivedHash,
                replayBytes = derivedReplay.Length, replaySha256 = replayHash, replayByteIdentical = derivedFirst.AsSpan().SequenceEqual(derivedReplay),
                expectedStage = expectedStage.ToString(), expectedCategory = expectedCategory.ToString(),
                actualStage = exception.Stage.ToString(), actualCategory = exception.Category.ToString(),
                sourceSha256Before = beforeHash, sourceSha256After = Hash(await File.ReadAllBytesAsync(path)),
                filesUnchanged = filesBefore.SequenceEqual(filesAfter),
                conversionApplicability = conversionApplicable ? "REQUIRED" : "NO_IMAGE_CONVERSION_FOR_STANDALONE_VIDEO",
                conversionInvoked = conversion is not null, conversionSuccess = conversion?.Success,
                conversionOutputArtifact = conversion?.OutputArtifact?.Path,
                outputPublished = false, unexplainedResidue = false,
                extract = "NOT_REACHED_AFTER_INSPECTOR_REJECTION", clean = "NOT_REACHED_AFTER_INSPECTOR_REJECTION"
            };
            await using var evidence = new FileStream(Path.Combine(evidenceDirectory, "result.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(evidence, record, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private async Task AssertFrozenMalformedCaseAsync(
        string caseId,
        string mutatedSampleName,
        string mutationId,
        string mutationParameters,
        ImageContainer primaryContainer,
        SourceInspectionStage expectedStage,
        bool convertRequired,
        Func<byte[], byte[]> mutation,
        string? pairedPrimarySampleName = null)
    {
        FrozenSample canonical = await LoadFrozenSampleAsync(mutatedSampleName);
        FrozenSample? pairedPrimary = pairedPrimarySampleName == null
            ? null
            : await LoadFrozenSampleAsync(pairedPrimarySampleName);

        byte[] firstGeneration = mutation(canonical.Content.ToArray());
        byte[] replayGeneration = mutation(canonical.Content.ToArray());
        bool replayBytesMatch = firstGeneration.AsSpan().SequenceEqual(replayGeneration);
        string derivedHash = Hash(firstGeneration);
        string replayHash = Hash(replayGeneration);

        string root = GetPlan100WorkspaceRoot();
        string caseRoot = Path.Combine(root, "cases", caseId);
        Assert.False(Directory.Exists(caseRoot), $"PLAN100 case workspace already exists: {caseRoot}");
        Directory.CreateDirectory(caseRoot);

        string inputDirectoryName = pairedPrimary == null ? "primary" : "secondary";
        string inputDirectory = Path.Combine(caseRoot, inputDirectoryName);
        Directory.CreateDirectory(inputDirectory);
        string derivedPath = Path.Combine(inputDirectory, "derived" + Path.GetExtension(mutatedSampleName));
        await File.WriteAllBytesAsync(derivedPath, firstGeneration);
        string derivedHashBefore = await HashFileAsync(derivedPath);

        string primaryPath = pairedPrimary?.Path ?? derivedPath;
        string? secondaryPath = pairedPrimary == null ? null : derivedPath;
        string? outputDirectory = null;
        string[]? outputEntriesBefore = null;
        if (convertRequired)
        {
            outputDirectory = Path.Combine(caseRoot, "output");
            Directory.CreateDirectory(outputDirectory);
            outputEntriesBefore = SnapshotTreeEntries(outputDirectory);
        }

        string[] workspaceEntriesBefore = SnapshotTreeEntries(caseRoot);
        SourceInspectionException? inspectionError = null;
        Exception? inspectionUnexpectedError = null;
        SourceMediaFacts? inspectedFacts = null;
        try
        {
            inspectedFacts = await new SourceInspector().InspectAsync(primaryPath, secondaryPath);
        }
        catch (SourceInspectionException ex)
        {
            inspectionError = ex;
        }
        catch (Exception ex)
        {
            inspectionUnexpectedError = ex;
        }
        string[] workspaceEntriesAfterInspection = SnapshotTreeEntries(caseRoot);

        ImageConversionResult? conversionResult = null;
        Exception? conversionUnexpectedError = null;
        if (convertRequired)
        {
            try
            {
                conversionResult = await new ImageConverter().ConvertAsync(new ImageConversionRequest
                {
                    SourceArtifact = new MediaArtifact
                    {
                        Path = primaryPath,
                        Kind = MediaArtifactKind.PrimaryImage,
                        MimeType = primaryContainer == ImageContainer.Jpeg ? "image/jpeg" : "image/heic",
                        ImageContainer = primaryContainer,
                        ByteLength = new FileInfo(primaryPath).Length
                    },
                    TargetContainer = primaryContainer == ImageContainer.Jpeg ? ImageContainer.Heic : ImageContainer.Jpeg,
                    TargetDirectory = outputDirectory!
                });
            }
            catch (Exception ex)
            {
                conversionUnexpectedError = ex;
            }
        }

        string[]? outputEntriesAfter = outputDirectory == null
            ? null
            : SnapshotTreeEntries(outputDirectory);
        string[] workspaceEntriesAfterOperations = SnapshotTreeEntries(caseRoot);
        string derivedHashAfter = await HashFileAsync(derivedPath);

        var sampleIdentityAfter = new List<object>();
        bool canonicalSamplesUnchanged = true;
        FrozenSample[] canonicalSamples = pairedPrimary == null
            ? [canonical]
            : [canonical, pairedPrimary];
        foreach (FrozenSample sample in canonicalSamples)
        {
            long currentBytes = new FileInfo(sample.Path).Length;
            string currentHash = await HashFileAsync(sample.Path);
            bool unchanged = currentBytes == sample.Bytes && currentHash == sample.Sha256;
            canonicalSamplesUnchanged &= unchanged;
            sampleIdentityAfter.Add(new
            {
                path = sample.Path,
                expectedBytes = sample.Bytes,
                actualBytes = currentBytes,
                expectedSha256 = sample.Sha256,
                actualSha256 = currentHash,
                unchanged
            });
        }

        string downstreamDisposition = inspectionError != null
            ? "NOT_REACHED_AFTER_INSPECTOR_REJECTION"
            : inspectionUnexpectedError != null
                ? "NOT_EXECUTED_AFTER_UNEXPECTED_INSPECTOR_ERROR"
                : "NOT_EXECUTED_AFTER_UNEXPECTED_INSPECTOR_ACCEPT";
        string downstreamRationale = inspectionError != null
            ? "Inspector rejected this malformed input before Extract/Clean could be reached."
            : "PLAN100 recovers the Inspector finding only. It does not grant Extract/Clean authority for malformed input; no downstream mutation was invoked, and unexpected Inspector acceptance remains a failing product finding.";

        var record = new
        {
            schemaVersion = 1,
            plan = "PLAN100",
            caseId,
            mutationId,
            mutationParameters,
            evidenceClass = new { baseSample = "RealSample", derivedInput = "Synthetic/Derived" },
            canonicalSamples = pairedPrimary == null
                ? new[] { new { path = canonical.Path, bytes = canonical.Bytes, sha256 = canonical.Sha256 } }
                : new[]
                {
                    new { path = pairedPrimary.Path, bytes = pairedPrimary.Bytes, sha256 = pairedPrimary.Sha256 },
                    new { path = canonical.Path, bytes = canonical.Bytes, sha256 = canonical.Sha256 }
                },
            derivedInput = new
            {
                path = derivedPath,
                bytes = firstGeneration.LongLength,
                sha256 = derivedHash,
                replayBytes = replayGeneration.LongLength,
                replaySha256 = replayHash,
                replayBytesMatch,
                differsFromCanonical = !canonical.Content.AsSpan().SequenceEqual(firstGeneration)
            },
            expectedInspector = new
            {
                decision = "REJECT",
                stage = expectedStage.ToString(),
                category = SourceInspectionFailureCategory.Malformed.ToString()
            },
            actualInspector = new
            {
                decision = inspectionError == null && inspectionUnexpectedError == null ? "ACCEPT" : "REJECT",
                stage = inspectionError?.Stage.ToString(),
                category = inspectionError?.Category.ToString(),
                reason = inspectionError?.Reason,
                unexpectedError = inspectionUnexpectedError?.GetType().FullName,
                unexpectedErrorMessage = inspectionUnexpectedError?.Message,
                acceptedProtocol = inspectedFacts?.Protocol.ToString()
            },
            conversionApplicability = convertRequired ? "REQUIRED" : "NO_PRODUCT_OPERATION_FOR_THIS_CASE",
            conversion = convertRequired
                ? new
                {
                    invoked = true,
                    success = conversionResult?.Success,
                    outputArtifactPath = conversionResult?.OutputArtifact?.Path,
                    failureStage = conversionResult?.ExecutionRecord.Truth.FailureStage.ToString(),
                    failureCategory = conversionResult?.ExecutionRecord.Truth.FailureCategory.ToString(),
                    errorMessage = conversionResult?.ErrorMessage,
                    unexpectedError = conversionUnexpectedError?.GetType().FullName,
                    unexpectedErrorMessage = conversionUnexpectedError?.Message,
                    outputEntriesBefore,
                    outputEntriesAfter
                }
                : null,
            extract = downstreamDisposition,
            clean = downstreamDisposition,
            downstreamRationale,
            workspaceEntriesBefore,
            workspaceEntriesAfterInspection,
            workspaceEntriesAfterOperations,
            derivedSha256Before = derivedHashBefore,
            derivedSha256After = derivedHashAfter,
            canonicalSamplesAfter = sampleIdentityAfter,
            canonicalSamplesUnchanged
        };

        await SaveCaseRecordAsync(caseId, record);
        _output.WriteLine(JsonSerializer.Serialize(record));

        Assert.NotNull(inspectionError);
        Assert.Null(inspectionUnexpectedError);
        Assert.Equal(expectedStage, inspectionError.Stage);
        Assert.Equal(SourceInspectionFailureCategory.Malformed, inspectionError.Category);
        Assert.True(replayBytesMatch, $"{caseId} mutation replay produced different bytes.");
        Assert.Equal(derivedHash, replayHash);
        Assert.NotEqual(canonical.Sha256, derivedHash);
        Assert.True(canonicalSamplesUnchanged, $"{caseId} changed a frozen RealSample.");
        Assert.Equal(derivedHashBefore, derivedHashAfter);
        Assert.Equal(workspaceEntriesBefore, workspaceEntriesAfterInspection);
        Assert.Equal(workspaceEntriesBefore, workspaceEntriesAfterOperations);

        if (convertRequired)
        {
            Assert.Null(conversionUnexpectedError);
            Assert.NotNull(conversionResult);
            Assert.False(conversionResult.Success);
            Assert.Null(conversionResult.OutputArtifact);
            Assert.Equal(ConversionFailureStage.SourceInspection, conversionResult.ExecutionRecord.Truth.FailureStage);
            Assert.Equal(ConversionFailureCategory.SourceInspection, conversionResult.ExecutionRecord.Truth.FailureCategory);
            Assert.Empty(outputEntriesBefore!);
            Assert.Empty(outputEntriesAfter!);
        }
    }

    private async Task<FrozenSample> LoadFrozenSampleAsync(string fileName)
    {
        string sampleRootValue = Environment.GetEnvironmentVariable("P7_R1_R1A_SAMPLE_ROOT")
            ?? throw new InvalidOperationException("P7_R1_R1A_SAMPLE_ROOT must point to .ai-tmp/cache/samples.");
        string sampleRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sampleRootValue));
        string samplePath = Path.GetFullPath(Path.Combine(sampleRoot, fileName));
        Assert.StartsWith(sampleRoot + Path.DirectorySeparatorChar, samplePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(samplePath), $"Frozen RealSample is missing: {samplePath}");

        (long bytes, string sha256) = FrozenSampleIdentity(fileName);
        byte[] content = await File.ReadAllBytesAsync(samplePath);
        Assert.Equal(bytes, content.LongLength);
        Assert.Equal(sha256, Hash(content));
        return new FrozenSample(fileName, samplePath, bytes, sha256, content);
    }

    private static (long Bytes, string Sha256) FrozenSampleIdentity(string fileName) => fileName switch
    {
        "红米老款-GV1.JPG" => (8943926, "48C7490F30C25D5F9E7CFC763D766BA3FDEDA8B12AA72A38426167233276C477"),
        "小米.jpg" => (4038241, "71F49FB21B5A7397D8A0FB499F644A1CD7D701D5C6FA6BBC9BC251E55E27F9E3"),
        "华为Mate80.heic" => (6816675, "904E94CD37CC0336060409C07025C1341A5621EDA3AB53AE2840EE6613F5834A"),
        "一加.jpg" => (13762654, "2065994C59495F65FFDA59D768D6CC2C73E8DFB861CD5D815E1273C82BA54C01"),
        "苹果双文件.HEIC" => (2856220, "868F29D1408D090193D04EBE5C71FEA139705381B9C1B8673C40C62E1A456998"),
        "苹果双文件.MOV" => (2782940, "D47C0E8FC052393E91A6ECB0AA941AFB20758CD244220943E3C06C52BC2DF381"),
        "三星.jpg" => (7749650, "6CAEF9FD8F5AA65CE13BA4282A9BE7773DFBD5BD52ECB568AA3354E71C8C8488"),
        "华为-Mate80.jpg" => (9316483, "317526B2B7B6A5774CC41FBB232FB2526901EE358AB4B470ADEEF7AC3617794A"),
        _ => throw new ArgumentOutOfRangeException(nameof(fileName), fileName, "Not in the frozen R1-A RealSample inventory.")
    };

    private static string GetPlan100WorkspaceRoot()
    {
        string root = Environment.GetEnvironmentVariable("P7_R1_R1A_WORKSPACE")
            ?? throw new InvalidOperationException("P7_R1_R1A_WORKSPACE must point to the stable PLAN100 scratch workspace.");
        return Path.GetFullPath(root);
    }

    private static async Task SaveCaseRecordAsync(string caseId, object record)
    {
        string directory = Path.Combine(GetPlan100WorkspaceRoot(), "case-records");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, caseId + ".json");
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(json);
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string[] SnapshotTreeEntries(string directory) => Directory
        .GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(directory, path))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static byte[] ReplaceJpegXmpAttribute(byte[] bytes, string attribute, string replacement)
    {
        JpegSegment segment = FindJpegXmpApp1(bytes);
        string xml = ReadJpegXmpXml(bytes, segment);
        string attributePrefix = attribute + "=\"";
        int attributeOffset = xml.IndexOf(attributePrefix, StringComparison.Ordinal);
        Assert.True(attributeOffset >= 0, $"Frozen XMP APP1 has no {attribute} attribute.");
        Assert.Equal(-1, xml.IndexOf(attributePrefix, attributeOffset + attributePrefix.Length, StringComparison.Ordinal));
        int valueStart = attributeOffset + attributePrefix.Length;
        int valueEnd = xml.IndexOf('"', valueStart);
        Assert.True(valueEnd > valueStart, $"Frozen {attribute} attribute is not closed.");
        string updatedXml = xml[..valueStart] + replacement + xml[valueEnd..];
        Assert.NotEqual(xml, updatedXml);
        return ReplaceJpegXmpXml(bytes, segment, updatedXml);
    }

    private static byte[] ReplaceMotionPhotoItemLength(byte[] bytes, string replacement)
    {
        JpegSegment segment = FindJpegXmpApp1(bytes);
        string xml = ReadJpegXmpXml(bytes, segment);
        const string semantic = "Item:Semantic=\"MotionPhoto\"";
        int semanticOffset = xml.IndexOf(semantic, StringComparison.Ordinal);
        Assert.True(semanticOffset >= 0, "Frozen XMP APP1 has no MotionPhoto directory item.");
        Assert.Equal(-1, xml.IndexOf(semantic, semanticOffset + semantic.Length, StringComparison.Ordinal));
        int tagStart = xml.LastIndexOf("<Container:Item", semanticOffset, StringComparison.Ordinal);
        int tagEnd = xml.IndexOf('>', semanticOffset);
        Assert.True(tagStart >= 0 && tagEnd > semanticOffset, "MotionPhoto item tag bounds are malformed.");
        string itemTag = xml[tagStart..(tagEnd + 1)];
        const string lengthPrefix = "Item:Length=\"";
        int lengthOffset = itemTag.IndexOf(lengthPrefix, StringComparison.Ordinal);
        Assert.True(lengthOffset >= 0, "Frozen MotionPhoto item has no Item:Length.");
        Assert.Equal(-1, itemTag.IndexOf(lengthPrefix, lengthOffset + lengthPrefix.Length, StringComparison.Ordinal));
        int valueStart = lengthOffset + lengthPrefix.Length;
        int valueEnd = itemTag.IndexOf('"', valueStart);
        Assert.True(valueEnd > valueStart, "MotionPhoto Item:Length value is malformed.");
        string updatedTag = itemTag[..valueStart] + replacement + itemTag[valueEnd..];
        string updatedXml = xml[..tagStart] + updatedTag + xml[(tagEnd + 1)..];
        return ReplaceJpegXmpXml(bytes, segment, updatedXml);
    }

    private static JpegSegment FindJpegXmpApp1(byte[] bytes)
    {
        Assert.True(bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8, "Canonical input is not a JPEG stream.");
        byte[] identifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int cursor = 2;
        while (cursor < bytes.Length)
        {
            Assert.Equal((byte)0xFF, bytes[cursor]);
            int markerOffset = cursor;
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            Assert.True(cursor < bytes.Length, "JPEG marker is incomplete.");
            byte marker = bytes[cursor++];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            Assert.True(cursor + 2 <= bytes.Length, "JPEG marker length is incomplete.");
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(length >= 2, "JPEG marker length is below its two-byte length field.");
            int end = checked(cursor + length);
            Assert.True(end <= bytes.Length, "JPEG marker exceeds the canonical input bounds.");
            int payloadOffset = cursor + 2;
            if (marker == 0xE1 && length - 2 >= identifier.Length &&
                bytes.AsSpan(payloadOffset, identifier.Length).SequenceEqual(identifier))
            {
                return new JpegSegment(markerOffset, payloadOffset, end);
            }
            if (marker == 0xDA) break;
            cursor = end;
        }
        throw new Xunit.Sdk.XunitException("No structurally bounded JPEG XMP APP1 segment was found.");
    }

    private static string ReadJpegXmpXml(byte[] bytes, JpegSegment segment)
    {
        byte[] identifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int xmlStart = segment.PayloadOffset + identifier.Length;
        Assert.True(xmlStart <= segment.EndOffset);
        return Encoding.UTF8.GetString(bytes, xmlStart, segment.EndOffset - xmlStart);
    }

    private static byte[] ReplaceJpegXmpXml(byte[] bytes, JpegSegment segment, string xml)
    {
        byte[] identifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
        byte[] payload = new byte[identifier.Length + xmlBytes.Length];
        Buffer.BlockCopy(identifier, 0, payload, 0, identifier.Length);
        Buffer.BlockCopy(xmlBytes, 0, payload, identifier.Length, xmlBytes.Length);
        int lengthOffset = segment.MarkerOffset + 2;
        int newSegmentLength = checked(payload.Length + 2);
        Assert.InRange(newSegmentLength, 2, ushort.MaxValue);
        int oldSegmentLength = segment.EndOffset - lengthOffset;
        int newSize = checked(bytes.Length + newSegmentLength - oldSegmentLength);
        byte[] result = new byte[newSize];
        Buffer.BlockCopy(bytes, 0, result, 0, segment.PayloadOffset);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(lengthOffset, 2), checked((ushort)newSegmentLength));
        Buffer.BlockCopy(payload, 0, result, segment.PayloadOffset, payload.Length);
        Buffer.BlockCopy(bytes, segment.EndOffset, result, segment.PayloadOffset + payload.Length, bytes.Length - segment.EndOffset);
        return result;
    }

    private static byte[] TruncateBeforeJpegXmpApp1End(byte[] bytes)
    {
        JpegSegment segment = FindJpegXmpApp1(bytes);
        Assert.True(segment.EndOffset > segment.PayloadOffset);
        return bytes[..(segment.EndOffset - 1)];
    }

    private static byte[] SetFirstIlocExtentCountToMaximum(byte[] bytes)
    {
        IsoBox meta = FindIsoBox(bytes, 0, bytes.Length, "meta");
        IsoBox iloc = FindIsoBox(bytes, meta.PayloadOffset + 4, meta.EndOffset, "iloc");
        int extentCountOffset = FindFirstIlocExtentCountOffset(bytes, iloc);
        ushort canonicalCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(extentCountOffset, 2));
        Assert.True(canonicalCount > 0 && canonicalCount < ushort.MaxValue);
        byte[] result = bytes.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(extentCountOffset, 2), ushort.MaxValue);
        return result;
    }

    private static byte[] TruncateBeforeHeifIlocEnd(byte[] bytes)
    {
        IsoBox meta = FindIsoBox(bytes, 0, bytes.Length, "meta");
        IsoBox iloc = FindIsoBox(bytes, meta.PayloadOffset + 4, meta.EndOffset, "iloc");
        Assert.True(iloc.EndOffset > iloc.PayloadOffset);
        return bytes[..(iloc.EndOffset - 1)];
    }

    private static int FindFirstIlocExtentCountOffset(byte[] bytes, IsoBox iloc)
    {
        int cursor = iloc.PayloadOffset;
        byte version = bytes[cursor];
        Assert.InRange(version, (byte)0, (byte)2);
        cursor += 4; // FullBox version and flags.
        byte sizes = bytes[cursor++];
        int offsetSize = sizes >> 4;
        int lengthSize = sizes & 0x0F;
        byte baseAndIndex = bytes[cursor++];
        int baseOffsetSize = baseAndIndex >> 4;
        int indexSize = version >= 1 ? baseAndIndex & 0x0F : 0;
        Assert.True(IsSupportedIlocFieldWidth(offsetSize) && IsSupportedIlocFieldWidth(lengthSize) &&
            IsSupportedIlocFieldWidth(baseOffsetSize) && IsSupportedIlocFieldWidth(indexSize));
        uint itemCount = version >= 2
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
        cursor += version >= 2 ? 4 : 2;
        Assert.InRange(itemCount, 1u, 1024u);
        int itemIdWidth = version >= 2 ? 4 : 2;
        for (uint item = 0; item < itemCount; item++)
        {
            Assert.True(cursor <= iloc.EndOffset - itemIdWidth);
            cursor += itemIdWidth;
            if (version >= 1)
            {
                Assert.True(cursor <= iloc.EndOffset - 2);
                cursor += 2; // construction_method
            }
            Assert.True(cursor <= iloc.EndOffset - (2 + baseOffsetSize));
            cursor += 2; // data_reference_index
            cursor += baseOffsetSize;
            int extentCountOffset = cursor;
            Assert.True(cursor <= iloc.EndOffset - 2);
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
            if (item == 0) return extentCountOffset;
            for (int extent = 0; extent < extentCount; extent++)
            {
                int fieldsWidth = indexSize + offsetSize + lengthSize;
                Assert.True(cursor <= iloc.EndOffset - fieldsWidth);
                cursor += fieldsWidth;
            }
        }
        throw new Xunit.Sdk.XunitException("The bounded iloc box contained no first item extent_count.");
    }

    private static bool IsSupportedIlocFieldWidth(int width) => width is 0 or 1 or 2 or 4 or 8;

    private static byte[] TruncateBeforeAppleContentIdentifierDataEnd(byte[] bytes)
    {
        IsoBox moov = FindIsoBox(bytes, 0, bytes.Length, "moov");
        IsoBox meta = FindIsoBox(bytes, moov.PayloadOffset, moov.EndOffset, "meta");
        IReadOnlyList<IsoBox> metaChildren = ReadIsoBoxes(bytes, meta.PayloadOffset, meta.EndOffset);
        IsoBox keys = Assert.Single(metaChildren, box => box.Type == "keys");
        IsoBox ilst = Assert.Single(metaChildren, box => box.Type == "ilst");
        int keysCursor = keys.PayloadOffset;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(keysCursor, 4)));
        uint keyCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(keysCursor + 4, 4));
        Assert.InRange(keyCount, 1u, 1024u);
        keysCursor += 8;
        uint contentIdentifierIndex = 0;
        for (uint index = 1; index <= keyCount; index++)
        {
            Assert.True(keysCursor <= keys.EndOffset - 8);
            uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(keysCursor, 4));
            Assert.True(entrySize >= 8 && entrySize <= keys.EndOffset - keysCursor);
            Assert.Equal("mdta", Encoding.ASCII.GetString(bytes, keysCursor + 4, 4));
            string key = Encoding.UTF8.GetString(bytes, keysCursor + 8, checked((int)entrySize - 8));
            if (key == "com.apple.quicktime.content.identifier")
            {
                Assert.Equal(0u, contentIdentifierIndex);
                contentIdentifierIndex = index;
            }
            keysCursor += checked((int)entrySize);
        }
        Assert.True(contentIdentifierIndex > 0);

        IsoBox item = Assert.Single(ReadIsoBoxes(bytes, ilst.PayloadOffset, ilst.EndOffset),
            box => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(box.Offset + 4, 4)) == contentIdentifierIndex);
        IsoBox value = Assert.Single(ReadIsoBoxes(bytes, item.PayloadOffset, item.EndOffset), box => box.Type == "data");
        Assert.True(value.EndOffset > value.PayloadOffset + 8);
        return bytes[..(value.EndOffset - 1)];
    }

    private static byte[] TruncateSamsungMotionEntryByOneByte(byte[] bytes)
    {
        SefEntry motionEntry = FindSamsungMotionEntry(bytes);
        Assert.True(motionEntry.Size > 24);
        byte[] result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(motionEntry.SizeOffset, 4), motionEntry.Size - 1);
        return result;
    }

    private static SefEntry FindSamsungMotionEntry(byte[] bytes)
    {
        int footer = bytes.Length - 8;
        Assert.True(footer >= 12);
        Assert.Equal("SEFT", Encoding.ASCII.GetString(bytes, footer + 4, 4));
        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footer, 4));
        Assert.InRange(totalSize, 12u, checked((uint)(footer)));
        int sefh = checked(footer - (int)totalSize);
        Assert.Equal("SEFH", Encoding.ASCII.GetString(bytes, sefh, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sefh + 8, 4));
        Assert.True(count > 0 && count <= (footer - sefh - 12) / 12);
        Assert.Equal(footer, sefh + 12 + checked((int)count * 12));
        for (int index = 0; index < count; index++)
        {
            int entry = sefh + 12 + index * 12;
            ushort marker = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 2, 2));
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8, 4));
            if (marker == 0x0A30)
            {
                Assert.True(entrySize >= 24);
                return new SefEntry(entry + 8, entrySize);
            }
        }
        throw new Xunit.Sdk.XunitException("The bounded SEF directory has no MotionPhoto_Data index entry.");
    }

    private static byte[] TruncateHuaweiLiveFieldByOneByte(byte[] bytes)
    {
        int liveFieldOffset = bytes.Length - 20;
        Assert.True(liveFieldOffset >= 0);
        Assert.Equal("LIVE_", Encoding.ASCII.GetString(bytes, liveFieldOffset, 5));
        int firstDigit = liveFieldOffset + 5;
        int digitEnd = firstDigit;
        while (digitEnd < bytes.Length && bytes[digitEnd] is >= (byte)'0' and <= (byte)'9') digitEnd++;
        Assert.True(digitEnd - firstDigit >= 2, "Frozen Huawei LIVE_ value has too few decimal digits.");
        for (int cursor = digitEnd; cursor < bytes.Length; cursor++)
        {
            Assert.True(bytes[cursor] is (byte)' ' or 0, "Frozen Huawei LIVE_ field has unsupported trailing bytes.");
        }
        byte[] result = bytes.ToArray();
        result[digitEnd - 1] = (byte)' ';
        return result;
    }

    private static IsoBox FindIsoBox(byte[] bytes, int start, int end, string type)
    {
        Assert.True(start >= 0 && end >= start && end <= bytes.Length);
        int cursor = start;
        while (cursor < end)
        {
            IsoBox box = ReadIsoBoxAt(bytes, cursor, end);
            if (box.Type == type) return box;
            cursor = box.EndOffset;
        }
        throw new Xunit.Sdk.XunitException($"Bounded ISO-BMFF hierarchy has no {type} box.");
    }

    private static IReadOnlyList<IsoBox> ReadIsoBoxes(byte[] bytes, int start, int end)
    {
        Assert.True(start >= 0 && end >= start && end <= bytes.Length);
        List<IsoBox> boxes = [];
        int cursor = start;
        while (cursor < end)
        {
            IsoBox box = ReadIsoBoxAt(bytes, cursor, end);
            boxes.Add(box);
            cursor = box.EndOffset;
        }
        Assert.Equal(end, cursor);
        return boxes;
    }

    private static IsoBox ReadIsoBoxAt(byte[] bytes, int offset, int parentEnd)
    {
        Assert.True(offset >= 0 && parentEnd >= offset && parentEnd <= bytes.Length);
        Assert.True(parentEnd - offset >= 8, "ISO-BMFF child region ends inside a box header.");
        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
        int headerSize = 8;
        ulong size = size32;
        if (size32 == 1)
        {
            Assert.True(parentEnd - offset >= 16, "Extended ISO-BMFF box header is truncated.");
            size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            size = checked((ulong)(parentEnd - offset));
        }
        Assert.True(size >= (ulong)headerSize && size <= (ulong)(parentEnd - offset), $"ISO-BMFF {type} box exceeds its parent bounds.");
        return new IsoBox(offset, checked((int)size), headerSize, type);
    }

    private sealed record FrozenSample(string FileName, string Path, long Bytes, string Sha256, byte[] Content);
    private readonly record struct JpegSegment(int MarkerOffset, int PayloadOffset, int EndOffset);
    private readonly record struct IsoBox(int Offset, int Size, int HeaderSize, string Type)
    {
        public int PayloadOffset => checked(Offset + HeaderSize);
        public int EndOffset => checked(Offset + Size);
    }
    private readonly record struct SefEntry(int SizeOffset, uint Size);

    private static byte[] CreateXmpJpeg() => SyntheticProtocolFixtures.CreateJpegWithXmp(
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");

    private static byte[] CreateGoogleV1JpegWithValidOffset()
    {
        byte[] mp4 = SyntheticProtocolFixtures.CreateMinimalMp4();
        string offset = mp4.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MicroVideo=\"1\" GCamera:MicroVideoVersion=\"1\" GCamera:MicroVideoOffset=\"{offset}\" /></rdf:RDF></x:xmpmeta>";
        return [.. SyntheticProtocolFixtures.CreateJpegWithXmp(xmp), .. mp4];
    }

    private static byte[] ReplaceGoogleV1Offset(byte[] bytes, string newOffset)
    {
        int marker = FindBytes(bytes, [0xFF, 0xE1]);
        Assert.True(marker >= 0, "Canonical Google V1 fixture must contain an APP1 marker.");
        ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(marker + 2, 2));
        Assert.InRange(segmentLength, (ushort)2, (ushort)(bytes.Length - marker - 2));
        int payloadStart = marker + 4;
        int payloadLength = segmentLength - 2;
        int segmentEnd = marker + 2 + segmentLength;
        string payload = Encoding.UTF8.GetString(bytes, payloadStart, payloadLength);
        string oldValue = SyntheticProtocolFixtures.CreateMinimalMp4().Length
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        string oldAttribute = $"GCamera:MicroVideoOffset=\"{oldValue}\"";
        string newAttribute = $"GCamera:MicroVideoOffset=\"{newOffset}\"";
        Assert.Contains(oldAttribute, payload, StringComparison.Ordinal);
        string mutatedPayload = payload.Replace(oldAttribute, newAttribute, StringComparison.Ordinal);
        Assert.NotEqual(payload, mutatedPayload);

        byte[] mutatedPayloadBytes = Encoding.UTF8.GetBytes(mutatedPayload);
        int newSegmentLength = checked(mutatedPayloadBytes.Length + 2);
        Assert.InRange(newSegmentLength, 2, ushort.MaxValue);
        using var result = new MemoryStream(bytes.Length + mutatedPayloadBytes.Length - payloadLength);
        result.Write(bytes.AsSpan(0, marker + 2));
        Span<byte> encodedLength = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(encodedLength, checked((ushort)newSegmentLength));
        result.Write(encodedLength);
        result.Write(mutatedPayloadBytes);
        result.Write(bytes.AsSpan(segmentEnd));
        return result.ToArray();
    }

    private static byte[] CreateSamsungHeic()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("canonical-heif", ".heic");
        SyntheticProtocolFixtures.CreateSamsungHeic(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] CreateSamsungSefJpeg()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("canonical-sef", ".jpg");
        SyntheticProtocolFixtures.CreateSamsungSefJpeg(path);
        return File.ReadAllBytes(path);
    }

    private static int FindSefDirectoryEntry(byte[] bytes)
    {
        int footer = bytes.Length - 8;
        Assert.True(footer >= 0);
        Assert.Equal("SEFT", Encoding.ASCII.GetString(bytes, footer + 4, 4));
        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footer, 4));
        Assert.InRange(totalSize, 24u, (uint)footer);
        int directory = checked(footer - (int)totalSize);
        int sefh = FindBytes(bytes.AsSpan(directory, (int)totalSize).ToArray(), Encoding.ASCII.GetBytes("SEFH"));
        Assert.True(sefh >= 0);
        sefh = checked(directory + sefh);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sefh + 8, 4));
        Assert.True(count > 0);
        int firstEntry = checked(sefh + 12);
        Assert.True(firstEntry + 12 <= footer);
        return firstEntry;
    }

    private static int FindTopLevelBox(byte[] bytes, string type)
    {
        int cursor = 0;
        while (cursor <= bytes.Length - 8)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));
            Assert.InRange(size, 8u, (uint)(bytes.Length - cursor));
            if (Encoding.ASCII.GetString(bytes, cursor + 4, 4) == type)
            {
                return cursor;
            }
            cursor = checked(cursor + (int)size);
        }
        throw new Xunit.Sdk.XunitException($"Top-level box '{type}' was not found in the canonical fixture.");
    }

    private static int FindChildBox(byte[] bytes, int parentOffset, string type, int fullBoxPrefixLength = 0)
    {
        uint parentSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(parentOffset, 4));
        Assert.InRange(parentSize, (uint)(8 + fullBoxPrefixLength), (uint)(bytes.Length - parentOffset));
        int cursor = checked(parentOffset + 8 + fullBoxPrefixLength);
        int end = checked(parentOffset + (int)parentSize);
        while (cursor <= end - 8)
        {
            uint childSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));
            Assert.InRange(childSize, 8u, (uint)(end - cursor));
            if (Encoding.ASCII.GetString(bytes, cursor + 4, 4) == type)
            {
                return cursor;
            }
            cursor = checked(cursor + (int)childSize);
        }
        throw new Xunit.Sdk.XunitException($"Child box '{type}' was not found in the canonical fixture.");
    }

    private static int FindBytes(byte[] bytes, byte[] needle) => FindBytes(bytes.AsSpan(), needle);

    private static int FindBytes(ReadOnlySpan<byte> bytes, byte[] needle)
    {
        int offset = bytes.IndexOf(needle);
        return offset;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class R1MetadataCollisionTests
{
    private const string GoogleCamera = "http://ns.google.com/photos/1.0/camera/";
    private const string GoogleContainer = "http://ns.google.com/photos/1.0/container/";
    private const string GoogleItem = "http://ns.google.com/photos/1.0/container/item/";
    private const string AdobeXmpHeader = "http://ns.adobe.com/xap/1.0/\0";
    private const string CaseEvidenceEnvironmentVariable = "LPB_R1B_CASE_EVIDENCE_ROOT";

    private readonly ITestOutputHelper _output;

    public R1MetadataCollisionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task DuplicateConflictingMetadata_IsMalformedProtocol()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = BuildGoogleV2Jpeg(duplicateMotionPhotoElement: "0");
        byte[] replay = BuildGoogleV2Jpeg(duplicateMotionPhotoElement: "0");
        return AssertRejectedAsync("MD-001", "synthetic-google-v2-valid", canonical, derived, replay,
            "same rdf:Description has GCamera:MotionPhoto attribute=1 and child element=0",
            SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task DuplicateAuthoritativeXmpPackets_AmbiguousMetadata()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = DuplicateFirstStandardXmpApp1(canonical);
        byte[] replay = DuplicateFirstStandardXmpApp1(BuildGoogleV2Jpeg());
        return AssertRejectedAsync("MD-002", "synthetic-google-v2-one-authoritative-app1", canonical, derived, replay,
            "duplicate the byte-identical authoritative standard-XMP APP1 segment once before SOS",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Metadata);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public async Task SplitDescriptionOwners_AmbiguousMetadata()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = await BuildFixtureBytesAsync(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithCrossDescriptionOwner, "md003-derived");
        byte[] replay = await BuildFixtureBytesAsync(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithCrossDescriptionOwner, "md003-replay");
        await AssertRejectedAsync("MD-003", "synthetic-google-v2-one-rdf-description-owner", canonical, derived, replay,
            "split GCamera properties and Container:Directory across two rdf:Description owners",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Metadata);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task WrongNamespaceGoogleLocalNames_DoNotClaimGoogle()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = BuildWrongNamespaceClaimWithPlausibleVideo();
        byte[] replay = BuildWrongNamespaceClaimWithPlausibleVideo();
        return AssertRejectedAsync("NS-001", "synthetic-google-v2-valid", canonical, derived, replay,
            "bind MotionPhoto, MotionPhotoVersion, Directory, Item, Mime, Semantic and Length only to unrelated namespace URIs",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Pairing);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public async Task CustomSameLocalName_DoesNotShadowGoogle()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = await BuildFixtureBytesAsync(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithNormalMotionPhotoText, "ns002-derived");
        byte[] replay = await BuildFixtureBytesAsync(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithNormalMotionPhotoText, "ns002-replay");
        await AssertIdentifiedAsync("NS-002", "synthetic-google-v2-valid", canonical, derived, replay,
            "add unrelated Other:MotionPhoto and same-local-name Container-like properties without changing the authoritative Google claim",
            SourceProtocol.GoogleMotionPhotoV2,
            "separate-clean-path-test=CleanerTrustChainTests.Clean_Native_Xmp_CustomNamespaceSameLocalName_PreservedIntact");
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task XmpAfterJpegEoi_IsNotAuthoritative()
    {
        byte[] canonicalJpeg = SyntheticProtocolFixtures.CreateMinimalJpeg();
        byte[] canonical = [.. canonicalJpeg, .. SyntheticProtocolFixtures.CreateMinimalMp4()];
        byte[] derived = AddFakeXmpAfterEoi(canonicalJpeg);
        byte[] replay = AddFakeXmpAfterEoi(SyntheticProtocolFixtures.CreateMinimalJpeg());
        return AssertRejectedAsync("FM-001", "synthetic-minimal-jpeg-plus-plausible-mp4", canonical, derived, replay,
            "insert complete-looking Google XMP/XML bytes after JPEG EOI and before a plausible MP4 tail",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Pairing);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task ProtocolTextInJpegScan_IsNotAuthoritative()
    {
        byte[] canonicalJpeg = SyntheticProtocolFixtures.CreateMinimalJpeg();
        byte[] canonical = [.. canonicalJpeg, .. SyntheticProtocolFixtures.CreateMinimalMp4()];
        byte[] derived = AddFakeXmpInsideEntropy(canonicalJpeg);
        byte[] replay = AddFakeXmpInsideEntropy(SyntheticProtocolFixtures.CreateMinimalJpeg());
        return AssertRejectedAsync("FM-002", "synthetic-minimal-jpeg-plus-plausible-mp4", canonical, derived, replay,
            "insert complete-looking Google XMP/XML bytes in JPEG scan entropy after SOS and before EOI; add no APP1 XMP segment",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Pairing);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task HuaweiAndGoogleV2DualOwnership_IsAmbiguousProtocol()
    {
        byte[] canonical = SyntheticProtocolFixtures.CreateGoogleV2JpegWithHuaweiFooter(includeHuaweiClaim: false);
        byte[] derived = SyntheticProtocolFixtures.CreateGoogleV2JpegWithHuaweiFooter(includeHuaweiClaim: true);
        byte[] replay = SyntheticProtocolFixtures.CreateGoogleV2JpegWithHuaweiFooter(includeHuaweiClaim: true);
        return AssertRejectedAsync("CP-001", "synthetic-google-v2-with-neutral-footer", canonical, derived, replay,
            "set a valid Huawei LIVE_ footer and MP4-size-plus-20 identity while retaining structurally complete Google V2 ownership",
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Protocol);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public async Task HuaweiWithIncompleteGoogleV2Claim_RemainsHuawei()
    {
        byte[] canonical = await BuildFixtureBytesAsync(SyntheticProtocolFixtures.CreateHuaweiJpeg, "cp002-canonical");
        byte[] derived = SyntheticProtocolFixtures.CreateHuaweiJpegWithIncompleteGoogleV2Claim();
        byte[] replay = SyntheticProtocolFixtures.CreateHuaweiJpegWithIncompleteGoogleV2Claim();
        await AssertIdentifiedAsync("CP-002", "synthetic-huawei-moving-photo-valid", canonical, derived, replay,
            "add incomplete Google MotionPhoto attribute without a complete Google V2 version/directory claim",
            SourceProtocol.HuaweiMovingPhoto);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task GoogleV2WithWrongNamespaceOppoClaim_RemainsGoogle()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        const string invalidOppoClaim = " xmlns:OpCamera=\"urn:example:oplus-camera/\" OpCamera:MotionPhotoOwner=\"oplus\" OpCamera:OLivePhotoVersion=\"2\" OpCamera:VideoLength=\"100\"";
        byte[] derived = BuildGoogleV2Jpeg(additionalDescriptionAttributes: invalidOppoClaim);
        byte[] replay = BuildGoogleV2Jpeg(additionalDescriptionAttributes: invalidOppoClaim);
        return AssertIdentifiedAsync("CP-003", "synthetic-google-v2-valid", canonical, derived, replay,
            "add syntactically plausible OPlus owner/version/video-length properties under a non-OPlus URI",
            SourceProtocol.GoogleMotionPhotoV2);
    }

    private async Task AssertRejectedAsync(string caseId, string canonicalIdentity, byte[] canonical,
        byte[] derived, byte[] replay, string mutation, SourceInspectionFailureCategory expectedCategory,
        SourceInspectionStage expectedStage)
    {
        Assert.Equal(derived, replay);
        string canonicalSha = Sha256(canonical);
        string derivedSha = Sha256(derived);
        string replaySha = Sha256(replay);
        Assert.Equal(derivedSha, replaySha);
        string artifact = PersistDerivedEvidence(caseId, derived);

        using var workspace = new TestFixtureWorkspace();
        string inputPath = workspace.AllocateFilePath(caseId, ".jpg");
        await File.WriteAllBytesAsync(inputPath, derived);
        string beforeSha = await ComputeSha256Async(inputPath);
        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(inputPath));
        string afterSha = await ComputeSha256Async(inputPath);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);

        Assert.Equal(expectedCategory, error.Category);
        Assert.Equal(expectedStage, error.Stage);
        Assert.Equal(beforeSha, afterSha);
        Assert.Equal(new[] { Path.GetFileName(inputPath) }, workspaceFiles);
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, caseId + "-output.jpg")));

        string conversionDirectory = Path.Combine(workspace.RootDirectory, "conversion-output");
        Directory.CreateDirectory(conversionDirectory);
        ImageConversionResult conversion = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = inputPath, Kind = MediaArtifactKind.PrimaryImage, MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg, ByteLength = derived.LongLength
            },
            TargetContainer = ImageContainer.Heic, TargetDirectory = conversionDirectory
        });
        Assert.False(conversion.Success);
        Assert.Null(conversion.OutputArtifact);
        Assert.Equal(ConversionFailureStage.SourceInspection, conversion.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.SourceInspection, conversion.ExecutionRecord.Truth.FailureCategory);
        Assert.Empty(Directory.GetFileSystemEntries(conversionDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(beforeSha, await ComputeSha256Async(inputPath));
        Assert.Equal(workspaceFiles, GetWorkspaceFileNames(workspace));
        _output.WriteLine($"R1B_CONVERT|id={caseId}|invoked=true|success=false|failureStage=SourceInspection|failureCategory=SourceInspection|outputPublished=false|sourceUnchanged=true|unexplainedResidue=false");

        _output.WriteLine(
            $"R1B_CASE|id={caseId}|canonical={canonicalIdentity}|mutation={mutation}|canonicalBytes={canonical.Length}|canonicalSha256={canonicalSha}|derivedBytes={derived.Length}|derivedSha256={derivedSha}|replayBytes={replay.Length}|replaySha256={replaySha}|expectedProtocol=none|actualProtocol=none|expectedCategory={expectedCategory}|actualCategory={error.Category}|expectedStage={expectedStage}|actualStage={error.Stage}|downstream=NOT_REACHED_AFTER_INSPECTOR_REJECTION(reason={error.Stage}/{error.Category})|residuePolicy=input-unchanged-no-output|sourceSha256Before={beforeSha}|sourceSha256After={afterSha}|workspaceFiles={string.Join(",", workspaceFiles)}|outputExists=false|artifact={artifact}");
    }

    private async Task AssertIdentifiedAsync(string caseId, string canonicalIdentity, byte[] canonical,
        byte[] derived, byte[] replay, string mutation, SourceProtocol expectedProtocol,
        string downstreamEvidence = "InspectorOnly")
    {
        Assert.Equal(derived, replay);
        string canonicalSha = Sha256(canonical);
        string derivedSha = Sha256(derived);
        string replaySha = Sha256(replay);
        Assert.Equal(derivedSha, replaySha);
        string artifact = PersistDerivedEvidence(caseId, derived);

        using var workspace = new TestFixtureWorkspace();
        string inputPath = workspace.AllocateFilePath(caseId, ".jpg");
        await File.WriteAllBytesAsync(inputPath, derived);
        string beforeSha = await ComputeSha256Async(inputPath);
        var facts = await new SourceInspector().InspectAsync(inputPath);
        string afterSha = await ComputeSha256Async(inputPath);
        string[] workspaceFiles = GetWorkspaceFileNames(workspace);

        Assert.Equal(expectedProtocol, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(beforeSha, afterSha);
        Assert.Equal(new[] { Path.GetFileName(inputPath) }, workspaceFiles);
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, caseId + "-output.jpg")));

        _output.WriteLine(
            $"R1B_CASE|id={caseId}|canonical={canonicalIdentity}|mutation={mutation}|canonicalBytes={canonical.Length}|canonicalSha256={canonicalSha}|derivedBytes={derived.Length}|derivedSha256={derivedSha}|replayBytes={replay.Length}|replaySha256={replaySha}|expectedProtocol={expectedProtocol}|actualProtocol={facts.Protocol}|expectedCategory=none|actualCategory=none|expectedStage=none|actualStage=none|downstream={downstreamEvidence}|residuePolicy=input-unchanged-no-output|sourceSha256Before={beforeSha}|sourceSha256After={afterSha}|workspaceFiles={string.Join(",", workspaceFiles)}|outputExists=false|artifact={artifact}");
    }

    private static byte[] BuildGoogleV2Jpeg(string additionalDescriptionAttributes = "",
        string? duplicateMotionPhotoElement = null)
    {
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        string duplicateElement = duplicateMotionPhotoElement is null
            ? string.Empty
            : $"<GCamera:MotionPhoto>{duplicateMotionPhotoElement}</GCamera:MotionPhoto>";
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"{GoogleCamera}\" xmlns:Container=\"{GoogleContainer}\" xmlns:Item=\"{GoogleItem}\"{additionalDescriptionAttributes} GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\">{duplicateElement}<Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{video.Length}\" Item:Padding=\"0\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        return [.. jpeg, .. video];
    }

    private static byte[] BuildWrongNamespaceClaimWithPlausibleVideo()
    {
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:Other=\"urn:example:camera\" xmlns:WrongContainer=\"urn:example:container\" xmlns:WrongItem=\"urn:example:item\" Other:MotionPhoto=\"1\" Other:MotionPhotoVersion=\"1\"><WrongContainer:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><WrongContainer:Item WrongItem:Mime=\"image/jpeg\" WrongItem:Semantic=\"Primary\" WrongItem:Length=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><WrongContainer:Item WrongItem:Mime=\"video/mp4\" WrongItem:Semantic=\"MotionPhoto\" WrongItem:Length=\"{video.Length}\" /></rdf:li></rdf:Seq></WrongContainer:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        return [.. jpeg, .. video];
    }

    private static byte[] AddFakeXmpAfterEoi(byte[] jpeg)
    {
        string fakeXmp = BuildGoogleV2Xmp();
        return [.. jpeg, .. Encoding.UTF8.GetBytes(fakeXmp), .. SyntheticProtocolFixtures.CreateMinimalMp4()];
    }

    private static byte[] AddFakeXmpInsideEntropy(byte[] jpeg)
    {
        int eoi = FindMarker(jpeg, 0xD9, 0);
        if (eoi < 0) throw new InvalidDataException("Synthetic JPEG fixture has no EOI marker.");
        byte[] fakeXmp = Encoding.UTF8.GetBytes(BuildGoogleV2Xmp());
        byte[] fakeJpeg = [.. jpeg.AsSpan(0, eoi), .. fakeXmp, .. jpeg.AsSpan(eoi)];
        return [.. fakeJpeg, .. SyntheticProtocolFixtures.CreateMinimalMp4()];
    }

    private static string BuildGoogleV2Xmp()
    {
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        return $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"{GoogleCamera}\" xmlns:Container=\"{GoogleContainer}\" xmlns:Item=\"{GoogleItem}\" GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{video.Length}\" Item:Padding=\"0\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
    }

    private static byte[] DuplicateFirstStandardXmpApp1(byte[] jpeg)
    {
        byte[] header = Encoding.ASCII.GetBytes(AdobeXmpHeader);
        int position = 2;
        while (position + 4 <= jpeg.Length)
        {
            int segmentStart = position;
            if (jpeg[position] != 0xFF) throw new InvalidDataException("Invalid marker before JPEG SOS.");
            while (position < jpeg.Length && jpeg[position] == 0xFF) position++;
            if (position >= jpeg.Length) break;
            byte marker = jpeg[position++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7) continue;
            if (position + 2 > jpeg.Length) throw new InvalidDataException("Truncated JPEG segment length.");
            int segmentLength = (jpeg[position] << 8) | jpeg[position + 1];
            int segmentEnd = checked(position + segmentLength);
            if (segmentLength < 2 || segmentEnd > jpeg.Length)
                throw new InvalidDataException("Invalid JPEG segment length.");
            int payloadStart = position + 2;
            int payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength >= header.Length &&
                jpeg.AsSpan(payloadStart, header.Length).SequenceEqual(header))
            {
                return [.. jpeg.AsSpan(0, segmentStart), .. jpeg.AsSpan(segmentStart, segmentEnd - segmentStart),
                    .. jpeg.AsSpan(segmentStart, segmentEnd - segmentStart), .. jpeg.AsSpan(segmentEnd)];
            }
            position = segmentEnd;
        }
        throw new InvalidDataException("No standard XMP APP1 segment found.");
    }

    private static int FindMarker(byte[] bytes, byte marker, int start)
    {
        for (int i = start; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == marker) return i;
        }
        return -1;
    }

    private static async Task<byte[]> BuildFixtureBytesAsync(Action<string> create, string name)
    {
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath(name, ".jpg");
        create(path);
        return await File.ReadAllBytesAsync(path);
    }

    private static string PersistDerivedEvidence(string caseId, byte[] derived)
    {
        string? root = Environment.GetEnvironmentVariable(CaseEvidenceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root)) return "none";
        string caseDirectory = Path.Combine(root, caseId);
        Directory.CreateDirectory(caseDirectory);
        string path = Path.Combine(caseDirectory, "input.jpg");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(derived);
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static string[] GetWorkspaceFileNames(IMediaWorkspace workspace) =>
        Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static async Task<string> ComputeSha256Async(string path) => Sha256(await File.ReadAllBytesAsync(path));

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

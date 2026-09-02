using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class SyntheticProtocolCleanerTests
{
    private readonly SourceInspector _inspector = new();
    private readonly SourceExtractor _extractor = new();
    private readonly SourceProtocolCleaner _cleaner = new();

    [Fact]
    public async Task Clean_GoogleV1_Synthetic_StripsXmpAndTrailer()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("google_v1", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1Jpeg(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        Assert.NotEmpty(cleanResult.RemovedFacts);

        // Assert exact byte marker removal
        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("MicroVideo", cleanText);
    }

    [Fact]
    public async Task Clean_GoogleV2_Synthetic_StripsMotionPhotoPreservesGainMap()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("google_v2", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2Jpeg(inputPath, withGainMap: true);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        // Assert exact byte marker removal and preservation
        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("MotionPhoto", cleanText);
        Assert.Contains("GainMap", cleanText);
    }

    [Fact]
    public async Task Clean_Oppo_Synthetic_StripsOLivePhoto()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo", ".jpg");
        SyntheticProtocolFixtures.CreateOppoJpeg(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("OLivePhotoVersion", cleanText);
        Assert.DoesNotContain("VideoLength", cleanText);
        Assert.DoesNotContain("MotionPhotoOwner", cleanText);
        Assert.DoesNotContain("MotionPhotoPrimaryPresentationTimestampUs", cleanText);
        Assert.DoesNotContain("MotionPhotoEnable", cleanText);
    }

    [Fact]
    public async Task Clean_VivoX300_Synthetic_StripsVCamera()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_x300", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300Jpeg(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.VivoLivePhoto, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("VMotionPhotoVersion", cleanText);
    }

    [Fact]
    public async Task Clean_VivoLegacyDual_Synthetic_StripsCameralbumAndUuid()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("vivo_dual", ".jpg");
        string vidPath = ws.AllocateFilePath("vivo_dual", ".mp4");
        SyntheticProtocolFixtures.CreateVivoLegacyDualJpeg(imgPath);
        SyntheticProtocolFixtures.CreateVivoLegacyDualMp4(vidPath);

        var facts = await _inspector.InspectAsync(imgPath, vidPath);
        Assert.Equal(SourceProtocol.VivoLegacyDualFile, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, imgPath, vidPath, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        Assert.NotNull(cleanResult.CleanedVideo);

        // Assert exact byte marker removal
        byte[] cleanImg = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string imgText = Encoding.UTF8.GetString(cleanImg);
        Assert.DoesNotContain("cameralbum!", imgText);

        byte[] cleanVid = await File.ReadAllBytesAsync(cleanResult.CleanedVideo.Path);
        string vidText = Encoding.UTF8.GetString(cleanVid);
        Assert.DoesNotContain("vivoMediaExtInfo", vidText);
    }

    [Fact]
    public async Task Clean_SamsungSef_Synthetic_StripsMotionPhotoPreservesDualCam()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("samsung_sef", ".jpg");
        SyntheticProtocolFixtures.CreateSamsungSefJpeg(inputPath, includeNonLiveTag: true);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoJpeg, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        // Assert 0x0A30 is removed but SEF with DualCam (0x0A01) is preserved
        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("MotionPhoto_Data", cleanText);
        Assert.Contains("DualCam_DataNON_LIVE_METADATA", cleanText);
        Assert.Contains("SEFT", cleanText);
    }

    [Fact]
    public async Task Clean_SamsungHeic_Synthetic_StripsMpvdBox()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("samsung", ".heic");
        SyntheticProtocolFixtures.CreateSamsungHeic(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoHeic, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.DoesNotContain("mpvd", cleanText);
    }

    [Fact]
    public async Task Clean_Huawei_Synthetic_StripsLiveTail()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("huawei", ".jpg");
        SyntheticProtocolFixtures.CreateHuaweiJpeg(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        // Clean JPEG should end with 0xFF 0xD9 and not have 60-byte LIVE_ tail
        Assert.Equal(0xFF, cleanBytes[^2]);
        Assert.Equal(0xD9, cleanBytes[^1]);
    }

    [Fact]
    public async Task Clean_Apple_Synthetic_Strips0x0011AndMdia()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("apple", ".jpg");
        string movPath = ws.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var facts = await _inspector.InspectAsync(imgPath, movPath);
        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, imgPath, movPath, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        Assert.NotNull(cleanResult.CleanedVideo);

        // Assert MOV mdta key is removed
        byte[] cleanMov = await File.ReadAllBytesAsync(cleanResult.CleanedVideo.Path);
        string movText = Encoding.UTF8.GetString(cleanMov);
        Assert.DoesNotContain("com.apple.quicktime.content.identifier", movText);
        Assert.DoesNotContain("mebx", movText);
    }

    [Fact]
    public async Task Clean_Idempotency_Synthetic()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("google_v2_idem", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2Jpeg(inputPath, withGainMap: true);

        var facts = await _inspector.InspectAsync(inputPath, null);
        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult1 = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult1.Success, cleanResult1.ErrorMessage);

        // Second clean on already cleaned artifact
        var recheckFacts = await _inspector.InspectAsync(cleanResult1.CleanedImage!.Path, null);
        Assert.Equal(SourceProtocol.NonLive, recheckFacts.Protocol);

        var extracted2 = await _extractor.ExtractAsync(recheckFacts, cleanResult1.CleanedImage.Path, null, ws);
        var cleanResult2 = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = recheckFacts,
            ExtractedBundle = extracted2
        }, ws);

        Assert.True(cleanResult2.Success);
        Assert.NotNull(cleanResult2.CleanedImage);
    }

    [Fact]
    public async Task Clean_NegativeCollision_NormalCommentNotCorrupted()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("normal_photo", ".jpg");

        // Normal JPEG with user comment containing "MotionPhoto" or "LIVE_" in normal text
        string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">This is a normal photo describing LIVE_ and MotionPhoto</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        await File.WriteAllBytesAsync(inputPath, jpeg);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);

        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(cleanResult.Success);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        string cleanText = Encoding.UTF8.GetString(cleanBytes);
        Assert.Contains("This is a normal photo describing LIVE_ and MotionPhoto", cleanText);
    }

    [Fact]
    public async Task Clean_LiveXmpCollision_PreservesUnrelatedDescription()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("live_collision", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWithNormalMotionPhotoText(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var result = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(result.Success, result.ErrorMessage);
        string cleanText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(result.CleanedImage!.Path));
        Assert.Contains("A normal note mentioning MotionPhoto and LIVE_ must survive.", cleanText);
        Assert.Contains("Other:MotionPhoto=\"1\"", cleanText);
        Assert.DoesNotContain("Camera:MotionPhotoVersion", cleanText);
        Assert.Contains("Other:Semantic=\"MotionPhoto\"", cleanText);
        Assert.Contains("<Other:Item", cleanText);
        Assert.DoesNotContain("<Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\"", cleanText);
    }

    [Fact]
    public async Task Clean_ScopedNamespaceRebinding_RemovesOnlyGoogleAttribute()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("scoped_namespace", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWithScopedPrefixRebinding(inputPath);

        var facts = await _inspector.InspectAsync(inputPath, null);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        var extracted = await _extractor.ExtractAsync(facts, inputPath, null, ws);
        var result = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted
        }, ws);

        Assert.True(result.Success, result.ErrorMessage);
        string cleanText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(result.CleanedImage!.Path));
        Assert.DoesNotContain("GCamera:MotionPhoto=\"1\"", cleanText);
        Assert.Contains("GCamera:MotionPhoto=\"keep-this\"", cleanText);
        Assert.DoesNotContain("Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\"", cleanText);
    }
}

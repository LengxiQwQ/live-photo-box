using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class SourceProtocolCleanerTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static async Task RunSampleCleanAndVerifyAsync(
        string primarySampleName,
        string? secondarySampleName,
        SourceProtocol expectedInitialProtocol)
    {
        string primaryPath = ResolveSample(primarySampleName);
        string? secondaryPath = secondarySampleName != null ? ResolveSample(secondarySampleName) : null;

        string primaryShaBefore = ComputeSha256(primaryPath);
        string? secondaryShaBefore = secondaryPath != null ? ComputeSha256(secondaryPath) : null;

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        // 1. Inspect
        var facts = await inspector.InspectAsync(primaryPath, secondaryPath);
        Assert.Equal(expectedInitialProtocol, facts.Protocol);

        // 2. Extract
        var extracted = await extractor.ExtractAsync(facts, primaryPath, secondaryPath, workspace);
        Assert.NotNull(extracted.PrimaryImage);
        Assert.True(File.Exists(extracted.PrimaryImage.Path));

        // 3. Clean
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        var failedItems = cleanResult.PreservationReport?.Items
            .Where(i => i.Status == PreservationCheckStatus.Failed)
            .Select(i => $"{i.Name}: {i.Details}")
            .ToList() ?? [];
        Assert.True(cleanResult.PreservationOutcome == PreservationOutcome.Preserved,
            $"Preservation checks failed: {string.Join(" | ", failedItems)}");
        Assert.NotNull(cleanResult.PreservationReport);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationReport.OverallOutcome);
        Assert.NotNull(cleanResult.CleanupPlan);
        Assert.NotEmpty(cleanResult.CleanupPlan.Actions);

        // A source protocol may be removed by range extraction (for example
        // Huawei's 60-byte trailer) before the media cleaner runs.  Keep the
        // two responsibilities explicit and assert the combined audit trail.
        Assert.NotEmpty(extracted.ExtractedProtocolFacts.Concat(cleanResult.RemovedFacts));

        // 4. Source Immutability Assertion
        Assert.Equal(primaryShaBefore, ComputeSha256(primaryPath));
        if (secondaryPath != null)
        {
            Assert.Equal(secondaryShaBefore, ComputeSha256(secondaryPath));
        }

        // 5. Re-Inspection of Cleaned Media: Must be NonLive
        string cleanedImgPath = cleanResult.CleanedImage.Path;
        string? cleanedVidPath = cleanResult.CleanedVideo?.Path;

        var recheckFacts = await inspector.InspectAsync(cleanedImgPath, secondaryPath != null ? cleanedVidPath : null);
        Assert.Equal(SourceProtocol.NonLive, recheckFacts.Protocol);

        // 6. Idempotency test: Cleaning already cleaned media produces NonLive output without error
        using var secondWorkspace = new MediaWorkspace();
        var secondExtracted = await extractor.ExtractAsync(recheckFacts, cleanedImgPath, secondaryPath != null ? cleanedVidPath : null, secondWorkspace);
        var secondCleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = secondExtracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, secondWorkspace);

        Assert.True(secondCleanResult.Success, secondCleanResult.ErrorMessage);
        Assert.NotNull(secondCleanResult.CleanedImage);
        Assert.True(File.Exists(secondCleanResult.CleanedImage.Path));
        Assert.Empty(secondCleanResult.RemovedFacts);
        Assert.Equal(PreservationOutcome.Preserved, secondCleanResult.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Apple_StripsMakerNoteAndMebx()
    {
        await RunSampleCleanAndVerifyAsync("苹果双文件.HEIC", "苹果双文件.MOV", SourceProtocol.AppleLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_GoogleV1_StripsMicroVideo()
    {
        await RunSampleCleanAndVerifyAsync("红米老款-GV1.JPG", null, SourceProtocol.GoogleMicroVideoV1);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_GoogleV2_Xiaomi_StripsMotionPhoto()
    {
        await RunSampleCleanAndVerifyAsync("小米.jpg", null, SourceProtocol.GoogleMotionPhotoV2);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Oppo_StripsOLivePhoto()
    {
        await RunSampleCleanAndVerifyAsync("oppo.jpg", null, SourceProtocol.OppoLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_VivoX300_StripsVMotionPhoto()
    {
        await RunSampleCleanAndVerifyAsync("vivo.jpg", null, SourceProtocol.VivoLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_VivoLegacyDual_StripsAlbumTailAndMp4Keys()
    {
        await RunSampleCleanAndVerifyAsync("vivo双文件.jpg", "vivo双文件.mp4", SourceProtocol.VivoLegacyDualFile);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungJpeg_StripsSefTrailer()
    {
        await RunSampleCleanAndVerifyAsync("三星.jpg", null, SourceProtocol.SamsungMotionPhotoJpeg);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungHeic_StripsMpvd()
    {
        await RunSampleCleanAndVerifyAsync("三星.heic", null, SourceProtocol.SamsungMotionPhotoHeic);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungHeic_StripsMotionPhotoXmpWithoutDroppingHdrDirectory()
    {
        string primaryPath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(primaryPath);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoHeic, facts.Protocol);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        string cleanedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanResult.CleanedImage!.Path));
        Assert.DoesNotContain("GCamera:MotionPhoto", cleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Semantic=\"MotionPhoto\"", cleanedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sefd", cleanedText, StringComparison.Ordinal);
        Assert.Contains("Semantic=\"GainMap\"", cleanedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_HuaweiJpeg_StripsLiveTail()
    {
        await RunSampleCleanAndVerifyAsync("华为-Mate80.jpg", null, SourceProtocol.HuaweiMovingPhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_HuaweiHeic_StripsLiveTail()
    {
        await RunSampleCleanAndVerifyAsync("华为Mate80.heic", null, SourceProtocol.HuaweiMovingPhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Honor_StripsMovingPhoto()
    {
        await RunSampleCleanAndVerifyAsync("荣耀.jpg", null, SourceProtocol.HonorMovingPhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_MissingAuthorization_FailsClosed()
    {
        string primaryPath = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(primaryPath);
        // Clear all confirmed residues to simulate missing authorization authority
        var tamperedFacts = facts with { ConfirmedResidues = Array.Empty<ConfirmedProtocolResidue>() };

        var extracted = await extractor.ExtractAsync(tamperedFacts, primaryPath, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.False(cleanResult.Success);
        Assert.Equal(CleanerFailureCategory.CleanupAuthorizationMissing, cleanResult.FailureCategory);
        Assert.Null(cleanResult.CleanedImage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_UnauthorizedAction_NativeDoesNotModifyTarget()
    {
        string primaryPath = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(primaryPath);

        string tempImage = workspace.AllocateFilePath("test-adversarial-unauthorized", ".jpg");
        File.Copy(primaryPath, tempImage, overwrite: true);
        string cleanedImage = workspace.AllocateFilePath("test-adversarial-unauthorized-cleaned", ".jpg");

        // Authorize only version and pts, deliberately EXCLUDING google-v2-xmp-motionphoto
        var actions = facts.ConfirmedResidues
            .Where(r => r.Id != "google-v2-xmp-motionphoto")
            .Select(r => new PlannedCleanupAction
            {
                ResidueId = r.Id,
                OwnerProtocol = facts.Protocol,
                ArtifactRole = r.ArtifactRole,
                StructureKind = r.StructureKind,
                Selector = r.Selector,
                RemovalMode = r.RemovalMode,
                IsMandatory = true,
                ExpectedSemantic = r.ExpectedSemantic ?? "",
                ExpectedFingerprint = r.ExpectedFingerprint
            })
            .ToList();

        var removedFacts = await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
            facts, actions, tempImage, null, cleanedImage, null);

        // Native must NOT have removed google-v2-xmp-motionphoto
        Assert.DoesNotContain(removedFacts, f => f.ResidueId == "google-v2-xmp-motionphoto");

        // The cleaned file must still contain GCamera:MotionPhoto because it was unauthorized
        string text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanedImage));
        Assert.Contains("GCamera:MotionPhoto", text);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_ReconciliationViolation_FailsClosed()
    {
        string primaryPath = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await inspector.InspectAsync(primaryPath);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);

        // Inject a simulated rogue cleaner that reports an unauthorized removal
        var rogueCleaner = new SourceProtocolCleaner(cleanInvoker: async (f, actions, inImg, inVid, outImg, outVid, ct) =>
        {
            var realFacts = await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                f, actions, inImg, inVid, outImg, outVid, ct);

            var tampered = realFacts.ToList();
            // Inject an unauthorized rogue fact
            tampered.Add(new RemovedProtocolFact
            {
                ProtocolName = "Google",
                Component = "Rogue Injection",
                Description = "Adversarial un-authorized removal",
                ResidueId = "rogue-unauthorized-residue",
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Operation = "Removed",
                AfterStatus = "Removed"
            });
            return tampered;
        });

        var cleanResult = await rogueCleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        // Reconciliation gate must trigger, failing closed
        Assert.False(cleanResult.Success);
        Assert.Equal(CleanerFailureCategory.RemovalWouldTouchUnknownData, cleanResult.FailureCategory);
        Assert.Null(cleanResult.CleanedImage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_NonProtocolXmp_NeverOverkilled()
    {
        string primaryPath = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        // Prepare an image with custom non-protocol XMP properties
        string customImg = workspace.AllocateFilePath("adversarial-xmp", ".jpg");
        byte[] originalBytes = await File.ReadAllBytesAsync(primaryPath);
        string originalXmp = PreservationTestHelpers.ExtractXmp(originalBytes);
        Assert.NotEmpty(originalXmp);

        // Inject custom dc:creator and custom:SpecialTag into the XMP block
        const string customTag = "<dc:creator xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Adversarial Photographer</dc:creator><custom:SpecialTag xmlns:custom=\"http://example.com/custom\">Preserve12345</custom:SpecialTag>";
        int descPos = originalXmp.IndexOf("<rdf:Description", StringComparison.Ordinal);
        Assert.True(descPos > 0);
        int tagEnd = originalXmp.IndexOf('>', descPos);
        string tamperedXmp = originalXmp.Insert(tagEnd + 1, customTag);

        bool injected = LivePhotoBox.Interop.NativeJpegEditor.TryInjectXmp(
            originalBytes, Encoding.UTF8.GetBytes(tamperedXmp), out byte[]? outputBytes, out string? err);
        Assert.True(injected, err);
        await File.WriteAllBytesAsync(customImg, outputBytes!);

        var facts = await inspector.InspectAsync(customImg);
        var extracted = await extractor.ExtractAsync(facts, customImg, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        string cleanedXmp = PreservationTestHelpers.ExtractXmp(await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path));
        Assert.Contains("Adversarial Photographer", cleanedXmp);
        Assert.Contains("Preserve12345", cleanedXmp);

        var xmpItem = cleanResult.PreservationReport!.Items.First(i => i.Name == "XmpNonTarget");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, xmpItem.Status);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_HdrGainMap_NeverOverkilled()
    {
        string primaryPath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(primaryPath);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        // HDR GainMap must be preserved
        string cleanedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path));
        Assert.Contains("Semantic=\"GainMap\"", cleanedText, StringComparison.OrdinalIgnoreCase);

        var hdrItem = cleanResult.PreservationReport!.Items.First(i => i.Name == "Hdr");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, hdrItem.Status);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Adversarial_NonLiveMakerNote_NeverOverkilled()
    {
        string primaryPath = ResolveSample("苹果双文件.HEIC");
        string secondaryPath = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(primaryPath, secondaryPath);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, secondaryPath, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        var makerNoteItem = cleanResult.PreservationReport!.Items.First(i => i.Name == "MakerNote");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, makerNoteItem.Status);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Verifier_Adversarial_TamperedOrCorruptedData_CorrectlyClassified()
    {
        string primaryPath = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await inspector.InspectAsync(primaryPath);
        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);

        string originalImagePath = extracted.PrimaryImage.Path;

        // Case A: Tamper by removing ICC profile bytes
        string noIccImagePath = workspace.AllocateFilePath("tampered-no-icc", ".jpg");
        byte[] imgBytes = await File.ReadAllBytesAsync(originalImagePath);
        // Zero-out ICC marker bytes (0xFF, 0xE2)
        for (int i = 0; i < imgBytes.Length - 4; i++)
        {
            if (imgBytes[i] == 0xFF && imgBytes[i + 1] == 0xE2)
            {
                imgBytes[i] = 0xFF;
                imgBytes[i + 1] = 0xFE; // convert to comment marker
                break;
            }
        }
        await File.WriteAllBytesAsync(noIccImagePath, imgBytes);

        var reportNoIcc = await MetadataPreservationVerifier.VerifyAsync(extracted, noIccImagePath, null);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, reportNoIcc.OverallOutcome);
        var iccItem = reportNoIcc.Items.FirstOrDefault(i => i.Name == "Icc");
        Assert.NotNull(iccItem);
        Assert.Equal(PreservationCheckStatus.Failed, iccItem.Status);

        // Case B: Tamper by stripping GainMap/HDR metadata on HDR source
        string hdrSourcePath = ResolveSample("三星.heic");
        var hdrFacts = await inspector.InspectAsync(hdrSourcePath);
        var hdrExtracted = await extractor.ExtractAsync(hdrFacts, hdrSourcePath, null, workspace);

        // Create a copy of Samsung HEIC and wipe GainMap & hdrgm bytes to simulate HDR loss
        string strippedHdrPath = workspace.AllocateFilePath("tampered-no-gainmap", ".heic");
        byte[] heicBytes = await File.ReadAllBytesAsync(hdrExtracted.PrimaryImage.Path);
        void WipeBytes(byte[] target, byte[] replacement)
        {
            int p = 0;
            while (p < heicBytes.Length)
            {
                int idx = heicBytes.AsSpan(p).IndexOf(target);
                if (idx < 0) break;
                Buffer.BlockCopy(replacement, 0, heicBytes, p + idx, replacement.Length);
                p += idx + target.Length;
            }
        }
        WipeBytes("GainMap"u8.ToArray(), "Wiped__"u8.ToArray());
        WipeBytes("hdrgm"u8.ToArray(), "wiped"u8.ToArray());
        await File.WriteAllBytesAsync(strippedHdrPath, heicBytes);

        var reportHdrLost = await MetadataPreservationVerifier.VerifyAsync(hdrExtracted, strippedHdrPath, null);
        var hdrCheckItem = reportHdrLost.Items.FirstOrDefault(i => i.Name == "Hdr");
        Assert.NotNull(hdrCheckItem);
        Assert.Equal(PreservationCheckStatus.Failed, hdrCheckItem.Status);
        Assert.Equal(PreservationOutcome.DegradedToSdr, reportHdrLost.OverallOutcome);

        // Case C: Strict policy must immediately fail closed when preservation fails
        var cleaner = new SourceProtocolCleaner();
        var strictCleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = hdrExtracted,
            PreservationPolicy = PreservationPolicy.Strict
        }, workspace);

        // With real un-tampered cleaning, strict succeeds
        Assert.True(strictCleanResult.Success);
        Assert.Equal(PreservationOutcome.Preserved, strictCleanResult.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_AppleJpeg_StripsMakerNoteAndMebx()
    {
        await RunSampleCleanAndVerifyAsync("苹果-双文件.JPG", "苹果-双文件.MOV", SourceProtocol.AppleLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_OnePlus_StripsOLivePhoto()
    {
        await RunSampleCleanAndVerifyAsync("一加.jpg", null, SourceProtocol.OppoLivePhoto);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_Oppo_UnauthorizedGCameraXmpProperty_PreservedWithoutDeletion()
    {
        string primaryPath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        string customImg = workspace.AllocateFilePath("oppo-audit-prop", ".jpg");
        byte[] originalBytes = await File.ReadAllBytesAsync(primaryPath);
        string originalXmp = PreservationTestHelpers.ExtractXmp(originalBytes);
        Assert.NotEmpty(originalXmp);

        // Inject <GCamera:SpecialAuditProp>AuditPreserveValue123</GCamera:SpecialAuditProp> into XMP
        const string customProp = "<GCamera:SpecialAuditProp>AuditPreserveValue123</GCamera:SpecialAuditProp>";
        int descPos = originalXmp.IndexOf("<rdf:Description", StringComparison.Ordinal);
        Assert.True(descPos > 0);
        int tagEnd = originalXmp.IndexOf('>', descPos);
        string tamperedXmp = originalXmp.Insert(tagEnd + 1, customProp);

        bool injected = LivePhotoBox.Interop.NativeJpegEditor.TryInjectXmp(
            originalBytes, Encoding.UTF8.GetBytes(tamperedXmp), out byte[]? outputBytes, out string? err);
        Assert.True(injected, err);
        await File.WriteAllBytesAsync(customImg, outputBytes!);

        var facts = await inspector.InspectAsync(customImg);
        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);

        // Verify that SpecialAuditProp is NOT an authorized protocol residue
        Assert.DoesNotContain(facts.ConfirmedResidues, r => r.Selector.Contains("SpecialAuditProp"));

        var extracted = await extractor.ExtractAsync(facts, customImg, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        // Verify that protocol tags were cleaned but the unauthorized GCamera property was preserved intact
        string cleanedXmp = PreservationTestHelpers.ExtractXmp(await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path));
        Assert.DoesNotContain("OLivePhotoVersion", cleanedXmp, StringComparison.Ordinal);
        Assert.DoesNotContain("MotionPhotoOwner", cleanedXmp, StringComparison.Ordinal);
        Assert.Contains("AuditPreserveValue123", cleanedXmp, StringComparison.Ordinal);

        // Verify RemovedFacts contains only authorized removals and never touches SpecialAuditProp
        Assert.DoesNotContain(cleanResult.RemovedFacts, f => f.Description.Contains("SpecialAuditProp") || f.Component.Contains("SpecialAuditProp"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_SamsungJpeg_NonLiveSefEntries_PreservedBitwise()
    {
        string primaryPath = ResolveSample("三星.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        byte[] originalBytes = await File.ReadAllBytesAsync(primaryPath);
        var originalPayloads = ParseSefPayloads(originalBytes);

        // Pre-assertion: original Samsung JPEG has expected SEF entries
        Assert.True(originalPayloads.ContainsKey(0x0A01), "Image_UTC_Data (0x0A01) expected in source");
        Assert.True(originalPayloads.ContainsKey(0x0D21), "Camera_Scene_Info (0x0D21) expected in source");
        Assert.True(originalPayloads.ContainsKey(0x0CC1), "Color_Display_P3 (0x0CC1) expected in source");
        Assert.True(originalPayloads.ContainsKey(0x0CD2), "Photo_HDR_Info (0x0CD2) expected in source");
        Assert.True(originalPayloads.ContainsKey(0x0C61), "Camera_Capture_Mode_Info (0x0C61) expected in source");
        Assert.True(originalPayloads.ContainsKey(0x0A30), "MotionPhoto_Data (0x0A30) expected in source");

        var facts = await inspector.InspectAsync(primaryPath);
        Assert.Equal(SourceProtocol.SamsungMotionPhotoJpeg, facts.Protocol);

        var extracted = await extractor.ExtractAsync(facts, primaryPath, null, workspace);
        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);

        byte[] cleanedBytes = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
        var cleanedPayloads = ParseSefPayloads(cleanedBytes);

        // Post-assertion 1: MotionPhoto_Data (0x0A30) MUST be removed
        Assert.False(cleanedPayloads.ContainsKey(0x0A30), "MotionPhoto_Data (0x0A30) must be stripped from cleaned SEF");

        // Post-assertion 2: All non-live SEF entries MUST be preserved bitwise identical
        ushort[] nonLiveMarkers = [0x0A01, 0x0D21, 0x0CC1, 0x0CD2, 0x0C61];
        foreach (ushort marker in nonLiveMarkers)
        {
            Assert.True(cleanedPayloads.TryGetValue(marker, out byte[]? cleanedPayload), $"SEF marker 0x{marker:04X} missing in cleaned file");
            Assert.True(cleanedPayload.AsSpan().SequenceEqual(originalPayloads[marker]), $"SEF marker 0x{marker:04X} payload corrupted or mutated");
        }

        // Post-assertion 3: Cleaned image re-inspection must yield NonLive
        var recheck = await inspector.InspectAsync(cleanResult.CleanedImage.Path);
        Assert.Equal(SourceProtocol.NonLive, recheck.Protocol);
    }

    private static Dictionary<ushort, byte[]> ParseSefPayloads(byte[] fileBytes)
    {
        var result = new Dictionary<ushort, byte[]>();
        if (fileBytes.Length < 16) return result;
        if (Encoding.ASCII.GetString(fileBytes, fileBytes.Length - 4, 4) != "SEFT") return result;

        int sefhIdx = -1;
        for (int i = fileBytes.Length - 12; i >= 0; i--)
        {
            if (fileBytes[i] == 'S' && fileBytes[i + 1] == 'E' && fileBytes[i + 2] == 'F' && fileBytes[i + 3] == 'H')
            {
                sefhIdx = i;
                break;
            }
        }
        if (sefhIdx < 0) return result;

        uint count = BitConverter.ToUInt32(fileBytes, sefhIdx + 8);
        for (int i = 0; i < count; i++)
        {
            int ep = sefhIdx + 12 + i * 12;
            ushort marker = BitConverter.ToUInt16(fileBytes, ep + 2);
            uint offset = BitConverter.ToUInt32(fileBytes, ep + 4);
            uint size = BitConverter.ToUInt32(fileBytes, ep + 8);
            int payloadPos = sefhIdx - (int)offset;
            if (payloadPos >= 0 && payloadPos + size <= fileBytes.Length)
            {
                byte[] payload = new byte[size];
                Buffer.BlockCopy(fileBytes, payloadPos, payload, 0, (int)size);
                result[marker] = payload;
            }
        }
        return result;
    }
}

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class CleanerTrustChainTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    [Fact]
    public async Task Clean_FailsWhenSourceFactsIsMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = null!,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.FactsNotConfirmed, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    public async Task Clean_FailsWhenProtocolIsUnknown()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.Unknown,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.UnsupportedProtocol, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }


    [Fact]
    public void CleanRequest_GuaranteesSourceFactsDerivedSolelyFromExtractedBundle()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.SamsungMotionPhotoJpeg,
            PrimarySha256 = "SHA_AAA",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = "test.jpg",
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "SHA_AAA"
            }
        };

        var request = new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        };

        Assert.Same(bundle.SourceFacts, request.SourceFacts);
    }

    [Fact]
    public async Task Clean_FailsWhenPrimaryImageFileMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string nonExistentPath = Path.Combine(workspace.RootDirectory, "non-existent.jpg");

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.OppoLivePhoto,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = nonExistentPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 100,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactFactMismatch, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    public async Task Clean_FailsWhenDeclaredMotionVideoFileMissing()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("test-img", ".jpg");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        string nonExistentVid = Path.Combine(workspace.RootDirectory, "non-existent.mp4");

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.VivoLegacyDualFile,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 4, IsPresent = true },
            MotionVideo = new VideoFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true, SourceIndex = 1 }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = 4,
                Sha256 = "dummy"
            },
            MotionVideo = new MediaArtifact
            {
                Path = nonExistentVid,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                ByteLength = 100,
                Sha256 = "dummy"
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactFactMismatch, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Preflight, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenPrimaryImageByteLengthChangedSinceExtraction()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Tamper declared byte length
        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage! with { ByteLength = extracted.PrimaryImage.ByteLength + 10 }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenPrimaryImageSha256ChangedSinceExtraction()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Tamper declared SHA-256
        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage! with { Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_FailsWhenMotionVideoSha256ChangedSinceExtraction()
    {
        string sampleImg = ResolveSample("苹果双文件.HEIC");
        string sampleMov = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(sampleImg, sampleMov);
        var extracted = await extractor.ExtractAsync(facts, sampleImg, sampleMov, workspace);
        Assert.NotNull(extracted.MotionVideo);

        // Tamper declared video SHA-256
        var tamperedBundle = extracted with
        {
            MotionVideo = extracted.MotionVideo! with { Sha256 = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, result.FailureStage);
    }

    [Fact]
    public async Task Clean_NonLiveSourceBypassesMutationsVerbatim()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();

        string imgPath = workspace.AllocateFilePath("normal-img", ".jpg");
        byte[] imgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };
        await File.WriteAllBytesAsync(imgPath, imgBytes);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(imgBytes));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimarySha256 = imgSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = imgBytes.Length, IsPresent = true }
        };

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = imgBytes.Length,
                Sha256 = imgSha
            }
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = bundle
        }, workspace);

        Assert.True(result.Success);
        Assert.NotNull(result.CleanedImage);
        Assert.Equal(imgSha, result.CleanedImage.Sha256);
        Assert.Equal(imgBytes.Length, result.CleanedImage.ByteLength);
        Assert.Empty(result.RemovedFacts);
        Assert.NotNull(result.CleanupPlan);
        Assert.Empty(result.CleanupPlan.Actions);
        Assert.Equal(PreservationOutcome.Preserved, result.PreservationOutcome);
    }

    [Fact]
    public async Task Clean_FailsWhenDuplicateRemovalFactsReported()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Inject hook to simulate duplicate removal fact from native cleaner
        cleaner.FaultInjectionHook = (stage, bundle) =>
        {
            if (stage == CleanerFailureStage.Staging)
            {
                // Trigger an ambiguity / duplicate fact check
            }
            return Task.CompletedTask;
        };

        // Inject a duplicate residue directly into ConfirmedResidues
        var dupList = new System.Collections.Generic.List<ConfirmedProtocolResidue>(facts.ConfirmedResidues);
        if (dupList.Count > 0)
        {
            dupList.Add(dupList[0]);
            var tamperedFacts = facts with { ConfirmedResidues = dupList };
            var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = tamperedBundle
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.AuthorizedResidueAmbiguous, result.FailureCategory);
            Assert.Equal(CleanerFailureStage.Planning, result.FailureStage);
        }
    }

    [Fact]
    public async Task Clean_FailsWhenMandatoryAuthorizedResidueNotRemoved()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Inject an extra mandatory residue that native cleaner cannot possibly remove
        var list = new System.Collections.Generic.List<ConfirmedProtocolResidue>(facts.ConfirmedResidues);
        list.Add(new ConfirmedProtocolResidue
        {
            Id = "apple-fake-unremovable-residue",
            OwnerProtocol = SourceProtocol.AppleLivePhoto,
            ArtifactRole = MediaArtifactKind.PrimaryImage,
            StructureKind = ResidueStructureKind.ExifMakerNoteTag,
            Selector = "0xFFFF",
            ExpectedSemantic = "FakeTag",
            ExpectedFingerprint = "0123456789abcdef0123456789abcdef",
            RequiredAfterExtraction = true
        });

        var tamperedFacts = facts with { ConfirmedResidues = list };
        var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.AuthorizedResidueNotFound, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Staging, result.FailureStage);
    }

    [Fact]
    public async Task Clean_FailsWhenFingerprintMismatches()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Require an expected fingerprint that does not match what native cleaner reports
        var list = new System.Collections.Generic.List<ConfirmedProtocolResidue>();
        foreach (var r in facts.ConfirmedResidues)
        {
            list.Add(r with { ExpectedFingerprint = "expected-hash-456" });
        }

        var cleaner = new SourceProtocolCleaner(cleanInvoker: async (f, actions, inImg, inVid, outImg, outVid, ct) =>
        {
            // Copy files to stage paths
            if (outImg != null) File.Copy(inImg, outImg, true);
            if (inVid != null && outVid != null) File.Copy(inVid, outVid, true);

            var factsOut = new System.Collections.Generic.List<RemovedProtocolFact>();
            foreach (var a in actions)
            {
                factsOut.Add(new RemovedProtocolFact
                {
                    ProtocolName = f.Protocol.ToString(),
                    Component = "Test",
                    Description = "Test",
                    ResidueId = a.ResidueId,
                    ArtifactRole = a.ArtifactRole,
                    StructureKind = a.StructureKind,
                    Operation = "Removed",
                    BeforeFingerprint = "different-actual-hash-123", // Mismatch!
                    AfterStatus = "Removed"
                });
            }
            return factsOut;
        });

        var tamperedFacts = facts with { ConfirmedResidues = list };
        var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.StructureChanged, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Staging, result.FailureStage);
    }

    [Fact]
    public async Task Verifier_FailsClosed_WhenJpegEntropyScanIsTampered()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("sample", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1Jpeg(imgPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var facts = await inspector.InspectAsync(imgPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, null, workspace);

        // Tamper 1 byte of the JPEG entropy scan
        byte[] bytes = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        string tamperedPath = workspace.AllocateFilePath("tampered", ".jpg");

        // Find SOS marker (0xFF, 0xDA)
        for (int i = 0; i < bytes.Length - 10; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA)
            {
                int len = (bytes[i + 2] << 8) | bytes[i + 3];
                int scanStart = i + 2 + len;
                if (scanStart + 2 < bytes.Length)
                {
                    bytes[scanStart + 1] ^= 0xFF; // flip bits
                    break;
                }
            }
        }
        await File.WriteAllBytesAsync(tamperedPath, bytes);

        var report = await MetadataPreservationVerifier.VerifyAsync(extracted, tamperedPath, null);
        Assert.NotEqual(PreservationOutcome.Preserved, report.OverallOutcome);
        var payloadItem = report.Items.First(i => i.Name == "MediaPayload");
        Assert.Equal(PreservationCheckStatus.Failed, payloadItem.Status);
    }

    [Fact]
    public async Task Verifier_FailsClosed_WhenVideoMdatIsTampered()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);
        Assert.NotNull(extracted.MotionVideo);

        // Tamper 1 byte in the mdat box
        byte[] bytes = await File.ReadAllBytesAsync(extracted.MotionVideo.Path);
        string tamperedVidPath = workspace.AllocateFilePath("tampered", ".mov");
        for (int i = 0; i < bytes.Length - 8; i++)
        {
            if (bytes[i + 4] == 'm' && bytes[i + 5] == 'd' && bytes[i + 6] == 'a' && bytes[i + 7] == 't')
            {
                bytes[i + 8] ^= 0xAA; // flip bits in payload
                break;
            }
        }
        await File.WriteAllBytesAsync(tamperedVidPath, bytes);

        var report = await MetadataPreservationVerifier.VerifyAsync(extracted, extracted.PrimaryImage.Path, tamperedVidPath);
        Assert.NotEqual(PreservationOutcome.Preserved, report.OverallOutcome);
        var vidItem = report.Items.First(i => i.Name == "VideoStreams");
        Assert.Equal(PreservationCheckStatus.Failed, vidItem.Status);
    }

    [Fact]
    public void Native_AppleStripMakerNote_Selective_PreservesNonLiveAndUnspecifiedTags()
    {
        // Construct a synthetic Apple MakerNote containing tag 0x0011 (CID), 0x0017 (Live), and 0x0001 (Non-Live)
        byte[] makerNote = new byte[100];
        Encoding.ASCII.GetBytes("Apple iOS\0").CopyTo(makerNote, 0);
        makerNote[10] = 0;
        makerNote[11] = 1;
        makerNote[12] = (byte)'M';
        makerNote[13] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(14, 2), 3); // 3 tags

        // Tag 1: 0x0011 (ContentIdentifier)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(16, 2), 0x0011);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(18, 2), 2); // ASCII
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(20, 4), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(24, 4), 60);

        // Tag 2: 0x0017 (Live Photo tag)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(28, 2), 0x0017);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(30, 2), 4); // LONG
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(32, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(36, 4), 12345);

        // Tag 3: 0x0001 (Non-Live tag)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(40, 2), 0x0001);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(42, 2), 4); // LONG
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(44, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(48, 4), 99999);

        // Wrap into minimal TIFF structure
        byte[] tiff = new byte[makerNote.Length + 32];
        tiff[0] = (byte)'M'; tiff[1] = (byte)'M';
        tiff[2] = 0; tiff[3] = 42;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4, 4), 8); // IFD0 at 8
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8, 2), 1); // 1 tag in IFD0
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10, 2), 0x927C); // MakerNote tag
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12, 2), 7); // UNDEFINED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14, 4), (uint)makerNote.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18, 4), 26); // Value offset
        makerNote.CopyTo(tiff, 26);

        // Authorize stripping ONLY tag 0x0011
        ushort[] authorized = new ushort[] { 0x0011 };
        bool ok = LivePhotoBox.Interop.NativeAppleMakerNoteWriter.TryStripLivePhotoEntriesSelective(tiff, authorized, out string? error);
        Assert.True(ok, error);

        // Verify:
        // Entry count at 26 + 14 should now be 2
        ushort entryCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(26 + 14, 2));
        Assert.Equal(2, entryCount);

        // Tag 2 (0x0017) was preserved and shifted into slot 0 (offset 26 + 16)
        ushort tagAtSlot0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(26 + 16, 2));
        Assert.Equal(0x0017, tagAtSlot0);

        // Tag 3 (0x0001) was preserved and shifted into slot 1 (offset 26 + 28)
        ushort tagAtSlot1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(26 + 28, 2));
        Assert.Equal(0x0001, tagAtSlot1);

        // Slot 2 (offset 26 + 40) is the reclaimed tail and should be zeroed out
        ushort slot2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(26 + 40, 2));
        Assert.Equal(0x0000, slot2);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_NativeE2E_Xmp_FingerprintAuthority_VerifiedAndAdversarialTamperRejected()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        Assert.NotEmpty(facts.ConfirmedResidues);
        foreach (var r in facts.ConfirmedResidues)
        {
            Assert.False(string.IsNullOrEmpty(r.ExpectedFingerprint), $"Residue {r.Id} must have non-empty expected fingerprint");
        }

        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // 1. Normal clean succeeds and BeforeFingerprint matches ExpectedFingerprint exactly
        var normalResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.True(normalResult.Success, normalResult.ErrorMessage);
        Assert.NotEmpty(normalResult.RemovedFacts);
        foreach (var fact in normalResult.RemovedFacts)
        {
            var matchingAction = normalResult.CleanupPlan?.Actions.FirstOrDefault(a => a.ResidueId == fact.ResidueId);
            if (matchingAction != null && !string.IsNullOrEmpty(matchingAction.ExpectedFingerprint))
            {
                Assert.Equal(matchingAction.ExpectedFingerprint, fact.BeforeFingerprint);
            }
        }

        // 2. Adversarial: Tamper an authorized XMP property in the extracted image
        byte[] imgBytes = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        string imgText = Encoding.Latin1.GetString(imgBytes);
        int verIdx = imgText.IndexOf("MotionPhotoVersion=\"1.0\"", StringComparison.Ordinal);
        if (verIdx < 0) verIdx = imgText.IndexOf("MotionPhotoVersion=\"1\"", StringComparison.Ordinal);
        if (verIdx < 0) verIdx = imgText.IndexOf("Version=\"1.0\"", StringComparison.Ordinal);
        Assert.True(verIdx >= 0, "Could not find XMP version attribute to tamper in sample");

        byte[] tamperedBytes = (byte[])imgBytes.Clone();
        tamperedBytes[verIdx + 19] = (byte)'2'; // change '1' to '2'

        string tamperedImgPath = workspace.AllocateFilePath("tampered-oppo", ".jpg");
        await File.WriteAllBytesAsync(tamperedImgPath, tamperedBytes);

        using var sha = SHA256.Create();
        string tamperedSha = Convert.ToHexString(sha.ComputeHash(tamperedBytes));

        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage with
            {
                Path = tamperedImgPath,
                Sha256 = tamperedSha,
                ByteLength = tamperedBytes.Length
            }
        };

        var tamperedResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(tamperedResult.Success);
        Assert.Equal(CleanerFailureCategory.StructureChanged, tamperedResult.FailureCategory);
        Assert.Null(tamperedResult.CleanedImage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_NativeE2E_SamsungSef_FingerprintAuthority_VerifiedAndAdversarialTamperRejected()
    {
        string samplePath = ResolveSample("三星.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var sefResidue = facts.ConfirmedResidues.FirstOrDefault(r => r.Id == "samsung-jpeg-sef-0a30");
        Assert.NotNull(sefResidue);
        Assert.False(string.IsNullOrEmpty(sefResidue.ExpectedFingerprint));

        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // 1. Normal clean succeeds
        var normalResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);
        Assert.True(normalResult.Success, normalResult.ErrorMessage);
        var sefFact = normalResult.RemovedFacts.FirstOrDefault(f => f.ResidueId == "samsung-jpeg-sef-0a30");
        Assert.NotNull(sefFact);
        Assert.Equal(sefResidue.ExpectedFingerprint, sefFact.BeforeFingerprint);

        // 2. Adversarial: Tamper 1 byte in the MotionPhoto_Data SEF payload
        byte[] imgBytes = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        int mpDataIdx = imgBytes.AsSpan().IndexOf("MotionPhoto_Data"u8);
        Assert.True(mpDataIdx >= 0, "MotionPhoto_Data marker not found");

        byte[] tamperedBytes = (byte[])imgBytes.Clone();
        tamperedBytes[mpDataIdx + 30] ^= 0xFF;

        string tamperedImgPath = workspace.AllocateFilePath("tampered-samsung", ".jpg");
        await File.WriteAllBytesAsync(tamperedImgPath, tamperedBytes);

        using var sha = SHA256.Create();
        string tamperedSha = Convert.ToHexString(sha.ComputeHash(tamperedBytes));

        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage with
            {
                Path = tamperedImgPath,
                Sha256 = tamperedSha,
                ByteLength = tamperedBytes.Length
            }
        };

        var tamperedResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(tamperedResult.Success);
        Assert.Null(tamperedResult.CleanedImage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_NativeE2E_AppleMakerNote_FingerprintAuthority_VerifiedAndAdversarialTamperRejected()
    {
        string sampleImg = ResolveSample("苹果-双文件.JPG");
        string sampleMov = ResolveSample("苹果-双文件.MOV");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(sampleImg, sampleMov);
        var cidResidue = facts.ConfirmedResidues.FirstOrDefault(r => r.Id == "apple-img-makernote-0011");
        Assert.NotNull(cidResidue);
        Assert.False(string.IsNullOrEmpty(cidResidue.ExpectedFingerprint));

        var extracted = await extractor.ExtractAsync(facts, sampleImg, sampleMov, workspace);

        // 1. Normal clean succeeds
        var normalResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);
        Assert.True(normalResult.Success, normalResult.ErrorMessage);
        var cidFact = normalResult.RemovedFacts.FirstOrDefault(f => f.ResidueId == "apple-img-makernote-0011");
        Assert.NotNull(cidFact);
        Assert.Equal(cidResidue.ExpectedFingerprint, cidFact.BeforeFingerprint);

        // 2. Adversarial: Tamper 1 byte of ContentIdentifier string in MakerNote
        byte[] imgBytes = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        int cidIdx = imgBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(facts.PairingIdentifier ?? ""));
        Assert.True(cidIdx >= 0, "PairingIdentifier not found in Apple JPEG");

        byte[] tamperedBytes = (byte[])imgBytes.Clone();
        tamperedBytes[cidIdx] = (byte)(tamperedBytes[cidIdx] == 'A' ? 'B' : 'A');

        string tamperedImgPath = workspace.AllocateFilePath("tampered-apple", ".jpg");
        await File.WriteAllBytesAsync(tamperedImgPath, tamperedBytes);

        using var sha = SHA256.Create();
        string tamperedSha = Convert.ToHexString(sha.ComputeHash(tamperedBytes));

        var tamperedBundle = extracted with
        {
            PrimaryImage = extracted.PrimaryImage with
            {
                Path = tamperedImgPath,
                Sha256 = tamperedSha,
                ByteLength = tamperedBytes.Length
            }
        };

        var tamperedResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(tamperedResult.Success);
        Assert.Null(tamperedResult.CleanedImage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_NativeAdversarial_MachineAuthority_RejectsMismatchedIdentity()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Sub-test A: Tamper RemovalMode
        {
            var cleaner = new SourceProtocolCleaner();
            var tamperedResidues = facts.ConfirmedResidues.Select(r => r with { RemovalMode = ResidueRemovalMode.ZeroFill }).ToList();
            var tamperedFacts = facts with { ConfirmedResidues = tamperedResidues };
            var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = tamperedBundle }, workspace);
            Assert.False(result.Success);
            Assert.True(result.FailureCategory == CleanerFailureCategory.AuthorizedResidueNotFound || result.FailureCategory == CleanerFailureCategory.StructureChanged);
        }

        // Sub-test B: Tamper Selector
        {
            var cleaner = new SourceProtocolCleaner();
            var tamperedResidues = facts.ConfirmedResidues.Select(r => r with { Selector = "Bogus:Selector" }).ToList();
            var tamperedFacts = facts with { ConfirmedResidues = tamperedResidues };
            var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = tamperedBundle }, workspace);
            Assert.False(result.Success);
            Assert.True(result.FailureCategory == CleanerFailureCategory.AuthorizedResidueNotFound || result.FailureCategory == CleanerFailureCategory.StructureChanged);
        }

        // Sub-test C: Tamper ArtifactRole
        {
            var cleaner = new SourceProtocolCleaner();
            var tamperedResidues = facts.ConfirmedResidues.Select(r => r with { ArtifactRole = MediaArtifactKind.MotionVideo }).ToList();
            var tamperedFacts = facts with { ConfirmedResidues = tamperedResidues };
            var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = tamperedBundle }, workspace);
            Assert.False(result.Success);
            Assert.True(result.FailureCategory == CleanerFailureCategory.AuthorizedResidueNotFound || result.FailureCategory == CleanerFailureCategory.StructureChanged);
        }

        // Sub-test D: Tamper StructureKind
        {
            var cleaner = new SourceProtocolCleaner();
            var tamperedResidues = facts.ConfirmedResidues.Select(r => r with { StructureKind = ResidueStructureKind.IsoBmffBox }).ToList();
            var tamperedFacts = facts with { ConfirmedResidues = tamperedResidues };
            var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = tamperedBundle }, workspace);
            Assert.False(result.Success);
            Assert.True(result.FailureCategory == CleanerFailureCategory.AuthorizedResidueNotFound || result.FailureCategory == CleanerFailureCategory.StructureChanged);
        }
    }

    [Fact]
    public async Task Verifier_Adversarial_NoDateTimeOriginal_TimingIsNotApplicable()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("no-timing", ".jpg");
        byte[] jpeg = SyntheticProtocolFixtures.CreateMinimalJpeg();
        await File.WriteAllBytesAsync(imgPath, jpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(jpeg));

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLive,
                PrimarySha256 = imgSha,
                PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = jpeg.Length, IsPresent = true }
            },
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = jpeg.Length,
                Sha256 = imgSha
            }
        };

        var report = await MetadataPreservationVerifier.VerifyAsync(bundle, imgPath, null);
        var timingItem = report.Items.First(i => i.Name == "Timing");
        Assert.Equal(PreservationCheckStatus.NotApplicable, timingItem.Status);
        Assert.NotEqual(PreservationCheckStatus.VerifiedPreserved, timingItem.Status);
    }

    [Fact]
    public async Task Verifier_Adversarial_ExtendedXmp_PreservedAndTamperFailsClosed()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("ext-xmp", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:CreatorTool=\"TestApp\" /></rdf:RDF></x:xmpmeta>");

        byte[] extHeader = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
        byte[] guid = Encoding.ASCII.GetBytes("1234567890ABCDEF1234567890ABCDEF");
        byte[] extPayload = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:custom=\"http://example.com/custom/\" custom:Secret=\"ExtValue\" /></rdf:RDF></x:xmpmeta>");

        byte[] extChunk = new byte[extHeader.Length + guid.Length + 8 + extPayload.Length];
        Buffer.BlockCopy(extHeader, 0, extChunk, 0, extHeader.Length);
        Buffer.BlockCopy(guid, 0, extChunk, extHeader.Length, guid.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(extChunk.AsSpan(extHeader.Length + guid.Length, 4), (uint)extPayload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(extChunk.AsSpan(extHeader.Length + guid.Length + 4, 4), 0);
        Buffer.BlockCopy(extPayload, 0, extChunk, extHeader.Length + guid.Length + 8, extPayload.Length);

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]); // SOI
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xE1]);
        int len2 = extChunk.Length + 2;
        ms.WriteByte((byte)(len2 >> 8)); ms.WriteByte((byte)(len2 & 0xFF));
        ms.Write(extChunk);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(imgPath, fullJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(fullJpeg));

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLive,
                PrimarySha256 = imgSha,
                PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
            },
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = fullJpeg.Length,
                Sha256 = imgSha
            }
        };

        // Case A: Untampered clean reports ExtendedXmp VerifiedPreserved
        var report = await MetadataPreservationVerifier.VerifyAsync(bundle, imgPath, null);
        var extItem = report.Items.First(i => i.Name == "ExtendedXmp");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, extItem.Status);

        // Case B: Tampering Extended XMP causes Failed status
        byte[] tampered = (byte[])fullJpeg.Clone();
        int customIdx = tampered.AsSpan().IndexOf("ExtValue"u8);
        Assert.True(customIdx >= 0);
        tampered[customIdx] = (byte)'X';

        string tamperedPath = workspace.AllocateFilePath("tampered-ext", ".jpg");
        await File.WriteAllBytesAsync(tamperedPath, tampered);

        var reportTampered = await MetadataPreservationVerifier.VerifyAsync(bundle, tamperedPath, null);
        var extTamperedItem = reportTampered.Items.First(i => i.Name == "ExtendedXmp");
        Assert.Equal(PreservationCheckStatus.Failed, extTamperedItem.Status);
        Assert.NotEqual(PreservationOutcome.Preserved, reportTampered.OverallOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_BestEffort_FailsClosed_WhenMediaPayloadCorrupted()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        var cleaner = new SourceProtocolCleaner();
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.PreservationDiff)
            {
                var files = Directory.GetFiles(workspace.RootDirectory, "stage-img.*", SearchOption.AllDirectories);
                bool foundSos = false;
                foreach (var file in files)
                {
                    if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        int p = 2;
                        while (p + 4 <= bytes.Length)
                        {
                            if (bytes[p] != 0xFF) break;
                            while (p < bytes.Length && bytes[p] == 0xFF) p++;
                            if (p >= bytes.Length) break;
                            byte marker = bytes[p++];
                            if (marker == 0xDA) // Top-level SOS
                            {
                                int sosHeaderLen = (bytes[p] << 8) | bytes[p + 1];
                                int scanStart = p + sosHeaderLen;
                                if (scanStart + 2 < bytes.Length)
                                {
                                    bytes[scanStart + 1] ^= 0xFF;
                                    foundSos = true;
                                    break;
                                }
                            }
                            if (marker == 0xD9) break;
                            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                            if (p + 2 > bytes.Length) break;
                            int len = (bytes[p] << 8) | bytes[p + 1];
                            if (len < 2 || p + len > bytes.Length) break;
                            p += len;
                        }
                        File.WriteAllBytes(file, bytes);
                    }
                }
                Assert.True(foundSos, "Top-level SOS marker was not found in staged image");
            }
            return Task.CompletedTask;
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, workspace);

        Assert.False(result.Success);
        Assert.NotEqual(PreservationOutcome.Preserved, result.PreservationOutcome);
        Assert.Null(result.CleanedImage);
        Assert.Null(result.CleanedVideo);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_NativeAdversarial_MachineAuthority_RejectsWrongResidueId()
    {
        string primaryPath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(primaryPath);

        string tempImage = workspace.AllocateFilePath("test-adversarial-wrong-residue", ".jpg");
        File.Copy(primaryPath, tempImage, overwrite: true);
        string cleanedImage = workspace.AllocateFilePath("test-adversarial-wrong-residue-cleaned", ".jpg");

        // Tamper the ResidueId of google-v2-xmp-motionphoto to an unauthorized bogus ID
        var actions = facts.ConfirmedResidues
            .Select(r => new PlannedCleanupAction
            {
                ResidueId = r.Id == "google-v2-xmp-motionphoto" ? "adversarial-fake-residue-id" : r.Id,
                OwnerProtocol = facts.Protocol,
                ArtifactRole = r.ArtifactRole,
                StructureKind = r.StructureKind,
                Selector = r.Selector,
                RemovalMode = r.RemovalMode,
                ExpectedFingerprint = r.ExpectedFingerprint,
                ExpectedSemantic = r.ExpectedSemantic,
                IsMandatory = true
            })
            .ToList();

        var removedFacts = await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
            facts, actions, tempImage, null, cleanedImage, null);

        // Native must NOT have removed google-v2-xmp-motionphoto under the fake residue id
        Assert.DoesNotContain(removedFacts, f => f.ResidueId == "google-v2-xmp-motionphoto");
        Assert.DoesNotContain(removedFacts, f => f.ResidueId == "adversarial-fake-residue-id");

        // The cleaned file must still contain GCamera:MotionPhoto because authorization for it failed
        string text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanedImage));
        Assert.Contains("GCamera:MotionPhoto", text);

        // Furthermore, if all actions are tampered with wrong residue IDs, native must reject completely
        var allWrongActions = facts.ConfirmedResidues
            .Select(r => new PlannedCleanupAction
            {
                ResidueId = "wrong-" + r.Id,
                OwnerProtocol = facts.Protocol,
                ArtifactRole = r.ArtifactRole,
                StructureKind = r.StructureKind,
                Selector = r.Selector,
                RemovalMode = r.RemovalMode,
                ExpectedFingerprint = r.ExpectedFingerprint,
                ExpectedSemantic = r.ExpectedSemantic,
                IsMandatory = true
            })
            .ToList();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, allWrongActions, tempImage, null, workspace.AllocateFilePath("all-wrong-out", ".jpg"), null);
        });
    }

    [Fact]
    public async Task Clean_Native_Xmp_CustomNamespaceSameLocalName_PreservedIntact()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-custom-ns", ".jpg");
        string cleanedImage = workspace.AllocateFilePath("test-custom-ns-cleaned", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:custom=\"http://example.com/custom/\" GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\" custom:MotionPhoto=\"KeepThisCustomProperty\" /></rdf:RDF></x:xmpmeta>");

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]); // SOI
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(tempImage, fullJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(fullJpeg));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimarySha256 = imgSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
        };

        var actions = new List<PlannedCleanupAction>
        {
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto",
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            },
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-version",
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhotoVersion",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhotoVersion",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhotoVersion", "1"),
                IsMandatory = true
            }
        };

        var removedFacts = await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
            facts, actions, tempImage, null, cleanedImage, null);

        Assert.Contains(removedFacts, f => f.ResidueId == "google-v2-xmp-motionphoto");
        Assert.Contains(removedFacts, f => f.ResidueId == "google-v2-xmp-version");

        string cleanedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cleanedImage));
        Assert.DoesNotContain("GCamera:MotionPhoto=", cleanedText);
        Assert.DoesNotContain("GCamera:MotionPhotoVersion=", cleanedText);
        Assert.Contains("KeepThisCustomProperty", cleanedText);
        Assert.Contains("custom:MotionPhoto", cleanedText);
    }

    [Fact]
    public async Task Clean_Native_RejectsForeignOwnerProtocol()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-foreign-proto", ".jpg");
        string cleanedImage = workspace.AllocateFilePath("test-foreign-proto-cleaned", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\" /></rdf:RDF></x:xmpmeta>");

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(tempImage, fullJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(fullJpeg));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimarySha256 = imgSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
        };

        // Pass an action with foreign OwnerProtocol (Samsung instead of Google)
        var actions = new List<PlannedCleanupAction>
        {
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto",
                OwnerProtocol = SourceProtocol.SamsungMotionPhotoJpeg, // Foreign!
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            }
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, actions, tempImage, null, cleanedImage, null);
        });
    }

    [Fact]
    public async Task Clean_Native_RejectsDuplicateResidueId()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-dup-residue", ".jpg");
        string cleanedImage = workspace.AllocateFilePath("test-dup-residue-cleaned", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\" /></rdf:RDF></x:xmpmeta>");

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(tempImage, fullJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(fullJpeg));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimarySha256 = imgSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
        };

        // Duplicate action with same ResidueId
        var actions = new List<PlannedCleanupAction>
        {
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto",
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            },
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto", // Duplicate!
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            }
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, actions, tempImage, null, cleanedImage, null);
        });
    }

    [Fact]
    public async Task Clean_Planner_RejectsOwnerProtocolMismatch()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Tamper one confirmed residue to have foreign OwnerProtocol
        var list = new List<ConfirmedProtocolResidue>();
        foreach (var r in facts.ConfirmedResidues)
        {
            list.Add(r with { OwnerProtocol = SourceProtocol.HuaweiMovingPhoto });
        }

        var tamperedFacts = facts with { ConfirmedResidues = list };
        var tamperedBundle = extracted with { SourceFacts = tamperedFacts };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = tamperedBundle
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.CleanupAuthorizationMissing, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Planning, result.FailureStage);
    }

    [Fact]
    public async Task Clean_Native_RejectsEmptyOrMissingArtifactTargets()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-no-targets", ".jpg");
        string cleanedImage = workspace.AllocateFilePath("test-no-targets-cleaned", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\" /></rdf:RDF></x:xmpmeta>");

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(tempImage, fullJpeg);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimarySha256 = Convert.ToHexString(SHA256.HashData(fullJpeg)),
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
        };

        var actions = new List<PlannedCleanupAction>
        {
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto",
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            }
        };

        // Pass empty targets list -> native must fail closed
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, actions, new List<PlannedArtifactTarget>(), tempImage, null, cleanedImage, null);
        });
    }

    [Fact]
    public async Task Clean_Native_TOCTOU_FailsWhenImageAlteredBeforeClean()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-toctou-img", ".jpg");
        string cleanedImage = workspace.AllocateFilePath("test-toctou-img-cleaned", ".jpg");

        byte[] standardXmp = Encoding.UTF8.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\" /></rdf:RDF></x:xmpmeta>");

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE1]);
        int len1 = standardXmp.Length + 2;
        ms.WriteByte((byte)(len1 >> 8)); ms.WriteByte((byte)(len1 & 0xFF));
        ms.Write(standardXmp);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(tempImage, fullJpeg);

        string originalSha = Convert.ToHexString(SHA256.HashData(fullJpeg));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimarySha256 = originalSha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
        };

        var actions = new List<PlannedCleanupAction>
        {
            new PlannedCleanupAction
            {
                ResidueId = "google-v2-xmp-motionphoto",
                OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                ArtifactRole = MediaArtifactKind.PrimaryImage,
                StructureKind = ResidueStructureKind.XmpProperty,
                Selector = "GCamera:MotionPhoto",
                RemovalMode = ResidueRemovalMode.Delete,
                ExpectedSemantic = "MotionPhoto",
                ExpectedFingerprint = ComputeXmpPropertyFingerprint("http://ns.google.com/photos/1.0/camera/", "MotionPhoto", "1"),
                IsMandatory = true
            }
        };

        var targets = new List<PlannedArtifactTarget>
        {
            new PlannedArtifactTarget
            {
                Role = MediaArtifactKind.PrimaryImage,
                ExpectedByteLength = fullJpeg.Length,
                ExpectedSha256 = originalSha
            }
        };

        // Adversarial tampering: alter 1 byte in the file without changing its length!
        byte[] tamperedJpeg = (byte[])fullJpeg.Clone();
        tamperedJpeg[^3] ^= 0x42; // flip bits in the payload
        await File.WriteAllBytesAsync(tempImage, tamperedJpeg);

        var ex = await Assert.ThrowsAsync<CleanerException>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, actions, targets, tempImage, null, cleanedImage, null);
        });

        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, ex.Category);
        Assert.False(File.Exists(cleanedImage));
    }

    [Fact]
    public async Task Clean_Native_TOCTOU_FailsWhenVideoAlteredBeforeClean()
    {
        using var workspace = new MediaWorkspace();
        string tempImage = workspace.AllocateFilePath("test-toctou-vid", ".jpg");
        string tempVideo = workspace.AllocateFilePath("test-toctou-vid", ".mov");
        string cleanedImage = workspace.AllocateFilePath("test-toctou-vid-cleaned", ".jpg");
        string cleanedVideo = workspace.AllocateFilePath("test-toctou-vid-cleaned", ".mov");

        SyntheticProtocolFixtures.CreateAppleJpeg(tempImage);
        SyntheticProtocolFixtures.CreateAppleMov(tempVideo);

        byte[] origImgBytes = await File.ReadAllBytesAsync(tempImage);
        byte[] origVidBytes = await File.ReadAllBytesAsync(tempVideo);

        string origImgSha = Convert.ToHexString(SHA256.HashData(origImgBytes));
        string origVidSha = Convert.ToHexString(SHA256.HashData(origVidBytes));

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(tempImage, tempVideo);

        var actions = facts.ConfirmedResidues.Select(r => new PlannedCleanupAction
        {
            ResidueId = r.Id,
            OwnerProtocol = facts.Protocol,
            ArtifactRole = r.ArtifactRole,
            StructureKind = r.StructureKind,
            Selector = r.Selector,
            RemovalMode = r.RemovalMode,
            ExpectedSemantic = r.ExpectedSemantic ?? "",
            ExpectedFingerprint = r.ExpectedFingerprint,
            IsMandatory = true
        }).ToList();

        var targets = new List<PlannedArtifactTarget>
        {
            new PlannedArtifactTarget
            {
                Role = MediaArtifactKind.PrimaryImage,
                ExpectedByteLength = origImgBytes.Length,
                ExpectedSha256 = origImgSha
            },
            new PlannedArtifactTarget
            {
                Role = MediaArtifactKind.MotionVideo,
                ExpectedByteLength = origVidBytes.Length,
                ExpectedSha256 = origVidSha
            }
        };

        // Adversarial tampering: alter 1 byte in video file
        byte[] tamperedVid = (byte[])origVidBytes.Clone();
        tamperedVid[10] ^= 0x55;
        await File.WriteAllBytesAsync(tempVideo, tamperedVid);

        var ex = await Assert.ThrowsAsync<CleanerException>(async () =>
        {
            await LivePhotoBox.Interop.NativeCleanService.CleanSourceProtocolAsync(
                facts, actions, targets, tempImage, tempVideo, cleanedImage, cleanedVideo);
        });

        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, ex.Category);
        Assert.False(File.Exists(cleanedImage));
        Assert.False(File.Exists(cleanedVideo));
    }

    [Fact]
    public async Task Clean_Orchestrator_TOCTOU_FailsWhenArtifactChangedBetweenExtractionAndCleaning()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("apple", ".jpg");
        string movPath = workspace.AllocateFilePath("apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Adversarial TOCTOU tampering: modify extracted primary image on disk between extraction and cleaning!
        byte[] imgBytes = await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        imgBytes[20] ^= 0x77;
        await File.WriteAllBytesAsync(extracted.PrimaryImage.Path, imgBytes);

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, result.FailureCategory);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Verifier_Adversarial_HeicGainMapAssociationTamperFailsClosed()
    {
        string samplePath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);

        var snapshot = MetadataPreservationVerifier.ExtractHeicAuxRelationSnapshot(rawBytes);
        Assert.NotNull(snapshot);
        Assert.Equal((uint)49, snapshot.PrimaryItemId);
        Assert.Equal((uint)55, snapshot.AuxiliaryItemId);
        Assert.Equal((uint)55, snapshot.FromItemId);
        Assert.Equal((uint)49, snapshot.ToItemId);

        using var sha = SHA256.Create();
        string origSha = Convert.ToHexString(sha.ComputeHash(rawBytes));

        string copyPath = workspace.AllocateFilePath("samsung-untampered", ".heic");
        await File.WriteAllBytesAsync(copyPath, rawBytes);

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.SamsungMotionPhotoHeic,
                PrimarySha256 = origSha,
                PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = rawBytes.Length, IsPresent = true }
            },
            PrimaryImage = new MediaArtifact
            {
                Path = copyPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/heic",
                ImageContainer = ImageContainer.Heic,
                ByteLength = rawBytes.Length,
                Sha256 = origSha
            }
        };

        // Untampered must pass
        var untamperedReport = await MetadataPreservationVerifier.VerifyAsync(bundle, copyPath, null);
        var hdrUntampered = untamperedReport.Items.First(i => i.Name == "Hdr");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, hdrUntampered.Status);

        // Now tamper the auxl reference in iref while keeping the item 55 payload bytes 100% identical
        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int auxlPos = -1;
        for (int i = 0; i <= tamperedBytes.Length - 14; i++)
        {
            if (tamperedBytes[i + 4] == 'a' && tamperedBytes[i + 5] == 'u' && tamperedBytes[i + 6] == 'x' && tamperedBytes[i + 7] == 'l')
            {
                auxlPos = i;
                break;
            }
        }
        Assert.True(auxlPos >= 0);

        // In三星.heic, auxl box has ver 0: from_id at auxlPos+8 (55), to_id at auxlPos+12 (49).
        // Tamper to_id from 49 to 50:
        tamperedBytes[auxlPos + 13] = 50;

        string tamperedPath = workspace.AllocateFilePath("samsung-tampered-auxl", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        // Verifier must detect the tampered association and fail closed
        var tamperedReport = await MetadataPreservationVerifier.VerifyAsync(bundle, tamperedPath, null);
        var hdrTampered = tamperedReport.Items.First(i => i.Name == "Hdr");
        Assert.Equal(PreservationCheckStatus.Failed, hdrTampered.Status);
        Assert.NotEqual(PreservationOutcome.Preserved, tamperedReport.OverallOutcome);
    }

    [Fact]
    public async Task Verifier_Adversarial_GainMapSubstringOnly_IsNotVerifiedPreserved()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("substring-gainmap", ".jpg");

        // JPEG containing plain text "GainMap" and "hdrgm" in dummy scan / comment bytes,
        // but without any structured XMP properties or HEIC auxl box.
        byte[] plainGainMapBytes = "GainMap and hdrgm plain text without structured metadata"u8.ToArray();
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]); // SOI
        ms.Write([0xFF, 0xFE]); // COM marker
        int comLen = plainGainMapBytes.Length + 2;
        ms.WriteByte((byte)(comLen >> 8)); ms.WriteByte((byte)(comLen & 0xFF));
        ms.Write(plainGainMapBytes);
        ms.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9]);
        byte[] fullJpeg = ms.ToArray();
        await File.WriteAllBytesAsync(imgPath, fullJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(fullJpeg));

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLive,
                PrimarySha256 = imgSha,
                PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = fullJpeg.Length, IsPresent = true }
            },
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = fullJpeg.Length,
                Sha256 = imgSha
            }
        };

        var report = await MetadataPreservationVerifier.VerifyAsync(bundle, imgPath, null);
        var hdrItem = report.Items.First(i => i.Name == "Hdr");

        // Must NOT grant VerifiedPreserved based on substring alone
        Assert.NotEqual(PreservationCheckStatus.VerifiedPreserved, hdrItem.Status);
        Assert.Equal(PreservationCheckStatus.NotApplicable, hdrItem.Status);
    }

    [Fact]
    public async Task Verifier_DetachedGainMap_ReportsSemanticallyPreserved_NotVerifiedPreserved()
    {
        using var workspace = new MediaWorkspace();
        string imgPath = workspace.AllocateFilePath("test-primary", ".jpg");
        string gmPath = workspace.AllocateFilePath("test-gainmap", ".jpg");

        byte[] plainJpeg = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0x00, 0x00, 0xFF, 0xD9];
        await File.WriteAllBytesAsync(imgPath, plainJpeg);
        await File.WriteAllBytesAsync(gmPath, plainJpeg);

        using var sha = SHA256.Create();
        string imgSha = Convert.ToHexString(sha.ComputeHash(plainJpeg));

        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLive,
                PrimarySha256 = imgSha,
                PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = plainJpeg.Length, IsPresent = true }
            },
            PrimaryImage = new MediaArtifact
            {
                Path = imgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = plainJpeg.Length,
                Sha256 = imgSha
            },
            GainMap = new MediaArtifact
            {
                Path = gmPath,
                Kind = MediaArtifactKind.GainMap,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = plainJpeg.Length,
                Sha256 = imgSha
            }
        };

        var report = await MetadataPreservationVerifier.VerifyAsync(bundle, imgPath, null);
        var hdrItem = report.Items.First(i => i.Name == "Hdr");

        // Detached GainMap must report SemanticallyPreserved, NOT VerifiedPreserved
        Assert.Equal(PreservationCheckStatus.SemanticallyPreserved, hdrItem.Status);
        Assert.NotEqual(PreservationCheckStatus.VerifiedPreserved, hdrItem.Status);
        Assert.Contains("detached artifact", hdrItem.Details);

        var gmItem = report.Items.First(i => i.Name == "GainMap");
        Assert.Equal(PreservationCheckStatus.VerifiedPreserved, gmItem.Status);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Verifier_HeifAuthoritativeBoxParsing_IgnoresPayloadNoiseInMdat()
    {
        string samplePath = ResolveSample("三星.heic");
        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);

        var baselineSnapshot = MetadataPreservationVerifier.ExtractHeicAuxRelationSnapshot(rawBytes);
        Assert.NotNull(baselineSnapshot);

        // Find the mdat box in rawBytes and append fake "auxl", "pitm", "iloc", "iref" strings inside mdat
        byte[] noisyBytes = (byte[])rawBytes.Clone();
        int mdatPos = -1;
        for (int i = 0; i <= noisyBytes.Length - 8; i++)
        {
            if (noisyBytes[i + 4] == 'm' && noisyBytes[i + 5] == 'd' && noisyBytes[i + 6] == 'a' && noisyBytes[i + 7] == 't')
            {
                mdatPos = i;
                break;
            }
        }
        Assert.True(mdatPos >= 0);

        // Inject fake box signatures into mdat payload area (offset mdatPos + 100)
        byte[] fakeBoxes = "....auxl....pitm....iloc....iref"u8.ToArray();
        Buffer.BlockCopy(fakeBoxes, 0, noisyBytes, mdatPos + 100, fakeBoxes.Length);

        // Authoritative parsing must ignore payload noise and return exact same baseline items
        var noisySnapshot = MetadataPreservationVerifier.ExtractHeicAuxRelationSnapshot(noisyBytes);
        Assert.NotNull(noisySnapshot);
        Assert.Equal(baselineSnapshot.PrimaryItemId, noisySnapshot.PrimaryItemId);
        Assert.Equal(baselineSnapshot.AuxiliaryItemId, noisySnapshot.AuxiliaryItemId);
        Assert.Equal(baselineSnapshot.FromItemId, noisySnapshot.FromItemId);
        Assert.Equal(baselineSnapshot.ToItemId, noisySnapshot.ToItemId);
        Assert.Equal(MetadataPreservationVerifier.ExtractHeicPrimaryItemId(rawBytes), MetadataPreservationVerifier.ExtractHeicPrimaryItemId(noisyBytes));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Verifier_HeifAuthoritativeBoxParsing_RejectsShadowIref()
    {
        string samplePath = ResolveSample("三星.heic");
        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);

        // Find meta box and iref box
        int metaPos = -1;
        int irefPos = -1;
        for (int i = 0; i <= rawBytes.Length - 8; i++)
        {
            if (rawBytes[i + 4] == 'm' && rawBytes[i + 5] == 'e' && rawBytes[i + 6] == 't' && rawBytes[i + 7] == 'a')
                metaPos = i;
            if (rawBytes[i + 4] == 'i' && rawBytes[i + 5] == 'r' && rawBytes[i + 6] == 'e' && rawBytes[i + 7] == 'f')
                irefPos = i;
        }
        Assert.True(metaPos >= 0 && irefPos >= 0);

        uint irefSize = BinaryPrimitives.ReadUInt32BigEndian(rawBytes.AsSpan(irefPos, 4));

        // Duplicate iref box inside meta: expand meta by irefSize and insert duplicate iref
        byte[] tamperedBytes = new byte[rawBytes.Length + irefSize];
        int irefEnd = irefPos + (int)irefSize;
        Buffer.BlockCopy(rawBytes, 0, tamperedBytes, 0, irefEnd);
        Buffer.BlockCopy(rawBytes, irefPos, tamperedBytes, irefEnd, (int)irefSize);
        Buffer.BlockCopy(rawBytes, irefEnd, tamperedBytes, irefEnd + (int)irefSize, rawBytes.Length - irefEnd);

        uint metaSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4));
        BinaryPrimitives.WriteUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4), metaSize + irefSize);

        // Authoritative parser must reject shadow/duplicate iref box and fail closed
        var snapshot = MetadataPreservationVerifier.ExtractHeicAuxRelationSnapshot(tamperedBytes);
        Assert.Null(snapshot);
    }

    private static string ComputeXmpPropertyFingerprint(string uri, string localName, string value)
    {
        string canonical = $"{uri}:{localName}={value}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R1Bx007ProductPathTests
{
    private const string SampleName = "红米老款-GV1.JPG";
    private const long ExpectedSourceLength = 8_943_926;
    private const long ExpectedEmbeddedStart = 5_153_721;
    private const long ExpectedJpegEnd = 5_066_313;
    private const long ExpectedSubimageStart = 5_066_313;
    private const long ExpectedSubimageLength = 49_371;
    private const string ExpectedSubimageSha256 = "9DE80EB9B34CC515E64D86D0F48FDB47A95B73C0FFAE5013A33AFAE5BA30C85F";
    private const long ExpectedLenswatermarkStart = 5_115_684;
    private const long ExpectedLenswatermarkLength = 38_037;
    private const string ExpectedLenswatermarkSha256 = "FC6119FBE2E912E829347597022FF0CAC9B889BE638A4E059A6313D4CEFC26FC";
    private const string ExpectedSourceSha256 = "48C7490F30C25D5F9E7CFC763D766BA3FDEDA8B12AA72A38426167233276C477";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_CanonicalAndIdentityDerivedControl_ReturnAcceptLiveWithBoundedRanges()
    {
        string canonicalPath = TestSampleResolver.ResolveSample(SampleName);
        string derivedPath = ControlPath("inspector-extract");
        FileIdentity canonicalBefore = CaptureFileIdentity(canonicalPath);
        FileIdentity derivedBefore = CaptureFileIdentity(derivedPath);
        SourceMediaFacts? canonicalFacts = null;
        SourceMediaFacts? derivedFacts = null;
        Exception? operationError = null;

        try
        {
            var inspector = new SourceInspector();
            canonicalFacts = await inspector.InspectAsync(canonicalPath);
            derivedFacts = await inspector.InspectAsync(derivedPath);
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        FileIdentity canonicalAfter = CaptureFileIdentity(canonicalPath);
        FileIdentity derivedAfter = CaptureFileIdentity(derivedPath);
        WriteEvidence("02-inspector-result.json", new
        {
            schema = "P7-R1-BX007-inspector-result-v1",
            caseId = "BX-007",
            mutationId = "ISOBMFF-LARGESIZE-VALID-CONTROL",
            parameters = "size32=1;largeSize=header+payload;within parent",
            expected = new
            {
                decision = "ACCEPT_LIVE",
                stage = "None",
                category = "None",
                resultCode = "SourceMediaFacts returned"
            },
            inputs = new[]
            {
                new
                {
                    role = "canonical RealSample",
                    classification = "RealSample",
                    identityBefore = canonicalBefore,
                    identityAfter = canonicalAfter,
                    facts = FactsSnapshot(canonicalFacts)
                },
                new
                {
                    role = "identity-derived Inspector/Extract control",
                    classification = "Synthetic/Derived",
                    identityBefore = derivedBefore,
                    identityAfter = derivedAfter,
                    facts = FactsSnapshot(derivedFacts)
                }
            },
            canonicalUnchanged = SameIdentity(canonicalBefore, canonicalAfter),
            derivedUnchanged = SameIdentity(derivedBefore, derivedAfter),
            operationError = operationError is null ? null : ExceptionSnapshot(operationError)
        });

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        Assert.NotNull(canonicalFacts);
        Assert.NotNull(derivedFacts);
        AssertGoogleV1Facts(canonicalFacts, canonicalBefore.Bytes);
        AssertGoogleV1Facts(derivedFacts, derivedBefore.Bytes);
        Assert.Equal(canonicalFacts.PrimaryImage, derivedFacts.PrimaryImage);
        Assert.Equal(canonicalFacts.MotionVideo, derivedFacts.MotionVideo);
        Assert.Equal(ExpectedSourceSha256, canonicalFacts.PrimarySha256);
        Assert.Equal(ExpectedSourceSha256, derivedFacts.PrimarySha256);
        Assert.True(SameIdentity(canonicalBefore, canonicalAfter), "The canonical RealSample changed during Inspector execution.");
        Assert.True(SameIdentity(derivedBefore, derivedAfter), "The identity-derived Inspector control changed during Inspector execution.");
        Assert.True(SameIdentity(canonicalBefore, derivedBefore), "The derived control does not match the canonical input byte-for-byte.");
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Extract_UsesInspectorIssuedPlanAndCopiesExactBoundedRanges()
    {
        string sourcePath = ControlPath("inspector-extract");
        FileIdentity sourceBefore = CaptureFileIdentity(sourcePath);
        using var workspace = new MediaWorkspace();
        InspectedSource? inspected = null;
        ExtractedMediaBundle? bundle = null;
        Exception? operationError = null;

        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
            bundle = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                sourcePath,
                secondaryPath: null,
                workspace);
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        FileIdentity sourceAfter = CaptureFileIdentity(sourcePath);
        object? primaryEvidence = null;
        object? videoEvidence = null;
        if (inspected is not null && bundle is not null)
        {
            primaryEvidence = ExtractedRangeSnapshot(
                sourcePath,
                inspected.Facts.PrimaryImage.ByteOffset,
                inspected.Facts.PrimaryImage.ByteLength,
                bundle.PrimaryImage);
            if (inspected.Facts.MotionVideo is { IsPresent: true } videoFacts && bundle.MotionVideo is not null)
            {
                videoEvidence = ExtractedRangeSnapshot(
                    sourcePath,
                    videoFacts.ByteOffset,
                    videoFacts.ByteLength,
                    bundle.MotionVideo);
            }
        }

        IReadOnlyList<WorkspaceFileIdentity> workspaceFiles = CaptureWorkspaceFiles(workspace.RootDirectory);
        string workspaceRoot = workspace.RootDirectory;
        bundle?.CleanupPlan?.Dispose();
        inspected?.Dispose();
        workspace.Dispose();
        bool workspaceRemovedAfterDispose = !Directory.Exists(workspaceRoot);

        WriteEvidence("03-extractor-result.json", new
        {
            schema = "P7-R1-BX007-extractor-result-v1",
            caseId = "BX-007",
            input = sourceBefore,
            inputAfter = sourceAfter,
            inputClassification = "Synthetic/Derived",
            inspectorIssuedPlanUsed = inspected is not null,
            extractorIssuedProductionCleanupPlan = bundle?.CleanupPlan is not null,
            facts = FactsSnapshot(inspected?.Facts),
            primaryImage = primaryEvidence,
            motionVideo = videoEvidence,
            sourceUnchanged = SameIdentity(sourceBefore, sourceAfter),
            workspaceFilesBeforeDispose = workspaceFiles,
            workspaceRemovedAfterDispose,
            operationError = operationError is null ? null : ExceptionSnapshot(operationError)
        });

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        Assert.NotNull(inspected);
        Assert.NotNull(bundle);
        AssertGoogleV1Facts(inspected.Facts, sourceBefore.Bytes);
        Assert.NotNull(bundle.CleanupPlan);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Null(bundle.GainMap);
        Assert.Null(bundle.CleanupSource);
        AssertEmbeddedMiCameraDescriptors(bundle.AuxiliaryMedia);
        Assert.NotNull(primaryEvidence);
        Assert.NotNull(videoEvidence);
        Assert.True(SameIdentity(sourceBefore, sourceAfter), "The derived source changed during extraction.");
        Assert.True(workspaceRemovedAfterDispose, "The extraction workspace remained after disposal.");
        Assert.Empty(FindUnexplainedWorkspaceFiles(workspaceFiles, new[] { bundle.PrimaryImage.Path, bundle.MotionVideo.Path }));
        AssertRangeSnapshotMatches(primaryEvidence);
        AssertRangeSnapshotMatches(videoEvidence);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Clean_UsesExtractorIssuedAuthority_PreservesMetadataAndRemovesBinding()
    {
        string canonicalPath = TestSampleResolver.ResolveSample(SampleName);
        string sourcePath = ControlPath("clean");
        FileIdentity canonicalBefore = CaptureFileIdentity(canonicalPath);
        FileIdentity sourceBefore = CaptureFileIdentity(sourcePath);
        var workspace = new MediaWorkspace();
        string workspaceRoot = workspace.RootDirectory;
        InspectedSource? inspected = null;
        ExtractedMediaBundle? bundle = null;
        ProtocolCleanResult? cleanResult = null;
        SourceMediaFacts? postCleanFacts = null;
        Exception? operationError = null;
        string failureStage = "Inspector";

        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
            failureStage = "Extractor";
            bundle = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                sourcePath,
                secondaryPath: null,
                workspace);

            failureStage = "Cleaner";
            CleanupPlan? issuedCleanupPlan = bundle.CleanupPlan;
            var request = new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = issuedCleanupPlan,
                PreservationPolicy = PreservationPolicy.Strict
            };
            cleanResult = await new SourceProtocolCleaner().CleanAsync(request, workspace);

            if (cleanResult.Success && cleanResult.CleanedImage is not null)
            {
                failureStage = "Post-clean Inspector";
                postCleanFacts = await new SourceInspector().InspectAsync(cleanResult.CleanedImage.Path);
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        FileIdentity canonicalAfter = CaptureFileIdentity(canonicalPath);
        FileIdentity sourceAfter = CaptureFileIdentity(sourcePath);
        IReadOnlyList<WorkspaceFileIdentity> workspaceFiles = CaptureWorkspaceFiles(workspaceRoot);
        string[] declaredArtifactPaths = DeclaredArtifactPaths(bundle, cleanResult);
        string[] unexplainedWorkspaceFiles = FindUnexplainedWorkspaceFiles(workspaceFiles, declaredArtifactPaths);
        object? primaryExtraction = bundle is null ? null : ArtifactSnapshot(bundle.PrimaryImage);
        object? cleanedImage = cleanResult?.CleanedImage is null ? null : ArtifactSnapshot(cleanResult.CleanedImage);
        long? cleanedImageLength = cleanResult?.CleanedImage is null
            ? null
            : new FileInfo(cleanResult.CleanedImage.Path).Length;
        object? cleanedImageRetentionEvidence = null;
        if (cleanResult?.Success == true && cleanResult.CleanedImage is not null)
        {
            cleanedImageRetentionEvidence = RetainCleanedImageEvidence(cleanResult.CleanedImage.Path);
        }

        object? preservation = PreservationSnapshot(cleanResult?.PreservationReport);
        object[] cleanupActions = cleanResult?.CleanupPlan?.Actions
            .Select(action => (object)new
            {
                residueId = action.ResidueId,
                ownerProtocol = action.OwnerProtocol.ToString(),
                artifactRole = action.ArtifactRole.ToString(),
                structureKind = action.StructureKind.ToString(),
                selector = action.Selector,
                semantic = action.ExpectedSemantic,
                coordinateSpace = action.CoordinateSpace.ToString(),
                removalMode = action.RemovalMode.ToString(),
                expectedFingerprint = action.ExpectedFingerprint,
                mandatory = action.IsMandatory
            }).ToArray() ?? Array.Empty<object>();
        object[] removedFacts = cleanResult?.RemovedFacts
            .Select(fact => (object)new
            {
                protocol = fact.ProtocolName,
                component = fact.Component,
                description = fact.Description,
                kind = fact.Kind.ToString(),
                residueId = fact.ResidueId,
                artifactRole = fact.ArtifactRole?.ToString(),
                structureKind = fact.StructureKind?.ToString(),
                operation = fact.Operation,
                beforeFingerprint = fact.BeforeFingerprint,
                afterStatus = fact.AfterStatus
            }).ToArray() ?? Array.Empty<object>();

        inspected?.Dispose();
        bundle?.CleanupPlan?.Dispose();
        workspace.Dispose();
        bool workspaceRemovedAfterDispose = !Directory.Exists(workspaceRoot);

        WriteEvidence("04-cleaner-result.json", new
        {
            schema = "P7-R1-BX007-cleaner-result-v1",
            caseId = "BX-007",
            inputClassification = "Synthetic/Derived",
            canonicalIdentityBefore = canonicalBefore,
            canonicalIdentityAfter = canonicalAfter,
            canonicalUnchanged = SameIdentity(canonicalBefore, canonicalAfter),
            cleanControlIdentityBefore = sourceBefore,
            cleanControlIdentityAfter = sourceAfter,
            cleanControlUnchanged = SameIdentity(sourceBefore, sourceAfter),
            inspectorIssuedPlanUsed = inspected is not null,
            extractorIssuedCleanupPlanUsed = bundle?.CleanupPlan is not null,
            expectedProtocol = SourceProtocol.GoogleMicroVideoV1.ToString(),
            factsBeforeClean = FactsSnapshot(inspected?.Facts),
            extractionPrimaryImage = primaryExtraction,
            cleanupSuccess = cleanResult?.Success,
            failureCategory = cleanResult?.FailureCategory?.ToString(),
            failureStage = cleanResult?.FailureStage?.ToString() ?? failureStage,
            transactionState = cleanResult?.TransactionState?.ToString(),
            cleanerError = cleanResult?.ErrorMessage,
            cleanPlanActions = cleanupActions,
            removedProtocolFacts = removedFacts,
            cleanedImage,
            cleanedImageRetentionEvidence,
            postCleanFacts = FactsSnapshot(postCleanFacts),
            preservation,
            declaredArtifactPaths,
            workspaceFilesBeforeDispose = workspaceFiles,
            unexplainedWorkspaceFiles,
            workspaceRemovedAfterDispose,
            operationError = operationError is null ? null : ExceptionSnapshot(operationError)
        });

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        Assert.NotNull(inspected);
        Assert.NotNull(bundle);
        Assert.NotNull(cleanResult);
        AssertGoogleV1Facts(inspected.Facts, sourceBefore.Bytes);
        Assert.NotNull(bundle.CleanupPlan);
        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedImage);
        Assert.NotNull(cleanResult.CleanupPlan);
        Assert.NotEmpty(cleanResult.CleanupPlan.Actions);
        Assert.All(cleanResult.CleanupPlan.Actions, action =>
        {
            Assert.Equal(SourceProtocol.GoogleMicroVideoV1, action.OwnerProtocol);
            Assert.Equal(MediaArtifactKind.PrimaryImage, action.ArtifactRole);
        });
        Assert.NotEmpty(cleanResult.RemovedFacts);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationOutcome);
        Assert.NotNull(cleanResult.PreservationReport);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationReport.OverallOutcome);
        Assert.DoesNotContain(cleanResult.PreservationReport.Items, item =>
            item.Status is PreservationCheckStatus.Failed or PreservationCheckStatus.UnableToVerify);
        Assert.NotNull(postCleanFacts);
        Assert.Equal(SourceProtocol.NonLive, postCleanFacts.Protocol);
        Assert.True(cleanedImageLength.HasValue);
        AssertPostCleanMiCameraFacts(postCleanFacts, cleanedImageLength.Value);
        Assert.Contains(cleanResult.PreservationReport.Items, item =>
            item.Name == "Auxiliary:MiCameraSubimage" && item.Status == PreservationCheckStatus.VerifiedPreserved);
        Assert.Contains(cleanResult.PreservationReport.Items, item =>
            item.Name == "Auxiliary:MiCameraLensWatermark" && item.Status == PreservationCheckStatus.VerifiedPreserved);
        Assert.NotEqual(bundle.PrimaryImage.Sha256, cleanResult.CleanedImage.Sha256);
        Assert.True(SameIdentity(canonicalBefore, canonicalAfter), "The canonical RealSample changed during Clean.");
        Assert.True(SameIdentity(sourceBefore, sourceAfter), "The derived Clean control changed during product operations.");
        Assert.Empty(unexplainedWorkspaceFiles);
        Assert.True(workspaceRemovedAfterDispose, "The Cleaner workspace remained after disposal.");
    }

    private static string ControlPath(string role)
    {
        string workspace = RequiredEnvironment("P7_R1_BX007_WORKSPACE");
        return Path.Combine(workspace, "controls", role, SampleName);
    }

    private static string RequiredEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required P7 BX-007 test environment variable '{name}' is missing.");
        }

        return Path.GetFullPath(value);
    }

    private static void WriteEvidence(string fileName, object evidence)
    {
        string evidenceDirectory = RequiredEnvironment("P7_R1_BX007_EVIDENCE_DIRECTORY");
        string path = Path.Combine(evidenceDirectory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, evidence, EvidenceJsonOptions);
        stream.Flush(flushToDisk: true);
    }

    private static object RetainCleanedImageEvidence(string sourcePath)
    {
        string evidenceDirectory = RequiredEnvironment("P7_R1_BX007_EVIDENCE_DIRECTORY");
        const string relativePath = "15-cleaned-image.jpg";
        string destinationPath = Path.Combine(evidenceDirectory, relativePath);
        FileIdentity sourceBeforeCopy = CaptureFileIdentity(sourcePath);

        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }

        FileIdentity sourceAfterCopy = CaptureFileIdentity(sourcePath);
        FileIdentity copiedImage = CaptureFileIdentity(destinationPath);
        bool sourceStableDuringCopy = SameIdentity(sourceBeforeCopy, sourceAfterCopy);
        bool byteForByteEqual = FilesAreByteForByteEqual(sourcePath, destinationPath);
        Assert.True(sourceStableDuringCopy, "The CleanedImage changed while its evidence copy was being retained.");
        Assert.Equal(sourceBeforeCopy.Bytes, copiedImage.Bytes);
        Assert.Equal(sourceBeforeCopy.Sha256, copiedImage.Sha256);
        Assert.True(byteForByteEqual, "The retained CleanedImage evidence is not byte-for-byte identical to the Cleaner output.");

        return new
        {
            path = relativePath,
            sourceBytes = sourceBeforeCopy.Bytes,
            sourceSha256 = sourceBeforeCopy.Sha256,
            sourceStableDuringCopy,
            copyBytes = copiedImage.Bytes,
            copySha256 = copiedImage.Sha256,
            byteForByteEqual
        };
    }

    private static bool FilesAreByteForByteEqual(string leftPath, string rightPath)
    {
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (left.Length != right.Length)
        {
            return false;
        }

        byte[] leftBuffer = new byte[64 * 1024];
        byte[] rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            int leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            int rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            for (int index = 0; index < leftRead; index++)
            {
                if (leftBuffer[index] != rightBuffer[index])
                {
                    return false;
                }
            }
        }
    }

    private static FileIdentity CaptureFileIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        return new FileIdentity(Path.GetFullPath(path), stream.Length, Convert.ToHexString(sha256.ComputeHash(stream)));
    }

    private static string HashRange(string path, long offset, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
        {
            throw new InvalidDataException($"Range [{offset},{EndExclusive(offset, length)}) is outside '{path}' ({stream.Length} bytes).");
        }

        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new EndOfStreamException($"Unable to read the full authorized range [{offset},{offset + length}).");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static object FactsSnapshot(SourceMediaFacts? facts)
    {
        if (facts is null)
        {
            return new { returned = false };
        }

        return new
        {
            returned = true,
            protocol = facts.Protocol.ToString(),
            primarySha256 = facts.PrimarySha256,
            primaryImage = new
            {
                present = facts.PrimaryImage.IsPresent,
                container = facts.PrimaryImage.Container.ToString(),
                offset = facts.PrimaryImage.ByteOffset,
                length = facts.PrimaryImage.ByteLength,
                endExclusive = EndExclusive(facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength)
            },
            motionVideo = facts.MotionVideo is null ? null : new
            {
                present = facts.MotionVideo.IsPresent,
                container = facts.MotionVideo.Container.ToString(),
                sourceIndex = facts.MotionVideo.SourceIndex,
                offset = facts.MotionVideo.ByteOffset,
                length = facts.MotionVideo.ByteLength,
                endExclusive = EndExclusive(facts.MotionVideo.ByteOffset, facts.MotionVideo.ByteLength)
            },
            auxiliaryItems = facts.AuxiliaryItems.Select(item => new
            {
                present = item.IsPresent,
                itemId = item.ItemId,
                semantic = item.Semantic,
                stableIdentity = item.StableIdentity,
                ownerIdentity = item.OwnerIdentity,
                relationship = item.Relationship,
                sourceIndex = item.SourceIndex,
                container = item.Container.ToString(),
                codec = item.Codec.ToString(),
                representation = item.Representation.ToString(),
                ownership = item.Ownership.ToString(),
                offset = item.ByteOffset,
                length = item.ByteLength,
                endExclusive = EndExclusive(item.ByteOffset, item.ByteLength),
                sha256 = item.Sha256
            }).ToArray(),
            confirmedResidues = facts.ConfirmedResidues.Select(residue => new
            {
                id = residue.Id,
                protocol = residue.OwnerProtocol.ToString(),
                artifactRole = residue.ArtifactRole.ToString(),
                structureKind = residue.StructureKind.ToString(),
                semantic = residue.ExpectedSemantic,
                selector = residue.Selector,
                fingerprint = residue.ExpectedFingerprint,
                residue.RequiredAfterExtraction
            }).ToArray()
        };
    }

    private static void AssertGoogleV1Facts(SourceMediaFacts facts, long sourceLength)
    {
        Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);
        Assert.True(facts.PrimaryImage.IsPresent);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(0, facts.PrimaryImage.ByteOffset);
        Assert.True(facts.PrimaryImage.ByteLength > 0);
        Assert.Equal(ExpectedEmbeddedStart, EndExclusive(facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength));

        VideoFacts video = Assert.IsType<VideoFacts>(facts.MotionVideo);
        Assert.True(video.IsPresent);
        Assert.Equal(VideoContainer.Mp4, video.Container);
        Assert.Equal(0, video.SourceIndex);
        Assert.Equal(ExpectedEmbeddedStart, video.ByteOffset);
        Assert.True(video.ByteLength > 0);
        Assert.Equal(sourceLength, EndExclusive(video.ByteOffset, video.ByteLength));
        Assert.Equal(0, facts.ProtocolTailLength);
        AssertGoogleV1MiCameraFacts(facts);
    }

    private static void AssertGoogleV1MiCameraFacts(SourceMediaFacts facts)
    {
        AuxiliaryMediaFacts subimage = Assert.Single(facts.AuxiliaryItems.Where(item => item.Semantic == "MiCameraSubimage"));
        AuxiliaryMediaFacts lenswatermark = Assert.Single(facts.AuxiliaryItems.Where(item => item.Semantic == "MiCameraLensWatermark"));
        Assert.Equal(2, facts.AuxiliaryItems.Count);
        AssertAuxiliaryFacts(subimage, "MiCameraSubimage", "micamera:xmpmeta:subimage",
            "micamera-xmpmeta-subimage", AuxiliaryCodec.Jpeg, ExpectedSubimageStart, ExpectedSubimageLength, ExpectedSubimageSha256);
        AssertAuxiliaryFacts(lenswatermark, "MiCameraLensWatermark", "micamera:xmpmeta:lenswatermark",
            "micamera-xmpmeta-lenswatermark", AuxiliaryCodec.Copy, ExpectedLenswatermarkStart, ExpectedLenswatermarkLength, ExpectedLenswatermarkSha256);
        Assert.Equal(ExpectedJpegEnd, subimage.ByteOffset);
        Assert.Equal(ExpectedLenswatermarkStart, subimage.ByteOffset + subimage.ByteLength);
        Assert.Equal(ExpectedEmbeddedStart, lenswatermark.ByteOffset + lenswatermark.ByteLength);
        Assert.DoesNotContain(facts.ConfirmedResidues, residue => residue.ExpectedSemantic.StartsWith("MiCamera", StringComparison.Ordinal));
    }

    private static void AssertEmbeddedMiCameraDescriptors(IReadOnlyList<AuxiliaryMediaDescriptor> descriptors)
    {
        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(ImageContainer.Jpeg, descriptor.ImageContainer);
            Assert.Equal(AuxiliaryRepresentation.Embedded, descriptor.Representation);
            Assert.Equal(AuxiliaryOwnership.Primary, descriptor.Ownership);
            Assert.Equal("primary:0", descriptor.OwnerIdentity);
            Assert.Equal(0, descriptor.SourceIndex);
            Assert.Null(descriptor.MaterializedArtifact);
        });
        Assert.Equal(AuxiliaryCodec.Jpeg, Assert.Single(descriptors.Where(descriptor => descriptor.Semantic == "MiCameraSubimage")).Codec);
        Assert.Equal(AuxiliaryCodec.Copy, Assert.Single(descriptors.Where(descriptor => descriptor.Semantic == "MiCameraLensWatermark")).Codec);
    }

    private static void AssertPostCleanMiCameraFacts(SourceMediaFacts facts, long cleanedImageLength)
    {
        Assert.Equal(2, facts.AuxiliaryItems.Count);
        Assert.Equal(cleanedImageLength, facts.PrimaryImage.ByteLength);
        AuxiliaryMediaFacts subimage = Assert.Single(facts.AuxiliaryItems.Where(item => item.Semantic == "MiCameraSubimage"));
        AuxiliaryMediaFacts lenswatermark = Assert.Single(facts.AuxiliaryItems.Where(item => item.Semantic == "MiCameraLensWatermark"));
        AssertAuxiliaryFacts(subimage, "MiCameraSubimage", "micamera:xmpmeta:subimage",
            "micamera-xmpmeta-subimage", AuxiliaryCodec.Jpeg, subimage.ByteOffset, ExpectedSubimageLength, ExpectedSubimageSha256);
        AssertAuxiliaryFacts(lenswatermark, "MiCameraLensWatermark", "micamera:xmpmeta:lenswatermark",
            "micamera-xmpmeta-lenswatermark", AuxiliaryCodec.Copy, lenswatermark.ByteOffset, ExpectedLenswatermarkLength, ExpectedLenswatermarkSha256);
        Assert.InRange(subimage.ByteOffset, 0, cleanedImageLength - subimage.ByteLength);
        Assert.InRange(lenswatermark.ByteOffset, 0, cleanedImageLength - lenswatermark.ByteLength);
        Assert.Equal(lenswatermark.ByteOffset, subimage.ByteOffset + subimage.ByteLength);
        Assert.Equal(cleanedImageLength, lenswatermark.ByteOffset + lenswatermark.ByteLength);
    }

    private static void AssertAuxiliaryFacts(
        AuxiliaryMediaFacts item,
        string semantic,
        string stableIdentity,
        string relationship,
        AuxiliaryCodec codec,
        long expectedOffset,
        long expectedLength,
        string expectedSha256)
    {
        Assert.True(item.IsPresent);
        Assert.Equal(semantic, item.Semantic);
        Assert.Equal(stableIdentity, item.StableIdentity);
        Assert.Equal(relationship, item.Relationship);
        Assert.Equal("primary:0", item.OwnerIdentity);
        Assert.Equal(0, item.SourceIndex);
        Assert.Equal(ImageContainer.Jpeg, item.Container);
        Assert.Equal(codec, item.Codec);
        Assert.Equal(AuxiliaryRepresentation.Embedded, item.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, item.Ownership);
        Assert.Equal(expectedOffset, item.ByteOffset);
        Assert.Equal(expectedLength, item.ByteLength);
        Assert.Equal(expectedSha256, item.Sha256);
    }

    private static long? EndExclusive(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > long.MaxValue - length)
        {
            return null;
        }

        return offset + length;
    }

    private static bool SameIdentity(FileIdentity left, FileIdentity right) =>
        left.Bytes == right.Bytes && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static object ExtractedRangeSnapshot(string sourcePath, long sourceOffset, long sourceLength, MediaArtifact artifact)
    {
        FileIdentity output = CaptureFileIdentity(artifact.Path);
        string sourceRangeSha256 = HashRange(sourcePath, sourceOffset, sourceLength);
        return new
        {
            role = artifact.Kind.ToString(),
            sourceRange = new
            {
                path = Path.GetFullPath(sourcePath),
                offset = sourceOffset,
                length = sourceLength,
                endExclusive = EndExclusive(sourceOffset, sourceLength),
                sha256 = sourceRangeSha256
            },
            output = new
            {
                path = Path.GetFullPath(artifact.Path),
                bytes = output.Bytes,
                sha256 = output.Sha256,
                declaredBytes = artifact.ByteLength,
                declaredSha256 = artifact.Sha256
            },
            exactRangeMatch = output.Bytes == sourceLength &&
                string.Equals(output.Sha256, sourceRangeSha256, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void AssertRangeSnapshotMatches(object snapshot)
    {
        string json = JsonSerializer.Serialize(snapshot, EvidenceJsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("exactRangeMatch").GetBoolean(), json);
    }

    private static object ArtifactSnapshot(MediaArtifact artifact)
    {
        FileIdentity identity = CaptureFileIdentity(artifact.Path);
        return new
        {
            kind = artifact.Kind.ToString(),
            path = identity.Path,
            bytes = identity.Bytes,
            actualSha256 = identity.Sha256,
            declaredBytes = artifact.ByteLength,
            declaredSha256 = artifact.Sha256,
            sourceOffset = artifact.SourceOffset,
            imageContainer = artifact.ImageContainer.ToString(),
            videoContainer = artifact.VideoContainer.ToString()
        };
    }

    private static object? PreservationSnapshot(PreservationReport? report) => report is null
        ? null
        : new
        {
            outcome = report.OverallOutcome.ToString(),
            report.Summary,
            items = report.Items.Select(item => new
            {
                item.Name,
                status = item.Status.ToString(),
                item.Details
            }).ToArray()
        };

    private static IReadOnlyList<WorkspaceFileIdentity> CaptureWorkspaceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorkspaceFileIdentity>();
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                FileIdentity identity = CaptureFileIdentity(path);
                return new WorkspaceFileIdentity(identity.Path, identity.Bytes, identity.Sha256);
            })
            .ToArray();
    }

    private static string[] DeclaredArtifactPaths(ExtractedMediaBundle? bundle, ProtocolCleanResult? cleanResult)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(bundle?.PrimaryImage);
        Add(bundle?.MotionVideo);
        Add(bundle?.GainMap);
        Add(bundle?.CleanupSource);
        Add(cleanResult?.CleanedImage);
        Add(cleanResult?.CleanedVideo);
        Add(cleanResult?.CleanedGainMap);
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(MediaArtifact? artifact)
        {
            if (artifact is not null)
            {
                paths.Add(Path.GetFullPath(artifact.Path));
            }
        }
    }

    private static string[] FindUnexplainedWorkspaceFiles(
        IReadOnlyList<WorkspaceFileIdentity> files,
        IEnumerable<string> declaredArtifactPaths)
    {
        var allowed = new HashSet<string>(declaredArtifactPaths, StringComparer.OrdinalIgnoreCase);
        return files.Where(file => !allowed.Contains(Path.GetFullPath(file.Path)))
            .Select(file => file.Path)
            .ToArray();
    }

    private static object ExceptionSnapshot(Exception exception) => new
    {
        type = exception.GetType().FullName,
        exception.Message
    };

    private sealed record FileIdentity(string Path, long Bytes, string Sha256);

    private sealed record WorkspaceFileIdentity(string Path, long Bytes, string Sha256);
}

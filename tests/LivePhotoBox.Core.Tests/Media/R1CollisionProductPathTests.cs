using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class R1CollisionProductPathTests
{
    private enum CleanExpectation
    {
        SuccessfulClean,
        RefusalAtNonLivePostCleanGate
    }

    private const string EvidenceRootVariable = "LPB_R1B_PRODUCT_EVIDENCE_ROOT";
    private const string GoogleCamera = "http://ns.google.com/photos/1.0/camera/";
    private const string GoogleContainer = "http://ns.google.com/photos/1.0/container/";
    private const string GoogleItem = "http://ns.google.com/photos/1.0/container/item/";
    private const string OtherNamespace = "urn:example:unrelated";
    private const string OppoWrongNamespace = "urn:example:oplus-camera/";
    private const string AdobeXmpHeader = "http://ns.adobe.com/xap/1.0/\0";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ITestOutputHelper _output;

    public R1CollisionProductPathTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task ProductClean_GoogleV2CustomSameLocalNamesPreservesCustomProperties()
    {
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = CreateFixtureBytes(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithNormalMotionPhotoText,
            "ns002-product-path");
        byte[] replay = CreateFixtureBytes(
            SyntheticProtocolFixtures.CreateGoogleV2JpegWithNormalMotionPhotoText,
            "ns002-product-path-replay");

        return VerifyInspectorExtractorCleanerAsync(
            "NS-002",
            "valid Google V2 owner plus unrelated same-local-name properties",
            canonical,
            derived,
            replay,
            SourceProtocol.GoogleMotionPhotoV2,
            preservedNamespaceUri: OtherNamespace,
            cleanExpectation: CleanExpectation.SuccessfulClean,
            removeGoogleV2Binding: true,
            removeHuaweiFooter: false);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task ProductClean_GoogleV2WrongNamespaceOppoClaimPreservesOppoLookalike()
    {
        const string invalidOppoClaim = " xmlns:OpCamera=\"urn:example:oplus-camera/\" OpCamera:MotionPhotoOwner=\"oplus\" OpCamera:OLivePhotoVersion=\"2\" OpCamera:VideoLength=\"100\"";
        byte[] canonical = BuildGoogleV2Jpeg();
        byte[] derived = BuildGoogleV2Jpeg(additionalDescriptionAttributes: invalidOppoClaim);
        byte[] replay = BuildGoogleV2Jpeg(additionalDescriptionAttributes: invalidOppoClaim);

        return VerifyInspectorExtractorCleanerAsync(
            "CP-003",
            "complete Google V2 owner plus OPlus-looking properties under a non-OPlus URI",
            canonical,
            derived,
            replay,
            SourceProtocol.GoogleMotionPhotoV2,
            preservedNamespaceUri: OppoWrongNamespace,
            cleanExpectation: CleanExpectation.SuccessfulClean,
            removeGoogleV2Binding: true,
            removeHuaweiFooter: false);
    }

    [Fact]
    [Trait("P7", "R1-B")]
    [Trait("EvidenceClass", "Synthetic")]
    public Task ProductClean_HuaweiWithIncompleteGoogleClaimFailsClosedAtNonLiveGate()
    {
        byte[] canonical = CreateFixtureBytes(
            SyntheticProtocolFixtures.CreateHuaweiJpeg,
            "cp002-product-path-canonical");
        byte[] derived = SyntheticProtocolFixtures.CreateHuaweiJpegWithIncompleteGoogleV2Claim();
        byte[] replay = SyntheticProtocolFixtures.CreateHuaweiJpegWithIncompleteGoogleV2Claim();

        return VerifyInspectorExtractorCleanerAsync(
            "CP-002",
            "valid Huawei LIVE_ owner plus incomplete Google MotionPhoto lookalike",
            canonical,
            derived,
            replay,
            SourceProtocol.HuaweiMovingPhoto,
            preservedNamespaceUri: GoogleCamera,
            cleanExpectation: CleanExpectation.RefusalAtNonLivePostCleanGate,
            removeGoogleV2Binding: false,
            removeHuaweiFooter: true);
    }

    private async Task VerifyInspectorExtractorCleanerAsync(
        string caseId,
        string mutation,
        byte[] canonical,
        byte[] derived,
        byte[] replay,
        SourceProtocol expectedProtocol,
        string preservedNamespaceUri,
        CleanExpectation cleanExpectation,
        bool removeGoogleV2Binding,
        bool removeHuaweiFooter)
    {
        Assert.Equal(derived, replay);
        string canonicalSha = Sha256(canonical);
        string derivedSha = Sha256(derived);
        string replaySha = Sha256(replay);
        Assert.Equal(derivedSha, replaySha);

        using var workspace = new MediaWorkspace();
        string workspaceRoot = workspace.RootDirectory;
        // The test owns this raw fixture; production workspace owns only artifacts
        // created through the registered extractor/cleaner publication path.
        using var inputWorkspace = new TestFixtureWorkspace();
        string inputPath = inputWorkspace.AllocateFilePath(caseId.ToLowerInvariant(), ".jpg");
        await File.WriteAllBytesAsync(inputPath, derived);
        string sourceShaBefore = await ComputeSha256Async(inputPath);

        InspectedSource? inspected = null;
        ExtractedMediaBundle? bundle = null;
        ProtocolCleanResult? cleanResult = null;
        ImageConversionResult? conversionResult = null;
        SourceMediaFacts? postCleanFacts = null;
        byte[]? cleanedOutput = null;
        IReadOnlyList<WorkspaceFileSnapshot> workspaceFiles = Array.Empty<WorkspaceFileSnapshot>();
        string[] declaredArtifacts = Array.Empty<string>();
        string[] unexplainedWorkspaceFiles = Array.Empty<string>();
        bool extractionSlicesMatchSource = false;
        string? independentStructureError = null;
        string[] inputNamespaceFacts = ReadNamespaceFacts(derived, preservedNamespaceUri);
        string[] outputNamespaceFacts = Array.Empty<string>();
        bool? googleBindingAbsentAfterClean = null;
        bool? huaweiFooterAbsentAfterClean = null;
        string? sourceShaAfterConversion = null;
        Exception? operationError = null;

        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(inputPath);
            Assert.Equal(expectedProtocol, inspected.Facts.Protocol);
            Assert.True(inspected.Facts.PrimaryImage.IsPresent);
            Assert.True(inspected.Facts.MotionVideo?.IsPresent == true);

            // The ordinary R2 image converter is applicable at the image boundary, but a
            // live-source cross-container request under Strict must fail before output publish.
            conversionResult = await new ImageConverter().ConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = inputPath,
                    Kind = MediaArtifactKind.PrimaryImage,
                    MimeType = "image/jpeg",
                    ImageContainer = ImageContainer.Jpeg,
                    ByteLength = new FileInfo(inputPath).Length
                },
                TargetContainer = ImageContainer.Heic,
                TargetDirectory = workspace.RootDirectory,
                Quality = 90,
                PreservationPolicy = PreservationPolicy.Strict
            });
            sourceShaAfterConversion = await ComputeSha256Async(inputPath);

            bundle = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                inputPath,
                secondaryPath: null,
                workspace);

            Assert.NotNull(bundle.CleanupPlan);
            Assert.NotNull(bundle.MotionVideo);
            extractionSlicesMatchSource =
                ArtifactMatchesInspectedSlice(derived, inspected.Facts.PrimaryImage, bundle.PrimaryImage, sourceIndex: 0) &&
                ArtifactMatchesInspectedSlice(derived, inspected.Facts.MotionVideo!, bundle.MotionVideo, sourceIndex: 0);
            inputNamespaceFacts = ReadNamespaceFacts(derived, preservedNamespaceUri);

            var request = new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = bundle.CleanupPlan,
                PreservationPolicy = PreservationPolicy.Strict
            };
            cleanResult = await new SourceProtocolCleaner().CleanAsync(request, workspace);

            if (cleanResult.Success && cleanResult.CleanedImage is not null)
            {
                cleanedOutput = await File.ReadAllBytesAsync(cleanResult.CleanedImage.Path);
                postCleanFacts = await new SourceInspector().InspectAsync(cleanResult.CleanedImage.Path);
                outputNamespaceFacts = ReadNamespaceFacts(cleanedOutput, preservedNamespaceUri);
                googleBindingAbsentAfterClean = !HasGoogleV2Binding(cleanedOutput);
                huaweiFooterAbsentAfterClean = !HasHuaweiLiveFooter(cleanedOutput);
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
            if (cleanResult?.CleanedImage is { } outputArtifact && File.Exists(outputArtifact.Path))
            {
                cleanedOutput = await File.ReadAllBytesAsync(outputArtifact.Path);
                try
                {
                    if (inputNamespaceFacts.Length == 0)
                    {
                        inputNamespaceFacts = ReadNamespaceFacts(derived, preservedNamespaceUri);
                    }
                    outputNamespaceFacts = ReadNamespaceFacts(cleanedOutput, preservedNamespaceUri);
                    googleBindingAbsentAfterClean = !HasGoogleV2Binding(cleanedOutput);
                    huaweiFooterAbsentAfterClean = !HasHuaweiLiveFooter(cleanedOutput);
                }
                catch (Exception structureException)
                {
                    independentStructureError = structureException.ToString();
                }
            }
        }

        string? sourceShaAfter = File.Exists(inputPath) ? await ComputeSha256Async(inputPath) : null;
        bool sourceBytesUnchanged = File.Exists(inputPath) &&
            File.ReadAllBytes(inputPath).AsSpan().SequenceEqual(derived);
        workspaceFiles = CaptureWorkspaceFiles(workspaceRoot);
        declaredArtifacts = DeclaredArtifactPaths(inputPath, bundle, cleanResult);
        unexplainedWorkspaceFiles = FindUnexplainedWorkspaceFiles(workspaceFiles, declaredArtifacts);
        string? outputSha = cleanedOutput is null ? null : Sha256(cleanedOutput);
        string? outputPath = cleanResult?.CleanedImage?.Path;
        string[] cleanupActions = cleanResult?.CleanupPlan?.Actions
            .Select(action => $"{action.OwnerProtocol}|{action.ArtifactRole}|{action.StructureKind}|{action.Selector}|{action.ExpectedSemantic}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        string[] removedFacts = cleanResult?.RemovedFacts
            .Select(fact => $"{fact.ProtocolName}|{fact.ResidueId}|{fact.ArtifactRole}|{fact.Operation}|{fact.AfterStatus}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        object factsBeforeClean = FactsSnapshot(inspected?.Facts);
        object? primaryArtifactSnapshot = ArtifactSnapshot(bundle?.PrimaryImage);
        object? motionArtifactSnapshot = ArtifactSnapshot(bundle?.MotionVideo);
        object? preservationSnapshot = PreservationSnapshot(cleanResult?.PreservationReport);
        object? operationErrorSnapshot = operationError is null ? null : ExceptionSnapshot(operationError);
        object? conversionSnapshot = conversionResult is null
            ? null
            : new
            {
                conversionResult.Success,
                requestedOperation = conversionResult.ExecutionRecord.Truth.RequestedOperationKind.ToString(),
                actualOperation = conversionResult.ExecutionRecord.Truth.ActualOperationKind.ToString(),
                failureStage = conversionResult.ExecutionRecord.Truth.FailureStage.ToString(),
                failureCategory = conversionResult.ExecutionRecord.Truth.FailureCategory.ToString(),
                conversionResult.OutputArtifact?.Path,
                outputExists = conversionResult.OutputArtifact is not null && File.Exists(conversionResult.OutputArtifact.Path)
            };

        inspected?.Dispose();
        bundle?.CleanupPlan?.Dispose();
        workspace.Dispose();
        bool workspaceRemovedAfterDispose = !Directory.Exists(workspaceRoot);

        string? workspaceEvidenceDirectory = PersistEvidence(caseId, canonical, derived, replay, cleanedOutput, new
        {
            schema = "LPB-P7-R1-B-product-path-v1",
            caseId,
            mutation,
            expectedProtocol = expectedProtocol.ToString(),
            cleanExpectation = cleanExpectation.ToString(),
            cleanExpectationRationale = cleanExpectation == CleanExpectation.RefusalAtNonLivePostCleanGate
                ? "The extracted primary retains an incomplete foreign Google MotionPhoto claim. The post-clean image must be re-inspected as NonLive; because inspection rejects that malformed claim, rollback is the required safe outcome and the claim must not be removed under Huawei authority."
                : "The unique Google V2 owner is cleaned while foreign or custom namespace facts remain unchanged.",
            canonical = new { bytes = canonical.LongLength, sha256 = canonicalSha },
            derived = new { bytes = derived.LongLength, sha256 = derivedSha },
            replay = new { bytes = replay.LongLength, sha256 = replaySha, matchesDerived = derivedSha == replaySha },
            sourceShaBefore,
            sourceShaAfter,
            sourceUnchanged = sourceShaBefore == sourceShaAfter,
            sourceBytesUnchanged,
            sourceShaAfterConversion,
            conversionApplicability = "Applicable at the image boundary; original live source cross-container conversion is exercised with Strict and must fail policy before output publish.",
            converter = conversionSnapshot,
            factsBeforeClean,
            extractor = new
            {
                inspectorIssuedPlanUsed = inspected is not null,
                extractorIssuedCleanupPlan = bundle?.CleanupPlan is not null,
                exactInspectedSlicesMatch = extractionSlicesMatchSource,
                primaryImage = primaryArtifactSnapshot,
                motionVideo = motionArtifactSnapshot
            },
            cleaner = new
            {
                cleanResult?.Success,
                expectedProtocol = cleanResult?.CleanupPlan?.Protocol.ToString(),
                failureCategory = cleanResult?.FailureCategory?.ToString(),
                failureStage = cleanResult?.FailureStage?.ToString(),
                transactionState = cleanResult?.TransactionState?.ToString(),
                errorMessage = cleanResult?.ErrorMessage,
                preservationOutcome = cleanResult?.PreservationOutcome.ToString(),
                preservationReport = preservationSnapshot,
                cleanupActions,
                removedFacts,
                outputPath,
                outputBytes = cleanedOutput?.LongLength,
                outputSha256 = outputSha,
                postCleanProtocol = postCleanFacts?.Protocol.ToString()
            },
            independentStructure = new
            {
                parser = "test-side JPEG marker-bound standard APP1 XMP scan and namespace-aware XML parse",
                preservedNamespaceUri,
                inputNamespaceFacts,
                outputNamespaceFacts,
                googleBindingAbsentAfterClean,
                huaweiFooterAbsentAfterClean,
                independentStructureError
            },
            workspaceRoot,
            declaredArtifacts,
            workspaceFiles,
            unexplainedWorkspaceFiles,
            workspaceRemovedAfterDispose,
            operationError = operationErrorSnapshot
        });

        _output.WriteLine(
            $"R1B_PRODUCT|id={caseId}|expectedProtocol={expectedProtocol}|actualProtocol={inspected?.Facts.Protocol}|cleanSuccess={cleanResult?.Success}|postCleanProtocol={postCleanFacts?.Protocol}|sourceSha256Before={sourceShaBefore}|sourceSha256After={sourceShaAfter}|canonicalSha256={canonicalSha}|derivedSha256={derivedSha}|replaySha256={replaySha}|outputSha256={outputSha}|evidence={workspaceEvidenceDirectory ?? "none"}");

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        Assert.NotNull(inspected);
        Assert.NotNull(bundle);
        Assert.NotNull(cleanResult);
        Assert.NotNull(conversionResult);
        Assert.False(conversionResult.Success, "Strict cross-container conversion unexpectedly published from the live collision input.");
        Assert.Null(conversionResult.OutputArtifact);
        Assert.Equal(ConversionFailureStage.PolicyRejection, conversionResult.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.PolicyRejection, conversionResult.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(sourceShaBefore, sourceShaAfterConversion);
        Assert.True(extractionSlicesMatchSource, "Extractor output did not match Inspector-authorized source ranges.");
        if (cleanExpectation == CleanExpectation.SuccessfulClean)
        {
            Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
            Assert.NotNull(cleanResult.CleanedImage);
            Assert.NotNull(cleanResult.CleanupPlan);
            Assert.Equal(expectedProtocol, cleanResult.CleanupPlan.Protocol);
            Assert.NotEmpty(cleanResult.CleanupPlan.Actions);
            Assert.All(cleanResult.CleanupPlan.Actions, action => Assert.Equal(expectedProtocol, action.OwnerProtocol));
            Assert.NotEmpty(cleanResult.RemovedFacts);
            Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationOutcome);
            Assert.NotNull(cleanResult.PreservationReport);
            Assert.Equal(PreservationOutcome.Preserved, cleanResult.PreservationReport.OverallOutcome);
            Assert.DoesNotContain(cleanResult.PreservationReport.Items, item =>
                item.Status is PreservationCheckStatus.Failed or PreservationCheckStatus.UnableToVerify);
            Assert.NotNull(cleanedOutput);
            Assert.NotNull(postCleanFacts);
            Assert.Equal(SourceProtocol.NonLive, postCleanFacts.Protocol);
            Assert.Equal(sourceShaBefore, sourceShaAfter);
            Assert.NotEmpty(inputNamespaceFacts);
            Assert.Equal(inputNamespaceFacts, outputNamespaceFacts);
            Assert.NotNull(googleBindingAbsentAfterClean);
            Assert.NotNull(huaweiFooterAbsentAfterClean);
            Assert.True(googleBindingAbsentAfterClean == removeGoogleV2Binding,
                "The Google V2 binding was not removed/preserved according to the unique-owner case.");
            Assert.Equal(removeHuaweiFooter, HasHuaweiLiveFooter(derived));
            Assert.True(huaweiFooterAbsentAfterClean.Value,
                "A successful neutral output must not retain a Huawei LIVE_ footer.");
        }
        else
        {
            Assert.Equal(CleanExpectation.RefusalAtNonLivePostCleanGate, cleanExpectation);
            Assert.False(cleanResult.Success,
                "The incomplete foreign Google claim unexpectedly passed the NonLive post-clean gate.");
            Assert.Equal(CleanerFailureCategory.MediaInvalid, cleanResult.FailureCategory);
            Assert.Equal(CleanerFailureStage.PostCleanInspection, cleanResult.FailureStage);
            Assert.Equal(CleanerTransactionState.RolledBack, cleanResult.TransactionState);
            Assert.Equal(PreservationOutcome.PartiallyPreserved, cleanResult.PreservationOutcome);
            Assert.Null(cleanResult.CleanedImage);
            Assert.Null(cleanResult.CleanedVideo);
            Assert.Null(cleanResult.CleanedGainMap);
            Assert.Empty(cleanResult.RemovedFacts);
            Assert.Null(cleanedOutput);
            Assert.Null(outputPath);
            Assert.Null(postCleanFacts);
            Assert.Equal(sourceShaBefore, sourceShaAfter);
            Assert.True(sourceBytesUnchanged,
                "Safe refusal changed the source bytes.");
            Assert.NotEmpty(inputNamespaceFacts);
            Assert.True(HasGoogleV2Binding(derived),
                "The incomplete Google claim was not present in the preserved Huawei collision fixture.");
            string[] expectedWorkspaceFiles =
            [
                Path.GetFullPath(bundle!.PrimaryImage.Path),
                Path.GetFullPath(bundle.MotionVideo!.Path)
            ];
            // Raw source belongs to inputWorkspace, whose identity/bytes were
            // independently checked above; the product root contains only outputs.
            Assert.True(File.Exists(inputPath));
            Assert.Equal(
                expectedWorkspaceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                workspaceFiles.Select(file => Path.GetFullPath(file.Path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }
        Assert.Empty(unexplainedWorkspaceFiles);
        Assert.True(workspaceRemovedAfterDispose, "The product-path workspace remained after disposal.");
    }

    private static byte[] BuildGoogleV2Jpeg(string additionalDescriptionAttributes = "")
    {
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"{GoogleCamera}\" xmlns:Container=\"{GoogleContainer}\" xmlns:Item=\"{GoogleItem}\"{additionalDescriptionAttributes} GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{video.Length}\" Item:Padding=\"0\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        return [.. jpeg, .. video];
    }

    private static byte[] CreateFixtureBytes(Action<string> create, string name)
    {
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath(name, ".jpg");
        create(path);
        return File.ReadAllBytes(path);
    }

    private static bool ArtifactMatchesInspectedSlice(
        byte[] sourceBytes,
        ImageFacts facts,
        MediaArtifact artifact,
        int sourceIndex)
    {
        return facts.IsPresent &&
               sourceIndex == 0 &&
               facts.ByteOffset >= 0 &&
               facts.ByteLength > 0 &&
               facts.ByteOffset <= sourceBytes.LongLength - facts.ByteLength &&
               artifact.SourceOffset == facts.ByteOffset &&
               artifact.ByteLength == facts.ByteLength &&
               File.ReadAllBytes(artifact.Path).AsSpan().SequenceEqual(
                   sourceBytes.AsSpan(checked((int)facts.ByteOffset), checked((int)facts.ByteLength)));
    }

    private static bool ArtifactMatchesInspectedSlice(
        byte[] sourceBytes,
        VideoFacts facts,
        MediaArtifact artifact,
        int sourceIndex)
    {
        return facts.IsPresent &&
               facts.SourceIndex == sourceIndex &&
               sourceIndex == 0 &&
               facts.ByteOffset >= 0 &&
               facts.ByteLength > 0 &&
               facts.ByteOffset <= sourceBytes.LongLength - facts.ByteLength &&
               artifact.SourceOffset == facts.ByteOffset &&
               artifact.ByteLength == facts.ByteLength &&
               File.ReadAllBytes(artifact.Path).AsSpan().SequenceEqual(
                   sourceBytes.AsSpan(checked((int)facts.ByteOffset), checked((int)facts.ByteLength)));
    }

    private static string[] ReadNamespaceFacts(byte[] media, string namespaceUri)
    {
        return ReadStandardXmpDocuments(media)
            .SelectMany(document => document.Root!.DescendantsAndSelf()
                .SelectMany(element =>
                {
                    string elementPath = ExpandedPath(element);
                    var facts = new List<string>();
                    if (string.Equals(element.Name.NamespaceName, namespaceUri, StringComparison.Ordinal))
                    {
                        facts.Add($"E|{elementPath}|{element.Value}");
                    }

                    foreach (XAttribute attribute in element.Attributes())
                    {
                        if (!attribute.IsNamespaceDeclaration &&
                            string.Equals(attribute.Name.NamespaceName, namespaceUri, StringComparison.Ordinal))
                        {
                            facts.Add($"A|{elementPath}|{attribute.Name.LocalName}|{attribute.Value}");
                        }
                    }

                    return facts;
                }))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ExpandedPath(XElement element)
    {
        return string.Join("/", element.AncestorsAndSelf().Reverse()
            .Select(node => $"{{{node.Name.NamespaceName}}}{node.Name.LocalName}"));
    }

    private static IReadOnlyList<XDocument> ReadStandardXmpDocuments(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("The product output is not a JPEG beginning with SOI.");
        }

        byte[] adobeHeader = Encoding.ASCII.GetBytes(AdobeXmpHeader);
        var documents = new List<XDocument>();
        int position = 2;
        bool sawEoi = false;
        while (position < jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
            {
                throw new InvalidDataException($"Expected JPEG marker prefix at offset {position} before SOS.");
            }

            while (position < jpeg.Length && jpeg[position] == 0xFF)
            {
                position++;
            }

            if (position >= jpeg.Length)
            {
                throw new InvalidDataException("JPEG ends inside a marker prefix.");
            }

            byte marker = jpeg[position++];
            if (marker == 0xD9)
            {
                sawEoi = true;
                break;
            }

            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
            {
                continue;
            }

            if (position + 2 > jpeg.Length)
            {
                throw new InvalidDataException("JPEG segment length is truncated.");
            }

            int length = (jpeg[position] << 8) | jpeg[position + 1];
            if (length < 2 || length > jpeg.Length - position)
            {
                throw new InvalidDataException($"JPEG segment at offset {position - 2} has invalid length {length}.");
            }

            int payloadStart = position + 2;
            int payloadLength = length - 2;
            int segmentEnd = position + length;
            if (marker == 0xDA)
            {
                sawEoi = FindJpegScanEoi(jpeg, segmentEnd);
                if (!sawEoi)
                {
                    throw new InvalidDataException("JPEG scan has no EOI marker.");
                }

                break;
            }

            if (marker == 0xE1 && payloadLength >= adobeHeader.Length &&
                jpeg.AsSpan(payloadStart, adobeHeader.Length).SequenceEqual(adobeHeader))
            {
                using var xmlStream = new MemoryStream(jpeg, payloadStart + adobeHeader.Length, payloadLength - adobeHeader.Length, writable: false);
                using XmlReader reader = XmlReader.Create(xmlStream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = false,
                    IgnoreWhitespace = false
                });
                documents.Add(XDocument.Load(reader, LoadOptions.PreserveWhitespace));
            }

            position = segmentEnd;
        }

        if (!sawEoi)
        {
            throw new InvalidDataException("JPEG has no structurally placed EOI marker.");
        }

        return documents;
    }

    private static bool FindJpegScanEoi(byte[] jpeg, int scanStart)
    {
        int position = scanStart;
        while (position < jpeg.Length)
        {
            if (jpeg[position++] != 0xFF)
            {
                continue;
            }

            while (position < jpeg.Length && jpeg[position] == 0xFF)
            {
                position++;
            }

            if (position >= jpeg.Length)
            {
                return false;
            }

            byte marker = jpeg[position++];
            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker == 0xD9)
            {
                return true;
            }

            throw new InvalidDataException($"Unexpected marker 0xFF{marker:X2} inside single-scan JPEG entropy data.");
        }

        return false;
    }

    private static bool HasGoogleV2Binding(byte[] jpeg)
    {
        XName motionPhoto = XName.Get("MotionPhoto", GoogleCamera);
        XName motionPhotoVersion = XName.Get("MotionPhotoVersion", GoogleCamera);
        XName directory = XName.Get("Directory", GoogleContainer);
        XName sequence = XName.Get("Seq", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
        XName listItem = XName.Get("li", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
        XName containerItem = XName.Get("Item", GoogleContainer);
        XName semantic = XName.Get("Semantic", GoogleItem);
        return ReadStandardXmpDocuments(jpeg).Any(document =>
            document.Root!.DescendantsAndSelf().Any(element =>
                element.Attributes().Any(attribute => attribute.Name == motionPhoto || attribute.Name == motionPhotoVersion)) ||
            document.Root!.DescendantsAndSelf(directory)
                .SelectMany(directoryElement => directoryElement.Elements(sequence))
                .SelectMany(sequenceElement => sequenceElement.Elements(listItem))
                .SelectMany(listItemElement => listItemElement.Elements(containerItem))
                .Any(item => item.Attribute(semantic)?.Value == "MotionPhoto"));
    }

    private static bool HasHuaweiLiveFooter(byte[] jpeg)
    {
        const int footerLength = 60;
        if (jpeg.Length < footerLength)
        {
            return false;
        }

        ReadOnlySpan<byte> footer = jpeg.AsSpan(jpeg.Length - footerLength, footerLength);
        return footer.Slice(40, 5).SequenceEqual("LIVE_"u8);
    }

    private static IReadOnlyList<WorkspaceFileSnapshot> CaptureWorkspaceFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<WorkspaceFileSnapshot>();
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new WorkspaceFileSnapshot(path, new FileInfo(path).Length, Sha256(File.ReadAllBytes(path))))
            .ToArray();
    }

    private static string[] DeclaredArtifactPaths(
        string inputPath,
        ExtractedMediaBundle? bundle,
        ProtocolCleanResult? cleanResult)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(inputPath)
        };
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
        IReadOnlyList<WorkspaceFileSnapshot> files,
        IEnumerable<string> declaredArtifactPaths)
    {
        var allowed = new HashSet<string>(declaredArtifactPaths, StringComparer.OrdinalIgnoreCase);
        return files.Where(file => !allowed.Contains(Path.GetFullPath(file.Path)))
            .Select(file => file.Path)
            .ToArray();
    }

    private static string? PersistEvidence(
        string caseId,
        byte[] canonical,
        byte[] derived,
        byte[] replay,
        byte[]? cleanedOutput,
        object evidence)
    {
        string? root = Environment.GetEnvironmentVariable(EvidenceRootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        string evidenceRoot = Path.GetFullPath(root);
        string caseDirectory = Path.Combine(evidenceRoot, caseId);
        Directory.CreateDirectory(caseDirectory);
        WriteCreateOnly(Path.Combine(caseDirectory, "canonical.jpg"), canonical);
        WriteCreateOnly(Path.Combine(caseDirectory, "input.jpg"), derived);
        WriteCreateOnly(Path.Combine(caseDirectory, "replay.jpg"), replay);
        if (cleanedOutput is not null)
        {
            WriteCreateOnly(Path.Combine(caseDirectory, "cleaned-output.jpg"), cleanedOutput);
        }

        string resultPath = Path.Combine(caseDirectory, "product-case.json");
        using (var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, evidence, EvidenceJsonOptions);
            stream.Flush(flushToDisk: true);
        }

        return caseDirectory;
    }

    private static void WriteCreateOnly(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static object FactsSnapshot(SourceMediaFacts? facts) => facts is null
        ? new { present = false }
        : new
        {
            present = true,
            protocol = facts.Protocol.ToString(),
            primarySha256 = facts.PrimarySha256,
            primaryImage = new
            {
                facts.PrimaryImage.IsPresent,
                facts.PrimaryImage.ByteOffset,
                facts.PrimaryImage.ByteLength,
                container = facts.PrimaryImage.Container.ToString()
            },
            motionVideo = facts.MotionVideo is null
                ? null
                : new
                {
                    facts.MotionVideo.IsPresent,
                    facts.MotionVideo.ByteOffset,
                    facts.MotionVideo.ByteLength,
                    facts.MotionVideo.SourceIndex,
                    container = facts.MotionVideo.Container.ToString()
                },
            facts.ProtocolTailOffset,
            facts.ProtocolTailLength
        };

    private static object? ArtifactSnapshot(MediaArtifact? artifact) => artifact is null
        ? null
        : new
        {
            artifact.Kind,
            artifact.Path,
            artifact.ByteLength,
            artifact.SourceOffset,
            artifact.Sha256,
            actualSha256 = File.Exists(artifact.Path) ? Sha256(File.ReadAllBytes(artifact.Path)) : null
        };

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

    private static object ExceptionSnapshot(Exception exception) => new
    {
        type = exception.GetType().FullName,
        exception.Message
    };

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record WorkspaceFileSnapshot(string Path, long Bytes, string Sha256);
}

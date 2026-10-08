using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Interop;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P6NeutralPipelineTests
{
    private readonly ITestOutputHelper _output;

    public P6NeutralPipelineTests(ITestOutputHelper output) => _output = output;

    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    public static TheoryData<string, string, string?, SourceProtocol, bool> DirectNeutralRealSampleRoutes => new()
    {
        { "P6-S01", "红米老款-GV1.JPG", null, SourceProtocol.GoogleMicroVideoV1, false },
        { "P6-S02", "小米.jpg", null, SourceProtocol.GoogleMotionPhotoV2, true },
        { "P6-S03", "oppo.jpg", null, SourceProtocol.OppoLivePhoto, false },
        { "P6-S04", "一加.jpg", null, SourceProtocol.OppoLivePhoto, false },
        { "P6-S05", "一加-改了封面照片.jpg", null, SourceProtocol.OppoLivePhoto, false },
        { "P6-S06", "vivo.jpg", null, SourceProtocol.VivoLivePhoto, false },
        { "P6-S07", "三星.jpg", null, SourceProtocol.SamsungMotionPhotoJpeg, false },
        { "P6-S08", "三星.heic", null, SourceProtocol.SamsungMotionPhotoHeic, false },
        { "P6-S09", "华为-Mate80.jpg", null, SourceProtocol.HuaweiMovingPhoto, false },
        { "P6-S10", "华为Mate80.heic", null, SourceProtocol.HuaweiMovingPhoto, false },
        { "P6-S11", "荣耀.jpg", null, SourceProtocol.HonorMovingPhoto, false },
        { "P6-D01", "vivo双文件.jpg", "vivo双文件.mp4", SourceProtocol.VivoLegacyDualFile, false },
        { "P6-D02", "苹果-双文件.JPG", "苹果-双文件.MOV", SourceProtocol.AppleLivePhoto, false },
        { "P6-D03", "苹果双文件.HEIC", "苹果双文件.MOV", SourceProtocol.AppleLivePhoto, false }
    };

    [Theory]
    [MemberData(nameof(DirectNeutralRealSampleRoutes))]
    [Trait("Category", "RealSamples")]
    public async Task P6_DirectNeutral_RealSampleRouteMatrix_UsesFrozenRealSampleInputs(
        string routeId,
        string primaryFilename,
        string? secondaryFilename,
        SourceProtocol expectedProtocol,
        bool expectedUnsupported)
    {
        bool isAuthorizedXiaomiRoute = routeId == "P6-S02" &&
            string.Equals(primaryFilename, "小米.jpg", StringComparison.Ordinal) &&
            secondaryFilename == null && expectedProtocol == SourceProtocol.GoogleMotionPhotoV2;
        Assert.Equal(isAuthorizedXiaomiRoute, expectedUnsupported);

        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["routeId"] = routeId,
            ["primaryFilename"] = primaryFilename,
            ["secondaryFilename"] = secondaryFilename,
            ["expectedProtocol"] = expectedProtocol.ToString(),
            ["expectedOutcome"] = expectedUnsupported ? "Unsupported" : "NeutralMediaBundle"
        };

        string? primaryPath = null;
        string? secondaryPath = null;
        string? workspaceRoot = null;
        SampleIdentitySnapshot? primaryBefore = null;
        SampleIdentitySnapshot? secondaryBefore = null;
        Exception? unexpectedFailure = null;
        string? unsupportedReason = null;
        object? bundleEvidence = null;

        try
        {
            primaryPath = ResolveSample(primaryFilename);
            primaryBefore = await CaptureSampleIdentityAsync(primaryFilename, primaryPath);
            evidence["primaryBefore"] = primaryBefore;
            Assert.True(primaryBefore.Matches,
                $"{routeId} primary must match its required P4-v1 identity: {JsonSerializer.Serialize(primaryBefore)}");

            if (secondaryFilename != null)
            {
                secondaryPath = ResolveSample(secondaryFilename);
                secondaryBefore = await CaptureSampleIdentityAsync(secondaryFilename, secondaryPath);
                evidence["secondaryBefore"] = secondaryBefore;
                Assert.True(secondaryBefore.Matches,
                    $"{routeId} paired secondary must match its required P4-v1 identity: {JsonSerializer.Serialize(secondaryBefore)}");
            }

            SourceMediaFacts sourceFacts = await new SourceInspector()
                .InspectAsync(primaryPath, secondaryPath);
            evidence["sourceInspection"] = new
            {
                protocol = sourceFacts.Protocol.ToString(),
                primaryImageContainer = sourceFacts.PrimaryImage.Container.ToString(),
                motionVideoPresent = sourceFacts.MotionVideo is { IsPresent: true },
                gainMapPresent = sourceFacts.GainMap is { IsPresent: true },
                auxiliaryItemCount = sourceFacts.AuxiliaryItems.Count,
                protocolTailLength = sourceFacts.ProtocolTailLength,
                hasPairingIdentifier = sourceFacts.PairingIdentifier != null
            };
            Assert.Equal(expectedProtocol, sourceFacts.Protocol);

            using var workspace = new MediaWorkspace();
            workspaceRoot = workspace.RootDirectory;
            if (expectedUnsupported)
            {
                Assert.True(sourceFacts.MotionVideo is { IsPresent: true },
                    "The Xiaomi exception must occur after upstream motion inspection succeeds.");
                Assert.True(sourceFacts.GainMap is { IsPresent: true },
                    "The Xiaomi exception must occur after upstream GainMap inspection succeeds.");

                NotSupportedException unsupported = await Assert.ThrowsAsync<NotSupportedException>(async () =>
                    await new NeutralMediaService().CreateNeutralBundleAsync(
                        primaryPath, secondaryPath, workspace));
                unsupportedReason = unsupported.Message;
                Assert.Contains("final PrimaryImage orientation", unsupported.Message,
                    StringComparison.OrdinalIgnoreCase);
                evidence["outcome"] = "Unsupported";
            }
            else
            {
                NeutralMediaBundle bundle = await new NeutralMediaService()
                    .CreateNeutralBundleAsync(primaryPath, secondaryPath, workspace);
                evidence["outcome"] = "NeutralMediaBundle";
                bundleEvidence = await AssertAndCaptureDirectNeutralBundleAsync(
                    routeId,
                    expectedProtocol,
                    primaryBefore,
                    secondaryBefore,
                    bundle);
            }
        }
        catch (Exception exception)
        {
            unexpectedFailure = exception;
            throw;
        }
        finally
        {
            evidence["bundle"] = bundleEvidence;
            evidence["unsupportedReason"] = unsupportedReason;
            evidence["unexpectedFailure"] = unexpectedFailure == null
                ? null
                : new { type = unexpectedFailure.GetType().FullName, message = unexpectedFailure.Message };

            SampleIdentitySnapshot? primaryAfter = primaryPath == null
                ? null
                : await CaptureSampleIdentityAsync(primaryFilename, primaryPath);
            SampleIdentitySnapshot? secondaryAfter = secondaryFilename == null || secondaryPath == null
                ? null
                : await CaptureSampleIdentityAsync(secondaryFilename, secondaryPath);
            bool primaryUnchanged = primaryBefore != null && primaryAfter != null &&
                string.Equals(primaryBefore.ActualSha256, primaryAfter.ActualSha256, StringComparison.OrdinalIgnoreCase);
            bool? secondaryUnchanged = secondaryFilename == null
                ? null
                : secondaryBefore != null && secondaryAfter != null &&
                    string.Equals(secondaryBefore.ActualSha256, secondaryAfter.ActualSha256, StringComparison.OrdinalIgnoreCase);
            bool? workspaceCleanupConfirmed = workspaceRoot == null ? null : !Directory.Exists(workspaceRoot);

            evidence["primaryAfter"] = primaryAfter;
            evidence["secondaryAfter"] = secondaryAfter;
            evidence["sourceUnchanged"] = new { primary = primaryUnchanged, secondary = secondaryUnchanged };
            evidence["workspaceCleanupConfirmed"] = workspaceCleanupConfirmed;
            _output.WriteLine($"P6_NEUTRAL_ROUTE_RESULT_JSON={JsonSerializer.Serialize(evidence)}");

            if (primaryBefore != null)
                Assert.True(primaryUnchanged, $"{routeId} changed its frozen primary source.");
            if (secondaryBefore != null)
                Assert.True(secondaryUnchanged, $"{routeId} changed its frozen paired secondary source.");
            if (workspaceCleanupConfirmed.HasValue)
                Assert.True(workspaceCleanupConfirmed.Value, $"{routeId} left its MediaWorkspace behind.");
        }
    }

    [Fact]
    public void P6_NeutralSemanticGraph_HasNoPlatformBackendOrSourceProtocolAuthority()
    {
        var visited = new HashSet<Type>();
        InspectSemanticGraph(typeof(NeutralMediaSemantics), visited);

        Assert.DoesNotContain(typeof(WindowsFileIdentity), visited);
        Assert.DoesNotContain(typeof(ImageHdrGainMapSourceBinding), visited);
        Assert.DoesNotContain(typeof(SourceMediaFacts), visited);
        Assert.DoesNotContain(typeof(SourceProtocol), visited);
        Assert.DoesNotContain(typeof(VideoBackend), visited);
        Assert.DoesNotContain(typeof(VideoHardwareMode), visited);

        Type[] semanticTypes =
        [
            typeof(NeutralMediaSemantics),
            typeof(NeutralArtifactSemantics),
            typeof(NeutralAuxiliarySemantics),
            typeof(NeutralTimingSemantics),
            typeof(NeutralOrientationSemantics),
            typeof(NeutralOrientationTransform),
            typeof(NeutralColorHdrSemantics),
            typeof(NeutralArtifactPreservation),
            typeof(NeutralPreservationSemantics)
        ];
        var shape = semanticTypes.Select(type => new
        {
            type = type.FullName,
            properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new
                {
                    name = property.Name,
                    type = property.PropertyType.FullName ?? property.PropertyType.Name
                })
                .ToArray()
        }).ToArray();
        _output.WriteLine($"P6_SEMANTIC_MODEL_SHAPE_JSON={JsonSerializer.Serialize(shape)}");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_EquivalentFinalArtifacts_ProjectSameSemanticsAcrossDifferentSourceProtocols()
    {
        string primary = ResolveSample("苹果-双文件.JPG");
        string motion = ResolveSample("苹果-双文件.MOV");
        using var originalWorkspace = new MediaWorkspace();
        using var alteredProvenanceWorkspace = new MediaWorkspace();

        NeutralMediaBundle original = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, motion, originalWorkspace);
        NeutralMediaBundle alteredProvenance = await new NeutralMediaService(
                inspector: new SourceProtocolOverrideInspector(SourceProtocol.GoogleMotionPhotoV2))
            .CreateNeutralBundleAsync(primary, motion, alteredProvenanceWorkspace);

        Assert.Equal(SourceProtocol.AppleLivePhoto, original.SourceProvenance.Protocol);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, alteredProvenance.SourceProvenance.Protocol);
        Assert.NotEqual(original.PrimaryImage.Path, alteredProvenance.PrimaryImage.Path);
        Assert.Equal(original.Semantics.PrimaryImage.ContentIdentity, alteredProvenance.Semantics.PrimaryImage.ContentIdentity);
        Assert.Equal(original.Semantics.MotionVideo?.ContentIdentity, alteredProvenance.Semantics.MotionVideo?.ContentIdentity);
        Assert.Equal(JsonSerializer.Serialize(original.Semantics), JsonSerializer.Serialize(alteredProvenance.Semantics));
        Assert.Equal(await ContentIdentityAsync(original.PrimaryImage.Path), original.Semantics.PrimaryImage.ContentIdentity);
        Assert.DoesNotContain(original.PrimaryImage.Path, JsonSerializer.Serialize(original.Semantics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_AppleHeicPair_ProjectsFinalGainMapIdentityOwnerAndRelationship()
    {
        string primary = ResolveSample("苹果双文件.HEIC");
        string secondary = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, secondary, workspace);
        SourceMediaFacts finalFacts = await new SourceInspector()
            .InspectAsync(bundle.PrimaryImage.Path);
        GainMapFacts finalGainMap = Assert.IsType<GainMapFacts>(finalFacts.GainMap);
        AuxiliaryMediaFacts finalItem = finalFacts.AuxiliaryItems[checked((int)finalGainMap.AuxiliaryIndex)];
        NeutralAuxiliarySemantics semantic = Assert.IsType<NeutralAuxiliarySemantics>(bundle.Semantics.GainMap);
        NeutralArtifactManifest gainMapManifest = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");

        Assert.Equal(gainMapManifest.StableIdentity, semantic.StableIdentity);
        Assert.Equal(finalItem.StableIdentity, gainMapManifest.StableIdentity);
        Assert.Equal(finalItem.Sha256, gainMapManifest.Sha256, ignoreCase: true);
        Assert.Equal(finalItem.Semantic, semantic.Semantic);
        Assert.Equal(finalItem.OwnerIdentity, semantic.OwnerIdentity);
        Assert.Equal(finalItem.Relationship, semantic.Relationship);
        Assert.Equal(NeutralEvidenceState.Verified, semantic.EvidenceState);
        Assert.Equal(await ContentIdentityAsync(bundle.PrimaryImage.Path), bundle.Semantics.PrimaryImage.ContentIdentity);
        NeutralArtifactManifest imageManifest = Assert.Single(bundle.Manifest, item => item.Role == "PrimaryImage");
        Assert.Equal(imageManifest.PreservationOutcome, bundle.Semantics.Preservation.PrimaryImage.Outcome);
        Assert.NotEqual(NeutralEvidenceState.Unknown, bundle.Semantics.Preservation.PrimaryImage.EvidenceState);

        NeutralOrientationTransform imageOrientation = bundle.Semantics.Orientation.PrimaryImage;
        Assert.Equal(0, imageOrientation.ClockwiseRotationDegrees);
        Assert.Equal(NeutralReflection.None, imageOrientation.Reflection);
        Assert.Equal(NeutralEvidenceState.Verified, imageOrientation.RotationEvidence);
        Assert.Equal(NeutralEvidenceState.Verified, imageOrientation.ReflectionEvidence);

        NeutralOrientationTransform videoOrientation = Assert.IsType<NeutralOrientationTransform>(
            bundle.Semantics.Orientation.MotionVideo);
        VideoFacts finalVideoFacts = await new VideoConverter().ProbeAsync(bundle.MotionVideo!.Path);
        int? normalizedVideoRotation = NormalizeRotation(finalVideoFacts.RotationDegrees);
        Assert.Equal(normalizedVideoRotation, videoOrientation.ClockwiseRotationDegrees);
        Assert.Equal(NeutralEvidenceState.Verified, videoOrientation.RotationEvidence);
        _output.WriteLine($"P6_APPLE_FINAL_ORIENTATION_JSON={JsonSerializer.Serialize(new
        {
            primaryImage = new { imageOrientation.ClockwiseRotationDegrees, imageOrientation.Reflection },
            motionVideo = new { videoOrientation.ClockwiseRotationDegrees, videoOrientation.RotationEvidence },
            probedVideoRotationDegrees = finalVideoFacts.RotationDegrees
        })}");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_SamsungHeic_ProjectsFinalPrimaryItemRotationFromRealSample()
    {
        string primary = ResolveSample("三星.heic");
        NativeImageOrientationObservationV1 sourceOrientation = await NativeMediaService
            .ObserveImageOrientationAsync(primary, ImageContainer.Heic);
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, null, workspace);
        NativeImageOrientationObservationV1 finalOrientation = await NativeMediaService
            .ObserveImageOrientationAsync(bundle.PrimaryImage.Path, bundle.PrimaryImage.ImageContainer);
        NeutralOrientationTransform neutralOrientation = bundle.Semantics.Orientation.PrimaryImage;

        Assert.Equal(90, sourceOrientation.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationReflection.None, sourceOrientation.Reflection);
        Assert.Equal(finalOrientation.ClockwiseRotationDegrees, neutralOrientation.ClockwiseRotationDegrees);
        Assert.Equal(finalOrientation.Reflection == NativeImageOrientationReflection.None
            ? NeutralReflection.None
            : NeutralReflection.Horizontal, neutralOrientation.Reflection);
        Assert.Equal(NeutralEvidenceState.Verified, neutralOrientation.RotationEvidence);
        Assert.Equal(NeutralEvidenceState.Verified, neutralOrientation.ReflectionEvidence);
        _output.WriteLine($"P6_SAMSUNG_FINAL_ORIENTATION_JSON={JsonSerializer.Serialize(new
        {
            source = new { sourceOrientation.ClockwiseRotationDegrees, sourceOrientation.Reflection },
            final = new { finalOrientation.ClockwiseRotationDegrees, finalOrientation.Reflection },
            neutral = new { neutralOrientation.ClockwiseRotationDegrees, neutralOrientation.Reflection }
        })}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 270)]
    [InlineData(2, 180)]
    [InlineData(3, 90)]
    public async Task P6_HeifIrot_IsNormalizedToClockwiseNeutralRotation(byte irot, int expectedClockwiseDegrees)
    {
        using var workspace = new MediaWorkspace();
        string path = await WriteOrientationFixtureAsync(
            workspace,
            "heif-irot",
            ".heic",
            BuildHeifOrientationFixture([("irot", [irot])], 1));

        NativeImageOrientationObservationV1 observed = await NativeMediaService
            .ObserveImageOrientationAsync(path, ImageContainer.Heic);

        Assert.Equal(expectedClockwiseDegrees, observed.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationReflection.None, observed.Reflection);
        Assert.Equal(NativeImageOrientationEvidence.Verified, observed.RotationEvidence);
        Assert.Equal(NativeImageOrientationEvidence.Verified, observed.ReflectionEvidence);
    }

    [Fact]
    public async Task P6_HeifWithoutPrimaryTransform_UsesVerifiedIdentityTransform()
    {
        using var workspace = new MediaWorkspace();
        string path = await WriteOrientationFixtureAsync(
            workspace,
            "heif-no-primary-transform",
            ".heic",
            BuildHeifOrientationFixture(Array.Empty<(string Type, byte[] Payload)>()));

        NativeImageOrientationObservationV1 observed = await NativeMediaService
            .ObserveImageOrientationAsync(path, ImageContainer.Heic);

        Assert.Equal(0, observed.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationReflection.None, observed.Reflection);
        Assert.Equal(NativeImageOrientationStatus.Verified, observed.Status);
    }

    [Theory]
    [InlineData(false, 90)]
    [InlineData(true, 270)]
    public async Task P6_HeifIrotAndImir_ComposeInItemPropertyAssociationOrder(
        bool mirrorBeforeRotation,
        int expectedClockwiseDegrees)
    {
        using var workspace = new MediaWorkspace();
        byte[] heif = BuildHeifOrientationFixture(
            [("irot", [3]), ("imir", [1])],
            mirrorBeforeRotation ? [2, 1] : [1, 2]);
        string path = await WriteOrientationFixtureAsync(workspace, "heif-transform-order", ".heic", heif);

        NativeImageOrientationObservationV1 observed = await NativeMediaService
            .ObserveImageOrientationAsync(path, ImageContainer.Heic);

        Assert.Equal(expectedClockwiseDegrees, observed.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationReflection.Horizontal, observed.Reflection);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(3, 180, 0)]
    [InlineData(4, 180, 1)]
    [InlineData(5, 90, 1)]
    [InlineData(6, 90, 0)]
    [InlineData(7, 270, 1)]
    [InlineData(8, 270, 0)]
    public async Task P6_JpegExifOrientation_IsNormalizedToNeutralRotationAndReflection(
        ushort exifOrientation,
        int expectedClockwiseDegrees,
        int expectedReflection)
    {
        using var workspace = new MediaWorkspace();
        string path = await WriteOrientationFixtureAsync(
            workspace,
            "jpeg-exif-orientation",
            ".jpg",
            BuildJpegOrientationFixture(exifOrientation));

        NativeImageOrientationObservationV1 observed = await NativeMediaService
            .ObserveImageOrientationAsync(path, ImageContainer.Jpeg);

        Assert.Equal(expectedClockwiseDegrees, observed.ClockwiseRotationDegrees);
        Assert.Equal((NativeImageOrientationReflection)expectedReflection, observed.Reflection);
        Assert.Equal(NativeImageOrientationEvidence.Verified, observed.RotationEvidence);
        Assert.Equal(NativeImageOrientationEvidence.Verified, observed.ReflectionEvidence);
    }

    [Fact]
    public async Task P6_JpegWithoutExifOrientation_UsesVerifiedIdentityTransform()
    {
        using var workspace = new MediaWorkspace();
        string path = await WriteOrientationFixtureAsync(
            workspace,
            "jpeg-no-exif-orientation",
            ".jpg",
            BuildJpegOrientationFixture());

        NativeImageOrientationObservationV1 observed = await NativeMediaService
            .ObserveImageOrientationAsync(path, ImageContainer.Jpeg);

        Assert.Equal(0, observed.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationReflection.None, observed.Reflection);
        Assert.Equal(NativeImageOrientationStatus.Verified, observed.Status);
    }

    [Theory]
    [InlineData(1)] // Reserved irot bits.
    [InlineData(2)] // Reserved imir bits.
    [InlineData(3)] // Two irot properties associated with the primary item.
    [InlineData(4)] // An association references an absent property.
    [InlineData(5)] // Duplicate EXIF APP1 orientation sources.
    [InlineData(6)] // Out-of-range EXIF Orientation value.
    [InlineData(7)] // Duplicate ipma containers make property association ambiguous.
    public async Task P6_MalformedOrAmbiguousImageOrientation_FailsClosed(int fixtureKind)
    {
        using var workspace = new MediaWorkspace();
        ImageContainer container;
        string path;
        if (fixtureKind <= 4 || fixtureKind == 7)
        {
            container = ImageContainer.Heic;
            byte[] heif = fixtureKind switch
            {
                1 => BuildHeifOrientationFixture([("irot", [4])], 1),
                2 => BuildHeifOrientationFixture([("imir", [2])], 1),
                3 => BuildHeifOrientationFixture([("irot", [3]), ("irot", [1])], 1, 2),
                4 => BuildHeifOrientationFixture([("irot", [3])], 2),
                7 => BuildHeifOrientationFixture([("irot", [3])], true, 1),
                _ => throw new InvalidOperationException("Unknown HEIF fixture.")
            };
            path = await WriteOrientationFixtureAsync(workspace, "malformed-heif-orientation", ".heic", heif);
        }
        else
        {
            container = ImageContainer.Jpeg;
            byte[] jpeg = fixtureKind == 5
                ? BuildJpegOrientationFixture(1, 6)
                : BuildJpegOrientationFixture(0);
            path = await WriteOrientationFixtureAsync(workspace, "malformed-jpeg-orientation", ".jpg", jpeg);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await NativeMediaService.ObserveImageOrientationAsync(path, container));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_XiaomiJpegGainMap_IsUnsupportedWhenFinalOrientationCannotBeVerified()
    {
        string primary = ResolveSample("小米.jpg");
        string sourceSha256 = await FileSha256Async(primary);
        Assert.Equal("71F49FB21B5A7397D8A0FB499F644A1CD7D701D5C6FA6BBC9BC251E55E27F9E3", sourceSha256);
        SourceMediaFacts sourceFacts = await new SourceInspector().InspectAsync(primary);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, sourceFacts.Protocol);
        Assert.True(sourceFacts.MotionVideo is { IsPresent: true });
        Assert.True(sourceFacts.GainMap is { IsPresent: true });
        using var workspace = new MediaWorkspace();

        NotSupportedException unsupported = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await new NeutralMediaService().CreateNeutralBundleAsync(primary, null, workspace));

        Assert.Contains("final PrimaryImage orientation", unsupported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceSha256, await FileSha256Async(primary));
        _output.WriteLine($"P6_XIAOMI_NEUTRAL_UNSUPPORTED_JSON={JsonSerializer.Serialize(new
        {
            sourceSha256,
            sourceProtocol = sourceFacts.Protocol.ToString(),
            sourceMotionVideoPresent = sourceFacts.MotionVideo?.IsPresent,
            sourceGainMapPresent = sourceFacts.GainMap?.IsPresent,
            failure = unsupported.Message
        })}");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_AppleJpegPair_NoGainMap_ProjectsExplicitNoGainMapState()
    {
        string primary = ResolveSample("苹果-双文件.JPG");
        string secondary = ResolveSample("苹果-双文件.MOV");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, secondary, workspace);

        Assert.Null(bundle.Semantics.GainMap);
        Assert.False(bundle.Semantics.ColorHdr.HasGainMap);
        Assert.Equal(NeutralHdrState.NoGainMapObserved, bundle.Semantics.ColorHdr.HdrState);
        Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.GainMapEvidence);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_UnverifiedConversionTimingAndOrientation_AreNotCopiedFromSource()
    {
        string primary = ResolveSample("苹果双文件.HEIC");
        string secondary = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        var converter = new NotEvaluatingVideoConverter();
        var sourceTiming = new TimingFacts
        {
            CoverTimestampUs = 1_234_567,
            PrimaryTimestampUs = 765_432,
            CoverFrameIndex = 37,
            TotalFrames = 91
        };

        NeutralMediaBundle bundle = await new NeutralMediaService(
                inspector: new SourceProtocolOverrideInspector(SourceProtocol.AppleLivePhoto, sourceTiming),
                videoConverter: converter)
            .CreateNeutralBundleAsync(primary, secondary, workspace, new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Unknown,
                VideoContainer = VideoContainer.Unknown,
                VideoCodec = VideoCodec.Copy,
                TargetFps = 24
            });

        Assert.True(converter.ConvertCalled);
        Assert.Equal(sourceTiming, bundle.SourceProvenance.Timing);
        Assert.Equal(NeutralEvidenceState.Unknown, bundle.Semantics.Timing.EvidenceState);
        Assert.Null(bundle.Semantics.Timing.CoverTimestampUs);
        Assert.Null(bundle.Semantics.Timing.PrimaryTimestampUs);
        Assert.Null(bundle.Semantics.Timing.CoverFrameIndex);
        MediaArtifact finalVideo = Assert.IsType<MediaArtifact>(bundle.MotionVideo);
        VideoFacts observedFinalVideo = await converter.ProbeAsync(finalVideo.Path);
        NeutralOrientationTransform finalVideoOrientation = Assert.IsType<NeutralOrientationTransform>(
            bundle.Semantics.Orientation.MotionVideo);
        Assert.Equal(NormalizeRotation(observedFinalVideo.RotationDegrees), finalVideoOrientation.ClockwiseRotationDegrees);
        Assert.Equal(NeutralEvidenceState.Verified, finalVideoOrientation.RotationEvidence);
        Assert.Equal(NeutralEvidenceState.Unknown, finalVideoOrientation.ReflectionEvidence);
        Assert.Equal(ConversionComponentOutcome.NotEvaluated, finalVideoOrientation.Preservation);
    }

    [Theory]
    [InlineData(ConversionComponentOutcome.NotEvaluated)]
    [InlineData(ConversionComponentOutcome.NotApplicable)]
    [Trait("Category", "RealSamples")]
    public async Task P6_ImageConversionComponentTruth_DoesNotFallbackToCleanerEvidence(
        ConversionComponentOutcome componentOutcome)
    {
        string primary = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var converter = new ComponentOutcomeOverrideImageConverter(new ImageConverter(), componentOutcome);

        NeutralMediaBundle bundle = await new NeutralMediaService(imageConverter: converter)
            .CreateNeutralBundleAsync(primary, null, workspace, new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Unknown,
                VideoCodec = VideoCodec.Copy
            });

        ImageConversionResult actualConversion = Assert.IsType<ImageConversionResult>(converter.InnerResult);
        Assert.True(actualConversion.Success);
        MediaArtifact convertedImage = Assert.IsType<MediaArtifact>(actualConversion.OutputArtifact);
        Assert.True(File.Exists(convertedImage.Path));
        Assert.Equal(ImageContainer.Jpeg, actualConversion.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Heic, actualConversion.ExecutionRecord.OutputContainer);
        Assert.NotEqual(ConversionOperationKind.Unsupported, actualConversion.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(componentOutcome, bundle.Semantics.ColorHdr.ColorIccPreservation);
        Assert.Equal(componentOutcome, bundle.Semantics.ColorHdr.GainMapPreservation);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Manifest_PrimaryAndMotionRowsHaveStableIdentityAndApplicableCodecs()
    {
        string primary = ResolveSample("苹果-双文件.JPG");
        string motion = ResolveSample("苹果-双文件.MOV");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, motion, workspace);

        NeutralArtifactManifest image = Assert.Single(bundle.Manifest, item => item.Role == "PrimaryImage");
        NeutralArtifactManifest video = Assert.Single(bundle.Manifest, item => item.Role == "MotionVideo");
        Assert.Equal($"sha256:{image.Sha256.ToLowerInvariant()}", image.StableIdentity);
        Assert.Equal("Primary", image.Semantic);
        Assert.Equal(bundle.PrimaryImage.ImageContainer, image.ImageContainer);
        Assert.Equal(bundle.PrimaryImage.ImageCodec, image.ImageCodec);
        Assert.NotEqual(ImageCodec.Unknown, image.ImageCodec);
        Assert.Equal(NeutralAuxiliaryRepresentation.Materialized, image.SemanticRepresentation);
        Assert.Equal("root", image.Relationship);
        Assert.Equal(new FileInfo(bundle.PrimaryImage.Path).Length, image.ByteLength);
        Assert.Equal(await FileSha256Async(bundle.PrimaryImage.Path), image.Sha256, ignoreCase: true);

        MediaArtifact finalMotion = Assert.IsType<MediaArtifact>(bundle.MotionVideo);
        Assert.Equal($"sha256:{video.Sha256.ToLowerInvariant()}", video.StableIdentity);
        Assert.Equal("MotionVideo", video.Semantic);
        Assert.Equal(finalMotion.VideoContainer, video.VideoContainer);
        Assert.Equal(finalMotion.VideoCodec, video.VideoCodec);
        Assert.NotEqual(VideoCodec.Unknown, video.VideoCodec);
        Assert.Equal(image.StableIdentity, video.OwnerIdentity);
        Assert.Equal("motion-companion", video.Relationship);
        Assert.Equal(NeutralAuxiliaryRepresentation.Materialized, video.SemanticRepresentation);
        Assert.Equal(new FileInfo(finalMotion.Path).Length, video.ByteLength);
        Assert.Equal(await FileSha256Async(finalMotion.Path), video.Sha256, ignoreCase: true);
        Assert.DoesNotContain(bundle.PrimaryImage.Path, image.StableIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(finalMotion.Path, video.StableIdentity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Manifest_EmbeddedOnlyGainMapUsesFinalInspectorRangeIdentity()
    {
        string primary = ResolveSample("苹果双文件.HEIC");
        string motion = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, motion, workspace);
        SourceMediaFacts finalFacts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        GainMapFacts finalGainMap = Assert.IsType<GainMapFacts>(finalFacts.GainMap);
        AuxiliaryMediaFacts finalItem = finalFacts.AuxiliaryItems[checked((int)finalGainMap.AuxiliaryIndex)];
        NeutralArtifactManifest row = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");

        Assert.Null(bundle.GainMap);
        Assert.Equal(GainMapRepresentation.Embedded, bundle.GainMapRepresentation);
        Assert.Equal(finalItem.StableIdentity, row.StableIdentity);
        Assert.Equal(finalItem.Semantic, row.Semantic);
        Assert.Equal(finalItem.OwnerIdentity, row.OwnerIdentity);
        Assert.Equal(finalItem.Relationship, row.Relationship);
        Assert.Equal(finalItem.Sha256, row.Sha256, ignoreCase: true);
        Assert.Equal(finalItem.ByteLength, row.ByteLength);
        Assert.Equal(finalItem.ByteOffset, row.OwnerByteOffset);
        Assert.Equal(finalItem.Container, row.ImageContainer);
        Assert.Equal(finalItem.Codec, row.AuxiliaryCodec);
        Assert.Equal(bundle.PrimaryImage.Path, row.Path);
        Assert.Equal(NeutralAuxiliaryRepresentation.Embedded, row.SemanticRepresentation);
        Assert.Equal(GainMapRepresentation.Embedded, row.GainMapRepresentation);
        Assert.Equal(NeutralAuxiliaryRepresentation.Embedded, bundle.Semantics.GainMap!.Representation);
        Assert.NotEqual(new FileInfo(bundle.PrimaryImage.Path).Length, row.ByteLength);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Manifest_JpegReassemblyUsesOneGainMapWithBothRepresentation()
    {
        string primary = ResolveSample("vivo.jpg");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, null, workspace);
        SourceMediaFacts finalFacts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        GainMapFacts finalGainMap = Assert.IsType<GainMapFacts>(finalFacts.GainMap);
        AuxiliaryMediaFacts finalItem = finalFacts.AuxiliaryItems[checked((int)finalGainMap.AuxiliaryIndex)];
        NeutralArtifactManifest row = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");
        MediaArtifact detached = Assert.IsType<MediaArtifact>(bundle.GainMap);

        Assert.Equal(GainMapRepresentation.Both, bundle.GainMapRepresentation);
        Assert.Equal(GainMapRepresentation.Both, row.GainMapRepresentation);
        Assert.Equal(GainMapRepresentation.Both,
            Assert.Single(bundle.Manifest, item => item.Role == "PrimaryImage").GainMapRepresentation);
        Assert.Equal(NeutralAuxiliaryRepresentation.Both, row.SemanticRepresentation);
        Assert.Equal(NeutralAuxiliaryRepresentation.Both, bundle.Semantics.GainMap!.Representation);
        Assert.Equal(finalItem.StableIdentity, row.StableIdentity);
        Assert.Equal(finalItem.OwnerIdentity, row.OwnerIdentity);
        Assert.Equal(finalItem.Relationship, row.Relationship);
        Assert.Equal(finalItem.ByteOffset, row.OwnerByteOffset);
        Assert.Equal(finalItem.ByteLength, row.ByteLength);
        Assert.Equal(finalItem.Sha256, row.Sha256, ignoreCase: true);
        Assert.Equal(detached.Path, row.Path);
        Assert.Equal(detached.ByteLength, row.ByteLength);
        Assert.Equal(await FileSha256Async(detached.Path), row.Sha256, ignoreCase: true);
        Assert.Equal(1, bundle.Manifest.Count(item => item.Role == "GainMap"));
        Assert.Equal(1, bundle.Manifest.Count(item => item.StableIdentity == row.StableIdentity));
        Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.GainMapMetadataEvidence);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Manifest_ReencodedGainMapDoesNotExposeStaleDetachedRepresentation()
    {
        string primary = ResolveSample("vivo.jpg");
        using var workspace = new MediaWorkspace();
        var converter = new CapturingImageConverter(new ImageConverter());

        NeutralMediaBundle bundle = await new NeutralMediaService(imageConverter: converter)
            .CreateNeutralBundleAsync(primary, null, workspace, new MediaFormatRequirement
            {
                ImageContainer = ImageContainer.Heic,
                VideoContainer = VideoContainer.Unknown,
                VideoCodec = VideoCodec.Copy
            });

        ImageConversionResult conversion = Assert.IsType<ImageConversionResult>(converter.LastResult);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, conversion.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Reencoded, conversion.ExecutionRecord.Truth.HdrGainMap);
        SourceMediaFacts finalFacts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        GainMapFacts finalGainMap = Assert.IsType<GainMapFacts>(finalFacts.GainMap);
        AuxiliaryMediaFacts finalItem = finalFacts.AuxiliaryItems[checked((int)finalGainMap.AuxiliaryIndex)];
        NeutralArtifactManifest row = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");

        Assert.Null(bundle.GainMap);
        Assert.Equal(GainMapRepresentation.Embedded, bundle.GainMapRepresentation);
        Assert.Equal(NeutralAuxiliaryRepresentation.Embedded, row.SemanticRepresentation);
        Assert.Equal(finalItem.StableIdentity, row.StableIdentity);
        Assert.Equal(finalItem.Sha256, row.Sha256, ignoreCase: true);
        Assert.Equal(finalItem.ByteLength, row.ByteLength);
        Assert.Equal(finalItem.ByteOffset, row.OwnerByteOffset);
        Assert.Equal(bundle.PrimaryImage.Path, row.Path);
        Assert.Equal(NeutralAuxiliaryRepresentation.Embedded, bundle.Semantics.GainMap!.Representation);
        Assert.All(bundle.AuxiliaryMedia.Where(item => item.Semantic == "GainMap"),
            item => Assert.Null(item.MaterializedArtifact));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Manifest_MaterializedAuxiliaryUsesExactArtifactBytesAndRelationship()
    {
        string primary = ResolveSample("一加-改了封面照片.jpg");
        using var workspace = new MediaWorkspace();

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(primary, null, workspace);
        NeutralArtifactManifest row = Assert.Single(bundle.Manifest,
            item => item.Role == "Auxiliary" && item.Semantic == "Original");
        NeutralAuxiliarySemantics semantic = Assert.Single(bundle.Semantics.AuxiliaryMedia,
            item => item.StableIdentity == row.StableIdentity);

        Assert.True(File.Exists(row.Path));
        Assert.Equal(new FileInfo(row.Path).Length, row.ByteLength);
        Assert.Equal(await FileSha256Async(row.Path), row.Sha256, ignoreCase: true);
        Assert.Equal(row.StableIdentity, semantic.StableIdentity);
        Assert.Equal(AuxiliaryStableIdentity(row.Semantic, row.Sha256), row.StableIdentity);
        Assert.Equal(row.OwnerIdentity, semantic.OwnerIdentity);
        Assert.Equal(row.Relationship, semantic.Relationship);
        Assert.Equal("Original", row.Semantic);
        Assert.Equal("Original", semantic.Relationship);
        Assert.Equal(8_178_321, row.ByteLength);
        Assert.Equal("1BCC7C7FB61FC2616511A4F9662A734AEAA3EC00E33B39F1E4C4773104971322",
            row.Sha256, ignoreCase: true);
        Assert.Equal(row.Sha256, row.SourceSha256, ignoreCase: true);
        Assert.Equal(PreservationOutcome.Preserved, row.PreservationOutcome);
        Assert.NotEqual(string.Empty, row.OwnerIdentity);
        Assert.NotEqual(string.Empty, row.Relationship);
        Assert.Equal(NeutralAuxiliaryRepresentation.Materialized, row.SemanticRepresentation);
        Assert.Equal(row.SemanticRepresentation, semantic.Representation);
        Assert.Equal(row.PreservationOutcome, semantic.PreservationOutcome);
        Assert.Null(row.OwnerByteOffset);
        Assert.NotEqual(AuxiliaryCodec.Unknown, row.AuxiliaryCodec);
        Assert.Equal(row.ByteLength, row.SourceLength);
        Assert.Equal(row.Sha256, row.SourceSha256, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Controlled")]
    public async Task P6_Manifest_DetachedGainMapUsesOneVerifiedMaterializedRepresentation()
    {
        using var workspace = new MediaWorkspace();
        string gainMapSource = ResolveSample("小米.jpg");
        using InspectedSource gainMapInspection = await new SourceInspector()
            .InspectWithPlanAsync(gainMapSource);
        ExtractedMediaBundle gainMapSourceArtifacts = await new SourceExtractor().ExtractAsync(
            gainMapInspection.ExtractionPlan,
            gainMapSource,
            null,
            workspace);
        MediaArtifact realGainMap = Assert.IsType<MediaArtifact>(gainMapSourceArtifacts.GainMap);
        string noGainMapPrimary = ResolveSample("华为Mate80.heic");
        var cleaner = new ControlledDetachedGainMapCleaner(realGainMap);
        _output.WriteLine("P6_CONTROLLED_DETACHED_GAINMAP=real materialized Xiaomi GainMap bytes attached through a test cleaner seam to a no-GainMap HEIC output; this is not a product route or RealSample route result.");

        NeutralMediaBundle bundle = await new NeutralMediaService(cleaner: cleaner)
            .CreateNeutralBundleAsync(noGainMapPrimary, null, workspace);
        NeutralArtifactManifest row = Assert.Single(bundle.Manifest, item => item.Role == "GainMap");
        NeutralAuxiliarySemantics semantic = Assert.IsType<NeutralAuxiliarySemantics>(bundle.Semantics.GainMap);

        Assert.Equal(GainMapRepresentation.Detached, bundle.GainMapRepresentation);
        Assert.Equal(realGainMap.Path, Assert.IsType<MediaArtifact>(bundle.GainMap).Path);
        Assert.Equal(NeutralAuxiliaryRepresentation.Detached, row.SemanticRepresentation);
        Assert.Equal(NeutralAuxiliaryRepresentation.Detached, semantic.Representation);
        Assert.Equal("GainMap", row.Semantic);
        Assert.Equal(AuxiliaryStableIdentity(row.Semantic, row.Sha256), row.StableIdentity);
        Assert.False((await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).GainMap is { IsPresent: true });
        Assert.Null(row.OwnerByteOffset);
        Assert.Equal(row.SourceLength, row.ByteLength);
        Assert.Equal(row.SourceSha256, row.Sha256, ignoreCase: true);
        Assert.Equal(await FileSha256Async(realGainMap.Path), row.Sha256, ignoreCase: true);
        Assert.Equal(new FileInfo(realGainMap.Path).Length, row.ByteLength);
        Assert.NotEqual(string.Empty, row.OwnerIdentity);
        Assert.NotEqual(string.Empty, row.Relationship);
        Assert.True(bundle.Semantics.ColorHdr.HasGainMap);
        Assert.Equal(NeutralHdrState.GainMapPresent, bundle.Semantics.ColorHdr.HdrState);
        Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.GainMapEvidence);
        Assert.True(bundle.Semantics.ColorHdr.IsHdrRelevant);
        Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.HdrEvidence);
        Assert.Null(bundle.Semantics.ColorHdr.HdrCapacityMin);
        Assert.Null(bundle.Semantics.ColorHdr.HdrCapacityMax);
        Assert.Equal(NeutralEvidenceState.Unknown, bundle.Semantics.ColorHdr.GainMapMetadataEvidence);
    }

    private static async Task<string> ContentIdentityAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(stream);
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<SampleIdentitySnapshot> CaptureSampleIdentityAsync(string filename, string path)
    {
        CorpusSample expected = RealSampleCorpusManifest.GetRequiredSample(filename);
        if (!File.Exists(path))
        {
            return new SampleIdentitySnapshot(
                filename, expected.ByteSize, expected.Sha256, null, null, Exists: false);
        }

        long actualByteSize = new FileInfo(path).Length;
        string actualSha256 = await FileSha256Async(path);
        return new SampleIdentitySnapshot(
            filename, expected.ByteSize, expected.Sha256, actualByteSize, actualSha256, Exists: true);
    }

    private static async Task<object> AssertAndCaptureDirectNeutralBundleAsync(
        string routeId,
        SourceProtocol expectedProtocol,
        SampleIdentitySnapshot primaryInput,
        SampleIdentitySnapshot? secondaryInput,
        NeutralMediaBundle bundle)
    {
        Assert.Equal(expectedProtocol, bundle.SourceProvenance.Protocol);
        Assert.Equal(primaryInput.ActualSha256, bundle.SourceProvenance.PrimarySha256, ignoreCase: true);
        if (secondaryInput != null)
        {
            Assert.Equal(secondaryInput.ActualSha256, bundle.SourceProvenance.SecondarySha256, ignoreCase: true);
        }
        else
        {
            Assert.Null(bundle.SourceProvenance.SecondarySha256);
        }

        Assert.Equal(MediaArtifactKind.PrimaryImage, bundle.PrimaryImage.Kind);
        Assert.True(File.Exists(bundle.PrimaryImage.Path), $"{routeId} final primary artifact is missing.");
        SourceMediaFacts finalFacts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        object? realSampleMpfEvidence = await AssertRealSampleMpfRelationshipAsync(routeId, bundle, finalFacts);
        Assert.Equal(SourceProtocol.NonLive, finalFacts.Protocol);
        Assert.True(finalFacts.PrimaryImage.IsPresent);
        Assert.Null(finalFacts.MotionVideo);
        Assert.Null(finalFacts.PairingIdentifier);
        Assert.Equal(0, finalFacts.ProtocolTailLength);
        Assert.Empty(finalFacts.ConfirmedResidues);
        Assert.Equal(finalFacts.PrimaryImage.Container, bundle.PrimaryImage.ImageContainer);

        if (routeId == "P6-D01")
        {
            const string declaredVivoIccSha256 = "D2CA36B1E85BD83C5F257869E63299FE41FBA4334F0404825436FF0883706E7D";
            Assert.Equal(declaredVivoIccSha256, bundle.Semantics.ColorHdr.IccProfileSha256, ignoreCase: true);
            Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.IccProfileEvidence);
            Assert.Equal(ConversionComponentOutcome.Preserved, bundle.Semantics.ColorHdr.ColorIccPreservation);
        }

        NativeImageOrientationObservationV1 finalImageOrientation = await NativeMediaService
            .ObserveImageOrientationAsync(bundle.PrimaryImage.Path, finalFacts.PrimaryImage.Container);
        NeutralOrientationTransform neutralImageOrientation = bundle.Semantics.Orientation.PrimaryImage;
        Assert.Equal(finalImageOrientation.ClockwiseRotationDegrees, neutralImageOrientation.ClockwiseRotationDegrees);
        Assert.Equal(finalImageOrientation.Reflection switch
        {
            NativeImageOrientationReflection.None => NeutralReflection.None,
            NativeImageOrientationReflection.Horizontal => NeutralReflection.Horizontal,
            _ => NeutralReflection.Unknown
        }, neutralImageOrientation.Reflection);
        Assert.Equal(NeutralEvidenceState.Verified, neutralImageOrientation.RotationEvidence);
        Assert.Equal(NeutralEvidenceState.Verified, neutralImageOrientation.ReflectionEvidence);

        NeutralArtifactManifest primaryRow = Assert.Single(bundle.Manifest, item => item.Role == "PrimaryImage");
        long primaryByteLength = new FileInfo(bundle.PrimaryImage.Path).Length;
        string primarySha256 = await FileSha256Async(bundle.PrimaryImage.Path);
        Assert.True(primaryByteLength > 0);
        Assert.Equal(primaryByteLength, bundle.PrimaryImage.ByteLength);
        Assert.Equal(primaryByteLength, primaryRow.ByteLength);
        Assert.Equal(bundle.PrimaryImage.Sha256, primarySha256, ignoreCase: true);
        Assert.Equal(primarySha256, primaryRow.Sha256, ignoreCase: true);
        Assert.Equal($"sha256:{primarySha256.ToLowerInvariant()}", primaryRow.StableIdentity);
        Assert.Equal(primaryRow.StableIdentity, bundle.Semantics.PrimaryImage.ContentIdentity);
        Assert.Equal(await ContentIdentityAsync(bundle.PrimaryImage.Path), bundle.Semantics.PrimaryImage.ContentIdentity);
        Assert.Equal(NeutralArtifactRole.PrimaryImage, bundle.Semantics.PrimaryImage.Role);
        Assert.Equal("Primary", primaryRow.Semantic);
        Assert.Equal("root", primaryRow.Relationship);
        Assert.Equal(bundle.PrimaryImage.ImageContainer, primaryRow.ImageContainer);
        Assert.Equal(bundle.PrimaryImage.ImageCodec, primaryRow.ImageCodec);
        Assert.NotEqual(ImageContainer.Unknown, primaryRow.ImageContainer);
        Assert.NotEqual(ImageCodec.Unknown, primaryRow.ImageCodec);
        Assert.Equal(NeutralAuxiliaryRepresentation.Materialized, primaryRow.SemanticRepresentation);
        Assert.Equal(primaryRow.PreservationOutcome, bundle.Semantics.PrimaryImage.PreservationOutcome);
        Assert.Equal(primaryRow.PreservationOutcome, bundle.Semantics.Preservation.PrimaryImage.Outcome);
        Assert.Equal(primaryRow.StableIdentity, bundle.Semantics.Preservation.PrimaryImage.SemanticIdentity);
        Assert.NotEqual(NeutralEvidenceState.Unknown, bundle.Semantics.PrimaryImage.EvidenceState);
        Assert.NotEqual(NeutralEvidenceState.Unknown, bundle.Semantics.Preservation.PrimaryImage.EvidenceState);
        Assert.NotEqual(PreservationOutcome.Unsupported, primaryRow.PreservationOutcome);

        bool sourceHasMotionVideo = bundle.SourceProvenance.MotionVideo is { IsPresent: true };
        Assert.Equal(sourceHasMotionVideo, bundle.MotionVideo != null);
        NeutralArtifactManifest? videoRow = bundle.Manifest.SingleOrDefault(item => item.Role == "MotionVideo");
        object? finalVideoEvidence = null;
        if (bundle.MotionVideo == null)
        {
            Assert.Null(videoRow);
            Assert.Null(bundle.Semantics.MotionVideo);
            Assert.Null(bundle.Semantics.Preservation.MotionVideo);
            Assert.Null(bundle.Semantics.Orientation.MotionVideo);
            AssertTimingDoesNotInventSourceFacts(bundle.Semantics.Timing, bundle.SourceProvenance.Timing, hasMotionVideo: false);
        }
        else
        {
            MediaArtifact finalMotion = bundle.MotionVideo;
            NeutralArtifactManifest actualVideoRow = Assert.IsType<NeutralArtifactManifest>(videoRow);
            Assert.Equal(MediaArtifactKind.MotionVideo, finalMotion.Kind);
            Assert.True(File.Exists(finalMotion.Path), $"{routeId} final motion video artifact is missing.");
            long videoByteLength = new FileInfo(finalMotion.Path).Length;
            string videoSha256 = await FileSha256Async(finalMotion.Path);
            Assert.True(videoByteLength > 0);
            Assert.Equal(videoByteLength, finalMotion.ByteLength);
            Assert.Equal(videoByteLength, actualVideoRow.ByteLength);
            Assert.Equal(videoSha256, actualVideoRow.Sha256, ignoreCase: true);
            Assert.Equal($"sha256:{videoSha256.ToLowerInvariant()}", actualVideoRow.StableIdentity);
            Assert.Equal(primaryRow.StableIdentity, actualVideoRow.OwnerIdentity);
            Assert.Equal("motion-companion", actualVideoRow.Relationship);
            Assert.Equal(NeutralAuxiliaryRepresentation.Materialized, actualVideoRow.SemanticRepresentation);

            VideoFacts finalVideoFacts = await new VideoConverter().ProbeAsync(finalMotion.Path);
            Assert.True(finalVideoFacts.IsPresent, $"{routeId} final video probe did not find a video.");
            Assert.NotEqual(VideoContainer.Unknown, finalVideoFacts.Container);
            Assert.NotEqual(VideoCodec.Unknown, finalVideoFacts.Codec);
            Assert.Equal(finalVideoFacts.Container, finalMotion.VideoContainer);
            Assert.Equal(finalVideoFacts.Codec, finalMotion.VideoCodec);
            Assert.Equal(finalVideoFacts.Container, actualVideoRow.VideoContainer);
            Assert.Equal(finalVideoFacts.Codec, actualVideoRow.VideoCodec);

            NeutralArtifactSemantics motionSemantics = Assert.IsType<NeutralArtifactSemantics>(bundle.Semantics.MotionVideo);
            Assert.Equal(NeutralArtifactRole.MotionVideo, motionSemantics.Role);
            Assert.Equal(actualVideoRow.StableIdentity, motionSemantics.ContentIdentity);
            Assert.Equal(actualVideoRow.VideoContainer, motionSemantics.VideoContainer);
            Assert.Equal(actualVideoRow.VideoCodec, motionSemantics.VideoCodec);
            Assert.Equal(actualVideoRow.PreservationOutcome, motionSemantics.PreservationOutcome);
            Assert.Equal(actualVideoRow.PreservationOutcome, bundle.Semantics.Preservation.MotionVideo!.Outcome);
            Assert.Equal(actualVideoRow.StableIdentity, bundle.Semantics.Preservation.MotionVideo.SemanticIdentity);
            Assert.NotEqual(NeutralEvidenceState.Unknown, motionSemantics.EvidenceState);
            Assert.NotEqual(NeutralEvidenceState.Unknown, bundle.Semantics.Preservation.MotionVideo.EvidenceState);
            Assert.NotEqual(PreservationOutcome.Unsupported, actualVideoRow.PreservationOutcome);

            NeutralOrientationTransform videoOrientation = Assert.IsType<NeutralOrientationTransform>(
                bundle.Semantics.Orientation.MotionVideo);
            int? expectedVideoRotation = NormalizeRotation(finalVideoFacts.RotationDegrees);
            Assert.Equal(expectedVideoRotation, videoOrientation.ClockwiseRotationDegrees);
            Assert.Equal(expectedVideoRotation.HasValue ? NeutralEvidenceState.Verified : NeutralEvidenceState.Unknown,
                videoOrientation.RotationEvidence);
            if (!expectedVideoRotation.HasValue)
                Assert.Equal(NeutralEvidenceState.Unknown, videoOrientation.ReflectionEvidence);

            AssertTimingDoesNotInventSourceFacts(bundle.Semantics.Timing, bundle.SourceProvenance.Timing, hasMotionVideo: true);
            finalVideoEvidence = new
            {
                container = finalVideoFacts.Container.ToString(),
                codec = finalVideoFacts.Codec.ToString(),
                byteLength = videoByteLength,
                sha256 = videoSha256,
                rotationDegrees = finalVideoFacts.RotationDegrees,
                normalizedClockwiseRotationDegrees = expectedVideoRotation,
                neutralOrientation = videoOrientation,
                timing = bundle.Semantics.Timing
            };
        }

        Assert.Equal(bundle.Semantics.GainMap != null, bundle.Semantics.ColorHdr.HasGainMap);
        Assert.Equal(bundle.Semantics.GainMap == null ? NeutralHdrState.NoGainMapObserved : NeutralHdrState.GainMapPresent,
            bundle.Semantics.ColorHdr.HdrState);
        Assert.Equal(NeutralEvidenceState.Verified, bundle.Semantics.ColorHdr.GainMapEvidence);
        if (bundle.SourceProvenance.GainMap is { IsPresent: true })
        {
            Assert.NotNull(bundle.Semantics.GainMap);
            Assert.True(bundle.Semantics.ColorHdr.HasGainMap);
        }

        NeutralArtifactManifest? gainMapRow = bundle.Manifest.SingleOrDefault(item => item.Role == "GainMap");
        if (gainMapRow == null)
        {
            Assert.Null(bundle.Semantics.GainMap);
            Assert.Equal(GainMapRepresentation.None, bundle.GainMapRepresentation);
            Assert.Null(bundle.Semantics.Preservation.GainMap);
        }
        else
        {
            NeutralAuxiliarySemantics gainMapSemantics = Assert.IsType<NeutralAuxiliarySemantics>(bundle.Semantics.GainMap);
            Assert.Equal(bundle.GainMapRepresentation, gainMapRow.GainMapRepresentation);
            Assert.Equal(gainMapRow.StableIdentity, gainMapSemantics.StableIdentity);
            Assert.Equal(gainMapRow.Semantic, gainMapSemantics.Semantic);
            Assert.Equal(gainMapRow.OwnerIdentity, gainMapSemantics.OwnerIdentity);
            Assert.Equal(gainMapRow.Relationship, gainMapSemantics.Relationship);
            Assert.Equal(gainMapRow.SemanticRepresentation, gainMapSemantics.Representation);
            Assert.Equal(gainMapRow.Ownership, gainMapSemantics.Ownership);
            Assert.Equal(gainMapRow.PreservationOutcome, gainMapSemantics.PreservationOutcome);
            Assert.Equal(gainMapRow.PreservationOutcome, bundle.Semantics.Preservation.GainMap!.Outcome);
            Assert.Equal(gainMapRow.StableIdentity, bundle.Semantics.Preservation.GainMap.SemanticIdentity);
            Assert.NotEqual(NeutralEvidenceState.Unknown, gainMapSemantics.EvidenceState);
            Assert.NotEqual(NeutralEvidenceState.Unknown, bundle.Semantics.Preservation.GainMap.EvidenceState);
            Assert.NotEqual(PreservationOutcome.Unsupported, gainMapRow.PreservationOutcome);
            Assert.Equal(1, bundle.Manifest.Count(item => item.Role == "GainMap" &&
                string.Equals(item.StableIdentity, gainMapRow.StableIdentity, StringComparison.Ordinal)));

            if (bundle.GainMapRepresentation is GainMapRepresentation.Detached or GainMapRepresentation.Both)
                Assert.IsType<MediaArtifact>(bundle.GainMap);
            if (bundle.GainMapRepresentation == GainMapRepresentation.Embedded)
                Assert.Null(bundle.GainMap);
        }

        var auxiliaryManifestSemantics = new List<object>();
        foreach (NeutralArtifactManifest row in bundle.Manifest.Where(item => item.Role is "GainMap" or "Auxiliary"))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.StableIdentity));
            Assert.Matches("^[A-Fa-f0-9]{64}$", row.Sha256);
            Assert.True(row.ByteLength > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.Semantic));
            Assert.False(string.IsNullOrWhiteSpace(row.OwnerIdentity));
            Assert.False(string.IsNullOrWhiteSpace(row.Relationship));
            Assert.NotEqual(NeutralAuxiliaryRepresentation.Unknown, row.SemanticRepresentation);
            Assert.NotEqual(PreservationOutcome.Unsupported, row.PreservationOutcome);

            NeutralAuxiliarySemantics semantic = row.Role == "GainMap"
                ? Assert.IsType<NeutralAuxiliarySemantics>(bundle.Semantics.GainMap)
                : Assert.Single(bundle.Semantics.AuxiliaryMedia, item =>
                    string.Equals(item.StableIdentity, row.StableIdentity, StringComparison.Ordinal));
            Assert.Equal(row.Semantic, semantic.Semantic);
            Assert.Equal(row.OwnerIdentity, semantic.OwnerIdentity);
            Assert.Equal(row.Relationship, semantic.Relationship);
            Assert.Equal(row.SemanticRepresentation, semantic.Representation);
            Assert.Equal(row.Ownership, semantic.Ownership);
            Assert.Equal(row.ImageContainer, semantic.ImageContainer);
            Assert.Equal(row.VideoContainer, semantic.VideoContainer);
            Assert.Equal(row.AuxiliaryCodec, semantic.Codec);
            Assert.Equal(row.PreservationOutcome, semantic.PreservationOutcome);
            Assert.NotEqual(NeutralEvidenceState.Unknown, semantic.EvidenceState);

            bool hasMaterializedRepresentation = row.SemanticRepresentation is
                NeutralAuxiliaryRepresentation.Detached or
                NeutralAuxiliaryRepresentation.Materialized or
                NeutralAuxiliaryRepresentation.Both;
            long? pathByteLength = null;
            string? pathSha256 = null;
            if (hasMaterializedRepresentation)
            {
                Assert.True(File.Exists(row.Path), $"{routeId} materialized {row.Role}/{row.Semantic} artifact is missing.");
                pathByteLength = new FileInfo(row.Path).Length;
                pathSha256 = await FileSha256Async(row.Path);
                Assert.Equal(row.ByteLength, pathByteLength);
                Assert.Equal(row.Sha256, pathSha256, ignoreCase: true);
            }

            object? embeddedFactEvidence = null;
            if (row.SemanticRepresentation is NeutralAuxiliaryRepresentation.Embedded or NeutralAuxiliaryRepresentation.Both)
            {
                AuxiliaryMediaFacts finalItem = Assert.Single(finalFacts.AuxiliaryItems, item =>
                    string.Equals(item.StableIdentity, row.StableIdentity, StringComparison.Ordinal));
                Assert.True(finalItem.IsPresent);
                Assert.Equal(row.Semantic, finalItem.Semantic);
                Assert.Equal(row.OwnerIdentity, finalItem.OwnerIdentity);
                Assert.Equal(row.Relationship, finalItem.Relationship);
                Assert.Equal(row.ByteLength, finalItem.ByteLength);
                Assert.Equal(row.Sha256, finalItem.Sha256, ignoreCase: true);
                Assert.Equal(row.OwnerByteOffset, finalItem.ByteOffset);
                embeddedFactEvidence = new
                {
                    finalItem.StableIdentity,
                    finalItem.Semantic,
                    finalItem.OwnerIdentity,
                    finalItem.Relationship,
                    finalItem.ByteOffset,
                    finalItem.ByteLength,
                    finalItem.Sha256,
                    representation = finalItem.Representation.ToString(),
                    ownership = finalItem.Ownership.ToString()
                };
            }

            NeutralArtifactPreservation preservation = row.Role == "GainMap"
                ? Assert.IsType<NeutralArtifactPreservation>(bundle.Semantics.Preservation.GainMap)
                : Assert.Single(bundle.Semantics.Preservation.AuxiliaryMedia, item =>
                    string.Equals(item.SemanticIdentity, row.StableIdentity, StringComparison.Ordinal));
            Assert.Equal(row.StableIdentity, preservation.SemanticIdentity);
            Assert.Equal(row.PreservationOutcome, preservation.Outcome);
            Assert.NotEqual(NeutralEvidenceState.Unknown, preservation.EvidenceState);

            auxiliaryManifestSemantics.Add(new
            {
                row.Role,
                row.Semantic,
                row.StableIdentity,
                row.Sha256,
                row.ByteLength,
                row.OwnerIdentity,
                row.Relationship,
                representation = row.SemanticRepresentation.ToString(),
                ownership = row.Ownership.ToString(),
                preservationOutcome = row.PreservationOutcome.ToString(),
                preservationEvidence = preservation.EvidenceState.ToString(),
                row.OwnerByteOffset,
                materializedPath = hasMaterializedRepresentation ? row.Path : null,
                materializedByteLength = pathByteLength,
                materializedSha256 = pathSha256,
                embeddedFact = embeddedFactEvidence
            });
        }

        Assert.Equal(bundle.Manifest.Count(item => item.Role == "Auxiliary"), bundle.Semantics.AuxiliaryMedia.Count);
        return new
        {
            sourceProtocol = bundle.SourceProvenance.Protocol.ToString(),
            sourcePrimarySha256 = bundle.SourceProvenance.PrimarySha256,
            sourceSecondarySha256 = bundle.SourceProvenance.SecondarySha256,
            finalPrimary = new
            {
                protocol = finalFacts.Protocol.ToString(),
                container = finalFacts.PrimaryImage.Container.ToString(),
                byteLength = primaryByteLength,
                sha256 = primarySha256,
                orientation = new
                {
                    finalClockwiseRotationDegrees = finalImageOrientation.ClockwiseRotationDegrees,
                    reflection = finalImageOrientation.Reflection.ToString(),
                    neutralClockwiseRotationDegrees = neutralImageOrientation.ClockwiseRotationDegrees,
                    neutralReflection = neutralImageOrientation.Reflection.ToString(),
                    neutralImageOrientation.RotationEvidence,
                    neutralImageOrientation.ReflectionEvidence
                }
            },
            finalVideo = finalVideoEvidence,
            gainMapRepresentation = bundle.GainMapRepresentation.ToString(),
            realSampleMpf = realSampleMpfEvidence,
            manifest = bundle.Manifest.Select(row => new
            {
                row.Role,
                row.Semantic,
                row.Path,
                row.Sha256,
                row.StableIdentity,
                row.OwnerIdentity,
                row.Relationship,
                row.ByteLength,
                representation = row.SemanticRepresentation.ToString(),
                ownership = row.Ownership.ToString(),
                preservationOutcome = row.PreservationOutcome.ToString(),
                row.OwnerByteOffset,
                row.SourceLength,
                row.SourceSha256,
                row.ImageContainer,
                row.ImageCodec,
                row.VideoContainer,
                row.VideoCodec,
                row.AuxiliaryCodec,
                gainMapRepresentation = row.GainMapRepresentation.ToString()
            }).ToArray(),
            auxiliaryManifestSemantics,
            semantics = bundle.Semantics
        };
    }

    private static async Task<object?> AssertRealSampleMpfRelationshipAsync(
        string routeId,
        NeutralMediaBundle bundle,
        SourceMediaFacts finalFacts)
    {
        var expected = routeId switch
        {
            "P6-S04" => new MpfSampleExpectation(4_120_520, 4_120_520, 519_910,
                "881C3BA655DB7273A41512F2DF25AD4A191A1643A0D0D073D0BCF78E93CB6B19", 0, null),
            "P6-S05" => new MpfSampleExpectation(2_583_299, 2_583_299, 593_136,
                "7A6CAD0EB93929E514E4D287C3534F046AEECD5D1440CC5AE7766A1515AD8629", 0, null),
            "P6-S06" => new MpfSampleExpectation(2_001_479, 2_001_479, 477_680,
                "802B24A6256028298AB383AFBC1DA11204B69DCEF25A0E9BD832D136FBEAB34B", 0, null),
            "P6-S07" => new MpfSampleExpectation(2_927_026, 3_553_428, 74_262,
                "AFFD1723B1D8036A74F4538B1D5B82CDB0ED1823EAC3A24C773E35A4725E54E8", 626_402,
                "1FAD56E0272B84993E288C5D122C399B8E1C902344756C23263590AB17FC5DCC"),
            "P6-S11" => new MpfSampleExpectation(2_641_800, 2_641_800, 326_047,
                "6D7FB8AD115621D62FA795B30FA551A6AFBC1E036B7B6DFD8F1F195D50F27495", 0, null),
            _ => null
        };
        if (expected == null) return null;

        Assert.Equal(GainMapRepresentation.Both, bundle.GainMapRepresentation);
        MediaArtifact detachedGainMap = Assert.IsType<MediaArtifact>(bundle.GainMap);
        byte[] primary = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        byte[] detached = await File.ReadAllBytesAsync(detachedGainMap.Path);
        MpfSampleLayout layout = ReadSampleMpfLayout(primary);
        string detachedSha256 = Convert.ToHexString(SHA256.HashData(detached));

        Assert.Equal(expected.PrimaryEoiEnd, layout.EoiEnd);
        Assert.Equal((uint)expected.PrimaryEoiEnd, layout.PrimarySize);
        Assert.Equal(0u, layout.PrimaryOffset);
        Assert.Equal((uint)expected.GainMapOffset, layout.GainMapOffset);
        Assert.Equal((uint)expected.GainMapLength, layout.GainMapSize);
        Assert.Equal(expected.GainMapLength, detached.Length);
        Assert.Equal(expected.GainMapSha256, detachedSha256, ignoreCase: true);
        Assert.InRange((long)layout.GainMapOffset, (long)layout.EoiEnd, primary.LongLength);
        Assert.True((long)layout.GainMapSize <= primary.LongLength - layout.GainMapOffset);
        Assert.Equal(detached, primary.AsSpan(checked((int)layout.GainMapOffset),
            checked((int)layout.GainMapSize)).ToArray());

        int? actualGapLength = null;
        string? gapSha256 = null;
        if (expected.GapLength is int expectedGapLength)
        {
            actualGapLength = checked((int)layout.GainMapOffset - layout.EoiEnd);
            Assert.Equal(expectedGapLength, actualGapLength);
            gapSha256 = actualGapLength == 0
                ? null
                : Convert.ToHexString(SHA256.HashData(primary.AsSpan(layout.EoiEnd, actualGapLength.Value)));
            Assert.Equal(expected.GapSha256, gapSha256, ignoreCase: true);
        }
        Assert.Single(bundle.Manifest, row => row.Role == "GainMap" &&
            row.ByteLength == expected.GainMapLength && row.Sha256.Equals(expected.GainMapSha256,
                StringComparison.OrdinalIgnoreCase));

        if (routeId == "P6-S05")
        {
            string xmp = ReadStandardXmp(primary);
            Assert.Equal(new[] { "Primary", "GainMap" }, ReadNeutralDirectorySemantics(xmp));
            Assert.Contains("hdrgm:Version=\"1.0\"", xmp, StringComparison.Ordinal);
        }

        return new
        {
            routeId,
            layout.EoiEnd,
            layout.PrimarySize,
            layout.GainMapOffset,
            layout.GainMapSize,
            detachedSha256,
            actualGapLength,
            gapSha256
        };
    }

    private static MpfSampleLayout ReadSampleMpfLayout(byte[] bytes)
    {
        int cursor = 2;
        bool insideScan = false;
        int eoiEnd = -1;
        int tiffOffset = -1;
        uint primarySize = 0;
        uint primaryOffset = 0;
        uint gainMapSize = 0;
        uint gainMapRelativeOffset = 0;
        while (cursor < bytes.Length)
        {
            if (insideScan)
            {
                bool foundMarker = false;
                while (cursor < bytes.Length)
                {
                    if (bytes[cursor] != 0xFF)
                    {
                        cursor++;
                        continue;
                    }
                    int markerStart = cursor;
                    int codePosition = cursor + 1;
                    while (codePosition < bytes.Length && bytes[codePosition] == 0xFF) codePosition++;
                    Assert.True(codePosition < bytes.Length, "JPEG entropy scan ends before a marker.");
                    byte scanCode = bytes[codePosition];
                    if (scanCode == 0x00 || scanCode is >= 0xD0 and <= 0xD7)
                    {
                        cursor = codePosition + 1;
                        continue;
                    }
                    cursor = markerStart;
                    foundMarker = true;
                    break;
                }
                Assert.True(foundMarker, "JPEG entropy scan has no terminating marker.");
                insideScan = false;
            }

            Assert.True(cursor + 1 < bytes.Length && bytes[cursor] == 0xFF, "JPEG marker stream is malformed.");
            int markerCodePosition = cursor + 1;
            while (markerCodePosition < bytes.Length && bytes[markerCodePosition] == 0xFF) markerCodePosition++;
            Assert.True(markerCodePosition < bytes.Length, "JPEG marker stream is truncated.");
            byte marker = bytes[markerCodePosition];
            cursor = markerCodePosition + 1;
            if (marker == 0xD9)
            {
                eoiEnd = cursor;
                break;
            }
            Assert.DoesNotContain(marker, new byte[] { 0x00, 0xD8, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7 });
            if (marker == 0x01) continue;
            Assert.True(cursor + 2 <= bytes.Length, "JPEG segment length is truncated.");
            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(segmentLength >= 2 && segmentLength <= bytes.Length - cursor,
                "JPEG segment length is outside its containing artifact.");
            int payloadStart = cursor + 2;
            int payloadEnd = cursor + segmentLength;
            if (marker == 0xE2 && payloadEnd - payloadStart >= 4 &&
                bytes.AsSpan(payloadStart, 4).SequenceEqual("MPF\0"u8))
            {
                Assert.Equal(-1, tiffOffset);
                tiffOffset = payloadStart + 4;
                bool littleEndian = bytes[tiffOffset] == 'I' && bytes[tiffOffset + 1] == 'I';
                Assert.True(littleEndian || (bytes[tiffOffset] == 'M' && bytes[tiffOffset + 1] == 'M'));
                Assert.Equal((ushort)42, SampleReadU16(bytes, tiffOffset + 2, littleEndian));
                int ifd = checked(tiffOffset + (int)SampleReadU32(bytes, tiffOffset + 4, littleEndian));
                ushort fieldCount = SampleReadU16(bytes, ifd, littleEndian);
                uint imageCount = 0;
                int mpEntryArray = -1;
                bool foundImageCount = false;
                for (int index = 0; index < fieldCount; index++)
                {
                    int field = ifd + 2 + index * 12;
                    ushort tag = SampleReadU16(bytes, field, littleEndian);
                    ushort type = SampleReadU16(bytes, field + 2, littleEndian);
                    uint count = SampleReadU32(bytes, field + 4, littleEndian);
                    uint value = SampleReadU32(bytes, field + 8, littleEndian);
                    if (tag == 0xB001)
                    {
                        Assert.False(foundImageCount);
                        Assert.Equal((ushort)4, type);
                        Assert.Equal(1u, count);
                        foundImageCount = true;
                        imageCount = value;
                    }
                    else if (tag == 0xB002)
                    {
                        Assert.Equal((ushort)7, type);
                        Assert.Equal(32u, count);
                        Assert.Equal(-1, mpEntryArray);
                        mpEntryArray = checked(tiffOffset + (int)value);
                    }
                }
                Assert.True(foundImageCount && imageCount == 2 && mpEntryArray >= 0 && mpEntryArray + 32 <= payloadEnd,
                    "MPF index must contain exactly two bounded image entries.");
                primarySize = SampleReadU32(bytes, mpEntryArray + 4, littleEndian);
                primaryOffset = SampleReadU32(bytes, mpEntryArray + 8, littleEndian);
                gainMapSize = SampleReadU32(bytes, mpEntryArray + 16 + 4, littleEndian);
                gainMapRelativeOffset = SampleReadU32(bytes, mpEntryArray + 16 + 8, littleEndian);
            }
            cursor = payloadEnd;
            if (marker == 0xDA) insideScan = true;
        }

        Assert.True(eoiEnd > 0 && tiffOffset >= 0, "JPEG must contain one MPF index before its EOI marker.");
        ulong gainMapAbsoluteOffset = (ulong)tiffOffset + gainMapRelativeOffset;
        Assert.True(gainMapAbsoluteOffset <= uint.MaxValue);
        return new MpfSampleLayout(eoiEnd, primarySize, primaryOffset, gainMapSize,
            (uint)gainMapAbsoluteOffset);
    }

    private static ushort SampleReadU16(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint SampleReadU32(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static string ReadStandardXmp(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int cursor = 2;
        string? found = null;
        while (cursor + 1 < bytes.Length)
        {
            Assert.True(bytes[cursor] == 0xFF, "JPEG marker stream is malformed before its image scan.");
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            byte marker = bytes[cursor++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x01) continue;
            Assert.True(cursor + 2 <= bytes.Length, "JPEG marker length is truncated.");
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(segmentLength >= 2 && segmentLength <= bytes.Length - cursor,
                "JPEG marker length is outside the artifact.");
            int payloadStart = cursor + 2;
            int payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength >= header.Length &&
                bytes.AsSpan(payloadStart, header.Length).SequenceEqual(header))
            {
                Assert.Null(found);
                found = Encoding.UTF8.GetString(bytes, payloadStart + header.Length,
                    payloadLength - header.Length);
            }
            cursor += segmentLength;
        }
        return Assert.IsType<string>(found);
    }

    private static string[] ReadNeutralDirectorySemantics(string xml)
    {
        XDocument document = XDocument.Parse(xml);
        XNamespace container = "http://ns.google.com/photos/1.0/container/";
        XNamespace item = "http://ns.google.com/photos/1.0/container/item/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XElement directory = Assert.Single(document.Descendants(container + "Directory"));
        XElement sequence = Assert.Single(directory.Elements(rdf + "Seq"));
        return sequence.Elements(rdf + "li")
            .Select(li => li.Elements(container + "Item").Single().Attribute(item + "Semantic")!.Value)
            .ToArray();
    }

    private sealed record MpfSampleExpectation(int PrimaryEoiEnd, int GainMapOffset, int GainMapLength,
        string GainMapSha256, int? GapLength, string? GapSha256);

    private sealed record MpfSampleLayout(int EoiEnd, uint PrimarySize, uint PrimaryOffset,
        uint GainMapSize, uint GainMapOffset);

    private static void AssertTimingDoesNotInventSourceFacts(
        NeutralTimingSemantics neutralTiming,
        TimingFacts sourceTiming,
        bool hasMotionVideo)
    {
        if (!hasMotionVideo)
        {
            Assert.Equal(NeutralEvidenceState.NotApplicable, neutralTiming.EvidenceState);
            Assert.Null(neutralTiming.CoverTimestampUs);
            Assert.Null(neutralTiming.PrimaryTimestampUs);
            Assert.Null(neutralTiming.CoverFrameIndex);
            Assert.Null(neutralTiming.TotalFrames);
            return;
        }

        if (neutralTiming.EvidenceState == NeutralEvidenceState.VerifiedPreserved)
        {
            Assert.Equal(sourceTiming.CoverTimestampUs, neutralTiming.CoverTimestampUs);
            Assert.Equal(sourceTiming.PrimaryTimestampUs, neutralTiming.PrimaryTimestampUs);
            Assert.Equal(sourceTiming.CoverFrameIndex, neutralTiming.CoverFrameIndex);
            int? expectedTotalFrames = sourceTiming.TotalFrames > 0 ? sourceTiming.TotalFrames : null;
            Assert.Equal(expectedTotalFrames, neutralTiming.TotalFrames);
            return;
        }

        Assert.Contains(neutralTiming.EvidenceState,
            new[] { NeutralEvidenceState.Unknown, NeutralEvidenceState.NotApplicable });
        Assert.Null(neutralTiming.CoverTimestampUs);
        Assert.Null(neutralTiming.PrimaryTimestampUs);
        Assert.Null(neutralTiming.CoverFrameIndex);
        Assert.Null(neutralTiming.TotalFrames);
    }

    private sealed record SampleIdentitySnapshot(
        string Filename,
        long ExpectedByteSize,
        string ExpectedSha256,
        long? ActualByteSize,
        string? ActualSha256,
        bool Exists)
    {
        public bool Matches => Exists && ActualByteSize == ExpectedByteSize &&
            string.Equals(ExpectedSha256, ActualSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string AuxiliaryStableIdentity(string semantic, string sha256) =>
        $"aux:{semantic}:sha256:{sha256.ToLowerInvariant()}";

    private static int? NormalizeRotation(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        return normalized is 0 or 90 or 180 or 270 ? normalized : null;
    }

    private static async Task<string> WriteOrientationFixtureAsync(
        IMediaWorkspace workspace,
        string prefix,
        string extension,
        byte[] contents)
    {
        string path = workspace.AllocateFilePath(prefix, extension);
        await File.WriteAllBytesAsync(path, contents);
        return path;
    }

    private static byte[] BuildJpegOrientationFixture(params ushort[] orientations)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        foreach (ushort orientation in orientations)
        {
            byte[] tiff = new byte[26];
            tiff[0] = 0x49;
            tiff[1] = 0x49;
            tiff[2] = 0x2A;
            tiff[3] = 0x00;
            tiff[4] = 0x08;
            tiff[8] = 0x01;
            tiff[10] = 0x12;
            tiff[11] = 0x01;
            tiff[12] = 0x03;
            tiff[14] = 0x01;
            tiff[18] = (byte)(orientation & 0xFF);
            tiff[19] = (byte)(orientation >> 8);

            byte[] exif = [.. Encoding.ASCII.GetBytes("Exif\0\0"), .. tiff];
            jpeg.Add(0xFF);
            jpeg.Add(0xE1);
            AppendUInt16BigEndian(jpeg, checked((ushort)(exif.Length + 2)));
            jpeg.AddRange(exif);
        }

        // The Native orientation observer only needs the legal JPEG header
        // through SOS; it does not pretend this structural fixture is a photo.
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static byte[] BuildHeifOrientationFixture(
        IReadOnlyList<(string Type, byte[] Payload)> properties,
        params byte[] associationOrder) =>
        BuildHeifOrientationFixture(properties, false, associationOrder);

    private static byte[] BuildHeifOrientationFixture(
        IReadOnlyList<(string Type, byte[] Payload)> properties,
        bool duplicateIpma,
        params byte[] associationOrder)
    {
        var ipcoBody = new List<byte>();
        foreach ((string type, byte[] payload) in properties)
            ipcoBody.AddRange(BuildIsoBox(type, payload));
        byte[] ipco = BuildIsoBox("ipco", ipcoBody.ToArray());

        var ipmaBody = new List<byte> { 0, 0, 0, 0 }; // version and flags
        AppendUInt32BigEndian(ipmaBody, 1); // entry_count
        AppendUInt16BigEndian(ipmaBody, 1); // primary item id
        ipmaBody.Add(checked((byte)associationOrder.Length));
        ipmaBody.AddRange(associationOrder);
        byte[] ipma = BuildIsoBox("ipma", ipmaBody.ToArray());

        byte[] pitm = BuildIsoBox("pitm", [0, 0, 0, 0, 0, 1]);
        byte[] iprp = BuildIsoBox("iprp", duplicateIpma ? Join(ipco, ipma, ipma) : Join(ipco, ipma));
        byte[] meta = BuildIsoBox("meta", Join([0, 0, 0, 0], pitm, iprp));
        byte[] ftyp = BuildIsoBox("ftyp", [.. Encoding.ASCII.GetBytes("mif1"), 0, 0, 0, 0]);
        return Join(ftyp, meta);
    }

    private static byte[] BuildIsoBox(string type, byte[] body)
    {
        if (type.Length != 4)
            throw new ArgumentException("ISO BMFF box types must be four characters.", nameof(type));

        byte[] box = new byte[checked(body.Length + 8)];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), checked((uint)box.Length));
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Join(params byte[][] arrays)
    {
        int length = arrays.Aggregate(0, (current, array) => checked(current + array.Length));
        byte[] joined = new byte[length];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            array.CopyTo(joined, offset);
            offset += array.Length;
        }
        return joined;
    }

    private static void AppendUInt16BigEndian(List<byte> destination, ushort value)
    {
        destination.Add((byte)(value >> 8));
        destination.Add((byte)value);
    }

    private static void AppendUInt32BigEndian(List<byte> destination, uint value)
    {
        destination.Add((byte)(value >> 24));
        destination.Add((byte)(value >> 16));
        destination.Add((byte)(value >> 8));
        destination.Add((byte)value);
    }

    private static void InspectSemanticGraph(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!visited.Add(type) || type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
            return;

        Assert.NotEqual(typeof(WindowsFileIdentity), type);
        Assert.NotEqual(typeof(ImageHdrGainMapSourceBinding), type);
        Assert.NotEqual(typeof(SourceMediaFacts), type);
        Assert.NotEqual(typeof(SourceProtocol), type);
        Assert.NotEqual(typeof(VideoBackend), type);
        Assert.NotEqual(typeof(VideoHardwareMode), type);

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                InspectSemanticGraph(argument, visited);
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.DoesNotContain("path", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("handle", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backend", property.Name, StringComparison.OrdinalIgnoreCase);
            InspectSemanticGraph(property.PropertyType, visited);
        }
    }

    private sealed class SourceProtocolOverrideInspector(
        SourceProtocol displayedProtocol,
        TimingFacts? displayedTiming = null) : ISourceInspector
    {
        private readonly SourceInspector _inner = new();

        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default) =>
            _inner.InspectAsync(primaryPath, secondaryPath, cancellationToken);

        public async Task<InspectedSource> InspectWithPlanAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            InspectedSource actual = await _inner
                .InspectWithPlanAsync(primaryPath, secondaryPath, cancellationToken);
            return new InspectedSource(actual.Facts with
            {
                Protocol = displayedProtocol,
                Timing = displayedTiming ?? actual.Facts.Timing
            }, actual.ExtractionPlan);
        }
    }

    private sealed class NotEvaluatingVideoConverter : IVideoConverter
    {
        private readonly VideoConverter _probe = new();

        public bool ConvertCalled { get; private set; }

        public Task<VideoFacts> ProbeAsync(string videoPath, CancellationToken cancellationToken = default) =>
            _probe.ProbeAsync(videoPath, cancellationToken);

        public Task<VideoConversionResult> ConvertAsync(
            VideoConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            ConvertCalled = true;
            return Task.FromResult(new VideoConversionResult
            {
                Success = true,
                OutputArtifact = request.SourceArtifact,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    InputCodec = request.SourceArtifact.VideoCodec,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = request.SourceArtifact.VideoContainer,
                    OutputCodec = request.SourceArtifact.VideoCodec,
                    RemuxUsed = true,
                    RotationPreserved = true,
                    Truth = new ConversionExecutionTruth
                    {
                        RequestedOperationKind = ConversionOperationKind.ContainerRemux,
                        ActualOperationKind = ConversionOperationKind.ContainerRemux,
                        PreservationOutcome = PreservationOutcome.Preserved,
                        Timing = ConversionComponentOutcome.NotEvaluated,
                        Orientation = ConversionComponentOutcome.NotEvaluated
                    }
                }
            });
        }
    }

    private sealed class ComponentOutcomeOverrideImageConverter(
        IImageConverter inner,
        ConversionComponentOutcome componentOutcome) : IImageConverter
    {
        public ImageConversionResult? InnerResult { get; private set; }

        public async Task<ImageConversionResult> ConvertAsync(
            ImageConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            ImageConversionResult result = await inner.ConvertAsync(request, cancellationToken);
            InnerResult = result;
            return result with
            {
                ExecutionRecord = result.ExecutionRecord with
                {
                    Truth = result.ExecutionRecord.Truth with
                    {
                        ColorIcc = componentOutcome,
                        HdrGainMap = componentOutcome
                    }
                }
            };
        }
    }

    private sealed class CapturingImageConverter(IImageConverter inner) : IImageConverter
    {
        public ImageConversionResult? LastResult { get; private set; }

        public async Task<ImageConversionResult> ConvertAsync(
            ImageConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastResult = await inner.ConvertAsync(request, cancellationToken);
            return LastResult;
        }
    }

    private sealed class ControlledDetachedGainMapCleaner(MediaArtifact gainMapArtifact) : ISourceProtocolCleaner
    {
        public async Task<ProtocolCleanResult> CleanAsync(
            ProtocolCleanRequest request,
            IMediaWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            string gainMapSha = await workspace.ComputeFileSha256Async(gainMapArtifact.Path, cancellationToken);
            long gainMapLength = new FileInfo(gainMapArtifact.Path).Length;
            string primarySha = await workspace.ComputeFileSha256Async(
                request.ExtractedBundle.PrimaryImage.Path, cancellationToken);
            var descriptor = new AuxiliaryMediaDescriptor
            {
                ArtifactRole = MediaArtifactKind.GainMap,
                StableIdentity = $"sha256:{gainMapSha.ToLowerInvariant()}",
                Semantic = "GainMap",
                OwnerIdentity = $"sha256:{primarySha.ToLowerInvariant()}",
                Relationship = "detached-gain-map",
                SourceIndex = 0,
                SourceOffset = 0,
                SourceLength = gainMapLength,
                SourceSha256 = gainMapSha,
                ImageContainer = gainMapArtifact.ImageContainer,
                Codec = gainMapArtifact.ImageCodec switch
                {
                    ImageCodec.Jpeg => AuxiliaryCodec.Jpeg,
                    ImageCodec.Hevc => AuxiliaryCodec.Hevc,
                    _ => AuxiliaryCodec.Unknown
                },
                Representation = AuxiliaryRepresentation.Detached,
                Ownership = AuxiliaryOwnership.Primary,
                MaterializedArtifact = gainMapArtifact,
                PreservationOutcome = PreservationOutcome.Preserved
            };

            return new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = request.ExtractedBundle.PrimaryImage,
                CleanedVideo = request.ExtractedBundle.MotionVideo,
                CleanedGainMap = gainMapArtifact,
                AuxiliaryMedia = [.. request.ExtractedBundle.AuxiliaryMedia, descriptor],
                PreservationCarriers = request.ExtractedBundle.PreservationCarriers,
                PreservationOutcome = PreservationOutcome.Preserved,
                GainMapExpectedSha256 = gainMapSha
            };
        }
    }
}

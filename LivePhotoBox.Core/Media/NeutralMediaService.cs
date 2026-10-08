using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media;

/// <summary>
/// Reference pipeline orchestrator:
/// Inspect -> Extract -> Clean -> Convert -> NeutralMediaBundle.
/// </summary>
public sealed class NeutralMediaService : INeutralMediaService
{
    private readonly ISourceInspector _inspector;
    private readonly ISourceExtractor _extractor;
    private readonly ISourceProtocolCleaner _cleaner;
    private readonly IImageConverter _imageConverter;
    private readonly IVideoConverter _videoConverter;

    public NeutralMediaService(
        ISourceInspector? inspector = null,
        ISourceExtractor? extractor = null,
        ISourceProtocolCleaner? cleaner = null,
        IImageConverter? imageConverter = null,
        IVideoConverter? videoConverter = null)
    {
        _inspector = inspector ?? new SourceInspector();
        _extractor = extractor ?? new SourceExtractor();
        _cleaner = cleaner ?? new SourceProtocolCleaner();
        _imageConverter = imageConverter ?? new ImageConverter();
        _videoConverter = videoConverter ?? new VideoConverter();
    }

    public async Task<NeutralMediaBundle> CreateNeutralBundleAsync(
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        MediaFormatRequirement? requirement = null,
        PreservationPolicy preservationPolicy = PreservationPolicy.BestEffort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(workspace);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Inspect
        using InspectedSource inspected = await _inspector
            .InspectWithPlanAsync(primaryPath, secondaryPath, cancellationToken)
            .ConfigureAwait(false);
        SourceMediaFacts facts = inspected.Facts;

        // 2. Extract
        ExtractedMediaBundle extracted = await _extractor
            .ExtractAsync(inspected.ExtractionPlan, primaryPath, secondaryPath, workspace, cancellationToken)
            .ConfigureAwait(false);

        // 3. Clean source Live/Motion Photo protocol
        ProtocolCleanResult cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted,
            ExtractionPlan = inspected.ExtractionPlan,
            PreservationPolicy = preservationPolicy
        }, workspace, cancellationToken).ConfigureAwait(false);

        if (!cleanResult.Success || cleanResult.CleanedImage == null)
        {
            throw new InvalidOperationException($"Failed to clean source protocol: {cleanResult.ErrorMessage}");
        }

        MediaArtifact finalImage = cleanResult.CleanedImage;
        MediaArtifact? finalVideo = cleanResult.CleanedVideo;

        // A cleaned JPEG and verified GainMap bytes are first tested as a
        // possible compound neutral image. Only the final Native Inspector
        // can establish that representation. If the authoritative Inspector
        // reports the appended bytes as ambiguous, retain the already-clean,
        // independently inspected primary and carry the same GainMap as a
        // detached semantic artifact.
        bool gainMapEmbeddedInPrimary = false;
        SourceMediaFacts neutralInputFacts = await _inspector
            .InspectAsync(finalImage.Path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (cleanResult.CleanedGainMap != null
            && finalImage.ImageContainer == ImageContainer.Jpeg)
        {
            if (!IsVerifiedNeutralPrimary(neutralInputFacts))
                throw new InvalidDataException("Cleaned primary is not a verified neutral image before auxiliary representation selection.");

            MediaArtifact cleanedPrimary = finalImage;
            MediaArtifact reassembledCandidate = await ReassembleJpegGainMapAsync(
                finalImage, cleanResult.CleanedGainMap, cleanResult.GainMapExpectedSha256,
                workspace, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                SourceMediaFacts candidateFacts = await _inspector
                    .InspectAsync(reassembledCandidate.Path, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (IsVerifiedNeutralPrimary(candidateFacts) && candidateFacts.GainMap is { IsPresent: true })
                {
                    finalImage = reassembledCandidate;
                    neutralInputFacts = candidateFacts;
                    gainMapEmbeddedInPrimary = true;
                }
                else
                {
                    finalImage = cleanedPrimary;
                }
            }
            catch (SourceInspectionException ex) when (ex.Category == SourceInspectionFailureCategory.Ambiguous)
            {
                // The append is not a recognized compound representation.
                // Keep the pre-inspected primary and let the verified
                // materialized handoff represent the GainMap as detached.
                finalImage = cleanedPrimary;
            }
        }

        // Bind R3 authority to the actual cleaned neutral artifact (not the
        // pre-clean source facts). This is also the identity handed to an
        // explicit cross-container semantic conversion below.
        if (!IsVerifiedNeutralPrimary(neutralInputFacts))
            throw new InvalidDataException("Cleaned primary did not pass authoritative post-clean inspection.");
        ImageHdrGainMapSourceBinding? hdrGainMapBinding = neutralInputFacts.GainMap is { IsPresent: true }
            ? await CreateHdrGainMapBindingAsync(
                finalImage, neutralInputFacts,
                cleanResult.AuxiliaryMedia.Count > 0 ? cleanResult.AuxiliaryMedia : extracted.AuxiliaryMedia,
                workspace, cancellationToken, gainMapEmbeddedInPrimary,
                gainMapEmbeddedInPrimary ? cleanResult.CleanedGainMap : null).ConfigureAwait(false)
            : null;
        var auxiliaryHandoff = new List<AuxiliaryMediaDescriptor>(
            cleanResult.AuxiliaryMedia.Count > 0 ? cleanResult.AuxiliaryMedia : extracted.AuxiliaryMedia);

        PreservationOutcome imageOutcome = cleanResult.PreservationOutcome;
        bool gainMapSemanticallyReencoded = false;
        PreservationOutcome videoOutcome = cleanResult.CleanedVideo == null
            ? PreservationOutcome.Preserved
            : cleanResult.PreservationOutcome;
        ConversionExecutionTruth? imageConversionTruth = null;
        ConversionExecutionTruth? videoConversionTruth = null;
        VideoFacts? resolvedVideoFacts = null;
        NativeImageOrientationObservationV1? preConversionImageOrientation = null;
        bool imageWasConverted = false;
        bool videoWasConverted = false;

        // 4. Convert formats if requested
        if (requirement != null)
        {
            // Convert Image if target differs
            if (requirement.ImageContainer != ImageContainer.Unknown &&
                requirement.ImageContainer != finalImage.ImageContainer)
            {
                preConversionImageOrientation = await NativeMediaService
                    .ObserveImageOrientationAsync(
                        finalImage.Path,
                        neutralInputFacts.PrimaryImage.Container,
                        cancellationToken)
                    .ConfigureAwait(false);

                var imgConv = await _imageConverter.ConvertAsync(new ImageConversionRequest
                {
                    SourceArtifact = finalImage,
                    TargetContainer = requirement.ImageContainer,
                    TargetDirectory = workspace.RootDirectory,
                    PreservationPolicy = preservationPolicy,
                    TrustedSourceFacts = ImageConversionEligibility.ToConversionFacts(neutralInputFacts),
                    TrustedHdrGainMapBinding = hdrGainMapBinding,
                    TargetHdrGainMapSemantic = hdrGainMapBinding is null
                        ? ImageHdrGainMapTargetSemantic.Unknown
                        : requirement.ImageContainer switch
                        {
                            ImageContainer.Jpeg => ImageHdrGainMapTargetSemantic.JpegIsoGainMap,
                            ImageContainer.Heic => ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary,
                            _ => ImageHdrGainMapTargetSemantic.Unknown
                        }
                }, cancellationToken).ConfigureAwait(false);

                if (!imgConv.Success || imgConv.OutputArtifact == null)
                {
                    throw new InvalidOperationException($"Image conversion failed in neutral pipeline: {imgConv.ErrorMessage}");
                }

                imageConversionTruth = imgConv.ExecutionRecord.Truth;
                imageWasConverted = imageConversionTruth.ActualOperationKind != ConversionOperationKind.Passthrough;

                if (preservationPolicy == PreservationPolicy.Strict &&
                    imgConv.ExecutionRecord.PreservationOutcome != PreservationOutcome.Preserved)
                {
                    throw new InvalidOperationException("Strict preservation policy failed during image conversion.");
                }

                finalImage = imgConv.OutputArtifact;
                imageOutcome = CombineOutcome(imageOutcome, imgConv.ExecutionRecord.PreservationOutcome);
                gainMapSemanticallyReencoded =
                    imgConv.ExecutionRecord.Truth.ActualOperationKind == ConversionOperationKind.HdrGainMapConversion &&
                    imgConv.ExecutionRecord.Truth.HdrGainMap == ConversionComponentOutcome.Reencoded;
                if (gainMapSemanticallyReencoded)
                {
                    // The converter only reports this truth after the target
                    // semantic and staged output graph have passed Native and
                    // managed post-validation. Reflect that verified output
                    // representation in the neutral artifact manifest.
                    gainMapEmbeddedInPrimary = true;

                    // The binding handed to the caller authorizes a future
                    // semantic conversion of the current neutral artifact.
                    // Replace the source-container identity with facts freshly
                    // inspected from the committed target before returning it.
                    SourceMediaFacts convertedFacts = await _inspector
                        .InspectAsync(finalImage.Path, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    AuxiliaryMediaDescriptor convertedGainMap = CreateInspectedGainMapDescriptor(
                        convertedFacts, imageOutcome);
                    auxiliaryHandoff.RemoveAll(item => item.ArtifactRole == MediaArtifactKind.GainMap ||
                        string.Equals(item.Semantic, "GainMap", StringComparison.Ordinal));
                    auxiliaryHandoff.Add(convertedGainMap);
                    hdrGainMapBinding = await CreateHdrGainMapBindingAsync(
                        finalImage, convertedFacts, [convertedGainMap], workspace, cancellationToken,
                        wasReassembledIntoPrimary: false, reassembledGainMapArtifact: null)
                        .ConfigureAwait(false);
                }
            }

            if (finalVideo != null)
            {
                resolvedVideoFacts = await _videoConverter
                    .ProbeAsync(finalVideo.Path, cancellationToken)
                    .ConfigureAwait(false);

                if (!resolvedVideoFacts.IsPresent ||
                    resolvedVideoFacts.Container == VideoContainer.Unknown ||
                    resolvedVideoFacts.Codec == VideoCodec.Unknown)
                {
                    throw new InvalidDataException("Neutral video facts could not be verified.");
                }

                finalVideo = finalVideo with
                {
                    VideoContainer = resolvedVideoFacts.Container,
                    VideoCodec = resolvedVideoFacts.Codec,
                    MimeType = resolvedVideoFacts.Container switch
                    {
                        VideoContainer.Mp4 => "video/mp4",
                        VideoContainer.Mov => "video/quicktime",
                        _ => throw new InvalidDataException("Neutral video container is unsupported.")
                    }
                };
            }

            // Convert Video if video exists and target differs
            if (finalVideo != null && (
                requirement.TargetFps is > 0 ||
                (requirement.VideoContainer != VideoContainer.Unknown && requirement.VideoContainer != finalVideo.VideoContainer) ||
                (requirement.VideoCodec != VideoCodec.Copy && requirement.VideoCodec != VideoCodec.Unknown && requirement.VideoCodec != finalVideo.VideoCodec)))
            {
                VideoFacts videoFacts = resolvedVideoFacts ??
                    throw new InvalidDataException("Neutral video facts were not resolved.");
                VideoCodec converterTargetCodec = requirement.VideoCodec is VideoCodec.Unknown or VideoCodec.Copy ||
                    requirement.VideoCodec == videoFacts.Codec
                    ? VideoCodec.Copy
                    : requirement.VideoCodec;
                var vidConv = await _videoConverter.ConvertAsync(new VideoConversionRequest
                {
                    SourceArtifact = finalVideo,
                    TargetContainer = requirement.VideoContainer != VideoContainer.Unknown ? requirement.VideoContainer : finalVideo.VideoContainer,
                    TargetCodec = converterTargetCodec,
                    TargetDirectory = workspace.RootDirectory,
                    TargetFps = requirement.TargetFps ?? 0,
                    PreservationPolicy = preservationPolicy,
                    TrustedSourceFacts = new VideoConversionSourceFacts
                    {
                        Container = videoFacts.Container,
                        Codec = videoFacts.Codec,
                        HasAudio = videoFacts.HasAudio,
                        RotationDegrees = videoFacts.RotationDegrees,
                        DurationSeconds = videoFacts.DurationSeconds
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (!vidConv.Success || vidConv.OutputArtifact == null)
                {
                    throw new InvalidOperationException($"Video conversion failed in neutral pipeline: {vidConv.ErrorMessage}");
                }

                finalVideo = vidConv.OutputArtifact;
                videoConversionTruth = vidConv.ExecutionRecord.Truth;
                videoWasConverted = true;
                videoOutcome = CombineOutcome(videoOutcome, vidConv.ExecutionRecord.Truth.PreservationOutcome);
            }

            if (finalVideo != null &&
                requirement.VideoCodec != VideoCodec.Copy &&
                requirement.VideoCodec != VideoCodec.Unknown &&
                finalVideo.VideoCodec != requirement.VideoCodec)
            {
                throw new InvalidDataException(
                    $"Neutral video codec mismatch: expected {requirement.VideoCodec}, actual {finalVideo.VideoCodec}.");
            }

            if (preservationPolicy == PreservationPolicy.Strict &&
                videoOutcome != PreservationOutcome.Preserved)
            {
                throw new InvalidOperationException("Strict preservation policy failed during video cleaning or conversion.");
            }
        }

        // The bundle represents final media, so probe the actual post-clean /
        // post-conversion video even when no format requirement requested a
        // conversion. The earlier requirement probe is only conversion input.
        if (finalVideo != null)
        {
            resolvedVideoFacts = await _videoConverter
                .ProbeAsync(finalVideo.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!resolvedVideoFacts.IsPresent ||
                resolvedVideoFacts.Container == VideoContainer.Unknown ||
                resolvedVideoFacts.Codec == VideoCodec.Unknown)
            {
                throw new InvalidDataException("Final neutral video facts could not be verified.");
            }

            finalVideo = finalVideo with
            {
                VideoContainer = resolvedVideoFacts.Container,
                VideoCodec = resolvedVideoFacts.Codec,
                MimeType = resolvedVideoFacts.Container switch
                {
                    VideoContainer.Mp4 => "video/mp4",
                    VideoContainer.Mov => "video/quicktime",
                    _ => throw new InvalidDataException("Final neutral video container is unsupported.")
                }
            };
        }

        // Final guard: a neutral bundle must not expose a still image that the
        // Native Inspector still recognizes as a Live/Motion Photo. This is a
        // post-clean check, not a second protocol parser or a writer check.
        SourceMediaFacts neutralFacts = await _inspector
            .InspectAsync(finalImage.Path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (neutralFacts.Protocol != SourceProtocol.NonLive
            || neutralFacts.MotionVideo != null
            || neutralFacts.ProtocolTailLength != 0
            || neutralFacts.PairingIdentifier != null)
        {
            throw new InvalidDataException(
                $"Neutral media validation failed: Inspector reported {neutralFacts.Protocol} for the cleaned image.");
        }

        // Capture final media-semantic facts only after the post-clean Inspector
        // has identified the exact non-live primary returned in the bundle.
        NativeImageOrientationObservationV1 finalImageOrientation;
        try
        {
            finalImageOrientation = await NativeMediaService
                .ObserveImageOrientationAsync(finalImage.Path, neutralFacts.PrimaryImage.Container, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new NotSupportedException(
                "Neutral media is unsupported because final PrimaryImage orientation could not be established.",
                ex);
        }
        PreservationObservation finalImageObservation = await NativeMediaService
            .CapturePreservationObservationAsync(
                finalImage.Path, SourceProtocol.Unknown, neutralFacts.PrimaryImage.Container, cancellationToken)
            .ConfigureAwait(false);
        if (finalImageObservation.IccParseError)
            throw new InvalidDataException("Final image ICC profile could not be authoritatively inspected.");

        NativeHeicPrimaryDecodeInfo? finalHeicPrimary = null;
        if (neutralFacts.PrimaryImage.Container == ImageContainer.Heic)
        {
            finalHeicPrimary = await NativeMediaService.DecodeHeicPrimaryAsync(finalImage.Path, cancellationToken)
                .ConfigureAwait(false);
            if (finalHeicPrimary.Value.HasNclx > 1 || finalHeicPrimary.Value.IsHdrRelevant > 1 ||
                finalHeicPrimary.Value.HasIcc > 1)
                throw new InvalidDataException("Native HEIC primary decode returned invalid color/HDR flags.");
        }

        // Carry verified descriptors and opaque preservation evidence across
        // the Cleaner boundary. Manifest rows are built later from the final
        // Inspector graph plus materialized artifacts that pass byte checks.
        for (int i = 0; i < auxiliaryHandoff.Count; i++)
        {
            AuxiliaryMediaDescriptor descriptor = auxiliaryHandoff[i];
            if (descriptor.MaterializedArtifact != null &&
                cleanResult.CleanedGainMap != null &&
                descriptor.MaterializedArtifact.Kind == MediaArtifactKind.GainMap)
            {
                auxiliaryHandoff[i] = descriptor with { MaterializedArtifact = cleanResult.CleanedGainMap };
            }
        }
        var preservationCarriers = new List<PreservationCarrier>(
            cleanResult.PreservationCarriers.Count > 0 ? cleanResult.PreservationCarriers : extracted.PreservationCarriers);
        await VerifyAuxiliaryHandoffAsync(auxiliaryHandoff, workspace, cancellationToken).ConfigureAwait(false);
        VerifyPreservationCarrierHandoff(preservationCarriers);

        // 5. Build the semantic manifest only from final Inspector facts and
        // verified materialized handoff artifacts. The cleaner's typed
        // GainMap slot is never treated as proof of final representation.
        NeutralManifestProjection manifestProjection = await BuildArtifactManifestAsync(
            finalImage,
            finalVideo,
            neutralFacts,
            auxiliaryHandoff,
            cleanResult,
            imageOutcome,
            videoOutcome,
            gainMapSemanticallyReencoded,
            workspace,
            cancellationToken).ConfigureAwait(false);
        List<NeutralArtifactManifest> manifest = manifestProjection.Manifest;
        GainMapRepresentation gainMapRep = manifestProjection.GainMapRepresentation;

        NativeGainMapMetadataV1? finalGainMapMetadata = null;
        if (gainMapRep is GainMapRepresentation.Embedded or GainMapRepresentation.Both)
        {
            if (neutralFacts.GainMap is not { IsPresent: true })
                throw new InvalidDataException("Embedded GainMap metadata inspection requires a freshly inspected primary binding.");

            string? materializedGainMapPath = null;
            if (gainMapRep == GainMapRepresentation.Both)
            {
                materializedGainMapPath = manifestProjection.GainMapArtifact?.Path
                    ?? throw new InvalidDataException("Both GainMap representation requires its verified materialized artifact.");
            }

            finalGainMapMetadata = await NativeMediaService.InspectGainMapMetadataAsync(
                finalImage.Path,
                materializedGainMapPath,
                cancellationToken).ConfigureAwait(false);
        }

        NeutralMediaSemantics semantics = ProjectNeutralSemantics(
            manifest,
            finalImage,
            finalVideo,
            neutralFacts,
            facts.Timing,
            cleanResult.PreservationReport,
            imageOutcome,
            videoOutcome,
            imageConversionTruth,
            videoConversionTruth,
            resolvedVideoFacts,
            preConversionImageOrientation,
            finalImageOrientation,
            finalImageObservation,
            finalHeicPrimary,
            finalGainMapMetadata,
            imageWasConverted,
            videoWasConverted,
            gainMapRep);

        return new NeutralMediaBundle
        {
            PrimaryImage = finalImage,
            MotionVideo = finalVideo,
            GainMap = manifestProjection.GainMapArtifact,
            HdrGainMapBinding = hdrGainMapBinding,
            GainMapRepresentation = gainMapRep,
            SourceProvenance = facts,
            RemovedProtocolFacts = [.. extracted.ExtractedProtocolFacts, .. cleanResult.RemovedFacts],
            Manifest = manifest,
            AuxiliaryMedia = auxiliaryHandoff,
            PreservationCarriers = preservationCarriers,
            Timing = facts.Timing,
            Semantics = semantics
        };
    }

    private sealed record NeutralManifestProjection(
        List<NeutralArtifactManifest> Manifest,
        GainMapRepresentation GainMapRepresentation,
        MediaArtifact? GainMapArtifact);

    private sealed record ArtifactByteIdentity(string Sha256, long ByteLength);

    private static bool IsVerifiedNeutralPrimary(SourceMediaFacts facts) =>
        facts.PrimaryImage.IsPresent &&
        facts.Protocol == SourceProtocol.NonLive &&
        facts.MotionVideo == null &&
        facts.ProtocolTailLength == 0 &&
        facts.PairingIdentifier == null;

    private static async Task<NeutralManifestProjection> BuildArtifactManifestAsync(
        MediaArtifact finalImage,
        MediaArtifact? finalVideo,
        SourceMediaFacts finalFacts,
        IReadOnlyList<AuxiliaryMediaDescriptor> auxiliaryHandoff,
        ProtocolCleanResult cleanResult,
        PreservationOutcome imageOutcome,
        PreservationOutcome videoOutcome,
        bool gainMapSemanticallyReencoded,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (finalImage.Kind != MediaArtifactKind.PrimaryImage ||
            finalImage.ImageContainer == ImageContainer.Unknown || finalImage.ImageCodec == ImageCodec.Unknown ||
            !finalFacts.PrimaryImage.IsPresent || !IsValidSha256(finalFacts.PrimarySha256))
        {
            throw new InvalidDataException("Final primary image identity, container, codec, or inspection evidence is incomplete.");
        }

        ArtifactByteIdentity primaryBytes = await VerifyArtifactByteIdentityAsync(
            finalImage, workspace, cancellationToken, "final primary image").ConfigureAwait(false);
        if (!string.Equals(primaryBytes.Sha256, finalFacts.PrimarySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Final Inspector primary hash does not identify the returned primary artifact bytes.");
        string primaryIdentity = ContentIdentity(primaryBytes.Sha256);
        var manifest = new List<NeutralArtifactManifest>
        {
            new()
            {
                Role = "PrimaryImage",
                Path = finalImage.Path,
                Sha256 = primaryBytes.Sha256,
                StableIdentity = primaryIdentity,
                Semantic = "Primary",
                Relationship = "root",
                Representation = AuxiliaryRepresentation.Materialized,
                Ownership = AuxiliaryOwnership.Primary,
                SourceLength = primaryBytes.ByteLength,
                SourceSha256 = primaryBytes.Sha256,
                ByteLength = primaryBytes.ByteLength,
                ImageContainer = finalImage.ImageContainer,
                ImageCodec = finalImage.ImageCodec,
                SemanticRepresentation = NeutralAuxiliaryRepresentation.Materialized,
                PreservationOutcome = imageOutcome
            }
        };

        if (finalVideo != null)
        {
            if (finalVideo.Kind != MediaArtifactKind.MotionVideo ||
                finalVideo.VideoContainer == VideoContainer.Unknown || finalVideo.VideoCodec == VideoCodec.Unknown)
            {
                throw new InvalidDataException("Final motion video container or codec is unknown.");
            }

            ArtifactByteIdentity videoBytes = await VerifyArtifactByteIdentityAsync(
                finalVideo, workspace, cancellationToken, "final motion video").ConfigureAwait(false);
            string videoIdentity = ContentIdentity(videoBytes.Sha256);
            manifest.Add(new NeutralArtifactManifest
            {
                Role = "MotionVideo",
                Path = finalVideo.Path,
                Sha256 = videoBytes.Sha256,
                StableIdentity = videoIdentity,
                Semantic = "MotionVideo",
                OwnerIdentity = primaryIdentity,
                Relationship = "motion-companion",
                Representation = AuxiliaryRepresentation.Materialized,
                Ownership = AuxiliaryOwnership.Auxiliary,
                SourceLength = videoBytes.ByteLength,
                SourceSha256 = videoBytes.Sha256,
                ByteLength = videoBytes.ByteLength,
                VideoContainer = finalVideo.VideoContainer,
                VideoCodec = finalVideo.VideoCodec,
                SemanticRepresentation = NeutralAuxiliaryRepresentation.Materialized,
                PreservationOutcome = videoOutcome
            });
        }

        AuxiliaryMediaFacts[] finalItems = finalFacts.AuxiliaryItems.Where(item => item.IsPresent).ToArray();
        foreach (AuxiliaryMediaFacts item in finalItems)
            ValidateFinalAuxiliaryFacts(item, primaryBytes.ByteLength);

        AuxiliaryMediaFacts? finalGainMap = ResolveFinalGainMap(finalFacts, finalItems);
        AuxiliaryMediaFacts[] gainMapFactsItems = finalItems
            .Where(item => string.Equals(item.Semantic, "GainMap", StringComparison.Ordinal))
            .ToArray();
        if ((finalGainMap == null && gainMapFactsItems.Length != 0) ||
            (finalGainMap != null && (gainMapFactsItems.Length != 1 || !ReferenceEquals(gainMapFactsItems[0], finalGainMap))))
        {
            throw new InvalidDataException("Final Inspector auxiliary graph contains an unbound or ambiguous GainMap item.");
        }

        AuxiliaryMediaDescriptor[] gainMapDescriptors = auxiliaryHandoff.Where(IsGainMapDescriptor).ToArray();
        AuxiliaryMediaDescriptor[] materializedGainMaps = gainMapDescriptors
            .Where(item => item.MaterializedArtifact != null)
            .ToArray();
        AuxiliaryMediaDescriptor? inspectedGainMapDescriptor = finalGainMap == null
            ? null
            : FindHandoffDescriptor(finalGainMap, auxiliaryHandoff);

        GainMapRepresentation gainMapRepresentation;
        MediaArtifact? gainMapArtifact = null;
        AuxiliaryMediaDescriptor? finalGainMapDescriptor = null;
        if (finalGainMap != null)
        {
            if (materializedGainMaps.Length > 1)
                throw new InvalidDataException("Final GainMap has multiple materialized handoff artifacts.");

            if (materializedGainMaps.Length == 1)
            {
                finalGainMapDescriptor = materializedGainMaps[0];
                if (!ReferenceEquals(finalGainMapDescriptor, inspectedGainMapDescriptor))
                    throw new InvalidDataException("Materialized GainMap does not identify the final Inspector GainMap.");
                ValidateDescriptorAgainstFinalFacts(finalGainMapDescriptor, finalGainMap);
                gainMapArtifact = finalGainMapDescriptor.MaterializedArtifact;
                ArtifactByteIdentity detachedBytes = await VerifyDescriptorArtifactAsync(
                    finalGainMapDescriptor, workspace, cancellationToken).ConfigureAwait(false);
                if (detachedBytes.ByteLength != finalGainMap.ByteLength ||
                    !string.Equals(detachedBytes.Sha256, finalGainMap.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Embedded and materialized GainMap bytes do not have one exact semantic identity.");
                }
                gainMapRepresentation = GainMapRepresentation.Both;
            }
            else
            {
                finalGainMapDescriptor = inspectedGainMapDescriptor;
                if (finalGainMapDescriptor != null)
                    ValidateDescriptorAgainstFinalFacts(finalGainMapDescriptor, finalGainMap);
                gainMapRepresentation = GainMapRepresentation.Embedded;
            }
        }
        else if (materializedGainMaps.Length == 1)
        {
            finalGainMapDescriptor = materializedGainMaps[0];
            ValidateCompleteDescriptor(finalGainMapDescriptor, requireMaterializedArtifact: true);
            gainMapArtifact = finalGainMapDescriptor.MaterializedArtifact;
            _ = await VerifyDescriptorArtifactAsync(finalGainMapDescriptor, workspace, cancellationToken)
                .ConfigureAwait(false);
            gainMapRepresentation = GainMapRepresentation.Detached;
        }
        else
        {
            if (materializedGainMaps.Length > 1)
                throw new InvalidDataException("Detached GainMap handoff is ambiguous.");
            gainMapRepresentation = GainMapRepresentation.None;
        }

        if (cleanResult.CleanedGainMap != null && materializedGainMaps.Length == 0 && !gainMapSemanticallyReencoded)
        {
            throw new InvalidDataException("Cleaner returned a GainMap artifact without a verified final semantic descriptor.");
        }

        var auxiliaryIdentities = new HashSet<string>(StringComparer.Ordinal);
        if (finalGainMap != null)
        {
            MediaArtifact? materialized = gainMapRepresentation == GainMapRepresentation.Both ? gainMapArtifact : null;
            NeutralAuxiliaryRepresentation semanticRepresentation = gainMapRepresentation switch
            {
                GainMapRepresentation.Both => NeutralAuxiliaryRepresentation.Both,
                GainMapRepresentation.Embedded => NeutralAuxiliaryRepresentation.Embedded,
                _ => throw new InvalidDataException("Inspected GainMap has an invalid final representation.")
            };
            manifest.Add(await CreateInspectedAuxiliaryManifestAsync(
                finalGainMap,
                finalGainMapDescriptor,
                materialized,
                semanticRepresentation,
                "GainMap",
                gainMapRepresentation,
                imageOutcome,
                finalImage,
                workspace,
                cancellationToken).ConfigureAwait(false));
            auxiliaryIdentities.Add(finalGainMap.StableIdentity);
        }
        else if (gainMapRepresentation == GainMapRepresentation.Detached)
        {
            AuxiliaryMediaDescriptor descriptor = finalGainMapDescriptor
                ?? throw new InvalidDataException("Detached GainMap has no verified descriptor.");
            manifest.Add(await CreateMaterializedAuxiliaryManifestAsync(
                descriptor,
                descriptor.MaterializedArtifact!,
                "GainMap",
                GainMapRepresentation.Detached,
                workspace,
                cancellationToken).ConfigureAwait(false));
            auxiliaryIdentities.Add(descriptor.StableIdentity);
        }

        var representedDescriptors = new HashSet<AuxiliaryMediaDescriptor>();
        foreach (AuxiliaryMediaFacts item in finalItems.Where(item => !IsGainMapFacts(item)))
        {
            AuxiliaryMediaDescriptor? descriptor = FindHandoffDescriptor(item, auxiliaryHandoff);
            if (descriptor != null)
            {
                ValidateDescriptorAgainstFinalFacts(descriptor, item);
                representedDescriptors.Add(descriptor);
            }

            NeutralAuxiliaryRepresentation representation = descriptor?.MaterializedArtifact != null
                ? NeutralAuxiliaryRepresentation.Both
                : MapRepresentation(item.Representation);
            if (representation == NeutralAuxiliaryRepresentation.Unknown)
                throw new InvalidDataException($"Final auxiliary '{item.StableIdentity}' has an unknown representation.");

            manifest.Add(await CreateInspectedAuxiliaryManifestAsync(
                item,
                descriptor,
                descriptor?.MaterializedArtifact,
                representation,
                "Auxiliary",
                GainMapRepresentation.None,
                imageOutcome,
                finalImage,
                workspace,
                cancellationToken).ConfigureAwait(false));
            if (!auxiliaryIdentities.Add(item.StableIdentity))
                throw new InvalidDataException($"Neutral manifest has duplicate auxiliary identity '{item.StableIdentity}'.");
        }

        foreach (AuxiliaryMediaDescriptor descriptor in auxiliaryHandoff)
        {
            if (IsGainMapDescriptor(descriptor) || descriptor.MaterializedArtifact == null || representedDescriptors.Contains(descriptor))
                continue;
            if (auxiliaryIdentities.Contains(descriptor.StableIdentity))
            {
                throw new InvalidDataException(
                    $"Materialized auxiliary '{descriptor.StableIdentity}' conflicts with a final Inspector identity.");
            }

            manifest.Add(await CreateMaterializedAuxiliaryManifestAsync(
                descriptor,
                descriptor.MaterializedArtifact,
                "Auxiliary",
                GainMapRepresentation.None,
                workspace,
                cancellationToken).ConfigureAwait(false));
            if (!auxiliaryIdentities.Add(descriptor.StableIdentity))
                throw new InvalidDataException($"Neutral manifest has duplicate auxiliary identity '{descriptor.StableIdentity}'.");
        }

        // The primary row carries the bundle's single semantic GainMap state
        // for callers that still consume this compatibility enum.
        manifest[0] = manifest[0] with { GainMapRepresentation = gainMapRepresentation };

        ValidateFinalManifest(manifest, primaryBytes.ByteLength);
        return new NeutralManifestProjection(manifest, gainMapRepresentation, gainMapArtifact);
    }

    private static async Task<NeutralArtifactManifest> CreateInspectedAuxiliaryManifestAsync(
        AuxiliaryMediaFacts item,
        AuxiliaryMediaDescriptor? descriptor,
        MediaArtifact? materializedArtifact,
        NeutralAuxiliaryRepresentation semanticRepresentation,
        string role,
        GainMapRepresentation gainMapRepresentation,
        PreservationOutcome fallbackOutcome,
        MediaArtifact ownerArtifact,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        string sha256 = item.Sha256;
        long byteLength = item.ByteLength;
        string path = ownerArtifact.Path;
        if (materializedArtifact != null)
        {
            ArtifactByteIdentity actual = await VerifyDescriptorArtifactAsync(
                descriptor ?? throw new InvalidDataException("Materialized auxiliary has no verified descriptor."),
                workspace,
                cancellationToken).ConfigureAwait(false);
            if (actual.ByteLength != item.ByteLength ||
                !string.Equals(actual.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Materialized auxiliary '{item.StableIdentity}' differs from final Inspector bytes.");
            }
            sha256 = actual.Sha256;
            byteLength = actual.ByteLength;
            path = materializedArtifact.Path;
        }

        return new NeutralArtifactManifest
        {
            Role = role,
            Path = path,
            Sha256 = sha256,
            // The final Inspector's stable identity is the identity of the
            // final semantic graph, not the source handoff descriptor.
            StableIdentity = item.StableIdentity,
            Semantic = item.Semantic,
            OwnerIdentity = item.OwnerIdentity,
            Relationship = item.Relationship,
            Representation = descriptor?.Representation ?? item.Representation,
            Ownership = item.Ownership,
            SourceOffset = descriptor?.SourceOffset ?? item.ByteOffset,
            SourceLength = descriptor?.SourceLength ?? item.ByteLength,
            SourceSha256 = descriptor?.SourceSha256 ?? item.Sha256,
            ByteLength = byteLength,
            ImageContainer = item.Container,
            ImageCodec = ImageCodecFromAuxiliaryCodec(item.Codec),
            AuxiliaryCodec = item.Codec,
            SemanticRepresentation = semanticRepresentation,
            OwnerByteOffset = item.Representation == AuxiliaryRepresentation.Embedded &&
                item.Ownership == AuxiliaryOwnership.Primary ? item.ByteOffset : null,
            PreservationOutcome = descriptor?.PreservationOutcome ?? fallbackOutcome,
            GainMapRepresentation = gainMapRepresentation
        };
    }

    private static async Task<NeutralArtifactManifest> CreateMaterializedAuxiliaryManifestAsync(
        AuxiliaryMediaDescriptor descriptor,
        MediaArtifact artifact,
        string role,
        GainMapRepresentation gainMapRepresentation,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ValidateCompleteDescriptor(descriptor, requireMaterializedArtifact: true);
        ArtifactByteIdentity actual = await VerifyDescriptorArtifactAsync(descriptor, workspace, cancellationToken)
            .ConfigureAwait(false);
        NeutralAuxiliaryRepresentation representation = role == "GainMap"
            ? NeutralAuxiliaryRepresentation.Detached
            : descriptor.Representation switch
            {
                AuxiliaryRepresentation.Materialized => NeutralAuxiliaryRepresentation.Materialized,
                AuxiliaryRepresentation.Detached => NeutralAuxiliaryRepresentation.Detached,
                _ => NeutralAuxiliaryRepresentation.Detached
            };
        ImageContainer container = descriptor.ImageContainer != ImageContainer.Unknown
            ? descriptor.ImageContainer
            : artifact.ImageContainer;
        if (container == ImageContainer.Unknown || descriptor.Codec == AuxiliaryCodec.Unknown)
            throw new InvalidDataException($"Materialized auxiliary '{descriptor.StableIdentity}' has unknown format facts.");

        return new NeutralArtifactManifest
        {
            Role = role,
            Path = artifact.Path,
            Sha256 = actual.Sha256,
            StableIdentity = AuxiliaryStableIdentity(descriptor.Semantic, actual.Sha256),
            Semantic = descriptor.Semantic,
            OwnerIdentity = descriptor.OwnerIdentity,
            Relationship = descriptor.Relationship,
            Representation = descriptor.Representation,
            Ownership = descriptor.Ownership,
            SourceOffset = descriptor.SourceOffset,
            SourceLength = descriptor.SourceLength,
            SourceSha256 = descriptor.SourceSha256,
            ByteLength = actual.ByteLength,
            ImageContainer = container,
            ImageCodec = ImageCodecFromAuxiliaryCodec(descriptor.Codec),
            AuxiliaryCodec = descriptor.Codec,
            SemanticRepresentation = representation,
            PreservationOutcome = descriptor.PreservationOutcome,
            GainMapRepresentation = gainMapRepresentation
        };
    }

    private static async Task<ArtifactByteIdentity> VerifyArtifactByteIdentityAsync(
        MediaArtifact artifact,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken,
        string label)
    {
        if (string.IsNullOrWhiteSpace(artifact.Path) || !File.Exists(artifact.Path))
            throw new InvalidDataException($"The {label} artifact is missing.");

        long actualLength = new FileInfo(artifact.Path).Length;
        string actualSha256 = await workspace.ComputeFileSha256Async(artifact.Path, cancellationToken)
            .ConfigureAwait(false);
        if (actualLength <= 0 || !IsValidSha256(actualSha256) ||
            (artifact.ByteLength > 0 && artifact.ByteLength != actualLength) ||
            (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
             !string.Equals(artifact.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"The {label} artifact bytes disagree with their recorded identity.");
        }
        return new ArtifactByteIdentity(actualSha256, actualLength);
    }

    private static async Task<ArtifactByteIdentity> VerifyDescriptorArtifactAsync(
        AuxiliaryMediaDescriptor descriptor,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ValidateCompleteDescriptor(descriptor, requireMaterializedArtifact: true);
        MediaArtifact artifact = descriptor.MaterializedArtifact!;
        ArtifactByteIdentity actual = await VerifyArtifactByteIdentityAsync(
            artifact, workspace, cancellationToken, $"auxiliary '{descriptor.StableIdentity}'").ConfigureAwait(false);
        if (actual.ByteLength != descriptor.SourceLength ||
            !string.Equals(actual.Sha256, descriptor.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Materialized auxiliary '{descriptor.StableIdentity}' disagrees with its verified Inspector identity.");
        }
        return actual;
    }

    private static void ValidateCompleteDescriptor(
        AuxiliaryMediaDescriptor descriptor,
        bool requireMaterializedArtifact)
    {
        if (string.IsNullOrWhiteSpace(descriptor.StableIdentity) ||
            string.IsNullOrWhiteSpace(descriptor.Semantic) ||
            string.IsNullOrWhiteSpace(descriptor.OwnerIdentity) ||
            string.IsNullOrWhiteSpace(descriptor.Relationship) ||
            descriptor.SourceLength <= 0 || !IsValidSha256(descriptor.SourceSha256) ||
            descriptor.ImageContainer == ImageContainer.Unknown || descriptor.Codec == AuxiliaryCodec.Unknown ||
            (requireMaterializedArtifact && descriptor.MaterializedArtifact == null))
        {
            throw new InvalidDataException($"Auxiliary descriptor '{descriptor.StableIdentity}' is incomplete.");
        }
    }

    private static void ValidateFinalAuxiliaryFacts(AuxiliaryMediaFacts item, long ownerLength)
    {
        if (string.IsNullOrWhiteSpace(item.StableIdentity) || string.IsNullOrWhiteSpace(item.Semantic) ||
            string.IsNullOrWhiteSpace(item.OwnerIdentity) || string.IsNullOrWhiteSpace(item.Relationship) ||
            item.ByteLength <= 0 || !IsValidSha256(item.Sha256) ||
            item.Container == ImageContainer.Unknown || item.Codec == AuxiliaryCodec.Unknown ||
            item.Representation == AuxiliaryRepresentation.Materialized && item.ByteOffset < 0)
        {
            throw new InvalidDataException("Final Inspector auxiliary facts are incomplete.");
        }

        if (item.Representation == AuxiliaryRepresentation.Embedded && item.Ownership == AuxiliaryOwnership.Primary)
        {
            long end;
            try { end = checked(item.ByteOffset + item.ByteLength); }
            catch (OverflowException ex) { throw new InvalidDataException("Final Inspector auxiliary range overflows.", ex); }
            if (item.ByteOffset < 0 || end > ownerLength)
                throw new InvalidDataException("Final Inspector auxiliary range is outside its authoritative owner artifact.");
        }
    }

    private static AuxiliaryMediaFacts? ResolveFinalGainMap(
        SourceMediaFacts finalFacts,
        IReadOnlyList<AuxiliaryMediaFacts> finalItems)
    {
        if (finalFacts.GainMap is not { IsPresent: true } facts)
            return null;
        if (facts.AuxiliaryIndex >= finalFacts.AuxiliaryItems.Count)
            throw new InvalidDataException("Final GainMap does not identify an in-range Inspector auxiliary item.");
        AuxiliaryMediaFacts item = finalFacts.AuxiliaryItems[checked((int)facts.AuxiliaryIndex)];
        if (!item.IsPresent || item.ItemId != facts.ItemId || item.ByteOffset != facts.ByteOffset ||
            item.ByteLength != facts.ByteLength || item.Container != facts.Container ||
            item.Representation != facts.Representation || item.Ownership != facts.Ownership ||
            !string.Equals(item.Relationship, facts.Relationship, StringComparison.Ordinal) ||
            !string.Equals(item.Semantic, "GainMap", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Final GainMap item does not match its authoritative Inspector binding.");
        }
        if (!finalItems.Contains(item))
            throw new InvalidDataException("Final GainMap binding is not present in the inspected auxiliary graph.");
        return item;
    }

    private static void ValidateDescriptorAgainstFinalFacts(
        AuxiliaryMediaDescriptor descriptor,
        AuxiliaryMediaFacts item)
    {
        ValidateCompleteDescriptor(descriptor, requireMaterializedArtifact: false);
        if (!string.Equals(descriptor.Semantic, item.Semantic, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Relationship, item.Relationship, StringComparison.Ordinal) ||
            descriptor.SourceLength != item.ByteLength ||
            !string.Equals(descriptor.SourceSha256, item.Sha256, StringComparison.OrdinalIgnoreCase) ||
            descriptor.ImageContainer != item.Container || descriptor.Codec != item.Codec ||
            descriptor.Ownership != item.Ownership)
        {
            throw new InvalidDataException(
                $"Handoff descriptor '{descriptor.StableIdentity}' disagrees with final Inspector semantic, relationship, or byte facts.");
        }
    }

    private static bool IsGainMapDescriptor(AuxiliaryMediaDescriptor descriptor) =>
        descriptor.ArtifactRole == MediaArtifactKind.GainMap ||
        string.Equals(descriptor.Semantic, "GainMap", StringComparison.Ordinal);

    private static bool IsGainMapFacts(AuxiliaryMediaFacts item) =>
        string.Equals(item.Semantic, "GainMap", StringComparison.Ordinal);

    private static ImageCodec ImageCodecFromAuxiliaryCodec(AuxiliaryCodec codec) => codec switch
    {
        AuxiliaryCodec.Jpeg => ImageCodec.Jpeg,
        AuxiliaryCodec.Hevc => ImageCodec.Hevc,
        _ => ImageCodec.Unknown
    };

    private static string AuxiliaryStableIdentity(string semantic, string sha256)
    {
        if (string.IsNullOrWhiteSpace(semantic) || !IsValidSha256(sha256))
            throw new InvalidDataException("Auxiliary semantic identity requires a semantic name and content SHA-256.");
        return $"aux:{semantic}:sha256:{sha256.ToLowerInvariant()}";
    }

    private static void ValidateFinalManifest(IReadOnlyList<NeutralArtifactManifest> manifest, long primaryLength)
    {
        var stableIdentities = new HashSet<(string Role, string StableIdentity)>();
        foreach (NeutralArtifactManifest item in manifest)
        {
            if (string.IsNullOrWhiteSpace(item.Role) || string.IsNullOrWhiteSpace(item.StableIdentity) ||
                string.IsNullOrWhiteSpace(item.Semantic) || string.IsNullOrWhiteSpace(item.Relationship) ||
                item.ByteLength <= 0 || !IsValidSha256(item.Sha256) ||
                item.PreservationOutcome == PreservationOutcome.Unsupported ||
                item.SemanticRepresentation == NeutralAuxiliaryRepresentation.Unknown ||
                !stableIdentities.Add((item.Role, item.StableIdentity)))
            {
                throw new InvalidDataException("Neutral artifact manifest contains an incomplete, unsupported, or duplicate semantic row.");
            }
            if (item.Role is "PrimaryImage" or "MotionVideo")
            {
                if (item.Role == "PrimaryImage" &&
                    (item.ImageContainer == ImageContainer.Unknown || item.ImageCodec == ImageCodec.Unknown))
                    throw new InvalidDataException("Primary image manifest row is missing applicable format facts.");
                if (item.Role == "MotionVideo" &&
                    (item.VideoContainer == VideoContainer.Unknown || item.VideoCodec == VideoCodec.Unknown ||
                     string.IsNullOrWhiteSpace(item.OwnerIdentity)))
                    throw new InvalidDataException("Motion video manifest row is missing applicable format or owner facts.");
            }
            else
            {
                if (item.ImageContainer == ImageContainer.Unknown || item.AuxiliaryCodec == AuxiliaryCodec.Unknown ||
                    string.IsNullOrWhiteSpace(item.OwnerIdentity))
                    throw new InvalidDataException($"Auxiliary manifest row '{item.StableIdentity}' is missing applicable format or owner facts.");
                if (item.OwnerByteOffset is long offset &&
                    (offset < 0 || item.ByteLength > primaryLength || offset > primaryLength - item.ByteLength))
                    throw new InvalidDataException($"Auxiliary manifest row '{item.StableIdentity}' has an out-of-range owner offset.");
            }
        }
    }

    private static NeutralMediaSemantics ProjectNeutralSemantics(
        IReadOnlyList<NeutralArtifactManifest> manifest,
        MediaArtifact finalImage,
        MediaArtifact? finalVideo,
        SourceMediaFacts finalFacts,
        TimingFacts inspectedSourceTiming,
        PreservationReport? preservationReport,
        PreservationOutcome imageOutcome,
        PreservationOutcome videoOutcome,
        ConversionExecutionTruth? imageConversionTruth,
        ConversionExecutionTruth? videoConversionTruth,
        VideoFacts? resolvedVideoFacts,
        NativeImageOrientationObservationV1? preConversionImageOrientation,
        NativeImageOrientationObservationV1 finalImageOrientation,
        PreservationObservation finalImageObservation,
        NativeHeicPrimaryDecodeInfo? finalHeicPrimary,
        NativeGainMapMetadataV1? finalGainMapMetadata,
        bool imageWasConverted,
        bool videoWasConverted,
        GainMapRepresentation gainMapRepresentation)
    {
        NeutralArtifactManifest primaryManifest = manifest.Single(item => item.Role == "PrimaryImage");
        NeutralArtifactSemantics primary = new()
        {
            Role = NeutralArtifactRole.PrimaryImage,
            ContentIdentity = ContentIdentity(primaryManifest.Sha256),
            Semantic = "Primary",
            ImageContainer = finalImage.ImageContainer,
            ImageCodec = finalImage.ImageCodec,
            PreservationOutcome = imageOutcome,
            EvidenceState = finalFacts.PrimaryImage.IsPresent
                ? NeutralEvidenceState.Verified
                : NeutralEvidenceState.Unknown
        };

        NeutralArtifactSemantics? motion = null;
        if (finalVideo != null)
        {
            NeutralArtifactManifest videoManifest = manifest.Single(item => item.Role == "MotionVideo");
            bool videoStructureVerified = videoWasConverted
                ? videoConversionTruth is { ActualOperationKind: not ConversionOperationKind.Unsupported }
                : resolvedVideoFacts is { IsPresent: true, Container: not VideoContainer.Unknown, Codec: not VideoCodec.Unknown };
            motion = new NeutralArtifactSemantics
            {
                Role = NeutralArtifactRole.MotionVideo,
                ContentIdentity = ContentIdentity(videoManifest.Sha256),
                Semantic = "MotionVideo",
                VideoContainer = finalVideo.VideoContainer,
                VideoCodec = finalVideo.VideoCodec,
                PreservationOutcome = videoOutcome,
                EvidenceState = videoStructureVerified
                    ? NeutralEvidenceState.Verified
                    : NeutralEvidenceState.Unknown
            };
        }

        NeutralAuxiliarySemantics? gainMap = ProjectFinalGainMap(manifest);
        var auxiliarySemantics = ProjectOtherAuxiliaryMedia(manifest);

        NeutralTimingSemantics timing = ProjectTiming(
            finalVideo != null,
            inspectedSourceTiming,
            preservationReport,
            videoConversionTruth,
            videoWasConverted);
        NeutralOrientationSemantics orientation = ProjectOrientation(
            preConversionImageOrientation,
            finalImageOrientation,
            imageConversionTruth,
            preservationReport,
            imageWasConverted,
            resolvedVideoFacts,
            videoConversionTruth,
            videoWasConverted);

        bool finalImageInspected = finalFacts.PrimaryImage.IsPresent;
        if (finalImageObservation.HasIcc && !IsValidSha256(finalImageObservation.IccSha256))
            throw new InvalidDataException("Final ICC profile does not have a valid content SHA-256 identity.");

        double? hdrCapacityMin = null;
        double? hdrCapacityMax = null;
        if (finalGainMapMetadata is { Kind: 2 } isoGainMapMetadata)
        {
            if (!double.IsFinite(isoGainMapMetadata.HdrCapacityMin) ||
                !double.IsFinite(isoGainMapMetadata.HdrCapacityMax) ||
                isoGainMapMetadata.HdrCapacityMin < 0 ||
                isoGainMapMetadata.HdrCapacityMax <= isoGainMapMetadata.HdrCapacityMin)
                throw new InvalidDataException("Final ISO GainMap metadata has invalid HDR capacity bounds.");
            hdrCapacityMin = isoGainMapMetadata.HdrCapacityMin;
            hdrCapacityMax = isoGainMapMetadata.HdrCapacityMax;
        }

        bool hasNclx = finalHeicPrimary is { HasNclx: 1 };
        bool? isHdrRelevant = gainMap != null || finalGainMapMetadata.HasValue
            ? true
            : finalHeicPrimary is { } heicFacts ? heicFacts.IsHdrRelevant != 0 : null;
        NeutralColorHdrSemantics colorHdr = new()
        {
            HasGainMap = finalImageInspected ? gainMap != null : null,
            HdrState = !finalImageInspected
                ? NeutralHdrState.Unknown
                : gainMap != null
                    ? NeutralHdrState.GainMapPresent
                    : NeutralHdrState.NoGainMapObserved,
            GainMapEvidence = finalImageInspected
                ? NeutralEvidenceState.Verified
                : NeutralEvidenceState.Unknown,
            ColorSpaceState = finalImageObservation.HasIcc || hasNclx
                ? NeutralColorSpaceState.Verified
                : NeutralColorSpaceState.Unknown,
            IccProfileSha256 = finalImageObservation.HasIcc ? finalImageObservation.IccSha256 : null,
            IccProfileEvidence = NeutralEvidenceState.Verified,
            EncodedBitDepth = finalHeicPrimary?.SourceBitDepth,
            DecodedSignalBitDepth = finalHeicPrimary?.DecodedSignalBitDepth,
            DecodedStorageBitDepth = finalHeicPrimary?.DecodedStorageBitDepth,
            NclxPrimaries = hasNclx ? finalHeicPrimary!.Value.NclxPrimaries : null,
            NclxTransfer = hasNclx ? finalHeicPrimary!.Value.NclxTransfer : null,
            NclxMatrix = hasNclx ? finalHeicPrimary!.Value.NclxMatrix : null,
            IsHdrRelevant = isHdrRelevant,
            HdrEvidence = gainMap != null || finalHeicPrimary.HasValue || finalGainMapMetadata.HasValue
                ? NeutralEvidenceState.Verified
                : NeutralEvidenceState.Unknown,
            HdrCapacityMin = hdrCapacityMin,
            HdrCapacityMax = hdrCapacityMax,
            GainMapMetadataEvidence = gainMap == null
                ? NeutralEvidenceState.NotApplicable
                : finalGainMapMetadata.HasValue ? NeutralEvidenceState.Verified : NeutralEvidenceState.Unknown,
            ColorIccPreservation = ComponentOutcomeFromTruthOrReport(
                imageConversionTruth?.ColorIcc, preservationReport, "Icc"),
            GainMapPreservation = ComponentOutcomeFromTruthOrReport(
                imageConversionTruth?.HdrGainMap, preservationReport, "GainMap", "Hdr"),
            PreservationOutcome = imageOutcome,
            PreservationEvidence = preservationReport != null || imageConversionTruth != null
                ? PreservationFact(string.Empty, imageOutcome, evidenceAvailable: true).EvidenceState
                : NeutralEvidenceState.Unknown
        };

        string primaryIdentity = primary.ContentIdentity;
        NeutralArtifactPreservation primaryPreservation = PreservationFact(
            primaryIdentity,
            imageOutcome,
            preservationReport != null || imageConversionTruth != null);
        NeutralArtifactPreservation? motionPreservation = motion == null
            ? null
            : PreservationFact(
                motion.ContentIdentity,
                videoOutcome,
                HasVideoPreservationEvidence(preservationReport) || videoConversionTruth != null);
        NeutralArtifactPreservation? gainMapPreservation = gainMap == null
            ? null
            : PreservationFact(
                gainMap.StableIdentity,
                gainMap.PreservationOutcome,
                gainMap.EvidenceState != NeutralEvidenceState.Unknown && gainMap.PreservationOutcome.HasValue);
        var auxiliaryPreservation = auxiliarySemantics
            .Select(item => PreservationFact(
                item.StableIdentity,
                item.PreservationOutcome,
                item.EvidenceState != NeutralEvidenceState.Unknown && item.PreservationOutcome.HasValue))
            .ToArray();

        return new NeutralMediaSemantics
        {
            PrimaryImage = primary,
            MotionVideo = motion,
            GainMap = gainMap,
            AuxiliaryMedia = auxiliarySemantics,
            Timing = timing,
            Orientation = orientation,
            ColorHdr = colorHdr,
            Preservation = new NeutralPreservationSemantics
            {
                PrimaryImage = primaryPreservation,
                MotionVideo = motionPreservation,
                GainMap = gainMapPreservation,
                AuxiliaryMedia = auxiliaryPreservation
            }
        };
    }

    private static NeutralAuxiliarySemantics? ProjectFinalGainMap(
        IReadOnlyList<NeutralArtifactManifest> manifest)
    {
        NeutralArtifactManifest? row = manifest.SingleOrDefault(item => item.Role == "GainMap");
        if (row == null)
            return null;
        if (row.Semantic != "GainMap" || row.SemanticRepresentation is not (
                NeutralAuxiliaryRepresentation.Embedded or
                NeutralAuxiliaryRepresentation.Detached or
                NeutralAuxiliaryRepresentation.Both))
        {
            throw new InvalidDataException("Final GainMap manifest row has an invalid semantic representation.");
        }

        return new NeutralAuxiliarySemantics
        {
            StableIdentity = row.StableIdentity,
            Semantic = row.Semantic,
            OwnerIdentity = row.OwnerIdentity,
            Relationship = row.Relationship,
            ImageContainer = row.ImageContainer,
            VideoContainer = row.VideoContainer,
            Codec = row.AuxiliaryCodec,
            Representation = row.SemanticRepresentation,
            Ownership = row.Ownership,
            PreservationOutcome = row.PreservationOutcome,
            EvidenceState = NeutralEvidenceState.Verified
        };
    }

    private static IReadOnlyList<NeutralAuxiliarySemantics> ProjectOtherAuxiliaryMedia(
        IReadOnlyList<NeutralArtifactManifest> manifest)
    {
        return manifest.Where(item => item.Role == "Auxiliary")
            .Select(item => new NeutralAuxiliarySemantics
            {
                StableIdentity = item.StableIdentity,
                Semantic = item.Semantic,
                OwnerIdentity = item.OwnerIdentity,
                Relationship = item.Relationship,
                ImageContainer = item.ImageContainer,
                VideoContainer = item.VideoContainer,
                Codec = item.AuxiliaryCodec,
                Representation = item.SemanticRepresentation,
                Ownership = item.Ownership,
                PreservationOutcome = item.PreservationOutcome,
                EvidenceState = NeutralEvidenceState.Verified
            })
            .ToArray();
    }

    private static AuxiliaryMediaDescriptor? FindHandoffDescriptor(
        AuxiliaryMediaFacts finalItem,
        IReadOnlyList<AuxiliaryMediaDescriptor> auxiliaryHandoff)
    {
        AuxiliaryMediaDescriptor[] matches = auxiliaryHandoff.Where(item =>
            string.Equals(item.Semantic, finalItem.Semantic, StringComparison.Ordinal) &&
            string.Equals(item.Relationship, finalItem.Relationship, StringComparison.Ordinal) &&
            item.SourceLength == finalItem.ByteLength &&
            string.Equals(item.SourceSha256, finalItem.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        AuxiliaryMediaDescriptor[] exact = matches.Where(item =>
            string.Equals(item.StableIdentity, finalItem.StableIdentity, StringComparison.Ordinal) &&
            string.Equals(item.OwnerIdentity, finalItem.OwnerIdentity, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1)
            return exact[0];
        if (exact.Length > 1 || matches.Length > 1)
            throw new InvalidDataException($"Final auxiliary '{finalItem.StableIdentity}' has ambiguous preservation handoff evidence.");
        return matches.SingleOrDefault();
    }

    private static NeutralTimingSemantics ProjectTiming(
        bool hasMotionVideo,
        TimingFacts sourceTiming,
        PreservationReport? preservationReport,
        ConversionExecutionTruth? videoConversionTruth,
        bool videoWasConverted)
    {
        if (!hasMotionVideo)
            return new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.NotApplicable };

        if (videoWasConverted)
        {
            if (videoConversionTruth?.Timing == ConversionComponentOutcome.Preserved)
                return TimingFromSource(sourceTiming, NeutralEvidenceState.VerifiedPreserved);
            if (videoConversionTruth?.Timing == ConversionComponentOutcome.NotApplicable)
                return new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.NotApplicable };
            return new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.Unknown };
        }

        PreservationCheckStatus? status = FindPreservationStatus(preservationReport, "Timing");
        return status switch
        {
            PreservationCheckStatus.VerifiedPreserved or PreservationCheckStatus.SemanticallyPreserved =>
                TimingFromSource(sourceTiming, NeutralEvidenceState.VerifiedPreserved),
            PreservationCheckStatus.NotApplicable =>
                new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.NotApplicable },
            _ => new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.Unknown }
        };
    }

    private static NeutralTimingSemantics TimingFromSource(TimingFacts timing, NeutralEvidenceState evidence) => new()
    {
        CoverTimestampUs = timing.CoverTimestampUs,
        PrimaryTimestampUs = timing.PrimaryTimestampUs,
        CoverFrameIndex = timing.CoverFrameIndex,
        TotalFrames = timing.TotalFrames > 0 ? timing.TotalFrames : null,
        EvidenceState = evidence
    };

    private static NeutralOrientationSemantics ProjectOrientation(
        NativeImageOrientationObservationV1? preConversionImageOrientation,
        NativeImageOrientationObservationV1 finalImageOrientation,
        ConversionExecutionTruth? imageConversionTruth,
        PreservationReport? preservationReport,
        bool imageWasConverted,
        VideoFacts? resolvedVideoFacts,
        ConversionExecutionTruth? videoConversionTruth,
        bool videoWasConverted)
    {
        if (imageWasConverted && !preConversionImageOrientation.HasValue)
            throw new InvalidDataException("Image orientation preservation cannot be evaluated without a verified pre-conversion observation.");

        ConversionComponentOutcome imageOrientationPreservation = imageWasConverted
            ? imageConversionTruth?.Orientation ?? ConversionComponentOutcome.NotEvaluated
            : ComponentOutcomeFromTruthOrReport(null, preservationReport, "Orientation");
        if (imageWasConverted &&
            imageOrientationPreservation == ConversionComponentOutcome.Preserved &&
            (preConversionImageOrientation!.Value.ClockwiseRotationDegrees != finalImageOrientation.ClockwiseRotationDegrees ||
             preConversionImageOrientation.Value.Reflection != finalImageOrientation.Reflection))
            throw new InvalidDataException("Orientation preservation evidence disagrees with final Native orientation observation.");

        NeutralOrientationTransform imageTransform = new()
        {
            ClockwiseRotationDegrees = finalImageOrientation.ClockwiseRotationDegrees,
            Reflection = finalImageOrientation.Reflection switch
            {
                NativeImageOrientationReflection.None => NeutralReflection.None,
                NativeImageOrientationReflection.Horizontal => NeutralReflection.Horizontal,
                _ => NeutralReflection.Unknown
            },
            RotationEvidence = NeutralEvidenceState.Verified,
            ReflectionEvidence = NeutralEvidenceState.Verified,
            Preservation = imageOrientationPreservation
        };

        if (resolvedVideoFacts is not { IsPresent: true })
            return new NeutralOrientationSemantics { PrimaryImage = imageTransform };

        int? rotation = NormalizeRotation(resolvedVideoFacts.RotationDegrees);
        ConversionComponentOutcome videoOrientationPreservation = videoWasConverted
            ? videoConversionTruth?.Orientation ?? ConversionComponentOutcome.NotEvaluated
            : ConversionComponentOutcome.NotEvaluated;
        NeutralOrientationTransform videoTransform = rotation == null
            ? new NeutralOrientationTransform { Preservation = videoOrientationPreservation }
            : new NeutralOrientationTransform
            {
                ClockwiseRotationDegrees = rotation,
                RotationEvidence = NeutralEvidenceState.Verified,
                // Native currently reports rotation but does not prove a reflection state.
                Reflection = NeutralReflection.Unknown,
                ReflectionEvidence = NeutralEvidenceState.Unknown,
                Preservation = videoOrientationPreservation
            };

        return new NeutralOrientationSemantics
        {
            PrimaryImage = imageTransform,
            MotionVideo = videoTransform
        };
    }

    private static int? NormalizeRotation(int degrees)
    {
        int normalized = ((degrees % 360) + 360) % 360;
        return normalized is 0 or 90 or 180 or 270 ? normalized : null;
    }

    private static string ContentIdentity(string sha256)
    {
        if (!IsValidSha256(sha256))
            throw new InvalidDataException("Final media artifact has no valid content SHA-256 identity.");
        return $"sha256:{sha256.ToLowerInvariant()}";
    }

    private static NeutralAuxiliaryRepresentation MapRepresentation(AuxiliaryRepresentation representation) => representation switch
    {
        AuxiliaryRepresentation.Embedded => NeutralAuxiliaryRepresentation.Embedded,
        AuxiliaryRepresentation.Detached => NeutralAuxiliaryRepresentation.Detached,
        AuxiliaryRepresentation.Materialized => NeutralAuxiliaryRepresentation.Materialized,
        _ => NeutralAuxiliaryRepresentation.Unknown
    };

    private static NeutralArtifactPreservation PreservationFact(
        string identity,
        PreservationOutcome? outcome,
        bool evidenceAvailable)
    {
        NeutralEvidenceState evidence = !evidenceAvailable || !outcome.HasValue
            ? NeutralEvidenceState.Unknown
            : outcome.Value switch
            {
                PreservationOutcome.Preserved => NeutralEvidenceState.VerifiedPreserved,
                PreservationOutcome.Reencoded or PreservationOutcome.TranscodedLossless => NeutralEvidenceState.VerifiedConverted,
                PreservationOutcome.DiscardedNotApplicable => NeutralEvidenceState.NotApplicable,
                PreservationOutcome.Unsupported => NeutralEvidenceState.Unknown,
                _ => NeutralEvidenceState.Verified
            };
        return new NeutralArtifactPreservation
        {
            SemanticIdentity = identity,
            Outcome = outcome,
            EvidenceState = evidence
        };
    }

    private static bool HasVideoPreservationEvidence(PreservationReport? report) =>
        FindPreservationStatus(report, "VideoStreams", "Timing") is
            PreservationCheckStatus.VerifiedPreserved or
            PreservationCheckStatus.SemanticallyPreserved or
            PreservationCheckStatus.NotApplicable;

    private static ConversionComponentOutcome ComponentOutcomeFromReport(
        PreservationReport? report,
        params string[] names)
    {
        PreservationCheckStatus? status = FindPreservationStatus(report, names);
        return status switch
        {
            PreservationCheckStatus.VerifiedPreserved or PreservationCheckStatus.SemanticallyPreserved =>
                ConversionComponentOutcome.Preserved,
            PreservationCheckStatus.NotApplicable => ConversionComponentOutcome.NotApplicable,
            PreservationCheckStatus.Failed or PreservationCheckStatus.IntentionallyRemovedProtocolData =>
                ConversionComponentOutcome.Lost,
            _ => ConversionComponentOutcome.NotEvaluated
        };
    }

    private static ConversionComponentOutcome ComponentOutcomeFromTruthOrReport(
        ConversionComponentOutcome? conversionOutcome,
        PreservationReport? report,
        params string[] names) =>
        conversionOutcome is { } outcome
            ? outcome
            : ComponentOutcomeFromReport(report, names);

    private static PreservationCheckStatus? FindPreservationStatus(
        PreservationReport? report,
        params string[] names)
    {
        if (report == null)
            return null;
        PreservationReportItem[] matches = report.Items.Where(item =>
            names.Contains(item.Name, StringComparer.Ordinal)).ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length == 1)
            return matches[0].Status;
        return matches.Any(item => item.Status == PreservationCheckStatus.Failed)
            ? PreservationCheckStatus.Failed
            : matches.All(item => item.Status == PreservationCheckStatus.NotApplicable)
                ? PreservationCheckStatus.NotApplicable
                : matches.All(item => item.Status is PreservationCheckStatus.VerifiedPreserved or PreservationCheckStatus.SemanticallyPreserved)
                    ? PreservationCheckStatus.VerifiedPreserved
                    : PreservationCheckStatus.UnableToVerify;
    }

    private static async Task VerifyAuxiliaryHandoffAsync(
        IReadOnlyList<AuxiliaryMediaDescriptor> descriptors,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var stableIdentities = new HashSet<string>(StringComparer.Ordinal);
        var sourceRelationships = new HashSet<(int SourceIndex, long Offset, long Length, string Semantic)>();

        foreach (AuxiliaryMediaDescriptor descriptor in descriptors)
        {
            if (string.IsNullOrWhiteSpace(descriptor.StableIdentity) ||
                !stableIdentities.Add(descriptor.StableIdentity))
            {
                throw new InvalidDataException(
                    $"Auxiliary handoff contains a missing or duplicate stable identity '{descriptor.StableIdentity}'.");
            }

            if (!sourceRelationships.Add((
                    descriptor.SourceIndex,
                    descriptor.SourceOffset,
                    descriptor.SourceLength,
                    descriptor.Semantic)))
            {
                throw new InvalidDataException(
                    $"Auxiliary handoff contains a duplicate source relationship for '{descriptor.StableIdentity}'.");
            }

            if (!IsValidSha256(descriptor.SourceSha256))
            {
                throw new InvalidDataException(
                    $"Auxiliary '{descriptor.StableIdentity}' is missing a valid Inspector source SHA-256.");
            }

            if (descriptor.Representation == AuxiliaryRepresentation.Materialized &&
                descriptor.MaterializedArtifact == null)
            {
                throw new InvalidDataException(
                    $"Materialized auxiliary '{descriptor.StableIdentity}' has no artifact in the neutral handoff.");
            }

            MediaArtifact? artifact = descriptor.MaterializedArtifact;
            if (artifact == null)
            {
                continue;
            }

            MediaArtifactKind expectedKind = descriptor.ArtifactRole == MediaArtifactKind.GainMap
                ? MediaArtifactKind.GainMap
                : MediaArtifactKind.AuxiliaryItem;
            if (artifact.Kind != expectedKind || !File.Exists(artifact.Path))
            {
                throw new InvalidDataException(
                    $"Auxiliary '{descriptor.StableIdentity}' has no valid materialized artifact.");
            }

            string actualSha = await workspace
                .ComputeFileSha256Async(artifact.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!IsValidSha256(artifact.Sha256) ||
                !string.Equals(actualSha, artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualSha, descriptor.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                (descriptor.SourceLength > 0 && new FileInfo(artifact.Path).Length != descriptor.SourceLength))
            {
                throw new InvalidDataException(
                    $"Materialized auxiliary '{descriptor.StableIdentity}' changed before neutral handoff.");
            }
        }
    }

    private static async Task<ImageHdrGainMapSourceBinding> CreateHdrGainMapBindingAsync(
        MediaArtifact sourceArtifact,
        SourceMediaFacts sourceFacts,
        IReadOnlyList<AuxiliaryMediaDescriptor> descriptors,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken,
        bool wasReassembledIntoPrimary,
        MediaArtifact? reassembledGainMapArtifact)
    {
        GainMapFacts gainMap = sourceFacts.GainMap
            ?? throw new InvalidDataException("GainMap binding was requested without inspected GainMap facts.");
        if (!gainMap.IsPresent || gainMap.AuxiliaryIndex >= sourceFacts.AuxiliaryItems.Count)
            throw new InvalidDataException("Inspected GainMap does not identify an in-range auxiliary graph entry.");

        AuxiliaryMediaFacts observed = sourceFacts.AuxiliaryItems[checked((int)gainMap.AuxiliaryIndex)];
        if (!observed.IsPresent || observed.ItemId == 0 || observed.ItemId != gainMap.ItemId ||
            observed.Container != gainMap.Container || observed.Representation != gainMap.Representation ||
            observed.Ownership != gainMap.Ownership ||
            !string.Equals(observed.Relationship, gainMap.Relationship, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(observed.StableIdentity) ||
            !string.Equals(observed.Semantic, "GainMap", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(observed.OwnerIdentity) ||
            observed.ByteLength <= 0 || observed.Sha256.Length != 64)
        {
            throw new InvalidDataException("Inspected GainMap identity, owner, relationship, representation, range, or content hash is incomplete or inconsistent.");
        }

        AuxiliaryMediaDescriptor[] matches = descriptors.Where(item =>
            item.ArtifactRole == MediaArtifactKind.GainMap &&
            string.Equals(item.StableIdentity, observed.StableIdentity, StringComparison.Ordinal) &&
            string.Equals(item.Semantic, observed.Semantic, StringComparison.Ordinal) &&
            string.Equals(item.OwnerIdentity, observed.OwnerIdentity, StringComparison.Ordinal) &&
            string.Equals(item.Relationship, observed.Relationship, StringComparison.Ordinal) &&
            item.ImageContainer == observed.Container &&
            item.Representation == observed.Representation &&
            item.Ownership == observed.Ownership &&
            item.SourceLength == observed.ByteLength &&
            string.Equals(item.SourceSha256, observed.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        // JPEG GainMaps can be extracted for cleaning and then reassembled as
        // one neutral JPEG. That creates a new primary owner identity while
        // preserving the exact separately verified GainMap bytes and stable
        // item identity. Permit only this explicit owner transition.
        AuxiliaryMediaDescriptor[] ownerTransitionCandidates = wasReassembledIntoPrimary
            ? descriptors.Where(item =>
                item.ArtifactRole == MediaArtifactKind.GainMap &&
                string.Equals(item.Semantic, observed.Semantic, StringComparison.Ordinal) &&
                string.Equals(item.Relationship, observed.Relationship, StringComparison.Ordinal) &&
                item.ImageContainer == observed.Container &&
                item.Representation == observed.Representation &&
                item.Ownership == observed.Ownership &&
                item.SourceLength == observed.ByteLength &&
                string.Equals(item.SourceSha256, observed.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        if (matches.Length != 1 && ownerTransitionCandidates.Length != 1)
        {
            string candidates = string.Join(" | ", descriptors
                .Where(item => item.ArtifactRole == MediaArtifactKind.GainMap ||
                    string.Equals(item.Semantic, "GainMap", StringComparison.Ordinal))
                .Select(item => $"id={item.StableIdentity},semantic={item.Semantic},owner={item.OwnerIdentity}," +
                    $"relationship={item.Relationship},length={item.SourceLength}," +
                    $"container={item.ImageContainer},representation={item.Representation},ownership={item.Ownership}"));
            throw new InvalidDataException(
                $"Neutral GainMap descriptor does not uniquely bind the inspected item identity and bytes " +
                $"(observed id={observed.StableIdentity},semantic={observed.Semantic},owner={observed.OwnerIdentity}," +
                $"relationship={observed.Relationship},length={observed.ByteLength}," +
                $"container={observed.Container},representation={observed.Representation},ownership={observed.Ownership}; " +
                $"candidates: {candidates}).");
        }

        AuxiliaryMediaDescriptor lineage = matches.Length == 1 ? matches[0] : ownerTransitionCandidates[0];

        if (matches.Length != 1)
        {
            if (reassembledGainMapArtifact == null)
                throw new InvalidDataException("Neutral GainMap owner changed without a verified reassembly artifact.");
            string reassembledSha = await workspace
                .ComputeFileSha256Async(reassembledGainMapArtifact.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(reassembledSha, observed.Sha256, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(reassembledGainMapArtifact.Sha256) &&
                 !string.Equals(reassembledSha, reassembledGainMapArtifact.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Reassembled GainMap bytes do not match the freshly inspected owner graph.");
            }
        }

        string sourceSha256 = await workspace.ComputeFileSha256Async(sourceArtifact.Path, cancellationToken).ConfigureAwait(false);
        if (sourceSha256.Length != 64 ||
            (!string.IsNullOrWhiteSpace(sourceArtifact.Sha256) &&
             !string.Equals(sourceArtifact.Sha256, sourceSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Cleaned GainMap source artifact changed or disagrees with inspected source identity.");
        }

        return new ImageHdrGainMapSourceBinding(
            sourceArtifact.Path,
            sourceSha256,
            sourceFacts.PrimarySha256,
            WindowsFileIdentity.Capture(sourceArtifact.Path),
            sourceFacts.PrimaryImage.Container,
            lineage.StableIdentity,
            observed.StableIdentity,
            observed.Semantic,
            lineage.OwnerIdentity,
            observed.OwnerIdentity,
            observed.Relationship,
            observed.Sha256,
            observed.ItemId,
            gainMap.AuxiliaryIndex,
            observed.ByteOffset,
            observed.ByteLength,
            lineage.SourceOffset,
            lineage.SourceLength,
            observed.Representation,
            observed.Ownership,
            gainMap.OwnerArtifactRole);
    }

    private static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        bool nonZero = false;
        foreach (char c in value)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
            if (c != '0') nonZero = true;
        }
        return nonZero;
    }

    private static AuxiliaryMediaDescriptor CreateInspectedGainMapDescriptor(
        SourceMediaFacts facts,
        PreservationOutcome preservationOutcome)
    {
        GainMapFacts gainMap = facts.GainMap
            ?? throw new InvalidDataException("Converted artifact inspection did not find its GainMap.");
        if (!gainMap.IsPresent || gainMap.AuxiliaryIndex >= facts.AuxiliaryItems.Count)
            throw new InvalidDataException("Converted artifact GainMap does not identify an in-range auxiliary entry.");

        AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[checked((int)gainMap.AuxiliaryIndex)];
        if (!auxiliary.IsPresent || auxiliary.ItemId != gainMap.ItemId ||
            auxiliary.Container != gainMap.Container || auxiliary.Representation != gainMap.Representation ||
            auxiliary.Ownership != gainMap.Ownership || auxiliary.ByteOffset != gainMap.ByteOffset ||
            auxiliary.ByteLength != gainMap.ByteLength ||
            !string.Equals(auxiliary.Semantic, "GainMap", StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Relationship, gainMap.Relationship, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(auxiliary.StableIdentity) ||
            string.IsNullOrWhiteSpace(auxiliary.OwnerIdentity) || !IsValidSha256(auxiliary.Sha256))
        {
            throw new InvalidDataException("Converted artifact GainMap identity, graph, range, or hash is incomplete or inconsistent.");
        }

        return new AuxiliaryMediaDescriptor
        {
            ArtifactRole = MediaArtifactKind.GainMap,
            StableIdentity = auxiliary.StableIdentity,
            Semantic = auxiliary.Semantic,
            OwnerIdentity = auxiliary.OwnerIdentity,
            Relationship = auxiliary.Relationship,
            SourceIndex = auxiliary.SourceIndex,
            SourceOffset = auxiliary.ByteOffset,
            SourceLength = auxiliary.ByteLength,
            SourceSha256 = auxiliary.Sha256,
            ImageContainer = auxiliary.Container,
            Codec = auxiliary.Codec,
            Representation = auxiliary.Representation,
            Ownership = auxiliary.Ownership,
            ItemType = auxiliary.ItemType,
            GraphComplete = auxiliary.GraphComplete,
            Dependencies = auxiliary.Dependencies,
            PreservationOutcome = preservationOutcome
        };
    }

    private static void VerifyPreservationCarrierHandoff(IReadOnlyList<PreservationCarrier> carriers)
    {
        var stableIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (PreservationCarrier carrier in carriers)
        {
            if (string.IsNullOrWhiteSpace(carrier.StableIdentity) ||
                !stableIdentities.Add(carrier.StableIdentity) ||
                carrier.Kind == PreservationCarrierKind.Unknown ||
                carrier.SourceIndex is < 0 or > 1 ||
                carrier.SourceOffset < 0 || carrier.SourceLength <= 0 ||
                !IsValidSha256(carrier.SourceSha256))
            {
                throw new InvalidDataException(
                    $"Preservation carrier '{carrier.StableIdentity}' is missing identity, range, or source SHA evidence.");
            }
        }
    }

    private static PreservationOutcome CombineOutcome(PreservationOutcome a, PreservationOutcome b)
    {
        if (a == PreservationOutcome.DegradedToSdr || b == PreservationOutcome.DegradedToSdr) return PreservationOutcome.DegradedToSdr;
        if (a == PreservationOutcome.PartiallyPreserved || b == PreservationOutcome.PartiallyPreserved) return PreservationOutcome.PartiallyPreserved;
        if (a == PreservationOutcome.DiscardedNotApplicable || b == PreservationOutcome.DiscardedNotApplicable) return PreservationOutcome.DiscardedNotApplicable;
        if (a == PreservationOutcome.Reencoded || b == PreservationOutcome.Reencoded) return PreservationOutcome.Reencoded;
        if (a == PreservationOutcome.TranscodedLossless || b == PreservationOutcome.TranscodedLossless) return PreservationOutcome.TranscodedLossless;
        return PreservationOutcome.Preserved;
    }

    private static async Task<MediaArtifact> ReassembleJpegGainMapAsync(
        MediaArtifact primaryImage,
        MediaArtifact gainMap,
        string? expectedGainMapSha256,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(primaryImage.Path))
            throw new FileNotFoundException("Cleaned primary image was not found.", primaryImage.Path);
        if (!File.Exists(gainMap.Path))
            throw new FileNotFoundException("Cleaned GainMap was not found.", gainMap.Path);
        if (string.IsNullOrWhiteSpace(expectedGainMapSha256))
            throw new InvalidDataException("Verified GainMap identity is required for final JPEG consumption.");

        string outputPath = workspace.AllocateFilePath("neutral-img-gainmap", ".jpg");
        
        await Interop.NativeMediaService.ReassembleJpegGainMapAsync(
            primaryImage.Path,
            gainMap.Path,
            outputPath,
            expectedGainMapSha256,
            cancellationToken).ConfigureAwait(false);

        return primaryImage with
        {
            Path = outputPath,
            ByteLength = new FileInfo(outputPath).Length,
            Sha256 = await workspace.ComputeFileSha256Async(outputPath, cancellationToken).ConfigureAwait(false),
            FileIdentity = WindowsFileIdentity.Capture(outputPath)
        };
    }
}

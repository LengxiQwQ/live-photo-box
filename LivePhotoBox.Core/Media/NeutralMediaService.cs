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

        // A Google/Vivo/Xiaomi Ultra HDR source carries the GainMap as a
        // second JPEG after the primary JPEG. Extraction keeps it separate so
        // the cleaner can remove only the motion-photo ranges. Restore the
        // standard single-file neutral JPEG before binding it for any R3
        // semantic conversion; Native binds both images and their exact
        // current byte ranges in that artifact.
        bool gainMapEmbeddedInPrimary = false;
        if (cleanResult.CleanedGainMap != null
            && finalImage.ImageContainer == ImageContainer.Jpeg)
        {
            finalImage = await ReassembleJpegGainMapAsync(
                finalImage, cleanResult.CleanedGainMap, cleanResult.GainMapExpectedSha256,
                workspace, cancellationToken)
                .ConfigureAwait(false);
            gainMapEmbeddedInPrimary = true;
        }

        // Bind R3 authority to the actual cleaned neutral artifact (not the
        // pre-clean source facts). This is also the identity handed to an
        // explicit cross-container semantic conversion below.
        SourceMediaFacts neutralInputFacts = await _inspector
            .InspectAsync(finalImage.Path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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

        // 4. Convert formats if requested
        if (requirement != null)
        {
            // Convert Image if target differs
            if (requirement.ImageContainer != ImageContainer.Unknown &&
                requirement.ImageContainer != finalImage.ImageContainer)
            {
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

            VideoFacts? resolvedVideoFacts = null;
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

        // Carry descriptors and opaque preservation evidence across the
        // Cleaner boundary.  A materialized GainMap is represented once by
        // the typed GainMap slot; other materialized auxiliary artifacts are
        // added to the manifest by stable identity below.
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

        // 5. Build Artifact Manifest with truthful outcomes and unambiguous GainMap ownership
        AuxiliaryMediaDescriptor? gainMapDescriptor = null;
        foreach (AuxiliaryMediaDescriptor descriptor in auxiliaryHandoff)
        {
            if (descriptor.ArtifactRole == MediaArtifactKind.GainMap ||
                string.Equals(descriptor.Semantic, "GainMap", StringComparison.Ordinal))
            {
                gainMapDescriptor = descriptor;
                break;
            }
        }

        GainMapRepresentation gainMapRep = GainMapRepresentation.None;
        if (cleanResult.CleanedGainMap != null)
        {
            gainMapRep = gainMapEmbeddedInPrimary ? GainMapRepresentation.Embedded : GainMapRepresentation.Detached;
        }
        else if (gainMapDescriptor is { Representation: AuxiliaryRepresentation.Embedded, Ownership: AuxiliaryOwnership.Primary })
        {
            // HEIF grid/derived GainMaps remain semantic members of the
            // primary container.  The descriptor is represented once in the
            // manifest, but never gets a detached pseudo-file.
            gainMapRep = GainMapRepresentation.Embedded;
        }

        var manifest = new List<NeutralArtifactManifest>
        {
            new NeutralArtifactManifest
            {
                Role = "PrimaryImage",
                Path = finalImage.Path,
                Sha256 = finalImage.Sha256 ?? await workspace.ComputeFileSha256Async(finalImage.Path, cancellationToken).ConfigureAwait(false),
                ByteLength = finalImage.ByteLength > 0 ? finalImage.ByteLength : new FileInfo(finalImage.Path).Length,
                ImageContainer = finalImage.ImageContainer,
                PreservationOutcome = imageOutcome,
                GainMapRepresentation = gainMapEmbeddedInPrimary ? GainMapRepresentation.Embedded : GainMapRepresentation.None
            }
        };

        if (finalVideo != null)
        {
            manifest.Add(new NeutralArtifactManifest
            {
                Role = "MotionVideo",
                Path = finalVideo.Path,
                Sha256 = finalVideo.Sha256 ?? await workspace.ComputeFileSha256Async(finalVideo.Path, cancellationToken).ConfigureAwait(false),
                ByteLength = finalVideo.ByteLength > 0 ? finalVideo.ByteLength : new FileInfo(finalVideo.Path).Length,
                VideoContainer = finalVideo.VideoContainer,
                VideoCodec = finalVideo.VideoCodec,
                PreservationOutcome = videoOutcome,
                GainMapRepresentation = GainMapRepresentation.None
            });
        }

        if (cleanResult.CleanedGainMap != null)
        {
            manifest.Add(new NeutralArtifactManifest
            {
                Role = "GainMap",
                Path = cleanResult.CleanedGainMap.Path,
                Sha256 = cleanResult.GainMapExpectedSha256
                    ?? throw new InvalidDataException("GainMap manifest identity is missing after verified consumption."),
                StableIdentity = gainMapDescriptor?.StableIdentity ?? "gainmap:typed",
                Semantic = gainMapDescriptor?.Semantic ?? "GainMap",
                OwnerIdentity = gainMapDescriptor?.OwnerIdentity ?? "primary:0",
                Relationship = gainMapDescriptor?.Relationship ?? "gain-map",
                Representation = gainMapDescriptor?.Representation ?? AuxiliaryRepresentation.Embedded,
                Ownership = gainMapDescriptor?.Ownership ?? AuxiliaryOwnership.Primary,
                SourceOffset = gainMapDescriptor?.SourceOffset ?? 0,
                SourceLength = gainMapDescriptor?.SourceLength ?? cleanResult.CleanedGainMap.ByteLength,
                SourceSha256 = gainMapDescriptor?.SourceSha256 ?? cleanResult.GainMapExpectedSha256 ?? string.Empty,
                ByteLength = cleanResult.CleanedGainMap.ByteLength > 0 ? cleanResult.CleanedGainMap.ByteLength : new FileInfo(cleanResult.CleanedGainMap.Path).Length,
                ImageContainer = cleanResult.CleanedGainMap.ImageContainer,
                PreservationOutcome = cleanResult.PreservationOutcome,
                GainMapRepresentation = gainMapEmbeddedInPrimary ? GainMapRepresentation.Embedded : GainMapRepresentation.Detached
            });
        }
        else if (gainMapDescriptor is { Representation: AuxiliaryRepresentation.Embedded, Ownership: AuxiliaryOwnership.Primary, MaterializedArtifact: null })
        {
            manifest.Add(new NeutralArtifactManifest
            {
                Role = "GainMap",
                Path = finalImage.Path,
                Sha256 = finalImage.Sha256 ?? await workspace.ComputeFileSha256Async(finalImage.Path, cancellationToken).ConfigureAwait(false),
                StableIdentity = gainMapDescriptor.StableIdentity,
                Semantic = gainMapDescriptor.Semantic,
                OwnerIdentity = gainMapDescriptor.OwnerIdentity,
                Relationship = gainMapDescriptor.Relationship,
                Representation = gainMapDescriptor.Representation,
                Ownership = gainMapDescriptor.Ownership,
                SourceOffset = gainMapDescriptor.SourceOffset,
                SourceLength = gainMapDescriptor.SourceLength,
                SourceSha256 = gainMapDescriptor.SourceSha256,
                ByteLength = finalImage.ByteLength > 0 ? finalImage.ByteLength : new FileInfo(finalImage.Path).Length,
                ImageContainer = finalImage.ImageContainer,
                PreservationOutcome = gainMapSemanticallyReencoded
                    ? imageOutcome
                    : gainMapDescriptor.PreservationOutcome,
                GainMapRepresentation = GainMapRepresentation.Embedded
            });
        }

        var materializedIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (AuxiliaryMediaDescriptor descriptor in auxiliaryHandoff)
        {
            MediaArtifact? artifact = descriptor.MaterializedArtifact;
            if (artifact == null)
            {
                continue;
            }

            if (!materializedIdentities.Add(descriptor.StableIdentity))
            {
                throw new InvalidDataException(
                    $"Auxiliary manifest would contain duplicate stable identity '{descriptor.StableIdentity}'.");
            }

            if (cleanResult.CleanedGainMap != null &&
                string.Equals(artifact.Path, cleanResult.CleanedGainMap.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            manifest.Add(new NeutralArtifactManifest
            {
                Role = descriptor.ArtifactRole.ToString(),
                Path = artifact.Path,
                Sha256 = artifact.Sha256 ?? await workspace.ComputeFileSha256Async(artifact.Path, cancellationToken).ConfigureAwait(false),
                StableIdentity = descriptor.StableIdentity,
                Semantic = descriptor.Semantic,
                OwnerIdentity = descriptor.OwnerIdentity,
                Relationship = descriptor.Relationship,
                Representation = descriptor.Representation,
                Ownership = descriptor.Ownership,
                SourceOffset = descriptor.SourceOffset,
                SourceLength = descriptor.SourceLength,
                SourceSha256 = descriptor.SourceSha256,
                ByteLength = artifact.ByteLength > 0 ? artifact.ByteLength : new FileInfo(artifact.Path).Length,
                ImageContainer = artifact.ImageContainer,
                PreservationOutcome = descriptor.PreservationOutcome,
                GainMapRepresentation = GainMapRepresentation.None
            });
        }

        return new NeutralMediaBundle
        {
            PrimaryImage = finalImage,
            MotionVideo = finalVideo,
            GainMap = cleanResult.CleanedGainMap,
            HdrGainMapBinding = hdrGainMapBinding,
            GainMapRepresentation = gainMapRep,
            SourceProvenance = facts,
            RemovedProtocolFacts = [.. extracted.ExtractedProtocolFacts, .. cleanResult.RemovedFacts],
            Manifest = manifest,
            AuxiliaryMedia = auxiliaryHandoff,
            PreservationCarriers = preservationCarriers,
            Timing = facts.Timing
        };
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

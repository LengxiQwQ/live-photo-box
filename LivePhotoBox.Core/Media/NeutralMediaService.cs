using System;
using System.Collections.Generic;
using System.IO;
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
        // the cleaner can remove only the motion-photo ranges. Before the
        // a JPEG neutral artifact leaves this workspace, restore that
        // standard representation; otherwise the retained hdrgm/Container
        // metadata would point at bytes that are no longer in the artifact.
        bool gainMapEmbeddedInPrimary = false;
        if (cleanResult.CleanedGainMap != null
            && finalImage.ImageContainer == ImageContainer.Jpeg
            && (requirement == null || requirement.ImageContainer != ImageContainer.Heic))
        {
            finalImage = await ReassembleJpegGainMapAsync(
                finalImage, cleanResult.CleanedGainMap, cleanResult.GainMapExpectedSha256,
                workspace, cancellationToken)
                .ConfigureAwait(false);
            gainMapEmbeddedInPrimary = true;
        }

        PreservationOutcome imageOutcome = cleanResult.PreservationOutcome;
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
                    PreservationPolicy = preservationPolicy
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
            }

            // Convert Video if video exists and target differs
            if (finalVideo != null && (
                (requirement.VideoContainer != VideoContainer.Unknown && requirement.VideoContainer != finalVideo.VideoContainer) ||
                (requirement.VideoCodec != VideoCodec.Copy && requirement.VideoCodec != VideoCodec.Unknown && requirement.VideoCodec != finalVideo.VideoCodec)))
            {
                var vidConv = await _videoConverter.ConvertAsync(new VideoConversionRequest
                {
                    SourceArtifact = finalVideo,
                    TargetContainer = requirement.VideoContainer != VideoContainer.Unknown ? requirement.VideoContainer : finalVideo.VideoContainer,
                    TargetCodec = requirement.VideoCodec != VideoCodec.Unknown ? requirement.VideoCodec : VideoCodec.Copy,
                    TargetDirectory = workspace.RootDirectory,
                    TargetFps = requirement.TargetFps ?? 0
                }, cancellationToken).ConfigureAwait(false);

                if (!vidConv.Success || vidConv.OutputArtifact == null)
                {
                    throw new InvalidOperationException($"Video conversion failed in neutral pipeline: {vidConv.ErrorMessage}");
                }

                finalVideo = vidConv.OutputArtifact;
                PreservationOutcome convOutcome = (vidConv.ExecutionRecord.AudioPreserved && vidConv.ExecutionRecord.RotationPreserved)
                    ? (vidConv.ExecutionRecord.RemuxUsed ? PreservationOutcome.Preserved : PreservationOutcome.Reencoded)
                    : PreservationOutcome.PartiallyPreserved;

                videoOutcome = CombineOutcome(videoOutcome, convOutcome);
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
        var auxiliaryHandoff = new List<AuxiliaryMediaDescriptor>(
            cleanResult.AuxiliaryMedia.Count > 0 ? cleanResult.AuxiliaryMedia : extracted.AuxiliaryMedia);
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
                PreservationOutcome = gainMapDescriptor.PreservationOutcome,
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
            Sha256 = await workspace.ComputeFileSha256Async(outputPath, cancellationToken).ConfigureAwait(false)
        };
    }
}

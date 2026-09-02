using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        SourceMediaFacts facts = await _inspector.InspectAsync(primaryPath, secondaryPath, cancellationToken).ConfigureAwait(false);

        // 2. Extract
        ExtractedMediaBundle extracted = await _extractor.ExtractAsync(facts, primaryPath, secondaryPath, workspace, cancellationToken).ConfigureAwait(false);

        // 3. Clean source Live/Motion Photo protocol
        ProtocolCleanResult cleanResult = await _cleaner.CleanAsync(new ProtocolCleanRequest
        {
            SourceFacts = facts,
            ExtractedBundle = extracted,
            PreservationPolicy = preservationPolicy
        }, workspace, cancellationToken).ConfigureAwait(false);

        if (!cleanResult.Success || cleanResult.CleanedImage == null)
        {
            throw new InvalidOperationException($"Failed to clean source protocol: {cleanResult.ErrorMessage}");
        }

        MediaArtifact finalImage = cleanResult.CleanedImage;
        MediaArtifact? finalVideo = cleanResult.CleanedVideo;

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
                    ? (vidConv.ExecutionRecord.RemuxUsed ? PreservationOutcome.Preserved : PreservationOutcome.TranscodedLossless)
                    : PreservationOutcome.PartiallyPreserved;

                videoOutcome = CombineOutcome(videoOutcome, convOutcome);
            }

            if (preservationPolicy == PreservationPolicy.Strict &&
                videoOutcome != PreservationOutcome.Preserved)
            {
                throw new InvalidOperationException("Strict preservation policy failed during video cleaning or conversion.");
            }
        }

        // 5. Build Artifact Manifest with truthful outcomes
        var manifest = new List<NeutralArtifactManifest>
        {
            new NeutralArtifactManifest
            {
                Role = "PrimaryImage",
                Path = finalImage.Path,
                Sha256 = finalImage.Sha256 ?? await workspace.ComputeFileSha256Async(finalImage.Path, cancellationToken).ConfigureAwait(false),
                ByteLength = finalImage.ByteLength > 0 ? finalImage.ByteLength : new FileInfo(finalImage.Path).Length,
                ImageContainer = finalImage.ImageContainer,
                PreservationOutcome = imageOutcome
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
                PreservationOutcome = videoOutcome
            });
        }

        if (cleanResult.CleanedGainMap != null)
        {
            manifest.Add(new NeutralArtifactManifest
            {
                Role = "GainMap",
                Path = cleanResult.CleanedGainMap.Path,
                Sha256 = cleanResult.CleanedGainMap.Sha256 ?? await workspace.ComputeFileSha256Async(cleanResult.CleanedGainMap.Path, cancellationToken).ConfigureAwait(false),
                ByteLength = cleanResult.CleanedGainMap.ByteLength > 0 ? cleanResult.CleanedGainMap.ByteLength : new FileInfo(cleanResult.CleanedGainMap.Path).Length,
                ImageContainer = cleanResult.CleanedGainMap.ImageContainer,
                PreservationOutcome = cleanResult.PreservationOutcome
            });
        }

        return new NeutralMediaBundle
        {
            PrimaryImage = finalImage,
            MotionVideo = finalVideo,
            GainMap = cleanResult.CleanedGainMap,
            SourceProvenance = facts,
            RemovedProtocolFacts = [.. extracted.ExtractedProtocolFacts, .. cleanResult.RemovedFacts],
            Manifest = manifest,
            Timing = facts.Timing
        };
    }

    private static PreservationOutcome CombineOutcome(PreservationOutcome a, PreservationOutcome b)
    {
        if (a == PreservationOutcome.DegradedToSdr || b == PreservationOutcome.DegradedToSdr) return PreservationOutcome.DegradedToSdr;
        if (a == PreservationOutcome.PartiallyPreserved || b == PreservationOutcome.PartiallyPreserved) return PreservationOutcome.PartiallyPreserved;
        if (a == PreservationOutcome.DiscardedNotApplicable || b == PreservationOutcome.DiscardedNotApplicable) return PreservationOutcome.DiscardedNotApplicable;
        if (a == PreservationOutcome.TranscodedLossless || b == PreservationOutcome.TranscodedLossless) return PreservationOutcome.TranscodedLossless;
        return PreservationOutcome.Preserved;
    }
}

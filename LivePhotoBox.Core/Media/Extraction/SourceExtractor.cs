using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Thin control plane wrapper that orchestrates workspace paths and delegates byte extraction to LivePhotoBox.Native.
/// </summary>
public sealed class SourceExtractor : ISourceExtractor
{
    public Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        return ExtractAsync(facts, primaryPath, secondaryPath, workspace, configureContext: null, cancellationToken);
    }

    internal async Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        Action<NativeContext>? configureContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(workspace);

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(primaryPath))
            throw new FileNotFoundException("Primary media file not found.", primaryPath);

        long primaryFileLength = new FileInfo(primaryPath).Length;

        // 1. Validate Primary Image facts & range
        if (!facts.PrimaryImage.IsPresent)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary image must be present in source facts.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        if (facts.PrimaryImage.Container == ImageContainer.Unknown)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.UnsupportedLayout,
                "Primary image container format is unknown or unsupported.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        ValidateRange(facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength, primaryFileLength, "primary image", primaryPath, MediaArtifactKind.PrimaryImage);

        // 2. Validate Motion Video facts & range
        string? videoSource = null;
        if (facts.MotionVideo is { IsPresent: true } videoFacts)
        {
            if (videoFacts.Container == VideoContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "Motion video container format is unknown or unsupported.",
                    artifactKind: MediaArtifactKind.MotionVideo);
            }

            if (videoFacts.SourceIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(secondaryPath) || !File.Exists(secondaryPath))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.InvalidFacts,
                        "Motion video specifies secondary source, but secondary file does not exist.",
                        artifactKind: MediaArtifactKind.MotionVideo,
                        sourcePath: secondaryPath);
                }
                videoSource = secondaryPath;
            }
            else if (videoFacts.SourceIndex == 0)
            {
                videoSource = primaryPath;
            }
            else
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Motion video specifies invalid SourceIndex: {videoFacts.SourceIndex}.",
                    artifactKind: MediaArtifactKind.MotionVideo);
            }

            long videoSourceLength = new FileInfo(videoSource).Length;
            ValidateRange(videoFacts.ByteOffset, videoFacts.ByteLength, videoSourceLength, "motion video", videoSource, MediaArtifactKind.MotionVideo);
        }

        // 3. Validate GainMap facts & range
        if (facts.GainMap is { IsPresent: true } gainMapFacts)
        {
            if (gainMapFacts.Container == ImageContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "GainMap container format is unknown or unsupported.",
                    artifactKind: MediaArtifactKind.GainMap,
                    sourcePath: primaryPath);
            }

            ValidateRange(gainMapFacts.ByteOffset, gainMapFacts.ByteLength, primaryFileLength, "GainMap", primaryPath, MediaArtifactKind.GainMap);
        }

        if (facts.ProtocolTailLength > 0)
        {
            ValidateRange(facts.ProtocolTailOffset, facts.ProtocolTailLength, primaryFileLength, "protocol trailer", primaryPath, null);
        }

        if (string.IsNullOrWhiteSpace(facts.PrimarySha256))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 is required and cannot be empty.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        byte[] primShaBytes;
        try
        {
            primShaBytes = Convert.FromHexString(facts.PrimarySha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 is malformed: '{facts.PrimarySha256}'.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath,
                innerException: ex);
        }

        if (primShaBytes.Length != 32)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {primShaBytes.Length} bytes.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        bool isAllZero = true;
        for (int i = 0; i < 32; i++)
        {
            if (primShaBytes[i] != 0) { isAllZero = false; break; }
        }
        if (isAllZero)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 cannot be all zeroes.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        string beforePrimarySha = await workspace.ComputeFileSha256Async(primaryPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(facts.PrimarySha256.Trim(), beforePrimarySha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.SourceChanged,
                $"Source media file '{primaryPath}' was modified after inspection: SHA-256 mismatch.",
                sourcePath: primaryPath);
        }

        string? beforeSecondarySha = null;
        if (videoSource == secondaryPath && secondaryPath != null)
        {
            if (string.IsNullOrWhiteSpace(facts.SecondarySha256))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    "Secondary source snapshot SHA-256 is required when motion video resides in secondary source.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            byte[] secShaBytes;
            try
            {
                secShaBytes = Convert.FromHexString(facts.SecondarySha256.Trim());
            }
            catch (FormatException ex)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 is malformed: '{facts.SecondarySha256}'.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath,
                    innerException: ex);
            }

            if (secShaBytes.Length != 32)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {secShaBytes.Length} bytes.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            bool secIsAllZero = true;
            for (int i = 0; i < 32; i++)
            {
                if (secShaBytes[i] != 0) { secIsAllZero = false; break; }
            }
            if (secIsAllZero)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    "Secondary source snapshot SHA-256 cannot be all zeroes.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            beforeSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(facts.SecondarySha256.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(facts.SecondarySha256) && secondaryPath != null && File.Exists(secondaryPath))
        {
            beforeSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(facts.SecondarySha256.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath);
            }
        }

        string imgExt = facts.PrimaryImage.Container == ImageContainer.Heic ? ".heic" : ".jpg";
        string outputImagePath = workspace.AllocateFilePath("primary", imgExt);
        if (File.Exists(outputImagePath))
            throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputImagePath}");

        string? outputVideoPath = null;
        if (facts.MotionVideo != null && facts.MotionVideo.IsPresent)
        {
            string vidExt = facts.MotionVideo.Container == VideoContainer.Mov ? ".mov" : ".mp4";
            outputVideoPath = workspace.AllocateFilePath("motion", vidExt);
            if (File.Exists(outputVideoPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputVideoPath}");
        }

        string? outputGainmapPath = null;
        if (facts.GainMap != null && facts.GainMap.IsPresent)
        {
            string gmExt = facts.GainMap.Container == ImageContainer.Heic ? ".heic" : ".jpg";
            outputGainmapPath = workspace.AllocateFilePath("gainmap", gmExt);
            if (File.Exists(outputGainmapPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputGainmapPath}");
        }

        try
        {
            await NativeMediaService.ExtractMediaAsync(
                primaryPath,
                secondaryPath,
                facts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath,
                configureContext,
                cancellationToken).ConfigureAwait(false);

            if (!File.Exists(outputImagePath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a primary image artifact.", MediaArtifactKind.PrimaryImage);
            if (outputVideoPath != null && !File.Exists(outputVideoPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a motion video artifact.", MediaArtifactKind.MotionVideo);
            if (outputGainmapPath != null && !File.Exists(outputGainmapPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a GainMap artifact.", MediaArtifactKind.GainMap);

            // Verify that source files were not modified in-place
            string afterPrimarySha = await workspace.ComputeFileSha256Async(primaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(beforePrimarySha, afterPrimarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Source file immutability violation: primary source '{primaryPath}' was modified during extraction!",
                    sourcePath: primaryPath);
            }

            if (secondaryPath != null && beforeSecondarySha != null)
            {
                string afterSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(beforeSecondarySha, afterSecondarySha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.SourceChanged,
                        $"Source file immutability violation: secondary source '{secondaryPath}' was modified during extraction!",
                        sourcePath: secondaryPath);
                }
            }

            var primaryArtifact = new MediaArtifact
            {
                Path = outputImagePath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outputImagePath).Length,
                SourceOffset = facts.PrimaryImage.ByteOffset,
                Sha256 = await workspace.ComputeFileSha256Async(outputImagePath, cancellationToken).ConfigureAwait(false)
            };

            MediaArtifact? videoArtifact = null;
            if (outputVideoPath != null && File.Exists(outputVideoPath))
            {
                videoArtifact = new MediaArtifact
                {
                    Path = outputVideoPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = facts.MotionVideo!.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = facts.MotionVideo.Container,
                    VideoCodec = facts.MotionVideo.Codec,
                    ByteLength = new FileInfo(outputVideoPath).Length,
                    SourceOffset = facts.MotionVideo.ByteOffset,
                    Sha256 = await workspace.ComputeFileSha256Async(outputVideoPath, cancellationToken).ConfigureAwait(false)
                };
            }

            MediaArtifact? gainmapArtifact = null;
            if (outputGainmapPath != null && File.Exists(outputGainmapPath))
            {
                gainmapArtifact = new MediaArtifact
                {
                    Path = outputGainmapPath,
                    Kind = MediaArtifactKind.GainMap,
                    MimeType = facts.GainMap!.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                    ImageContainer = facts.GainMap.Container,
                    ImageCodec = facts.GainMap.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteLength = new FileInfo(outputGainmapPath).Length,
                    SourceOffset = facts.GainMap.ByteOffset,
                    Sha256 = await workspace.ComputeFileSha256Async(outputGainmapPath, cancellationToken).ConfigureAwait(false)
                };
            }

            var extractedFacts = new List<RemovedProtocolFact>();
            if (facts.MotionVideo is { IsPresent: true, SourceIndex: 0 })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded motion video",
                    Description = "Materialized from the Inspector-validated source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.GainMap is { IsPresent: true })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded GainMap",
                    Description = "Materialized from the Inspector-validated source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.ProtocolTailLength > 0)
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Protocol trailer",
                    Description = "Excluded from the primary artifact using the Inspector-validated range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }

            return new ExtractedMediaBundle
            {
                PrimaryImage = primaryArtifact,
                MotionVideo = videoArtifact,
                GainMap = gainmapArtifact,
                SourceFacts = facts,
                ExtractedProtocolFacts = extractedFacts
            };
        }
        catch (Exception ex)
        {
            // If Native failed, Native already rolled back its staged/published outputs.
            // If post-Native validation in C# failed, delete published outputs and surface CleanupFailed if deletion fails.
            string? cleanupFailedFile = null;
            Exception? cleanupError = null;

            if (File.Exists(outputImagePath))
            {
                try { File.Delete(outputImagePath); }
                catch (Exception delEx) { cleanupFailedFile ??= outputImagePath; cleanupError ??= delEx; }
            }
            if (outputVideoPath != null && File.Exists(outputVideoPath))
            {
                try { File.Delete(outputVideoPath); }
                catch (Exception delEx) { cleanupFailedFile ??= outputVideoPath; cleanupError ??= delEx; }
            }
            if (outputGainmapPath != null && File.Exists(outputGainmapPath))
            {
                try { File.Delete(outputGainmapPath); }
                catch (Exception delEx) { cleanupFailedFile ??= outputGainmapPath; cleanupError ??= delEx; }
            }

            if (cleanupFailedFile != null)
            {
                ExtractionFailureCategory origCat = (ex is ExtractionException ee) ? ee.Category : ExtractionFailureCategory.InternalError;
                throw new ExtractionException(
                    ExtractionFailureCategory.CleanupFailed,
                    $"Post-extraction cleanup failed: unable to delete artifact '{cleanupFailedFile}'. Original error: {ex.Message}",
                    innerException: cleanupError,
                    originalCategory: origCat);
            }

            throw;
        }
    }

    private static void ValidateRange(
        long offset,
        long length,
        long sourceLength,
        string name,
        string? sourcePath,
        MediaArtifactKind? kind)
    {
        if (offset < 0 || length <= 0 || offset > sourceLength || length > sourceLength - offset)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Inspector returned an invalid {name} range: offset={offset}, length={length}, sourceLength={sourceLength}.",
                artifactKind: kind,
                sourcePath: sourcePath,
                offset: offset,
                length: length);
        }
    }
}

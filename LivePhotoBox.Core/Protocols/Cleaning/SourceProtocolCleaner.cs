using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Control plane service that orchestrates workspace files and delegates source Live/Motion Photo
/// protocol stripping to LivePhotoBox.Native.
/// </summary>
public sealed class SourceProtocolCleaner : ISourceProtocolCleaner
{
    public async Task<ProtocolCleanResult> CleanAsync(
        ProtocolCleanRequest request,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        string imgExt = request.ExtractedBundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
        string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);

        string? cleanVidPath = null;
        if (request.ExtractedBundle.MotionVideo != null)
        {
            if (!File.Exists(request.ExtractedBundle.MotionVideo.Path))
            {
                sw.Stop();
                return new ProtocolCleanResult
                {
                    Success = false,
                    ErrorMessage = "Extracted motion video is missing.",
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Duration = sw.Elapsed
                };
            }
            string vidExt = request.ExtractedBundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
        }

        try
        {
            var removedFacts = await NativeCleanService.CleanSourceProtocolAsync(
                request.SourceFacts,
                request.ExtractedBundle.PrimaryImage.Path,
                request.ExtractedBundle.MotionVideo?.Path,
                cleanImgPath,
                cleanVidPath,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            if (!File.Exists(cleanImgPath))
            {
                throw new FileNotFoundException("Cleaned output image was not generated.", cleanImgPath);
            }
            if (cleanVidPath != null && !File.Exists(cleanVidPath))
            {
                throw new FileNotFoundException("Cleaned output video was not generated.", cleanVidPath);
            }

            var cleanImgArtifact = new MediaArtifact
            {
                Path = cleanImgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = request.ExtractedBundle.PrimaryImage.MimeType,
                ImageContainer = request.ExtractedBundle.PrimaryImage.ImageContainer,
                ImageCodec = request.ExtractedBundle.PrimaryImage.ImageCodec,
                ByteLength = new FileInfo(cleanImgPath).Length,
                Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false)
            };

            MediaArtifact? cleanVidArtifact = null;
            if (cleanVidPath != null && File.Exists(cleanVidPath))
            {
                cleanVidArtifact = new MediaArtifact
                {
                    Path = cleanVidPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = request.ExtractedBundle.MotionVideo!.MimeType,
                    VideoContainer = request.ExtractedBundle.MotionVideo.VideoContainer,
                    VideoCodec = request.ExtractedBundle.MotionVideo.VideoCodec,
                    ByteLength = new FileInfo(cleanVidPath).Length,
                    Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false)
                };
            }

            // Preservation outcome
            // Cleaning does not discard the extracted GainMap; the artifact is
            // carried through unchanged, so reporting it as discarded would
            // be false even when AllowDiscard was requested for later format
            // conversion.
            PreservationOutcome outcome = PreservationOutcome.Preserved;

            return new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = cleanImgArtifact,
                CleanedVideo = cleanVidArtifact,
                CleanedGainMap = request.ExtractedBundle.GainMap,
                RemovedFacts = removedFacts,
                PreservationOutcome = outcome,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProtocolCleanResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                Duration = sw.Elapsed
            };
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Thin control plane wrapper that orchestrates workspace paths and delegates byte extraction to LivePhotoBox.Native.
/// </summary>
public sealed class SourceExtractor : ISourceExtractor
{
    public async Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(workspace);

        string beforePrimarySha = await workspace.ComputeFileSha256Async(primaryPath, cancellationToken).ConfigureAwait(false);
        string? beforeSecondarySha = (secondaryPath != null && File.Exists(secondaryPath))
            ? await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false)
            : null;

        string imgExt = facts.PrimaryImage.Container == ImageContainer.Heic ? ".heic" : ".jpg";
        string outputImagePath = workspace.AllocateFilePath("primary", imgExt);

        string? outputVideoPath = null;
        if (facts.MotionVideo != null && facts.MotionVideo.IsPresent)
        {
            string vidExt = facts.MotionVideo.Container == VideoContainer.Mov ? ".mov" : ".mp4";
            outputVideoPath = workspace.AllocateFilePath("motion", vidExt);
        }

        string? outputGainmapPath = null;
        if (facts.GainMap != null && facts.GainMap.IsPresent)
        {
            outputGainmapPath = workspace.AllocateFilePath("gainmap", ".jpg");
        }

        await NativeMediaService.ExtractMediaAsync(
            primaryPath,
            secondaryPath,
            facts,
            outputImagePath,
            outputVideoPath,
            outputGainmapPath,
            cancellationToken).ConfigureAwait(false);

        // Verify that source files were not modified in-place
        await workspace.AssertSourceUnmodifiedAsync(primaryPath, beforePrimarySha, cancellationToken).ConfigureAwait(false);
        if (secondaryPath != null && beforeSecondarySha != null)
        {
            await workspace.AssertSourceUnmodifiedAsync(secondaryPath, beforeSecondarySha, cancellationToken).ConfigureAwait(false);
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
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(outputGainmapPath).Length,
                SourceOffset = facts.GainMap!.ByteOffset,
                Sha256 = await workspace.ComputeFileSha256Async(outputGainmapPath, cancellationToken).ConfigureAwait(false)
            };
        }

        return new ExtractedMediaBundle
        {
            PrimaryImage = primaryArtifact,
            MotionVideo = videoArtifact,
            GainMap = gainmapArtifact,
            SourceFacts = facts
        };
    }
}

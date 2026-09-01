using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// Thin control plane wrapper that delegates image conversions to LivePhotoBox.Native.
/// </summary>
public sealed class ImageConverter : IImageConverter
{
    public async Task<ImageConversionResult> ConvertAsync(
        ImageConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        string ext = request.TargetContainer == ImageContainer.Heic ? ".heic" : ".jpg";
        string outPath = Path.Combine(request.TargetDirectory, $"img-conv-{Guid.NewGuid():N}{ext}");

        try
        {
            bool reencoded = await NativeMediaService.ConvertImageAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.Quality,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = request.TargetContainer == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = request.TargetContainer,
                ImageCodec = request.TargetContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outPath).Length
            };

            return new ImageConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = reencoded,
                    MetadataCopied = true,
                    PreservationOutcome = PreservationOutcome.Preserved,
                    Duration = sw.Elapsed
                }
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.DiscardedNotApplicable,
                    Duration = sw.Elapsed
                }
            };
        }
    }
}

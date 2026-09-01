using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Video;

/// <summary>
/// Thin control plane wrapper that delegates video probing, stream remuxing, and transcoding to LivePhotoBox.Native.
/// </summary>
public sealed class VideoConverter : IVideoConverter
{
    public Task<VideoFacts> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        return NativeMediaService.ProbeVideoAsync(videoPath, cancellationToken);
    }

    public async Task<VideoConversionResult> ConvertAsync(
        VideoConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        string ext = request.TargetContainer == VideoContainer.Mov ? ".mov" : ".mp4";
        string outPath = Path.Combine(request.TargetDirectory, $"vid-conv-{Guid.NewGuid():N}{ext}");

        try
        {
            string encoderUsed = await NativeMediaService.TranscodeVideoAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.TargetCodec,
                request.Crf,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            bool remuxUsed = encoderUsed.Contains("StreamCopy", StringComparison.OrdinalIgnoreCase) ||
                             encoderUsed.Contains("Remux", StringComparison.OrdinalIgnoreCase);

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = request.TargetContainer == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = request.TargetContainer,
                VideoCodec = request.TargetCodec == VideoCodec.Copy ? request.SourceArtifact.VideoCodec : request.TargetCodec,
                ByteLength = new FileInfo(outPath).Length
            };

            return new VideoConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = remuxUsed,
                    SelectedEncoder = encoderUsed,
                    HardwareFallbackOccurred = false,
                    Duration = sw.Elapsed
                }
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    Duration = sw.Elapsed
                }
            };
        }
    }
}

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

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        // TargetFps handling
        if (request.TargetFps > 0)
        {
            sw.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = "Custom TargetFps is not supported in the current media pipeline stage.",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    InputCodec = request.SourceArtifact.VideoCodec,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Duration = sw.Elapsed
                }
            };
        }

        string ext = request.TargetContainer == VideoContainer.Mov ? ".mov" : ".mp4";
        string outPath = Path.Combine(request.TargetDirectory, $"vid-conv-{Guid.NewGuid():N}{ext}");

        VideoFacts? sourceFacts = null;
        try
        {
            sourceFacts = await ProbeAsync(request.SourceArtifact.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = $"Source video probe failed: {ex.Message}",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = VideoContainer.Unknown,
                    InputCodec = VideoCodec.Unknown,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = VideoContainer.Unknown,
                    OutputCodec = VideoCodec.Unknown,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Duration = sw.Elapsed
                }
            };
        }

        try
        {
            // Transcode or Remux via Native
            string encoderUsed = await NativeMediaService.TranscodeVideoAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.TargetCodec,
                request.Crf,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            if (!File.Exists(outPath))
            {
                throw new FileNotFoundException("Converted output video was not found.", outPath);
            }

            // Re-probe actual output file to validate facts
            VideoFacts probed = await ProbeAsync(outPath, cancellationToken).ConfigureAwait(false);

            if (request.TargetContainer != VideoContainer.Unknown && probed.Container != request.TargetContainer)
            {
                throw new InvalidOperationException(
                    $"Output container mismatch: expected {request.TargetContainer}, actual {probed.Container}.");
            }

            if (request.TargetCodec != VideoCodec.Copy && request.TargetCodec != VideoCodec.Unknown && probed.Codec != request.TargetCodec)
            {
                throw new InvalidOperationException(
                    $"Output codec mismatch: expected {request.TargetCodec}, actual {probed.Codec}.");
            }

            if (probed.DurationSeconds <= 0)
            {
                throw new InvalidOperationException("Output video duration could not be determined or is zero.");
            }

            // Duration tolerance check: catch noticeable truncations
            if (sourceFacts.DurationSeconds > 0)
            {
                double durationTolerance = Math.Max(0.5, sourceFacts.DurationSeconds * 0.15);
                if (Math.Abs(probed.DurationSeconds - sourceFacts.DurationSeconds) > durationTolerance)
                {
                    throw new InvalidOperationException(
                        $"Output video duration discrepancy/truncation detected: expected ~{sourceFacts.DurationSeconds:F2}s, actual {probed.DurationSeconds:F2}s.");
                }
            }

            bool remuxUsed = encoderUsed.Contains("Stream", StringComparison.OrdinalIgnoreCase) ||
                             encoderUsed.Contains("Remux", StringComparison.OrdinalIgnoreCase);

            bool audioPreserved = sourceFacts.HasAudio ? probed.HasAudio : true;

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = probed.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = probed.Container,
                VideoCodec = probed.Codec,
                ByteLength = new FileInfo(outPath).Length
            };

            return new VideoConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = sourceFacts.Container,
                    InputCodec = sourceFacts.Codec,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = probed.Container,
                    OutputCodec = probed.Codec,
                    RemuxUsed = remuxUsed,
                    SelectedEncoder = encoderUsed,
                    HardwareFallbackOccurred = encoderUsed.Contains("Software", StringComparison.OrdinalIgnoreCase),
                    AudioPreserved = audioPreserved,
                    RotationPreserved = (sourceFacts.RotationDegrees == probed.RotationDegrees),
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            if (File.Exists(outPath))
            {
                try { File.Delete(outPath); } catch { /* ignore cleanup errors */ }
            }
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (File.Exists(outPath))
            {
                try { File.Delete(outPath); } catch { /* ignore cleanup errors */ }
            }

            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = sourceFacts?.Container ?? VideoContainer.Unknown,
                    InputCodec = sourceFacts?.Codec ?? VideoCodec.Unknown,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Duration = sw.Elapsed
                }
            };
        }
    }
}

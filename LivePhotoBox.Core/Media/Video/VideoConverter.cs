using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Video;

/// <summary>
/// General-purpose video converter prioritizing stream remuxing before falling back to
/// hardware/software transcoding. Zero vendor-specific target branching.
/// </summary>
public sealed class VideoConverter : IVideoConverter
{
    public async Task<VideoConversionResult> ConvertAsync(
        VideoConversionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceArtifact);

        var stopwatch = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        string sourcePath = request.SourceArtifact.Path;
        if (!File.Exists(sourcePath))
        {
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = $"Source video file not found: '{sourcePath}'",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    OutputContainer = request.TargetContainer,
                    InputCodec = request.SourceArtifact.VideoCodec,
                    RequestedCodec = request.TargetCodec,
                    OutputCodec = VideoCodec.Unknown,
                    Duration = stopwatch.Elapsed
                }
            };
        }

        VideoContainer inputContainer = request.SourceArtifact.VideoContainer != VideoContainer.Unknown
            ? request.SourceArtifact.VideoContainer
            : FormatInspector.DetectVideoContainer(sourcePath);

        VideoCodec inputCodec = request.SourceArtifact.VideoCodec;
        if (inputCodec == VideoCodec.Unknown)
        {
            var probedFacts = await FormatInspector.ProbeVideoFactsAsync(sourcePath, 0, new FileInfo(sourcePath).Length, ct).ConfigureAwait(false);
            if (probedFacts != null) inputCodec = probedFacts.Codec;
        }

        string targetDirectory = request.TargetDirectory;
        Directory.CreateDirectory(targetDirectory);

        string ext = request.TargetContainer == VideoContainer.Mov ? "mov" : "mp4";
        string targetFileName = request.OutputFileName ??
            $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.{ext}";
        string targetPath = Path.Combine(targetDirectory, targetFileName);

        bool needsTranscode = request.TargetCodec switch
        {
            VideoCodec.Copy => false,
            VideoCodec.H264 => inputCodec != VideoCodec.H264,
            VideoCodec.Hevc => inputCodec != VideoCodec.Hevc,
            _ => false
        };

        VideoCodec effectiveTargetCodec = request.TargetCodec switch
        {
            VideoCodec.Copy => inputCodec != VideoCodec.Unknown ? inputCodec : VideoCodec.H264,
            _ => request.TargetCodec
        };

        string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            // If containers and codecs match and FFmpeg is missing, simple file copy
            if (inputContainer == request.TargetContainer && !needsTranscode)
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                string sha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                long len = new FileInfo(targetPath).Length;

                stopwatch.Stop();
                return new VideoConversionResult
                {
                    Success = true,
                    OutputArtifact = new MediaArtifact
                    {
                        Path = targetPath,
                        Kind = MediaArtifactKind.MotionVideo,
                        MimeType = request.TargetContainer == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                        VideoContainer = request.TargetContainer,
                        VideoCodec = effectiveTargetCodec,
                        ByteLength = len,
                        Sha256 = sha256
                    },
                    ExecutionRecord = new VideoExecutionRecord
                    {
                        InputContainer = inputContainer,
                        OutputContainer = request.TargetContainer,
                        InputCodec = inputCodec,
                        RequestedCodec = request.TargetCodec,
                        OutputCodec = effectiveTargetCodec,
                        RemuxUsed = true,
                        HardwareFallbackOccurred = false,
                        AudioPreserved = true,
                        RotationPreserved = true,
                        Duration = stopwatch.Elapsed
                    }
                };
            }

            stopwatch.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = "FFmpeg was not found in PATH or Tools directory.",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = inputContainer,
                    OutputContainer = request.TargetContainer,
                    InputCodec = inputCodec,
                    RequestedCodec = request.TargetCodec,
                    OutputCodec = VideoCodec.Unknown,
                    Duration = stopwatch.Elapsed
                }
            };
        }

        try
        {
            // Path 1: Remux (Stream Copy)
            if (!needsTranscode)
            {
                if (inputContainer == request.TargetContainer)
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }
                else
                {
                    string movflags = request.TargetContainer == VideoContainer.Mp4 ? "-movflags +faststart" : "";
                    string args = $"-y -i \"{sourcePath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0 {movflags} \"{targetPath}\"";
                    await RunProcessAsync(ffmpegPath, args, ct).ConfigureAwait(false);
                }

                if (!File.Exists(targetPath))
                    throw new InvalidOperationException($"Remux failed to produce output file '{targetPath}'.");

                string sha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                long len = new FileInfo(targetPath).Length;

                stopwatch.Stop();
                return new VideoConversionResult
                {
                    Success = true,
                    OutputArtifact = new MediaArtifact
                    {
                        Path = targetPath,
                        Kind = MediaArtifactKind.MotionVideo,
                        MimeType = request.TargetContainer == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                        VideoContainer = request.TargetContainer,
                        VideoCodec = effectiveTargetCodec,
                        ByteLength = len,
                        Sha256 = sha256
                    },
                    ExecutionRecord = new VideoExecutionRecord
                    {
                        InputContainer = inputContainer,
                        OutputContainer = request.TargetContainer,
                        InputCodec = inputCodec,
                        RequestedCodec = request.TargetCodec,
                        OutputCodec = effectiveTargetCodec,
                        RemuxUsed = true,
                        HardwareFallbackOccurred = false,
                        AudioPreserved = true,
                        RotationPreserved = true,
                        Duration = stopwatch.Elapsed
                    }
                };
            }

            // Path 2: Transcode with hardware encoder probing and software fallback
            string codecName = effectiveTargetCodec == VideoCodec.Hevc ? "hevc" : "h264";
            string[] candidateEncoders = effectiveTargetCodec == VideoCodec.Hevc
                ? ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"]
                : ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];

            string? chosenEncoder = null;
            bool fallbackOccurred = false;

            foreach (string enc in candidateEncoders)
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);

                string movflags = request.TargetContainer == VideoContainer.Mp4 ? "-movflags +faststart" : "";
                string encoderParams = GetEncoderParams(enc);
                string args = $"-y -i \"{sourcePath}\" -c:v {enc} {encoderParams} -c:a copy -map 0:V:0 -map 0:a:0? -map_metadata 0 {movflags} \"{targetPath}\"";

                try
                {
                    await RunProcessAsync(ffmpegPath, args, ct).ConfigureAwait(false);
                    if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                    {
                        chosenEncoder = enc;
                        if (enc.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && candidateEncoders.Length > 1)
                        {
                            fallbackOccurred = true;
                        }
                        break;
                    }
                }
                catch
                {
                    // Continue to next fallback encoder
                    fallbackOccurred = true;
                }
            }

            if (string.IsNullOrEmpty(chosenEncoder) || !File.Exists(targetPath))
            {
                throw new InvalidOperationException($"Failed to transcode video to {effectiveTargetCodec} using available encoders.");
            }

            string resultSha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
            long resultLen = new FileInfo(targetPath).Length;

            stopwatch.Stop();
            return new VideoConversionResult
            {
                Success = true,
                OutputArtifact = new MediaArtifact
                {
                    Path = targetPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = request.TargetContainer == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = request.TargetContainer,
                    VideoCodec = effectiveTargetCodec,
                    ByteLength = resultLen,
                    Sha256 = resultSha256
                },
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = inputContainer,
                    OutputContainer = request.TargetContainer,
                    InputCodec = inputCodec,
                    RequestedCodec = request.TargetCodec,
                    OutputCodec = effectiveTargetCodec,
                    SelectedEncoder = chosenEncoder,
                    RemuxUsed = false,
                    HardwareFallbackOccurred = fallbackOccurred,
                    AudioPreserved = true,
                    RotationPreserved = true,
                    Duration = stopwatch.Elapsed
                }
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = inputContainer,
                    OutputContainer = request.TargetContainer,
                    InputCodec = inputCodec,
                    RequestedCodec = request.TargetCodec,
                    OutputCodec = VideoCodec.Unknown,
                    Duration = stopwatch.Elapsed
                }
            };
        }
    }

    private static string GetEncoderParams(string encoder)
    {
        return encoder switch
        {
            "hevc_nvenc" or "h264_nvenc" => "-preset p4 -cq 22",
            "hevc_qsv" or "h264_qsv" => "-preset medium -global_quality 22",
            "hevc_amf" or "h264_amf" => "-quality balanced -rc cqp -qp_i 22 -qp_p 22",
            "libx265" or "libx264" => "-preset medium -crf 20",
            _ => ""
        };
    }

    private static async Task RunProcessAsync(string executable, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string err = await stdErrTask.ConfigureAwait(false);
            throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {err}");
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

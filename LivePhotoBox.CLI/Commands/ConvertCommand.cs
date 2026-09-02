using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands;

/// <summary>
/// Rebuilt-only standalone media conversion.  This command deliberately calls
/// the Native image/video converters and never starts an external media CLI.
/// </summary>
internal static class ConvertCommand
{
    public static Command Create()
    {
        var source = new Argument<FileInfo>("source")
        {
            Description = "Source image or video (.jpg/.jpeg/.heic/.heif/.mp4/.mov)."
        };
        var output = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Destination path. The extension selects the target container."
        };
        var codec = new Option<string>("--codec")
        {
            DefaultValueFactory = _ => "copy",
            Description = "Video codec: copy, h264, or hevc. Ignored for images."
        };
        var overwrite = new Option<bool>("--overwrite", "-w")
        {
            Description = "Replace an existing destination file."
        };

        var command = new Command(
            "convert",
            "Convert a standalone image/video through the Rebuilt Native media pipeline.\n" +
            "Examples:\n" +
            "  lpb convert input.mov -o output.mp4 --codec h264\n" +
            "  lpb convert input.heic -o output.jpg\n" +
            "  lpb convert input.jpg -o output.heic")
        {
            source,
            output,
            codec,
            overwrite
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            FileInfo sourceFile = parseResult.GetValue(source)!;
            FileInfo? outputFile = parseResult.GetValue(output);
            string codecValue = parseResult.GetValue(codec)!;
            bool allowOverwrite = parseResult.GetValue(overwrite);

            if (outputFile == null)
            {
                CliConsole.WriteErrorLine("Error: Required option '--output' was not provided.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                int exitCode = await ProcessingPipelineRouter.RunRebuiltAsync(
                    "convert",
                    () => RunAsync(sourceFile, outputFile, codecValue, allowOverwrite, cancellationToken));
                Environment.ExitCode = exitCode;
            }
            catch (Exception ex)
            {
                CliConsole.WriteErrorLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task<int> RunAsync(
        FileInfo sourceFile,
        FileInfo outputFile,
        string codecValue,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(sourceFile.FullName);
        string outputPath = Path.GetFullPath(outputFile.FullName);
        if (!sourceFile.Exists)
        {
            CliConsole.WriteErrorLine($"Error: File not found: {sourcePath}");
            return 1;
        }

        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            CliConsole.WriteErrorLine("Error: Source and output paths must be different.");
            return 1;
        }

        if (File.Exists(outputPath) && !overwrite)
        {
            CliConsole.WriteErrorLine($"Error: Output already exists: {outputPath}. Use --overwrite to replace it.");
            return 1;
        }

        string outputExtension = Path.GetExtension(outputPath);
        string sourceExtension = Path.GetExtension(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (TryGetImageContainer(outputExtension, out ImageContainer imageContainer))
        {
            if (!TryGetImageContainer(sourceExtension, out _))
            {
                CliConsole.WriteErrorLine("Error: Image conversion requires an image source.");
                return 1;
            }

            SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(sourcePath, null, cancellationToken).ConfigureAwait(false);
            if (!facts.PrimaryImage.IsPresent)
            {
                CliConsole.WriteErrorLine("Error: Native inspection did not identify a primary image.");
                return 1;
            }

            var result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = sourcePath,
                    Kind = MediaArtifactKind.PrimaryImage,
                    ImageContainer = facts.PrimaryImage.Container,
                    ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteLength = sourceFile.Length
                },
                TargetContainer = imageContainer,
                TargetDirectory = Path.GetDirectoryName(outputPath)!,
                Quality = 95,
                PreservationPolicy = PreservationPolicy.BestEffort
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Success || result.OutputArtifact == null)
            {
                CliConsole.WriteErrorLine($"Error: Native image conversion failed: {result.ErrorMessage}");
                return 1;
            }

            File.Move(result.OutputArtifact.Path, outputPath, overwrite: true);
            CliConsole.WriteLine($"Converted image: {outputPath}", CliConsole.Success);
            return 0;
        }

        if (TryGetVideoContainer(outputExtension, out VideoContainer videoContainer))
        {
            if (!TryGetVideoContainer(sourceExtension, out _))
            {
                CliConsole.WriteErrorLine("Error: Video conversion requires a video source.");
                return 1;
            }

            VideoCodec targetCodec;
            try
            {
                targetCodec = ParseCodec(codecValue);
            }
            catch (ArgumentException ex)
            {
                CliConsole.WriteErrorLine($"Error: {ex.Message}");
                return 1;
            }

            var converter = new VideoConverter();
            VideoFacts facts = await converter.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            var result = await converter.ConvertAsync(new VideoConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = sourcePath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = facts.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = facts.Container,
                    VideoCodec = facts.Codec,
                    ByteLength = sourceFile.Length
                },
                TargetContainer = videoContainer,
                TargetCodec = targetCodec,
                TargetDirectory = Path.GetDirectoryName(outputPath)!,
                Crf = 23
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Success || result.OutputArtifact == null)
            {
                CliConsole.WriteErrorLine($"Error: Native video conversion failed: {result.ErrorMessage}");
                return 1;
            }

            File.Move(result.OutputArtifact.Path, outputPath, overwrite: true);
            CliConsole.WriteLine(
                $"Converted video: {outputPath} ({result.ExecutionRecord.InputCodec} -> {result.ExecutionRecord.OutputCodec}, " +
                $"{(result.ExecutionRecord.RemuxUsed ? "remux" : "transcode")})",
                CliConsole.Success);
            return 0;
        }

        CliConsole.WriteErrorLine("Error: Output extension must be .jpg, .jpeg, .heic, .heif, .mp4, or .mov.");
        return 1;
    }

    private static VideoCodec ParseCodec(string value) => value.Trim().ToLowerInvariant() switch
    {
        "copy" => VideoCodec.Copy,
        "h264" or "avc" => VideoCodec.H264,
        "hevc" or "h265" => VideoCodec.Hevc,
        _ => throw new ArgumentException("Video codec must be copy, h264, or hevc.")
    };

    private static bool TryGetImageContainer(string extension, out ImageContainer container)
    {
        container = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageContainer.Jpeg,
            ".heic" or ".heif" => ImageContainer.Heic,
            _ => ImageContainer.Unknown
        };
        return container != ImageContainer.Unknown;
    }

    private static bool TryGetVideoContainer(string extension, out VideoContainer container)
    {
        container = extension.ToLowerInvariant() switch
        {
            ".mp4" => VideoContainer.Mp4,
            ".mov" => VideoContainer.Mov,
            _ => VideoContainer.Unknown
        };
        return container != VideoContainer.Unknown;
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// General-purpose image converter implementing structure-level passthrough and full codec conversions.
/// Strictly free of live photo vendor-specific target branching.
/// </summary>
public sealed class ImageConverter : IImageConverter
{
    public async Task<ImageConversionResult> ConvertAsync(
        ImageConversionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceArtifact);

        var stopwatch = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        string sourcePath = request.SourceArtifact.Path;
        if (!File.Exists(sourcePath))
        {
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = $"Source image file not found: '{sourcePath}'",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    Duration = stopwatch.Elapsed
                }
            };
        }

        ImageContainer inputContainer = request.SourceArtifact.ImageContainer != ImageContainer.Unknown
            ? request.SourceArtifact.ImageContainer
            : Inspection.FormatInspector.DetectImageContainer(sourcePath);

        string targetDirectory = request.TargetDirectory;
        Directory.CreateDirectory(targetDirectory);

        string targetFileName = request.OutputFileName ??
            $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.{(request.TargetContainer == ImageContainer.Heic ? "heic" : "jpg")}";
        string targetPath = Path.Combine(targetDirectory, targetFileName);

        try
        {
            // Case 1: Same container -> Structure copy (no pixel re-encoding)
            if (inputContainer == request.TargetContainer && inputContainer is ImageContainer.Jpeg or ImageContainer.Heic)
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                string sha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                long len = new FileInfo(targetPath).Length;

                stopwatch.Stop();
                return new ImageConversionResult
                {
                    Success = true,
                    OutputArtifact = new MediaArtifact
                    {
                        Path = targetPath,
                        Kind = MediaArtifactKind.PrimaryImage,
                        MimeType = inputContainer == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                        ImageContainer = inputContainer,
                        ImageCodec = inputContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                        ByteLength = len,
                        Sha256 = sha256
                    },
                    ExecutionRecord = new ImageExecutionRecord
                    {
                        InputContainer = inputContainer,
                        OutputContainer = request.TargetContainer,
                        PixelReencoded = false,
                        MetadataCopied = true,
                        PreservationOutcome = PreservationOutcome.Preserved,
                        Duration = stopwatch.Elapsed
                    }
                };
            }

            // Case 2: HEIC -> JPEG
            if (inputContainer == ImageContainer.Heic && request.TargetContainer == ImageContainer.Jpeg)
            {
                bool hasGainMap = StandardHdrConversionService.HasAppleHeicGainMap(sourcePath, ct);
                PreservationOutcome outcome = PreservationOutcome.NotApplicable;

                string convertedJpegPath;
                if (hasGainMap)
                {
                    try
                    {
                        convertedJpegPath = await HeicConverterService.ConvertToJpegAsync(sourcePath, targetDirectory, request.Quality, ct).ConfigureAwait(false);
                        outcome = PreservationOutcome.Preserved;
                    }
                    catch (Exception) when (request.PreservationPolicy == PreservationPolicy.BestEffort)
                    {
                        convertedJpegPath = await HeicConverterService.ConvertToJpegAsync(sourcePath, targetDirectory, request.Quality, ct).ConfigureAwait(false);
                        outcome = PreservationOutcome.Downgraded;
                    }
                }
                else
                {
                    convertedJpegPath = await HeicConverterService.ConvertToJpegAsync(sourcePath, targetDirectory, request.Quality, ct).ConfigureAwait(false);
                    outcome = PreservationOutcome.Preserved;
                }

                if (!File.Exists(convertedJpegPath))
                {
                    throw new InvalidOperationException($"HEIC to JPEG conversion failed to produce '{convertedJpegPath}'.");
                }

                if (!string.Equals(convertedJpegPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(convertedJpegPath, targetPath, overwrite: true);
                }

                string sha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                long len = new FileInfo(targetPath).Length;

                stopwatch.Stop();
                return new ImageConversionResult
                {
                    Success = true,
                    OutputArtifact = new MediaArtifact
                    {
                        Path = targetPath,
                        Kind = MediaArtifactKind.PrimaryImage,
                        MimeType = "image/jpeg",
                        ImageContainer = ImageContainer.Jpeg,
                        ImageCodec = ImageCodec.Jpeg,
                        ByteLength = len,
                        Sha256 = sha256
                    },
                    ExecutionRecord = new ImageExecutionRecord
                    {
                        InputContainer = inputContainer,
                        OutputContainer = ImageContainer.Jpeg,
                        PixelReencoded = true,
                        MetadataCopied = true,
                        PreservationOutcome = outcome,
                        Duration = stopwatch.Elapsed
                    }
                };
            }

            // Case 3: JPEG -> HEIC
            if (inputContainer == ImageContainer.Jpeg && request.TargetContainer == ImageContainer.Heic)
            {
                bool hasGainMap = StandardHdrConversionService.HasStandardJpegGainMap(sourcePath, ct);
                PreservationOutcome outcome = PreservationOutcome.NotApplicable;

                string convertedHeicPath;
                if (hasGainMap)
                {
                    try
                    {
                        convertedHeicPath = await HeicConverterService.ConvertToHeicAsync(sourcePath, targetDirectory, ct).ConfigureAwait(false);
                        outcome = PreservationOutcome.Preserved;
                    }
                    catch (Exception) when (request.PreservationPolicy == PreservationPolicy.BestEffort)
                    {
                        convertedHeicPath = await HeicConverterService.ConvertToHeicAsync(sourcePath, targetDirectory, ct).ConfigureAwait(false);
                        outcome = PreservationOutcome.Downgraded;
                    }
                }
                else
                {
                    convertedHeicPath = await HeicConverterService.ConvertToHeicAsync(sourcePath, targetDirectory, ct).ConfigureAwait(false);
                    outcome = PreservationOutcome.Preserved;
                }

                if (!File.Exists(convertedHeicPath))
                {
                    throw new InvalidOperationException($"JPEG to HEIC conversion failed to produce '{convertedHeicPath}'.");
                }

                if (!string.Equals(convertedHeicPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(convertedHeicPath, targetPath, overwrite: true);
                }

                string sha256 = await ComputeSha256Async(targetPath, ct).ConfigureAwait(false);
                long len = new FileInfo(targetPath).Length;

                stopwatch.Stop();
                return new ImageConversionResult
                {
                    Success = true,
                    OutputArtifact = new MediaArtifact
                    {
                        Path = targetPath,
                        Kind = MediaArtifactKind.PrimaryImage,
                        MimeType = "image/heic",
                        ImageContainer = ImageContainer.Heic,
                        ImageCodec = ImageCodec.Hevc,
                        ByteLength = len,
                        Sha256 = sha256
                    },
                    ExecutionRecord = new ImageExecutionRecord
                    {
                        InputContainer = inputContainer,
                        OutputContainer = ImageContainer.Heic,
                        PixelReencoded = true,
                        MetadataCopied = true,
                        PreservationOutcome = outcome,
                        Duration = stopwatch.Elapsed
                    }
                };
            }

            stopwatch.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = $"Unsupported image conversion from {inputContainer} to {request.TargetContainer}.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = inputContainer,
                    OutputContainer = request.TargetContainer,
                    Duration = stopwatch.Elapsed
                }
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = inputContainer,
                    OutputContainer = request.TargetContainer,
                    Duration = stopwatch.Elapsed
                }
            };
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

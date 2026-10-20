using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

/// <summary>Compatibility signatures over the canonical Native image converter.</summary>
public static class HeicConverterService
{
    // Extension-only helper retained for callers deciding whether conversion is needed.
    public static bool IsHeicFile(string path) =>
        path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

    public static int DecoderIndex => 0;

    public static async Task<string> ConvertToJpegAsync(string heicPath, CancellationToken token = default)
    {
        if (!IsHeicFile(heicPath)) return heicPath;
        string fullSource = Path.GetFullPath(heicPath);
        string directory = Path.GetDirectoryName(fullSource)!;
        if (await StandardHdrConversionService.HasHeicGainMapAsync(fullSource, token).ConfigureAwait(false))
            return await ConvertHdrToJpegOrFailAsync(fullSource, directory, token).ConfigureAwait(false);

        MediaArtifact output = await ConvertOrdinaryAsync(fullSource, directory, ImageContainer.Heic,
            ImageContainer.Jpeg, 100, token).ConfigureAwait(false);
        string destination = Path.ChangeExtension(fullSource, ".jpg");
        ConverterArtifactPublisher.PublishNoReplace(output, destination, token);
        return destination;
    }

    public static Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory,
        CancellationToken token = default) => ConvertToJpegAsync(heicPath, outputDirectory, 100, token);

    public static async Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory,
        int quality, CancellationToken token = default)
    {
        if (!IsHeicFile(heicPath)) return heicPath;
        if (await StandardHdrConversionService.HasHeicGainMapAsync(heicPath, token).ConfigureAwait(false))
            return await ConvertHdrToJpegOrFailAsync(heicPath, outputDirectory, token).ConfigureAwait(false);

        MediaArtifact output = await ConvertOrdinaryAsync(heicPath, outputDirectory,
            ImageContainer.Heic, ImageContainer.Jpeg, quality, token).ConfigureAwait(false);
        return output.Path;
    }

    public static async Task<string> ConvertToHeicAsync(string sourcePath, string outputDirectory,
        CancellationToken token = default)
    {
        if (IsHeicFile(sourcePath)) return sourcePath;
        if (await StandardHdrConversionService.HasStandardJpegGainMapAsync(sourcePath, token).ConfigureAwait(false))
        {
            // This legacy signature has no trusted semantic binding. Preserve its
            // explicit Unsupported behavior; never fall back to plain SDR.
            return await StandardHdrConversionService.ConvertJpegToHeicAsync(
                sourcePath, outputDirectory, token).ConfigureAwait(false);
        }
        MediaArtifact output = await ConvertOrdinaryAsync(sourcePath, outputDirectory,
            ImageContainer.Jpeg, ImageContainer.Heic, 90, token).ConfigureAwait(false);
        return output.Path;
    }

    private static async Task<MediaArtifact> ConvertOrdinaryAsync(string sourcePath, string outputDirectory,
        ImageContainer sourceContainer, ImageContainer targetContainer, int quality, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sourcePath,
                Kind = MediaArtifactKind.PrimaryImage,
                ImageContainer = sourceContainer,
                ByteLength = new FileInfo(sourcePath).Length
            },
            TargetContainer = targetContainer,
            TargetDirectory = Path.GetFullPath(outputDirectory),
            Quality = quality,
            PreservationPolicy = PreservationPolicy.BestEffort
        }, token).ConfigureAwait(false);
        if (!result.Success || result.OutputArtifact is null)
            throw new InvalidOperationException($"Native image conversion failed: {result.ErrorMessage}");
        return result.OutputArtifact;
    }

    private static async Task<string> ConvertHdrToJpegOrFailAsync(string sourcePath, string outputDirectory,
        CancellationToken token)
    {
        try
        {
            return await StandardHdrConversionService.ConvertHeicToJpegAsync(
                sourcePath, outputDirectory, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"HDR/GainMap conversion failed for {Path.GetFileName(sourcePath)}; plain JPEG fallback is forbidden.", ex);
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Compatibility façade for legacy callers. GainMap observation is delegated
/// to the project Native Inspector. Conversion remains unavailable here until
/// the explicit R3 Native semantic operation is wired; this façade must never
/// downgrade into ordinary SDR conversion or managed pixel math.
/// </summary>
public static class StandardHdrConversionService
{
    public static async Task<bool> HasStandardJpegGainMapAsync(
        string sourcePath,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        SourceMediaFacts facts = await NativeMediaService
            .InspectMediaAsync(sourcePath, cancellationToken: token)
            .ConfigureAwait(false);
        return facts.PrimaryImage.Container == ImageContainer.Jpeg && facts.GainMap is { IsPresent: true };
    }

    public static async Task<bool> HasHeicGainMapAsync(string sourcePath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        SourceMediaFacts facts = await NativeMediaService
            .InspectMediaAsync(sourcePath, cancellationToken: token)
            .ConfigureAwait(false);
        return facts.PrimaryImage.Container == ImageContainer.Heic && facts.GainMap is { IsPresent: true };
    }

    public static Task<string> ConvertJpegToHeicAsync(
        string sourcePath, string outputDirectory, CancellationToken token = default) =>
        UnsupportedAsync(sourcePath, outputDirectory, token, "JPEG Ultra HDR to HEIC GainMap");

    public static Task<string> ConvertHeicToJpegAsync(
        string sourcePath, string outputDirectory, CancellationToken token = default) =>
        UnsupportedAsync(sourcePath, outputDirectory, token, "HEIC GainMap to JPEG Ultra HDR");

    private static Task<string> UnsupportedAsync(
        string sourcePath,
        string outputDirectory,
        CancellationToken token,
        string route)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!File.Exists(sourcePath))
            return Task.FromException<string>(new FileNotFoundException("HDR source was not found.", sourcePath));
        return Task.FromException<string>(new NotSupportedException(
            $"{route} requires the explicit Native P5-R3 semantic conversion path; no SDR or managed fallback is permitted."));
    }
}

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

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        // Check if strict preservation is requested for cross-container conversion
        if (request.PreservationPolicy == PreservationPolicy.Strict &&
            request.SourceArtifact.ImageContainer != request.TargetContainer &&
            request.SourceArtifact.ImageContainer != ImageContainer.Unknown)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "Strict preservation policy cannot be satisfied for cross-container image conversion without metadata loss.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Duration = sw.Elapsed
                }
            };
        }

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

            if (!File.Exists(outPath))
            {
                throw new FileNotFoundException("Converted output image was not found.", outPath);
            }

            // Inspect output header to verify actual container
            ImageContainer actualContainer = DetectOutputContainer(outPath);
            if (actualContainer != request.TargetContainer && request.TargetContainer != ImageContainer.Unknown)
            {
                throw new InvalidOperationException(
                    $"Output image container mismatch: expected {request.TargetContainer}, actual {actualContainer}.");
            }

            bool metadataCopied = !reencoded;
            PreservationOutcome preservationOutcome = !reencoded
                ? PreservationOutcome.Preserved
                : (request.PreservationPolicy == PreservationPolicy.AllowDiscard
                    ? PreservationOutcome.DiscardedNotApplicable
                    : PreservationOutcome.PartiallyPreserved);

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = actualContainer == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = actualContainer,
                ImageCodec = actualContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outPath).Length
            };

            return new ImageConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = actualContainer,
                    PixelReencoded = reencoded,
                    MetadataCopied = metadataCopied,
                    PreservationOutcome = preservationOutcome,
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
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Duration = sw.Elapsed
                }
            };
        }
    }

    private static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "miaf"];

    private static ImageContainer DetectOutputContainer(string path)
    {
        Span<byte> header = stackalloc byte[64];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(header);
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            {
                return ImageContainer.Jpeg;
            }
            if (read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            {
                // Check major brand
                string majorBrand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
                foreach (string b in HeifBrands)
                {
                    if (string.Equals(majorBrand, b, StringComparison.OrdinalIgnoreCase))
                    {
                        return ImageContainer.Heic;
                    }
                }

                // Check compatible brands
                for (int offset = 16; offset + 4 <= read; offset += 4)
                {
                    string brand = System.Text.Encoding.ASCII.GetString(header.Slice(offset, 4));
                    foreach (string b in HeifBrands)
                    {
                        if (string.Equals(brand, b, StringComparison.OrdinalIgnoreCase))
                        {
                            return ImageContainer.Heic;
                        }
                    }
                }
            }
        }
        return ImageContainer.Unknown;
    }
}

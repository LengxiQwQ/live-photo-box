using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// General-purpose image format converter supporting structure-level copy and pixel-level conversion
/// between JPEG and HEIC with detailed preservation execution records.
/// </summary>
public interface IImageConverter
{
    Task<ImageConversionResult> ConvertAsync(
        ImageConversionRequest request,
        CancellationToken ct = default);
}

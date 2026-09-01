using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Image;

public interface IImageConverter
{
    Task<ImageConversionResult> ConvertAsync(
        ImageConversionRequest request,
        CancellationToken cancellationToken = default);
}

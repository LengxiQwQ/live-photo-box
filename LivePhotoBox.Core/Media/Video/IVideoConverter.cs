using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Video;

public interface IVideoConverter
{
    Task<VideoConversionResult> ConvertAsync(
        VideoConversionRequest request,
        CancellationToken cancellationToken = default);

    Task<VideoFacts> ProbeAsync(
        string videoPath,
        CancellationToken cancellationToken = default);
}

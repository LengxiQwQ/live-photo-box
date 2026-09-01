using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Video;

/// <summary>
/// General-purpose video format converter supporting stream remuxing and hardware/software transcoding
/// with comprehensive execution telemetry.
/// </summary>
public interface IVideoConverter
{
    Task<VideoConversionResult> ConvertAsync(
        VideoConversionRequest request,
        CancellationToken ct = default);
}

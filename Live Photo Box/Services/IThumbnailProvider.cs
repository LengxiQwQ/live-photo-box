using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// HEIC 缩略图解码提供者接口。
    /// 允许在 Magick.NET 和 MagicScaler 等不同方案间切换对比。
    /// </summary>
    public interface IThumbnailProvider
    {
        /// <summary>提供者显示名称（用于日志/诊断）</summary>
        string Name { get; }

        /// <summary>
        /// 将 HEIC 文件解码为 JPEG 字节数组的缩略图。
        /// </summary>
        /// <param name="imagePath">HEIC 文件路径</param>
        /// <param name="targetSize">目标缩略图长边像素</param>
        /// <returns>(JPEG 字节数组, 宽度, 高度)</returns>
        Task<(byte[] data, int width, int height)> LoadHeicThumbnailAsync(string imagePath, uint targetSize);
    }
}

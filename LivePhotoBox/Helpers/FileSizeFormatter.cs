// <summary>
// File: FileSizeFormatter.cs
// 提供文件大小的格式化功能，将字节数转换为人类可读的字符串表示
// （KB / MB），用于在 UI 中显示文件体积信息。
// </summary>

namespace LivePhotoBox.Helpers
{
    // 文件大小格式化工具类。将字节（long）转换为带单位的可读字符串，
    // 小于 1 MB 时以 KB（保留 1 位小数）显示，否则以 MB（保留 2 位小数）显示。
    public static class FileSizeFormatter
    {
        // 将字节数格式化为人类可读的大小字符串。
        // bytes: 文件大小，单位为字节。
        // è¿å: 格式化后的大小字符串，例如 "256.0 KB" 或 "1.23 MB"。
        public static string Format(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}

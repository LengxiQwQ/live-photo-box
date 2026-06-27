using System;
using System.IO;

namespace LivePhotoBox.Services
{
    // 外部工具定位服务 — 在应用目录和系统 PATH 中查找 FFmpeg / ExifTool / jpegtran 等命令行工具。
    // 所有路径结果通过 Lazy&lt;T&gt; 线程安全地缓存，首次访问后不再重复扫描磁盘。
    // 视频转码、HEIC 转换、元数据处理等模块均通过此服务获取外部工具路径。
    public static class ExternalToolLocator
    {
        private static readonly Lazy<string?> _cachedFFmpegPath = new(ResolveFFmpegPath);
        private static readonly Lazy<string?> _cachedExifToolPath = new(ResolveExifToolPath);
        private static readonly Lazy<string?> _cachedJpegTranPath = new(ResolveJpegTranPath);

        // 获取缓存的 FFmpeg 可执行文件路径，未找到时返回 null。
        public static string? FindFFmpeg() => _cachedFFmpegPath.Value;
        // 获取缓存的 ExifTool 可执行文件路径，未找到时返回 null。
        public static string? FindExifTool() => _cachedExifToolPath.Value;
        // 获取缓存的 jpegtran 可执行文件路径，未找到时返回 null。
        public static string? FindJpegTran() => _cachedJpegTranPath.Value;
        // 检查 FFmpeg 是否可用（FindFFmpeg 不为 null）。
        public static bool IsFFmpegAvailable() => !string.IsNullOrEmpty(FindFFmpeg());

        private static string? ResolveFFmpegPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                Path.Combine(AppContext.BaseDirectory, "..", "Tools", "ffmpeg.exe"),
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;

                    if (candidate == "ffmpeg")
                    {
                        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                        if (!string.IsNullOrEmpty(pathEnv))
                        {
                            foreach (var part in pathEnv.Split(Path.PathSeparator))
                            {
                                try
                                {
                                    string clean = part.Trim(' ', '"');
                                    if (string.IsNullOrEmpty(clean)) continue;
                                    string fullPath = Path.Combine(clean, "ffmpeg.exe");
                                    if (File.Exists(fullPath)) return fullPath;
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static string? ResolveExifToolPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe"),
                Path.Combine(AppContext.BaseDirectory, "exiftool.exe"),
                "exiftool"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var part in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        string candidate = Path.Combine(part.Trim(), "exiftool.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static string? ResolveJpegTranPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran.exe"),
                Path.Combine(AppContext.BaseDirectory, "jpegtran.exe"),
                "jpegtran"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;

                    if (candidate == "jpegtran")
                    {
                        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                        if (!string.IsNullOrEmpty(pathEnv))
                        {
                            foreach (var part in pathEnv.Split(Path.PathSeparator))
                            {
                                try
                                {
                                    string clean = part.Trim(' ', '"');
                                    if (string.IsNullOrEmpty(clean)) continue;
                                    string fullPath = Path.Combine(clean, "jpegtran.exe");
                                    if (File.Exists(fullPath)) return fullPath;
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }
    }
}

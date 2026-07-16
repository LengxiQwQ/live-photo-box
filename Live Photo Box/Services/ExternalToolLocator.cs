using System;
using System.IO;

namespace LivePhotoBox.Services
{
    // 外部工具定位服务 — 仅在应用自带的 Tools 目录中查找命令行工具。
    // 不再扫描系统 PATH，确保所有用户运行的是应用分发的同一个工具版本。
    // 所有路径结果通过 Lazy&lt;T&gt; 线程安全地缓存，首次访问后不再重复扫描磁盘。
    public static class ExternalToolLocator
    {
        private static readonly Lazy<string?> _cachedFFmpegPath = new(ResolveFFmpegPath);
        private static readonly Lazy<string?> _cachedExifToolPath = new(ResolveExifToolPath);
        private static readonly Lazy<string?> _cachedJpegTranPath = new(ResolveJpegTranPath);
        private static readonly Lazy<string?> _cachedHeifEncPath = new(ResolveHeifEncPath);
        private static readonly Lazy<string?> _cachedHeifDecPath = new(ResolveHeifDecPath);

        // 获取缓存的 FFmpeg 可执行文件路径，未找到时返回 null。
        public static string? FindFFmpeg() => _cachedFFmpegPath.Value;
        // 获取缓存的 ExifTool 可执行文件路径，未找到时返回 null。
        public static string? FindExifTool() => _cachedExifToolPath.Value;
        // 获取缓存的 jpegtran 可执行文件路径，未找到时返回 null。
        public static string? FindJpegTran() => _cachedJpegTranPath.Value;
        // 获取缓存的 heif-enc 可执行文件路径，未找到时返回 null。
        public static string? FindHeifEnc() => _cachedHeifEncPath.Value;
        // 获取缓存的 heif-dec 可执行文件路径，未找到时返回 null。
        public static string? FindHeifDec() => _cachedHeifDecPath.Value;
        // 检查 FFmpeg 是否可用（FindFFmpeg 不为 null）。
        public static bool IsFFmpegAvailable() => !string.IsNullOrEmpty(FindFFmpeg());

        // 定位 ffmpeg.exe：Tools 子目录 → 应用根目录 → 上级 Tools（兼容 dotnet run 场景）
        private static string? ResolveFFmpegPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "Tools", "ffmpeg.exe"),
            };

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }

            return null;
        }

        // 定位 exiftool.exe：Tools 子目录 → 应用根目录
        private static string? ResolveExifToolPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe"),
                Path.Combine(AppContext.BaseDirectory, "exiftool.exe"),
            };

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }

            return null;
        }

        // 定位 jpegtran.exe：Tools 子目录 → 应用根目录
        private static string? ResolveJpegTranPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran.exe"),
                Path.Combine(AppContext.BaseDirectory, "jpegtran.exe"),
            };

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }

            return null;
        }

        // 定位 heif-enc.exe：Tools 子目录 → 应用根目录
        private static string? ResolveHeifEncPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "heif-enc.exe"),
                Path.Combine(AppContext.BaseDirectory, "heif-enc.exe"),
            };

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }

            return null;
        }

        // 定位 heif-dec.exe：Tools 子目录 → 应用根目录
        private static string? ResolveHeifDecPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "heif-dec.exe"),
                Path.Combine(AppContext.BaseDirectory, "heif-dec.exe"),
            };

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { }
            }

            return null;
        }
    }
}

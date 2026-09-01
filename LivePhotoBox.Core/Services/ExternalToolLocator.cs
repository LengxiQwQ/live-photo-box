using System;
using System.IO;
using LivePhotoBox.Models;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    // 外部工具定位服务 — 仅在应用自带的 Tools 目录中查找命令行工具。
    // 不再扫描系统 PATH，确保所有用户运行的是应用分发的同一个工具版本。
    // 所有路径结果通过 Lazy&lt;T&gt; 线程安全地缓存，首次访问后不再重复扫描磁盘。
    public static class ExternalToolLocator
    {
        private static readonly Lazy<string?> _cachedFFmpegPath = new(ResolveFFmpegPath);
        private static readonly Lazy<string?> _cachedFFprobePath = new(ResolveFFprobePath);
        private static readonly Lazy<string?> _cachedExifToolPath = new(ResolveExifToolPath);
        private static readonly Lazy<string?> _cachedJpegTranPath = new(ResolveJpegTranPath);
        private static readonly Lazy<string?> _cachedHeifEncPath = new(ResolveHeifEncPath);
        private static readonly Lazy<string?> _cachedHeifDecPath = new(ResolveHeifDecPath);

        // 获取缓存的 FFmpeg 可执行文件路径，未找到时返回 null。
        public static string? FindFFmpeg() => _cachedFFmpegPath.Value;
        // 获取缓存的 FFprobe 可执行文件路径，未找到时返回 null。
        public static string? FindFFprobe() => _cachedFFprobePath.Value;
        // 获取缓存的 ExifTool 可执行文件路径，未找到时返回 null。
        public static string? FindExifTool() => _cachedExifToolPath.Value;
        // 获取缓存的 jpegtran 可执行文件路径，未找到时返回 null。
        public static string? FindJpegTran() => _cachedJpegTranPath.Value;
        // 获取缓存的 heif-enc 可执行文件路径，未找到时返回 null。
        public static string? FindHeifEnc() => _cachedHeifEncPath.Value;
        // 获取缓存的 heif-dec 可执行文件路径，未找到时返回 null。
        public static string? FindHeifDec() => _cachedHeifDecPath.Value;
        public static bool IsFFmpegAvailable() => !string.IsNullOrEmpty(FindFFmpeg());

        // 从 BaseDirectory 向上搜索 Tools\<toolName>，并检查兄弟项目目录。
        // 覆盖 dotnet run 时 CLI/Core 输出目录到主项目 LivePhotoBox\Tools 的路径。
        private static string? FindTool(string toolName)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                if (TryPath(Path.Combine(dir, "Tools", toolName), out var found)) return found;
                if (TryPath(Path.Combine(dir, toolName), out found)) return found;

                // 也检查兄弟项目目录（CLI → LivePhotoBox, Core → LivePhotoBox）
                if (TryPath(Path.Combine(dir, "LivePhotoBox", "Tools", toolName), out found)) return found;

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            return null;
        }

        private static bool TryPath(string path, out string? result)
        {
            result = null;
            try { if (File.Exists(path)) { result = path; return true; } } catch { }
            return false;
        }

        private static string? ResolveFFmpegPath()
        {
            var path = FindTool("ffmpeg.exe");
            LogService.Info($"ExternalToolLocator: ffmpeg → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }

        private static string? ResolveFFprobePath()
        {
            var path = FindTool("ffprobe.exe");
            LogService.Info($"ExternalToolLocator: ffprobe → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }

        private static string? ResolveExifToolPath()
        {
            var path = FindTool("exiftool.exe");
            LogService.Info($"ExternalToolLocator: exiftool → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }

        private static string? ResolveJpegTranPath()
        {
            var path = FindTool("jpegtran.exe");
            LogService.Info($"ExternalToolLocator: jpegtran → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }

        private static string? ResolveHeifEncPath()
        {
            var path = FindTool("heif-enc.exe");
            LogService.Info($"ExternalToolLocator: heif-enc → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }

        private static string? ResolveHeifDecPath()
        {
            var path = FindTool("heif-dec.exe");
            LogService.Info($"ExternalToolLocator: heif-dec → {path ?? "NOT FOUND"}", LogSource.System);
            return path;
        }
    }
}

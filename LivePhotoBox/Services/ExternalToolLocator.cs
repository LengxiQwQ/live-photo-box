using System;
using System.IO;

namespace LivePhotoBox.Services
{
    /// <summary>Thread-safe locator for external tools (FFmpeg, exiftool, jpegtran).</summary>
    public static class ExternalToolLocator
    {
        private static readonly Lazy<string?> _cachedFFmpegPath = new(ResolveFFmpegPath);
        private static readonly Lazy<string?> _cachedFFprobePath = new(ResolveFFprobePath);
        private static readonly Lazy<string?> _cachedExifToolPath = new(ResolveExifToolPath);
        private static readonly Lazy<string?> _cachedJheadPath = new(ResolveJheadPath);

        public static string? FindFFmpeg() => _cachedFFmpegPath.Value;
        public static string? FindFFprobe() => _cachedFFprobePath.Value;
        public static string? FindExifTool() => _cachedExifToolPath.Value;
        public static string? FindJhead() => _cachedJheadPath.Value;
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

        private static string? ResolveFFprobePath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe"),
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe"),
                Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffprobe"),
                Path.Combine(AppContext.BaseDirectory, "..", "Tools", "ffprobe.exe"),
                "ffprobe"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;

                    if (candidate == "ffprobe")
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
                                    string fullPath = Path.Combine(clean, "ffprobe.exe");
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

        private static string? ResolveJheadPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "jhead.exe"),
                Path.Combine(AppContext.BaseDirectory, "jhead.exe"),
                "jhead"
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
                        string candidate = Path.Combine(part.Trim(), "jhead.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            return null;
        }
    }
}

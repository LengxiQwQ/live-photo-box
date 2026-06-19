using System;
using System.IO;

namespace LivePhotoBox.Services
{
    /// <summary>Thread-safe locator for external tools (FFmpeg, exiftool, jpegtran).</summary>
    public static class ExternalToolLocator
    {
        private static readonly Lazy<string?> _cachedFFmpegPath = new(ResolveFFmpegPath);
        private static readonly Lazy<string?> _cachedExifToolPath = new(ResolveExifToolPath);

        public static string? FindFFmpeg() => _cachedFFmpegPath.Value;
        public static string? FindExifTool() => _cachedExifToolPath.Value;
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
    }
}

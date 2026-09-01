using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Inspection;

/// <summary>
/// Probes image and video format details using header inspection and external tools (ffprobe / exiftool).
/// </summary>
public static class FormatInspector
{
    public static ImageContainer DetectImageContainer(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg") return ImageContainer.Jpeg;
        if (ext is ".heic" or ".heif") return ImageContainer.Heic;
        if (ext is ".png") return ImageContainer.Png;
        if (ext is ".webp") return ImageContainer.WebP;

        // Magic bytes fallback
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[16];
            int read = fs.Read(header, 0, 16);
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8) return ImageContainer.Jpeg;
            if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return ImageContainer.Png;
            if (read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p') return ImageContainer.Heic;
        }
        catch { }

        return ImageContainer.Unknown;
    }

    public static VideoContainer DetectVideoContainer(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".mp4") return VideoContainer.Mp4;
        if (ext is ".mov") return VideoContainer.Mov;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[16];
            int read = fs.Read(header, 0, 16);
            if (read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            {
                if (header[8] == (byte)'q' && header[9] == (byte)'t') return VideoContainer.Mov;
                return VideoContainer.Mp4;
            }
            if (read >= 8 && header[4] == (byte)'m' && header[5] == (byte)'o' && header[6] == (byte)'o' && header[7] == (byte)'v')
            {
                return VideoContainer.Mov;
            }
        }
        catch { }

        return VideoContainer.Unknown;
    }

    public static async Task<VideoFacts?> ProbeVideoFactsAsync(
        string videoPath,
        long byteOffset,
        long byteLength,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return null;

        VideoContainer container = DetectVideoContainer(videoPath);
        string? ffprobe = ExternalToolLocator.FindFFprobe();
        if (string.IsNullOrEmpty(ffprobe))
        {
            return new VideoFacts
            {
                Container = container,
                Codec = VideoCodec.Unknown,
                FilePath = videoPath,
                ByteOffset = byteOffset,
                ByteLength = byteLength > 0 ? byteLength : new FileInfo(videoPath).Length
            };
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            string jsonOutput = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(jsonOutput))
                return null;

            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;

            VideoCodec codec = VideoCodec.Unknown;
            int width = 0;
            int height = 0;
            int rotation = 0;
            double frameRate = 0;
            TimeSpan duration = TimeSpan.Zero;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var typeEl) &&
                        typeEl.GetString() == "video")
                    {
                        if (stream.TryGetProperty("codec_name", out var codecNameEl))
                        {
                            string cname = codecNameEl.GetString() ?? "";
                            if (cname.Contains("264", StringComparison.OrdinalIgnoreCase) || cname.Equals("avc1", StringComparison.OrdinalIgnoreCase))
                                codec = VideoCodec.H264;
                            else if (cname.Contains("hevc", StringComparison.OrdinalIgnoreCase) || cname.Contains("265", StringComparison.OrdinalIgnoreCase) || cname.Equals("hvc1", StringComparison.OrdinalIgnoreCase))
                                codec = VideoCodec.Hevc;
                        }

                        if (stream.TryGetProperty("width", out var wEl)) width = wEl.GetInt32();
                        if (stream.TryGetProperty("height", out var hEl)) height = hEl.GetInt32();

                        if (stream.TryGetProperty("r_frame_rate", out var fpsEl))
                        {
                            string fpsStr = fpsEl.GetString() ?? "";
                            var parts = fpsStr.Split('/');
                            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double num) &&
                                double.TryParse(parts[1], CultureInfo.InvariantCulture, out double den) && den > 0)
                            {
                                frameRate = num / den;
                            }
                        }

                        if (stream.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("rotate", out var rotEl) &&
                                int.TryParse(rotEl.GetString(), out int r))
                            {
                                rotation = (r % 360 + 360) % 360;
                            }
                        }

                        if (stream.TryGetProperty("side_data_list", out var sideData))
                        {
                            foreach (var item in sideData.EnumerateArray())
                            {
                                if (item.TryGetProperty("rotation", out var sideRot))
                                {
                                    rotation = (sideRot.GetInt32() % 360 + 360) % 360;
                                }
                            }
                        }

                        break;
                    }
                }
            }

            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var durEl) &&
                    double.TryParse(durEl.GetString(), CultureInfo.InvariantCulture, out double durSec))
                {
                    duration = TimeSpan.FromSeconds(durSec);
                }
            }

            return new VideoFacts
            {
                Container = container,
                Codec = codec,
                Width = width,
                Height = height,
                RotationDegrees = rotation,
                Duration = duration,
                FrameRate = frameRate,
                FilePath = videoPath,
                ByteOffset = byteOffset,
                ByteLength = byteLength > 0 ? byteLength : new FileInfo(videoPath).Length
            };
        }
        catch
        {
            return new VideoFacts
            {
                Container = container,
                Codec = VideoCodec.Unknown,
                FilePath = videoPath,
                ByteOffset = byteOffset,
                ByteLength = byteLength > 0 ? byteLength : new FileInfo(videoPath).Length
            };
        }
    }
}

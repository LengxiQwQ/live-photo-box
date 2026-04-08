using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoSplitResult
    {
        public required string ImageOutputPath { get; init; }
        public required string VideoOutputPath { get; init; }
    }

    public static class LivePhotoSplitService
    {
        private const int MetadataProbeBytes = 1024 * 1024; // 探测前 1MB 的元数据

        // 添加了 TimeSpan.FromSeconds(2) 作为超时保护，防止正则表达式遇到损坏文件陷入死循环
        private static readonly Regex MicroVideoOffsetRegex = new(
            "GCamera:MicroVideoOffset=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoLengthRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Length=\"(?<value>\\d+)\"|Item:Length=\"(?<value>\\d+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoMimeRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Mime=\"(?<value>[^\"]+)\"|Item:Mime=\"(?<value>[^\"]+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        public static async Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int selectedSplitFormatIndex, CancellationToken token)
        {
            Directory.CreateDirectory(outputDirectory);

            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length <= 0)
            {
                throw new InvalidDataException("Source file is empty.");
            }

            string metadataText = await ReadMetadataTextAsync(sourceStream, token);
            long videoLength = GetAppendedVideoLength(metadataText);
            long imageLength = sourceStream.Length - videoLength;

            if (videoLength <= 0 || imageLength <= 0)
            {
                throw new InvalidDataException("Unable to determine the appended motion video length or file is corrupted.");
            }

            string videoExtension = await ResolveVideoExtensionAsync(sourceStream, imageLength, metadataText, selectedSplitFormatIndex, token);
            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(sourcePath, outputDirectory, videoExtension);

            // 1. 提取图片部分
            sourceStream.Position = 0;
            await using (var imageOutputStream = new FileStream(imageOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactLengthAsync(sourceStream, imageOutputStream, imageLength, token);
            }

            // 2. 提取视频部分
            sourceStream.Position = imageLength;
            await using (var videoOutputStream = new FileStream(videoOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactLengthAsync(sourceStream, videoOutputStream, videoLength, token);
            }

            return new LivePhotoSplitResult
            {
                ImageOutputPath = imageOutputPath,
                VideoOutputPath = videoOutputPath
            };
        }

        private static async Task<string> ReadMetadataTextAsync(FileStream sourceStream, CancellationToken token)
        {
            sourceStream.Position = 0;
            int bufferLength = (int)Math.Min(sourceStream.Length, MetadataProbeBytes);
            byte[] buffer = new byte[bufferLength];
            int bytesRead = await sourceStream.ReadAsync(buffer, token);
            sourceStream.Position = 0;
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        private static long GetAppendedVideoLength(string metadataText)
        {
            if (TryGetLong(MicroVideoOffsetRegex.Match(metadataText), out long microVideoOffset))
                return microVideoOffset;

            if (TryGetLong(MotionPhotoLengthRegex.Match(metadataText), out long motionPhotoLength))
                return motionPhotoLength;

            throw new InvalidDataException("No motion video length metadata was found in the file.");
        }

        private static async Task<string> ResolveVideoExtensionAsync(FileStream sourceStream, long videoStartOffset, string metadataText, int selectedSplitFormatIndex, CancellationToken token)
        {
            return selectedSplitFormatIndex switch
            {
                1 => ".mp4",
                2 => ".mov",
                _ => await DetectDefaultVideoExtensionAsync(sourceStream, videoStartOffset, metadataText, token)
            };
        }

        private static async Task<string> DetectDefaultVideoExtensionAsync(FileStream sourceStream, long videoStartOffset, string metadataText, CancellationToken token)
        {
            // 1. 视频流头部魔数判断（权威最高优先级）
            byte[] header = new byte[32];
            sourceStream.Position = videoStartOffset;
            int bytesRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0; // 复位流指针

            if (bytesRead >= 12)
            {
                string boxType = Encoding.ASCII.GetString(header, 4, 4);

                if (boxType == "ftyp")
                {
                    string majorBrand = Encoding.ASCII.GetString(header, 8, 4);

                    // 匹配 Apple QuickTime
                    if (majorBrand.StartsWith("qt", StringComparison.OrdinalIgnoreCase))
                        return ".mov";

                    // 匹配 MP4 及其变种 (含 hvc1 等 HEVC 变种)
                    if (majorBrand.StartsWith("isom", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("mp4", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hev1", StringComparison.OrdinalIgnoreCase))
                        return ".mp4";
                }
                else if (boxType == "moov")
                {
                    // 兼容极少数无 ftyp 直接 moov 开头的老版本格式
                    return ".mov";
                }
            }

            // 2. 备用方案：如果二进制流因故未能识别，退回查阅 XMP 文本
            string? mimeType = MotionPhotoMimeRegex.Match(metadataText).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                var mime = mimeType.Trim().ToLowerInvariant();
                if (mime == "video/quicktime") return ".mov";
                if (mime == "video/mp4") return ".mp4";
            }

            // 3. 兜底方案
#if DEBUG
            Debug.WriteLine("[FormatDetector] WARNING: Failed to detect video format via Magic Number and XMP. Fallback to .mp4.");
#endif
            return ".mp4";
        }

        private static (string ImageOutputPath, string VideoOutputPath) BuildOutputPaths(string sourcePath, string outputDirectory, string videoExtension)
        {
            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            string imageExtension = Path.GetExtension(sourcePath);

            if (string.IsNullOrWhiteSpace(imageExtension))
            {
                imageExtension = ".jpg";
            }

            string imageOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}{imageExtension}");
            string videoOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}{videoExtension}");
            string sourceFullPath = Path.GetFullPath(sourcePath);

            // 防止输出文件覆盖掉正在读取的源文件
            if (string.Equals(Path.GetFullPath(imageOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                imageOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}_image{imageExtension}");
            }

            if (string.Equals(Path.GetFullPath(videoOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                videoOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}_video{videoExtension}");
            }

            return (imageOutputPath, videoOutputPath);
        }

        private static async Task CopyExactLengthAsync(Stream sourceStream, Stream destinationStream, long length, CancellationToken token)
        {
            // 81920 (80KB) 刚好低于 LOH (Large Object Heap) 的阈值，是最优的 IO 缓冲大小
            byte[] buffer = new byte[81920];
            long remaining = length;

            while (remaining > 0)
            {
                int bytesToRead = (int)Math.Min(buffer.Length, remaining);
                int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), token);

                if (bytesRead <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of file while splitting the live photo. The file might be corrupted.");
                }

                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                remaining -= bytesRead;
            }
        }

        private static bool TryGetLong(Match match, out long value)
        {
            value = 0;
            string rawValue = match.Groups["value"].Value;
            return !string.IsNullOrWhiteSpace(rawValue) && long.TryParse(rawValue, out value);
        }
    }
}
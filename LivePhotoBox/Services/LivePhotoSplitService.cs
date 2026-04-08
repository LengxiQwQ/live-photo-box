using System;
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
        private const int MetadataProbeBytes = 1024 * 1024;

        private static readonly Regex MicroVideoOffsetRegex = new(
            "GCamera:MicroVideoOffset=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex MotionPhotoLengthRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Length=\"(?<value>\\d+)\"|Item:Length=\"(?<value>\\d+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex MotionPhotoMimeRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Mime=\"(?<value>[^\"]+)\"|Item:Mime=\"(?<value>[^\"]+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

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
                throw new InvalidDataException("Unable to determine the appended motion video length.");
            }

            string videoExtension = await ResolveVideoExtensionAsync(sourceStream, imageLength, metadataText, selectedSplitFormatIndex, token);
            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(sourcePath, outputDirectory, videoExtension);

            sourceStream.Position = 0;
            await using (var imageOutputStream = new FileStream(imageOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactLengthAsync(sourceStream, imageOutputStream, imageLength, token);
            }

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
            {
                return microVideoOffset;
            }

            if (TryGetLong(MotionPhotoLengthRegex.Match(metadataText), out long motionPhotoLength))
            {
                return motionPhotoLength;
            }

            throw new InvalidDataException("No motion video length metadata was found.");
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
            // 1. XMP Mime (严格优先，完全匹配)
            string? mimeType = MotionPhotoMimeRegex.Match(metadataText).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                var mime = mimeType.Trim().ToLowerInvariant();
                if (mime == "video/quicktime")
                    return ".mov";
                if (mime == "video/mp4")
                    return ".mp4";
            }

            // 2. 视频流头部魔数判断（权威，Apple MOV 必须命中）
            byte[] header = new byte[32];
            sourceStream.Position = videoStartOffset;
            int bytesRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0;

            if (bytesRead >= 12)
            {
                // Apple MOV: ftypqt  (66 74 79 70 71 74 20 20)
                if (header[4] == 0x71 && header[5] == 0x74 && header[6] == 0x20 && header[7] == 0x20)
                    return ".mov";

                // 其它 QuickTime 变体
                string brand = Encoding.ASCII.GetString(header, 4, 8);
                if (brand.StartsWith("qt  ") || brand.StartsWith("qt"))
                    return ".mov";
                if (brand.StartsWith("moov"))
                    return ".mov";
                if (brand.StartsWith("isom") || brand.StartsWith("mp42") || brand.StartsWith("mp41"))
                    return ".mp4";

                // 兼容部分厂商写法
                if (brand.Trim().Equals("qt", StringComparison.OrdinalIgnoreCase))
                    return ".mov";
            }

            // 3. Fallback
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
            byte[] buffer = new byte[81920];
            long remaining = length;
            while (remaining > 0)
            {
                int bytesToRead = (int)Math.Min(buffer.Length, remaining);
                int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), token);
                if (bytesRead <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of file while splitting the live photo.");
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

using LivePhotoBox.Models;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoSplitFileInfo
    {
        public required string SourcePath { get; init; }
        public required long FileSizeBytes { get; init; }
    }

    public sealed class LivePhotoSplitScanResult
    {
        public required IReadOnlyList<LivePhotoSplitFileInfo> Files { get; init; }
        public required int RecognizedCount { get; init; }
        public required int SkippedCount { get; init; }
    }

    public static class LivePhotoSplitScanService
    {
        private const int MetadataProbeBytes = 256 * 1024;
        private const int MetadataCheckInterval = 4;
        private const int TrailerProbeBytes = 4 * 1024 * 1024;

        // 最小的合法 JPEG 体积（包含 SOI/EOI 及必要元数据），低于此值不可能是实况照片
        private const long MinImageBytes = 4 * 1024;
        // 视频流最小体积，低于此值也不可能是合法的实况照片
        private const long MinVideoBytes = 4 * 1024;

        private static readonly byte[][] MetadataMarkers =
        [
            Encoding.ASCII.GetBytes("GCamera:MotionPhoto"),
            Encoding.ASCII.GetBytes("GCamera:MicroVideo"),
            Encoding.ASCII.GetBytes("MicroVideoOffset"),
            Encoding.ASCII.GetBytes("Container:Directory"),
            Encoding.ASCII.GetBytes("MotionPhoto")
        ];

        // 复用 LivePhotoSplitService 中的偏移量解析正则（与拆分时判定标准完全一致）
        private static readonly Regex MicroVideoOffsetRegex = new(
            "GCamera:MicroVideoOffset=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoLengthRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Length=\"(?<value>\\d+)\"|Item:Length=\"(?<value>\\d+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        public static LivePhotoSplitScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null)
        {
            AppLogService.Scan($"Split scan started. Directory: {inputDirectory}");
            progress?.Report(new WorkProgressSnapshot(0, 0));

            var candidates = new List<string>();
            int enumerated = 0;
            try
            {
                foreach (var path in Directory.EnumerateFiles(inputDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    enumerated++;
                    if (IsSupportedImage(path))
                    {
                        candidates.Add(path);
                    }

                    if (enumerated == 1 || enumerated % 64 == 0)
                    {
                        progress?.Report(new WorkProgressSnapshot(0, enumerated));
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogService.Scan($"Access denied to directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (DirectoryNotFoundException ex)
            {
                AppLogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (IOException ex)
            {
                AppLogService.Scan($"IO error scanning directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (OperationCanceledException)
            {
                AppLogService.Scan("Split scan cancelled");
                throw;
            }

            int total = candidates.Count;
            if (total == 0)
            {
                AppLogService.Scan($"No image files found in directory: {inputDirectory}");
                progress?.Report(new WorkProgressSnapshot(0, enumerated));
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }

            AppLogService.Scan($"Found {total} image files, starting LivePhoto detection");

            var files = new List<LivePhotoSplitFileInfo>();
            int recognizedCount = 0;
            int skippedCount = 0;

            progress?.Report(new WorkProgressSnapshot(total, 0));

            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = candidates[i];
                var fileInfo = new FileInfo(path);
                if (IsLikelyLivePhoto(path, fileInfo.Length))
                {
                    files.Add(new LivePhotoSplitFileInfo { SourcePath = path, FileSizeBytes = fileInfo.Length });
                    recognizedCount++;
                }
                else
                {
                    skippedCount++;
                }

                int completed = i + 1;
                if (completed == 1 || completed % MetadataCheckInterval == 0 || completed == total)
                {
                    progress?.Report(new WorkProgressSnapshot(total, completed, recognizedCount, skippedCount));
                }
            }

            if (total > 0)
            {
                progress?.Report(new WorkProgressSnapshot(total, total, recognizedCount, skippedCount));
            }

            AppLogService.Scan($"Split scan completed. Found {recognizedCount} LivePhotos, skipped {skippedCount} regular images");

            return new LivePhotoSplitScanResult
            {
                Files = files.OrderBy(file => Path.GetFileName(file.SourcePath), StringComparer.OrdinalIgnoreCase).ToList(),
                RecognizedCount = recognizedCount,
                SkippedCount = skippedCount
            };
        }

        private static bool IsSupportedImage(string path)
        {
            return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyLivePhoto(string path, long fileSize)
        {
            // 严格识别实况照片（不依赖文件名，只看文件内容）：
            // 1. 文件头部必须包含 GCamera: MicroVideo/MotionPhoto 元数据标记
            // 2. 必须能解析出有效的视频偏移量（MicroVideoOffset 或 MotionPhotoLength）
            // 3. 偏移量必须满足：MinVideoBytes < offset < fileSize
            //    且 (fileSize - offset) >= MinImageBytes（剩余部分必须是合法 JPEG）
            // 满足全部条件才视为可拆分的实况照片，避免误判普通 JPEG 后拆分报错

            if (fileSize <= MinImageBytes + MinVideoBytes) return false;

            int probeSize = (int)Math.Min(fileSize, MetadataProbeBytes);
            byte[] headBuffer = ArrayPool<byte>.Shared.Rent(probeSize);
            int headRead = 0;
            string? metadataText = null;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

                headRead = stream.Read(headBuffer, 0, probeSize);
                if (headRead <= 0) return false;

                // 1. 检查文件头部是否含实况照片元数据标记
                var headData = new ReadOnlySpan<byte>(headBuffer, 0, headRead);
                bool hasMarker = false;
                foreach (var marker in MetadataMarkers)
                {
                    if (headData.IndexOf(marker) >= 0)
                    {
                        hasMarker = true;
                        break;
                    }
                }

                // 部分实况照片的 XMP / MicroVideo 标记不在文件最前面，扫一下头部文本中的偏移量
                // 如果头部找不到标记，但能解析出偏移量，也视为有效（少数实况照片实现）
                metadataText = Encoding.UTF8.GetString(headBuffer, 0, headRead);
                long? headOffset = TryParseVideoOffset(metadataText);
                if (!hasMarker && headOffset == null)
                {
                    // 2. 头部没有标记也没有偏移量，再尝试扫文件末尾是否包含视频流 trailer
                    int tailSize = (int)Math.Min(fileSize, TrailerProbeBytes);
                    byte[] tailBuffer = ArrayPool<byte>.Shared.Rent(tailSize);
                    int tailRead = 0;
                    try
                    {
                        stream.Seek(-tailSize, SeekOrigin.End);
                        tailRead = stream.Read(tailBuffer, 0, tailSize);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tailBuffer);
                    }

                    if (tailRead <= 0) return false;
                    var tailData = new ReadOnlySpan<byte>(tailBuffer, 0, tailRead);
                    if (tailData.IndexOf("ftyp"u8) < 0 && tailData.IndexOf("moov"u8) < 0)
                    {
                        return false;
                    }
                    // 文件末尾有视频流但头部找不到任何实况照片标记，仍视为可疑：
                    // 需要再做一次完整偏移量解析（XMP 可能写在文件前 1MB 之外，但 256KB 内）
                    // 这里因为元数据标记确实不在头部，认为是普通图片末尾恰好被某些工具追加视频
                    return false;
                }

                // 3. 解析视频偏移量
                long videoLength = headOffset ?? 0;
                if (videoLength <= 0)
                {
                    return false;
                }

                // 4. 校验偏移量合法性
                if (videoLength < MinVideoBytes) return false;
                if (videoLength >= fileSize) return false;

                long imageLength = fileSize - videoLength;
                if (imageLength < MinImageBytes) return false;

                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch { return false; }
            finally
            {
                ArrayPool<byte>.Shared.Return(headBuffer);
            }
        }

        private static long? TryParseVideoOffset(string metadataText)
        {
            if (string.IsNullOrEmpty(metadataText)) return null;

            var microMatch = MicroVideoOffsetRegex.Match(metadataText);
            if (microMatch.Success && long.TryParse(microMatch.Groups["value"].Value, out long microOffset) && microOffset > 0)
            {
                return microOffset;
            }

            var motionMatch = MotionPhotoLengthRegex.Match(metadataText);
            if (motionMatch.Success && long.TryParse(motionMatch.Groups["value"].Value, out long motionOffset) && motionOffset > 0)
            {
                return motionOffset;
            }

            return null;
        }
    }
}

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
        // 统一与 SplitService 相同的 1MB 探测深度，避免遗漏包含较大 EXIF 的实况照片
        private const int MetadataProbeBytes = 1024 * 1024;
        private const int MetadataCheckInterval = 4;

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

        private static readonly Regex MicroVideoOffsetRegex = LivePhotoConstants.MicroVideoOffsetRegex;
        private static readonly Regex MotionPhotoLengthRegex = LivePhotoConstants.MotionPhotoLengthRegex;

        public static LivePhotoSplitScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null,
            IProgress<LivePhotoSplitFileInfo>? itemProgress = null)
        {
            LogService.Scan($"Split scan started. Directory: {inputDirectory}");
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
                LogService.Scan($"Access denied to directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (IOException ex)
            {
                LogService.Scan($"IO error scanning directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (OperationCanceledException)
            {
                LogService.Scan("Split scan cancelled");
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }

            int total = candidates.Count;
            if (total == 0)
            {
                LogService.Scan($"No image files found in directory: {inputDirectory}");
                progress?.Report(new WorkProgressSnapshot(0, enumerated));
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }

            LogService.Scan($"Found {total} image files, starting LivePhoto detection");

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
                    var file = new LivePhotoSplitFileInfo { SourcePath = path, FileSizeBytes = fileInfo.Length };
                    files.Add(file);
                    recognizedCount++;
                    itemProgress?.Report(file);
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

            LogService.Scan($"Split scan completed. Found {recognizedCount} LivePhotos, skipped {skippedCount} regular images");

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
            // 基础过滤：文件大小必须大于图片和视频的最基本合法体积
            if (fileSize <= MinImageBytes + MinVideoBytes) return false;

            int probeSize = (int)Math.Min(fileSize, MetadataProbeBytes);
            byte[] headBuffer = ArrayPool<byte>.Shared.Rent(probeSize);
            int headRead = 0;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

                headRead = stream.Read(headBuffer, 0, probeSize);
                if (headRead <= 0) return false;

                // 1. 快速粗筛：通过字节流直接查找是否存在特征字符串（极速，避免对所有文件执行正则）
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

                // 如果头部连基本特征字眼都没有，绝不可能是实况照片，直接光速失败（彻底剔除原先读尾部 4MB 的耗时操作）
                if (!hasMarker) return false;

                // 2. 精准提取视频偏移量
                string metadataText = Encoding.UTF8.GetString(headBuffer, 0, headRead);
                long? headOffset = TryParseVideoOffset(metadataText);

                long videoLength = headOffset ?? 0;
                if (videoLength <= 0)
                {
                    return false;
                }

                // 3. 校验偏移量合法性（谷歌标准：videoLength 即为文件尾部的视频字节数）
                if (videoLength < MinVideoBytes) return false;
                if (videoLength >= fileSize) return false;

                long imageLength = fileSize - videoLength;
                if (imageLength < MinImageBytes) return false;

                // 完全通过，是一张标准的安卓实况照片
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Exception ex)
            {
                LogService.Scan($"Unexpected error checking LivePhoto candidate: {Path.GetFileName(path)}", LogLevel.Debug, ex);
                return false;
            }
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
using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private static readonly byte[][] MetadataMarkers =
        [
            Encoding.ASCII.GetBytes("GCamera:MotionPhoto"),
            Encoding.ASCII.GetBytes("GCamera:MicroVideo"),
            Encoding.ASCII.GetBytes("MicroVideoOffset"),
            Encoding.ASCII.GetBytes("Container:Directory"),
            Encoding.ASCII.GetBytes("MotionPhoto")
        ];

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
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("MVIMG_", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".MP.jpg", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".MP.jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fileSize < 512 * 1024) return false;
            if (fileSize <= 0) return false;

            int bufferSize = (int)Math.Min(fileSize, MetadataProbeBytes);
            byte[] buffer = new byte[bufferSize];

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0) return false;

                var data = buffer.AsSpan(0, bytesRead);
                foreach (var marker in MetadataMarkers)
                {
                    if (data.IndexOf(marker) >= 0) return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch { return false; }

            return false;
        }
    }
}

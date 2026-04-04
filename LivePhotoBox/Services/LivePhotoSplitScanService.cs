using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
        private const int MetadataProbeBytes = 1024 * 1024;
        private static readonly byte[][] MetadataMarkers =
        [
            Encoding.ASCII.GetBytes("GCamera:MotionPhoto"),
            Encoding.ASCII.GetBytes("GCamera:MicroVideo"),
            Encoding.ASCII.GetBytes("MicroVideoOffset"),
            Encoding.ASCII.GetBytes("Container:Directory"),
            Encoding.ASCII.GetBytes("MotionPhoto")
        ];

        public static LivePhotoSplitScanResult Scan(string inputDirectory)
        {
            var files = new List<LivePhotoSplitFileInfo>();
            int recognizedCount = 0;
            int skippedCount = 0;

            foreach (var path in Directory.EnumerateFiles(inputDirectory))
            {
                if (!IsSupportedImage(path))
                {
                    continue;
                }

                var fileInfo = new FileInfo(path);
                if (IsLikelyLivePhoto(path, fileInfo.Length))
                {
                    files.Add(new LivePhotoSplitFileInfo
                    {
                        SourcePath = path,
                        FileSizeBytes = fileInfo.Length
                    });
                    recognizedCount++;
                }
                else
                {
                    skippedCount++;
                }
            }

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

            if (fileSize <= 0)
            {
                return false;
            }

            int bufferSize = (int)Math.Min(fileSize, MetadataProbeBytes);
            byte[] buffer = new byte[bufferSize];

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    return false;
                }

                var data = buffer.AsSpan(0, bytesRead);
                foreach (var marker in MetadataMarkers)
                {
                    if (data.IndexOf(marker) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}

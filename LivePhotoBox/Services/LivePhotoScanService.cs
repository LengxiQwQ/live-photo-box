using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoFilePairInfo
    {
        public required string BaseName { get; init; }
        public required string ImagePath { get; init; }
        public required string VideoPath { get; init; }
        public required long ImageSizeBytes { get; init; }
        public required long VideoSizeBytes { get; init; }
    }

    public sealed class LivePhotoScanResult
    {
        public required IReadOnlyList<LivePhotoFilePairInfo> Pairs { get; init; }
        public required int StandaloneImagesCount { get; init; }
        public required int StandaloneVideosCount { get; init; }
    }

    public static class LivePhotoScanService
    {
        public static LivePhotoScanResult Scan(string inputDirectory)
        {
            var imgDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var vidDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(inputDirectory))
            {
                if (IsImageFile(path))
                {
                    imgDict[Path.GetFileNameWithoutExtension(path)] = path;
                    continue;
                }

                if (IsVideoFile(path))
                {
                    vidDict[Path.GetFileNameWithoutExtension(path)] = path;
                }
            }

            var pairs = new List<LivePhotoFilePairInfo>(Math.Min(imgDict.Count, vidDict.Count));

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                {
                    pairs.Add(new LivePhotoFilePairInfo
                    {
                        BaseName = kvp.Key,
                        ImagePath = kvp.Value,
                        VideoPath = vidPath,
                        // 仅对匹配上的文件获取大小，开销极小
                        ImageSizeBytes = new FileInfo(kvp.Value).Length,
                        VideoSizeBytes = new FileInfo(vidPath).Length
                    });
                }
            }

            int standaloneImagesCount = imgDict.Count - pairs.Count;
            int standaloneVideosCount = vidDict.Count - pairs.Count;

            return new LivePhotoScanResult
            {
                Pairs = pairs,
                StandaloneImagesCount = standaloneImagesCount,
                StandaloneVideosCount = standaloneVideosCount
            };
        }

        private static bool IsImageFile(string path)
        {
            return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoFile(string path)
        {
            return path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }
    }
}
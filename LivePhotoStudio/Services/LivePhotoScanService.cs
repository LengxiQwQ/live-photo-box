using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LivePhotoStudio.Services
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
            var allFiles = Directory.GetFiles(inputDirectory);
            var images = allFiles.Where(IsImageFile).ToList();
            var videos = allFiles.Where(IsVideoFile).ToList();

            var imageMap = CreateFileMap(images);
            var videoMap = CreateFileMap(videos);

            var pairs = new List<LivePhotoFilePairInfo>();
            int standaloneImagesCount = 0;
            int standaloneVideosCount = 0;

            foreach (var imageEntry in imageMap)
            {
                if (videoMap.TryGetValue(imageEntry.Key, out var videoPath))
                {
                    pairs.Add(new LivePhotoFilePairInfo
                    {
                        BaseName = imageEntry.Key,
                        ImagePath = imageEntry.Value,
                        VideoPath = videoPath,
                        ImageSizeBytes = new FileInfo(imageEntry.Value).Length,
                        VideoSizeBytes = new FileInfo(videoPath).Length
                    });
                }
                else
                {
                    standaloneImagesCount++;
                }
            }

            foreach (var videoEntry in videoMap)
            {
                if (!imageMap.ContainsKey(videoEntry.Key))
                {
                    standaloneVideosCount++;
                }
            }

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

        private static Dictionary<string, string> CreateFileMap(IEnumerable<string> files)
        {
            return files
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
                    StringComparer.OrdinalIgnoreCase);
        }
    }
}

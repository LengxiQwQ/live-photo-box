using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

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
        public static LivePhotoScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null)
        {
            LogService.Scan($"Scan started. Directory: {inputDirectory}");
            var imgDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var vidDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new WorkProgressSnapshot(0, 0));

            try
            {
                var allFiles = Directory.EnumerateFiles(inputDirectory).ToList();
                int total = allFiles.Count;
                LogService.Scan($"Found {total} files to scan");
                progress?.Report(new WorkProgressSnapshot(total, 0));

                for (int i = 0; i < allFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = allFiles[i];

                    if (IsImageFile(path))
                    {
                        imgDict[Path.GetFileNameWithoutExtension(path)] = path;
                    }
                    else if (IsVideoFile(path))
                    {
                        vidDict[Path.GetFileNameWithoutExtension(path)] = path;
                    }

                    int completed = i + 1;
                    if (completed == 1 || completed % 16 == 0 || completed == total)
                    {
                        progress?.Report(new WorkProgressSnapshot(total, completed, imgDict.Count));
                    }
                }

                progress?.Report(new WorkProgressSnapshot(total, total, imgDict.Count));
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.Scan($"Access denied to directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoScanResult
                {
                    Pairs = new List<LivePhotoFilePairInfo>(),
                    StandaloneImagesCount = 0,
                    StandaloneVideosCount = 0
                };
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoScanResult
                {
                    Pairs = new List<LivePhotoFilePairInfo>(),
                    StandaloneImagesCount = 0,
                    StandaloneVideosCount = 0
                };
            }
            catch (IOException ex)
            {
                LogService.Scan($"IO error scanning directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoScanResult
                {
                    Pairs = new List<LivePhotoFilePairInfo>(),
                    StandaloneImagesCount = 0,
                    StandaloneVideosCount = 0
                };
            }
            catch (OperationCanceledException)
            {
                LogService.Scan("Scan cancelled");
                throw;
            }

            var pairs = new List<LivePhotoFilePairInfo>(Math.Min(imgDict.Count, vidDict.Count));

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                {
                    try
                    {
                        pairs.Add(new LivePhotoFilePairInfo
                        {
                            BaseName = kvp.Key,
                            ImagePath = kvp.Value,
                            VideoPath = vidPath,
                            ImageSizeBytes = new FileInfo(kvp.Value).Length,
                            VideoSizeBytes = new FileInfo(vidPath).Length
                        });
                    }
                    catch (IOException ex)
                    {
                        LogService.Scan($"Failed to get file info for pair {kvp.Key}", LogLevel.Warning, ex);
                        continue;
                    }
                }
            }

            int standaloneImagesCount = imgDict.Count - pairs.Count;
            int standaloneVideosCount = vidDict.Count - pairs.Count;

            LogService.Scan($"Scan completed. Found {pairs.Count} pairs, {standaloneImagesCount} standalone images, {standaloneVideosCount} standalone videos");

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
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoFile(string path)
        {
            return path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }
    }
}

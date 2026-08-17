using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LivePhotoBox.Services
{
    // 通过文件名匹配发现的图片-视频文件对信息。
    public sealed class LivePhotoFilePairInfo
    {
        // 文件名基础部分（不含扩展名）。
        public required string BaseName { get; init; }
        // 图片文件完整路径。
        public required string ImagePath { get; init; }
        // 视频文件完整路径。
        public required string VideoPath { get; init; }
        // 图片文件字节大小。
        public required long ImageSizeBytes { get; init; }
        // 视频文件字节大小。
        public required long VideoSizeBytes { get; init; }
    }

    // 合并扫描结果，包含文件配对信息与未匹配文件的统计。
    public sealed class LivePhotoScanResult
    {
        // 通过文件名匹配到的图片-视频文件对。
        public required IReadOnlyList<LivePhotoFilePairInfo> Pairs { get; init; }
        // 未匹配的图片文件数。
        public required int StandaloneImagesCount { get; init; }
        // 未匹配的视频文件数。
        public required int StandaloneVideosCount { get; init; }
        // 文件名匹配后未配对的照片路径（供元数据匹配使用）。
        public IReadOnlyList<string> StandaloneImagePaths { get; init; } = Array.Empty<string>();
        // 文件名匹配后未配对的视频路径（供元数据匹配使用）。
        public IReadOnlyList<string> StandaloneVideoPaths { get; init; } = Array.Empty<string>();
    }

    // 合并扫描服务 — 遍历指定目录，通过文件名匹配图片（.jpg/.jpeg/.heic/.heif）
    // 与视频（.mov/.mp4）文件，识别实况照片对。
    // 支持递归扫描与进度报告，未配对的路径可传递给元数据匹配器进一步处理。
    public static class LivePhotoMergeScanService
    {
        // 扫描目录中的文件，按文件名基础部分（不含扩展名）匹配图片-视频对。
        // 支持递归扫描，通过 IProgress 报告进度。
        // inputDirectory: 要扫描的输入目录。
        // cancellationToken: 取消令牌。
        // progress: 进度报告器（total, completed, matchedPairs）。
        // 返回: 扫描结果，包含成功配对的列表与未配对文件的统计。
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
                bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var allFiles = Directory.EnumerateFiles(inputDirectory, "*.*", searchOption).ToList();
                int total = allFiles.Count;
                LogService.Scan($"Found {total} files to scan");
                progress?.Report(new WorkProgressSnapshot(total, 0));

                for (int i = 0; i < allFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = allFiles[i];

                    if (IsImageFile(path))
                    {
                        string key = PathHelper.GetPairingKey(inputDirectory, path);
                        imgDict[key] = path;
                    }
                    else if (IsVideoFile(path))
                    {
                        string key = PathHelper.GetPairingKey(inputDirectory, path);
                        vidDict[key] = path;
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
                return new LivePhotoScanResult
                {
                    Pairs = [],
                    StandaloneImagesCount = 0,
                    StandaloneVideosCount = 0
                };
            }

            var pairs = new List<LivePhotoFilePairInfo>(Math.Min(imgDict.Count, vidDict.Count));
            var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                {
                    try
                    {
                        pairs.Add(new LivePhotoFilePairInfo
                        {
                            BaseName = Path.GetFileName(kvp.Key),
                            ImagePath = kvp.Value,
                            VideoPath = vidPath,
                            ImageSizeBytes = new FileInfo(kvp.Value).Length,
                            VideoSizeBytes = new FileInfo(vidPath).Length
                        });
                        matchedKeys.Add(kvp.Key);
                    }
                    catch (IOException ex)
                    {
                        LogService.Scan($"Failed to get file info for pair {kvp.Key}", LogLevel.Warning, ex);
                        continue;
                    }
                }
            }

            // 收集未匹配的文件路径（供后续元数据匹配使用）
            var unmatchedImages = imgDict
                .Where(kvp => !matchedKeys.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();
            var unmatchedVideos = vidDict
                .Where(kvp => !matchedKeys.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            int standaloneImagesCount = unmatchedImages.Count;
            int standaloneVideosCount = unmatchedVideos.Count;

            LogService.Scan($"Scan completed. Found {pairs.Count} pairs, {standaloneImagesCount} standalone images, {standaloneVideosCount} standalone videos");

            return new LivePhotoScanResult
            {
                Pairs = pairs,
                StandaloneImagesCount = standaloneImagesCount,
                StandaloneVideosCount = standaloneVideosCount,
                StandaloneImagePaths = unmatchedImages,
                StandaloneVideoPaths = unmatchedVideos
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

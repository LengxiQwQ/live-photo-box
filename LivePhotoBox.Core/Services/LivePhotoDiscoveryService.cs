/*
 * LivePhotoDiscoveryService.cs
 *
 * 统一实况照片发现服务。
 *
 *   - 按 DiscoveryScanMode 标志位运行检测/匹配步骤，被各页共用
 *   - 检测（拆分/资源浏览页）：JPEG XMP 扫描 + HEIC 视频轨
 *   - 匹配（合并页，三选一互斥）：文件名 / Apple CID / vivo ID
 *   - 文件只被第一个命中它的步骤分类
 */

using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LivePhotoDiscoveryService
    {
        // ══════════════════════════════════════════════════════════════
        //  支持的文件扩展名
        // ══════════════════════════════════════════════════════════════
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".heic", ".heif"
        };

        private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg"
        };

        private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mov", ".mp4"
        };

        // ══════════════════════════════════════════════════════════════
        //  公开入口
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 扫描目录，按 scanMode 指定的步骤识别/匹对实况照片。
        /// </summary>
        /// <param name="inputDirectory">要扫描的目录</param>
        /// <param name="scanMode">要运行的检测/匹配步骤（默认 All）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="progress">批量进度报告（total, completed, livePhotoCount）</param>
        /// <param name="itemProgress">单个实况照片发现时的增量报告（用于流式呈现卡片）</param>
        public static Task<LivePhotoDiscoveryResult> ScanAsync(
            string inputDirectory,
            DiscoveryScanMode scanMode = DiscoveryScanMode.All,
            CancellationToken ct = default,
            IProgress<WorkProgressSnapshot>? progress = null,
            IProgress<LivePhotoDiscoveryItem>? itemProgress = null)
        {
            if (string.IsNullOrWhiteSpace(inputDirectory))
                throw new ArgumentException("Input directory is required.", nameof(inputDirectory));
            if (!Directory.Exists(inputDirectory))
                throw new DirectoryNotFoundException($"Directory not found: {inputDirectory}");

            return ScanRebuiltAsync(inputDirectory, scanMode, ct, progress, itemProgress);
        }

        /// <summary>
        /// Rebuilt discovery path.  It uses the Native source inspector for
        /// single-file facts and dual-file validation, and deliberately does
        /// not start ExifTool or any other external metadata process.
        /// </summary>
        private static async Task<LivePhotoDiscoveryResult> ScanRebuiltAsync(
            string inputDirectory,
            DiscoveryScanMode scanMode,
            CancellationToken ct,
            IProgress<WorkProgressSnapshot>? progress,
            IProgress<LivePhotoDiscoveryItem>? itemProgress)
        {
            LogService.Scan($"LivePhotoDiscovery rebuilt scan started. Directory: {inputDirectory}, mode: {scanMode}");
            var allItems = EnumerateDirectory(inputDirectory, ct);
            int totalFiles = allItems.Count;
            progress?.Report(new WorkProgressSnapshot(totalFiles, 0));
            if (totalFiles == 0)
                return new LivePhotoDiscoveryResult { Items = Array.Empty<LivePhotoDiscoveryItem>() };

            var images = allItems.Where(i => ImageExtensions.Contains(Path.GetExtension(i.FilePath))).ToList();
            var videos = allItems.Where(i => VideoExtensions.Contains(Path.GetExtension(i.FilePath))).ToList();
            var classifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inspector = new SourceInspector();
            int completed = 0;

            if (scanMode.HasFlag(DiscoveryScanMode.JpegMarkers) || scanMode.HasFlag(DiscoveryScanMode.HeicTrack))
            {
                foreach (var item in images)
                {
                    ct.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(item.FilePath);
                    bool enabled = JpegExtensions.Contains(ext)
                        ? scanMode.HasFlag(DiscoveryScanMode.JpegMarkers)
                        : scanMode.HasFlag(DiscoveryScanMode.HeicTrack);
                    if (enabled)
                    {
                        try
                        {
                            SourceMediaFacts facts = await inspector.InspectAsync(item.FilePath, null, ct).ConfigureAwait(false);
                            if (facts.Protocol != SourceProtocol.NonLive && facts.Protocol != SourceProtocol.Unknown &&
                                facts.MotionVideo?.IsPresent == true)
                            {
                                item.LivePhotoType = JpegExtensions.Contains(ext)
                                    ? LivePhotoType.SingleFileJpeg
                                    : LivePhotoType.SingleFileHeic;
                                item.DetectionMethod = JpegExtensions.Contains(ext)
                                    ? LivePhotoDetectionMethod.JpegByteMarkers
                                    : LivePhotoDetectionMethod.HeicVideoTrack;
                                item.AppendedVideoLength = facts.MotionVideo.ByteLength;
                                classifiedPaths.Add(item.FilePath);
                                itemProgress?.Report(item);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Scan($"Rebuilt inspection failed for '{item.FilePath}': {ex.Message}", LogLevel.Warning);
                        }
                    }
                    completed++;
                    progress?.Report(new WorkProgressSnapshot(totalFiles, Math.Min(totalFiles, completed)));
                }
            }

            var videoByBaseName = videos
                .GroupBy(v => Path.GetFileNameWithoutExtension(v.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var usedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var image in images.Where(i => !classifiedPaths.Contains(i.FilePath)))
            {
                ct.ThrowIfCancellationRequested();
                string baseName = Path.GetFileNameWithoutExtension(image.FilePath);
                if (!videoByBaseName.TryGetValue(baseName, out var video) || usedVideos.Contains(video.FilePath))
                    continue;

                SourceProtocol protocol = SourceProtocol.Unknown;
                LivePhotoDetectionMethod method = LivePhotoDetectionMethod.FilenamePairing;
                if (scanMode.HasFlag(DiscoveryScanMode.FilenamePair))
                {
                    protocol = SourceProtocol.Unknown;
                    method = LivePhotoDetectionMethod.FilenamePairing;
                }
                else if (scanMode.HasFlag(DiscoveryScanMode.VivoMatch))
                {
                    protocol = await InspectDualProtocolAsync(inspector, image.FilePath, video.FilePath, ct).ConfigureAwait(false);
                    if (protocol != SourceProtocol.VivoLegacyDualFile) continue;
                    method = LivePhotoDetectionMethod.VivoLivePhoto;
                }
                else if (scanMode.HasFlag(DiscoveryScanMode.CidMatch))
                {
                    protocol = await InspectDualProtocolAsync(inspector, image.FilePath, video.FilePath, ct).ConfigureAwait(false);
                    if (protocol != SourceProtocol.AppleLivePhoto) continue;
                    method = LivePhotoDetectionMethod.ContentIdentifier;
                }
                else
                {
                    continue;
                }

                image.LivePhotoType = LivePhotoType.DualFile;
                image.DetectionMethod = method;
                image.PairedVideoPath = video.FilePath;
                video.LivePhotoType = LivePhotoType.DualFile;
                video.DetectionMethod = method;
                video.PairedImagePath = image.FilePath;
                classifiedPaths.Add(image.FilePath);
                classifiedPaths.Add(video.FilePath);
                usedVideos.Add(video.FilePath);
                itemProgress?.Report(image);
                completed++;
                progress?.Report(new WorkProgressSnapshot(totalFiles, Math.Min(totalFiles, completed)));
            }

            int liveCount = allItems.Count(i => i.IsLivePhoto);
            progress?.Report(new WorkProgressSnapshot(totalFiles, totalFiles, liveCount));
            LogService.Scan($"LivePhotoDiscovery rebuilt scan complete. Total: {totalFiles}, LivePhotos: {liveCount}");
            return new LivePhotoDiscoveryResult
            {
                Items = allItems.OrderBy(i => Path.GetFileName(i.FilePath), StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private static async Task<SourceProtocol> InspectDualProtocolAsync(
            SourceInspector inspector, string imagePath, string videoPath, CancellationToken ct)
        {
            try
            {
                SourceMediaFacts facts = await inspector.InspectAsync(imagePath, videoPath, ct).ConfigureAwait(false);
                return facts.Protocol;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Scan($"Rebuilt dual-file inspection failed for '{imagePath}': {ex.Message}", LogLevel.Warning);
                return SourceProtocol.Unknown;
            }
        }

        /// <summary>
        /// 检测单个文件是否为单文件实况照片，返回其类型
        /// （<see cref="LivePhotoType.None"/> / <see cref="LivePhotoType.SingleFileJpeg"/> / <see cref="LivePhotoType.SingleFileHeic"/>）。
        /// 使用 Native SourceInspector 探测，不依赖任何外部工具。
        /// </summary>
        public static async Task<LivePhotoType> DetectSingleFileTypeAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return LivePhotoType.None;

            string ext = Path.GetExtension(filePath);
            if (!JpegExtensions.Contains(ext) && !HeicExtensions.Contains(ext))
                return LivePhotoType.None;

            try
            {
                var inspector = new SourceInspector();
                var facts = await inspector.InspectAsync(filePath, null, ct).ConfigureAwait(false);
                if (facts.Protocol != SourceProtocol.NonLive && facts.Protocol != SourceProtocol.Unknown &&
                    facts.MotionVideo?.IsPresent == true)
                {
                    return JpegExtensions.Contains(ext)
                        ? LivePhotoType.SingleFileJpeg
                        : LivePhotoType.SingleFileHeic;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Scan($"DetectSingleFileTypeAsync failed for '{filePath}': {ex.Message}", LogLevel.Warning);
            }

            return LivePhotoType.None;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 1: 文件枚举
        // ══════════════════════════════════════════════════════════════

        private static List<LivePhotoDiscoveryItem> EnumerateDirectory(
            string inputDirectory, CancellationToken ct)
        {
            var items = new List<LivePhotoDiscoveryItem>();
            bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            try
            {
                foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.*", searchOption))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(path);
                    if (!ImageExtensions.Contains(ext) && !VideoExtensions.Contains(ext))
                        continue;

                    try
                    {
                        var fileInfo = new FileInfo(path);
                        items.Add(new LivePhotoDiscoveryItem
                        {
                            FilePath = path,
                            FileSizeBytes = fileInfo.Length,
                            LastWriteTime = fileInfo.LastWriteTime
                        });
                    }
                    catch (IOException) { /* skip inaccessible files */ }
                    catch (UnauthorizedAccessException) { /* skip */ }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.Scan($"Access denied: {inputDirectory}", LogLevel.Error, ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                throw;
            }
            catch (IOException ex)
            {
                LogService.Scan($"IO error: {inputDirectory}", LogLevel.Error, ex);
            }

            return items;
        }
    }
}

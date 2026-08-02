/*
 * LivePhotoDiscoveryService.cs
 *
 * 统一实况照片发现服务。
 *
 * 按 DiscoveryScanMode 标志位运行对应检测/匹配步骤。
 * 检测步骤（拆分/资源浏览页面）：JPEG XMP 扫描 + HEIC 视频轨。
 * 匹配步骤（合并页面，三选一互斥）：文件名 / Apple CID / vivo ID。
 * 文件只被第一个命中它的步骤分类。
 *
 * 被 MergePage、SplitPage、KeyPhoto、RepairPage 共用。
 */

using LivePhotoBox.Models;
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

        /// <summary>HEIC 视频轨检测用的 exiftool 并行实例数</summary>
        private const int HeicDetectionPoolSize = 2;

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
        /// <param name="itemProgress">逐项进度报告（每发现一个实况照片时触发，支持流式 UI 加载）</param>
        /// <returns>统一扫描结果，包含所有文件及其分类</returns>
        public static async Task<LivePhotoDiscoveryResult> ScanAsync(
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

            LogService.Scan($"LivePhotoDiscovery scan started. Directory: {inputDirectory}, mode: {scanMode}");
            progress?.Report(new WorkProgressSnapshot(0, 0));

            // ── Step 1: 文件枚举 ──
            var allItems = EnumerateDirectory(inputDirectory, ct);
            int totalFiles = allItems.Count;
            LogService.Scan($"Enumeration complete: {totalFiles} files");
            progress?.Report(new WorkProgressSnapshot(totalFiles, 0));

            if (totalFiles == 0)
            {
                return new LivePhotoDiscoveryResult { Items = Array.Empty<LivePhotoDiscoveryItem>() };
            }

            ct.ThrowIfCancellationRequested();

            // ── 跟踪已分类文件（已被某步骤标记的，后续步骤跳过）──
            var classifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 获取按扩展名分类的未分类文件
            List<LivePhotoDiscoveryItem> GetUnclassified(Func<LivePhotoDiscoveryItem, bool>? filter = null)
            {
                var q = allItems.Where(i => !classifiedPaths.Contains(i.FilePath));
                if (filter != null) q = q.Where(filter);
                return q.ToList();
            }

            // ── Step 2: 运行各步骤 ──

            // ── 检测: JPEG XMP 字节标记 ──
            if (scanMode.HasFlag(DiscoveryScanMode.JpegMarkers))
            {
                var jpegs = GetUnclassified(i => JpegExtensions.Contains(Path.GetExtension(i.FilePath)));
                if (jpegs.Count > 0)
                {
                    LogService.Scan($"JPEG XMP scan: {jpegs.Count} files");
                    int found = 0;
                    foreach (var item in jpegs)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (LivePhotoSplitScanService.IsLikelyLivePhoto(item.FilePath, item.FileSizeBytes))
                        {
                            // 解析内嵌视频段长度（避免灯箱重复读取）
                            long videoLen = 0;
                            try
                            {
                                var metadataText = LivePhotoSplitService.ReadMetadataTextSync(item.FilePath);
                                videoLen = LivePhotoSplitService.GetAppendedVideoLength(metadataText);
                            }
                            catch { videoLen = 0; }

                            // XMP 无有效视频偏移 → 回落检查尾部标记（华为 LIVE_ / 三星 SEFH）
                            // 避免空 XMP 壳（如荣耀相机 GainMap 的 Container:Directory）被误判
                            if (videoLen <= 0 && !HasHuaweiLiveTail(item.FilePath))
                                continue;

                            item.LivePhotoType = LivePhotoType.SingleFileJpeg;
                            item.DetectionMethod = LivePhotoDetectionMethod.JpegByteMarkers;
                            item.AppendedVideoLength = videoLen > 0 ? videoLen : 0;
                            classifiedPaths.Add(item.FilePath);
                            found++;

                            // 逐项通知（供 SplitPage 流式加载）
                            itemProgress?.Report(item);
                        }
                    }
                    LogService.Scan($"JPEG XMP scan done: {found} single-file JPEG live photos found");
                }
            }

            ct.ThrowIfCancellationRequested();

            // ── 检测: HEIC 视频轨 ──
            if (scanMode.HasFlag(DiscoveryScanMode.HeicTrack))
            {
                var heics = GetUnclassified(i => HeicExtensions.Contains(Path.GetExtension(i.FilePath)));
                if (heics.Count > 0)
                {
                    LogService.Scan($"HEIC track scan: {heics.Count} files");
                    var found = await DetectHeicLivePhotosAsync(heics, ct);
                    foreach (var item in found)
                    {
                        item.LivePhotoType = LivePhotoType.SingleFileHeic;
                        item.DetectionMethod = LivePhotoDetectionMethod.HeicVideoTrack;
                        classifiedPaths.Add(item.FilePath);
                    }
                    LogService.Scan($"HEIC track scan done: {found.Count} single-file HEIC live photos found");
                }
            }

            ct.ThrowIfCancellationRequested();

            // ── 匹配: 文件名 ──
            bool hasFilenamePair = scanMode.HasFlag(DiscoveryScanMode.FilenamePair);
            if (hasFilenamePair)
            {
                var candidates = allItems.Where(i =>
                    ImageExtensions.Contains(Path.GetExtension(i.FilePath)) ||
                    VideoExtensions.Contains(Path.GetExtension(i.FilePath))).ToList();
                var result = FilenamePairing(candidates, inputDirectory, ct);
                foreach (var item in result)
                {
                    item.LivePhotoType = LivePhotoType.DualFile;
                    item.DetectionMethod = LivePhotoDetectionMethod.FilenamePairing;
                    classifiedPaths.Add(item.FilePath);
                }
                LogService.Scan($"Filename pairing complete: {result.Count} pairs");
            }

            ct.ThrowIfCancellationRequested();

            // ── 匹配: vivo ID ──
            bool hasVivoMatch = scanMode.HasFlag(DiscoveryScanMode.VivoMatch);
            if (hasVivoMatch)
            {
                var vivoImages = allItems
                    .Where(i => ImageExtensions.Contains(Path.GetExtension(i.FilePath)))
                    .Select(i => i.FilePath).ToList();
                var vivoVideos = allItems
                    .Where(i => VideoExtensions.Contains(Path.GetExtension(i.FilePath)))
                    .Select(i => i.FilePath).ToList();

                if (vivoImages.Count > 0 && vivoVideos.Count > 0)
                {
                    LogService.Scan(
                        $"vivo ID match: {vivoImages.Count} images, {vivoVideos.Count} videos");

                    try
                    {
                        var vivoOutput = await Task.Run(
                            () => LivePhotoMetadataMatcher.MatchVivo(vivoImages, vivoVideos),
                            ct);

                        foreach (var pair in vivoOutput.Pairs)
                        {
                            var imgItem = allItems.FirstOrDefault(i =>
                                string.Equals(i.FilePath, pair.ImagePath, StringComparison.OrdinalIgnoreCase));
                            if (imgItem != null && !classifiedPaths.Contains(imgItem.FilePath))
                            {
                                imgItem.LivePhotoType = LivePhotoType.DualFile;
                                imgItem.DetectionMethod = LivePhotoDetectionMethod.VivoLivePhoto;
                                imgItem.PairedVideoPath = pair.VideoPath;
                                classifiedPaths.Add(imgItem.FilePath);
                            }

                            var vidItem = allItems.FirstOrDefault(i =>
                                string.Equals(i.FilePath, pair.VideoPath, StringComparison.OrdinalIgnoreCase));
                            if (vidItem != null && !classifiedPaths.Contains(vidItem.FilePath))
                            {
                                vidItem.LivePhotoType = LivePhotoType.DualFile;
                                vidItem.DetectionMethod = LivePhotoDetectionMethod.VivoLivePhoto;
                                vidItem.PairedImagePath = pair.ImagePath;
                                classifiedPaths.Add(vidItem.FilePath);
                            }
                        }

                        LogService.Scan($"vivo ID match done: {vivoOutput.Pairs.Count} vivo pairs");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogService.Scan($"vivo matching failed: {ex.Message}", LogLevel.Warning);
                    }
                }
                else
                {
                    LogService.Scan("vivo match: no files to match, skipped");
                }
            }

            ct.ThrowIfCancellationRequested();

            // ── 匹配: Apple ContentIdentifier ── (Apple Live Photo) — processes all files
            if (scanMode.HasFlag(DiscoveryScanMode.CidMatch))
            {
                var standaloneImages = allItems
                    .Where(i => ImageExtensions.Contains(Path.GetExtension(i.FilePath)))
                    .Select(i => i.FilePath).ToList();
                var standaloneVideos = allItems
                    .Where(i => VideoExtensions.Contains(Path.GetExtension(i.FilePath)))
                    .Select(i => i.FilePath).ToList();

                if (standaloneImages.Count > 0 && standaloneVideos.Count > 0)
                {
                    string? exifToolPath = ExternalToolLocator.FindExifTool()
                        ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");

                    if (File.Exists(exifToolPath))
                    {
                        LogService.Scan(
                            $"CID match: {standaloneImages.Count} images, {standaloneVideos.Count} videos");

                        try
                        {
                            var matchOutput = await Task.Run(
                                () => LivePhotoMetadataMatcher.MatchAsync(
                                    standaloneImages, standaloneVideos, exifToolPath, ct),
                                ct);

                            foreach (var pair in matchOutput.Pairs)
                            {
                                var imgItem = allItems.FirstOrDefault(i =>
                                    string.Equals(i.FilePath, pair.ImagePath, StringComparison.OrdinalIgnoreCase));
                                if (imgItem != null && !classifiedPaths.Contains(imgItem.FilePath))
                                {
                                    imgItem.LivePhotoType = LivePhotoType.DualFile;
                                    imgItem.DetectionMethod = LivePhotoDetectionMethod.ContentIdentifier;
                                    imgItem.PairedVideoPath = pair.VideoPath;
                                    classifiedPaths.Add(imgItem.FilePath);
                                }

                                var vidItem = allItems.FirstOrDefault(i =>
                                    string.Equals(i.FilePath, pair.VideoPath, StringComparison.OrdinalIgnoreCase));
                                if (vidItem != null && !classifiedPaths.Contains(vidItem.FilePath))
                                {
                                    vidItem.LivePhotoType = LivePhotoType.DualFile;
                                    vidItem.DetectionMethod = LivePhotoDetectionMethod.ContentIdentifier;
                                    vidItem.PairedImagePath = pair.ImagePath;
                                    classifiedPaths.Add(vidItem.FilePath);
                                }
                            }

                            LogService.Scan($"CID match done: {matchOutput.Pairs.Count} CID pairs");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Scan($"CID matching failed: {ex.Message}", LogLevel.Warning);
                        }
                    }
                    else
                    {
                        LogService.Scan("CID match skipped: exiftool not found");
                    }
                }
            }

            // ── Step 3: 构建结果 ──
            int liveCount = allItems.Count(i => i.IsLivePhoto);
            LogService.Scan(
                $"LivePhotoDiscovery scan complete. " +
                $"Total: {totalFiles}, LivePhotos: {liveCount} " +
                $"(DualFile: {allItems.Count(i => i.LivePhotoType == LivePhotoType.DualFile)}, " +
                $"SingleFileJpeg: {allItems.Count(i => i.LivePhotoType == LivePhotoType.SingleFileJpeg)}, " +
                $"SingleFileHeic: {allItems.Count(i => i.LivePhotoType == LivePhotoType.SingleFileHeic)})");

            progress?.Report(new WorkProgressSnapshot(totalFiles, totalFiles, liveCount));

            return new LivePhotoDiscoveryResult
            {
                Items = allItems.OrderBy(i => Path.GetFileName(i.FilePath), StringComparer.OrdinalIgnoreCase).ToList()
            };
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

        // ══════════════════════════════════════════════════════════════
        // ── 检测: HEIC 视频轨 ──
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 使用 exiftool 批量检测 HEIC/HEIF 文件是否包含视频轨。
        /// 判定条件：ContentIdentifier 非空 且 MediaDuration > 0（Apple 标准），
        /// 或文件尾部含 LIVE_ 标记（华为/荣耀 Moving Photo）。
        /// 注意：不改动 Apple ContentIdentifier 检测逻辑，华为仅在 Apple 路径失败时回退。
        /// </summary>
        private static async Task<List<LivePhotoDiscoveryItem>> DetectHeicLivePhotosAsync(
            List<LivePhotoDiscoveryItem> heicItems, CancellationToken ct)
        {
            var result = new List<LivePhotoDiscoveryItem>();

            string? exifToolPath = ExternalToolLocator.FindExifTool()
                ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
            if (!File.Exists(exifToolPath))
            {
                LogService.Scan("HEIC track scan skipped: exiftool not found");
                return result;
            }

            // 使用 PersistentExifTool 池并行查询
            var pool = new List<PersistentExifTool>(HeicDetectionPoolSize);
            try
            {
                for (int i = 0; i < HeicDetectionPoolSize; i++)
                {
                    pool.Add(new PersistentExifTool(exifToolPath));
                }

                int batchSize = HeicDetectionPoolSize;
                for (int start = 0; start < heicItems.Count; start += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int end = Math.Min(start + batchSize, heicItems.Count);
                    int count = end - start;

                    var tasks = new Task<bool>[count];
                    for (int i = 0; i < count; i++)
                    {
                        var item = heicItems[start + i];
                        var tool = pool[i % HeicDetectionPoolSize];
                        tasks[i] = Task.Run(async () =>
                        {
                            // ── 快速检查（无进程启动，O(1) 文件 I/O）──
                            // 放到 exiftool 查询之前：即使 exiftool 崩溃、超时、
                            // 文件被锁定，这些检查仍然能命中 Google V2 / Samsung / 华为 HEIC。
                            try
                            {
                                // 华为 LIVE_ 尾标（60 字节固定布局）
                                if (HasHuaweiLiveTail(item.FilePath))
                                    return true;

                                // XMP MotionPhoto 标记（Google V2 HEIC / Samsung HEIC）
                                // 谷歌/三星 HEIC 实况照片使用 MotionPhoto V2 XMP + mpvd box
                                string xmpText = LivePhotoSplitService.ReadMetadataTextSync(item.FilePath);
                                if (xmpText.Contains("GCamera:MotionPhoto", StringComparison.Ordinal) ||
                                    xmpText.Contains("Container:Directory", StringComparison.Ordinal) ||
                                    xmpText.Contains("GContainer:Directory", StringComparison.Ordinal))
                                    return true;
                            }
                            catch { /* 回退检查失败 → 继续 exiftool 路径 */ }

                            // ── exiftool 查询（Apple ContentIdentifier + MediaDuration）──
                            try
                            {
                                string json = await tool.SendCommandAsync(
                                    ct, "-j", "-ContentIdentifier", "-MediaDuration", item.FilePath);
                                if (ParseHeicHasVideoTrack(json))
                                    return true;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { /* exiftool 失败 → 已经检查过快速路径，返回 false */ }

                            return false;
                        }, ct);
                    }

                    var results = await Task.WhenAll(tasks);
                    for (int i = 0; i < count; i++)
                    {
                        if (results[i])
                        {
                            var item = heicItems[start + i];
                            item.ContentIdentifier = null; // 实际解析太复杂，暂不存入
                            result.Add(item);
                        }
                    }
                }
            }
            finally
            {
                foreach (var tool in pool) try { tool.Dispose(); } catch { }
            }

            return result;
        }

        /// <summary>解析 exiftool JSON 输出，判断 HEIC 是否有视频轨</summary>
        private static bool ParseHeicHasVideoTrack(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("["))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];

                // ContentIdentifier 必须存在且非空
                string? cid = null;
                if (root.TryGetProperty("ContentIdentifier", out var cidEl))
                    cid = cidEl.GetString();
                if (string.IsNullOrWhiteSpace(cid)) return false;

                // MediaDuration 必须 > 0
                if (root.TryGetProperty("MediaDuration", out var durEl))
                {
                    if (durEl.ValueKind == JsonValueKind.String)
                    {
                        if (double.TryParse(durEl.GetString(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double dur) && dur > 0)
                            return true;
                    }
                    else if (durEl.ValueKind == JsonValueKind.Number)
                    {
                        if (durEl.GetDouble() > 0) return true;
                    }
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>检查文件尾部是否有华为 LIVE_ 标记（实况照片判定）</summary>
        private static bool HasHuaweiLiveTail(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 60) return false;
                int readSize = (int)Math.Min(fs.Length, 4096);
                byte[] buf = new byte[readSize];
                fs.Seek(-readSize, SeekOrigin.End);
                fs.ReadExactly(buf, 0, readSize);
                return buf.AsSpan().IndexOf("LIVE_"u8) >= 0;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        // ── 匹配: 文件名配对 ──
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 按文件名基础部分配对图片和视频。
        /// 返回所有被配对的文件（图片和视频都返回，各自标注对方路径）。
        /// </summary>
        private static List<LivePhotoDiscoveryItem> FilenamePairing(
            List<LivePhotoDiscoveryItem> unclassified,
            string inputDirectory,
            CancellationToken ct)
        {
            var paired = new List<LivePhotoDiscoveryItem>();

            var imgDict = new Dictionary<string, LivePhotoDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
            var vidDict = new Dictionary<string, LivePhotoDiscoveryItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in unclassified)
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(item.FilePath);
                string key = PathHelper.GetPairingKey(inputDirectory, item.FilePath);

                if (ImageExtensions.Contains(ext))
                    imgDict[key] = item;
                else if (VideoExtensions.Contains(ext))
                    vidDict[key] = item;
            }

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidItem))
                {
                    // 标注图片指向视频，视频指向图片
                    kvp.Value.PairedVideoPath = vidItem.FilePath;
                    vidItem.PairedImagePath = kvp.Value.FilePath;
                    paired.Add(kvp.Value);
                    paired.Add(vidItem);
                }
            }

            return paired;
        }
    }
}

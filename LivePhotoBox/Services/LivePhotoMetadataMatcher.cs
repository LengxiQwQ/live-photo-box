using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 元数据匹配结果 — 一组通过 ContentIdentifier 或拍摄日期匹配到的照片/视频对。
    /// </summary>
    public sealed class MetadataPair
    {
        /// <summary>照片文件的完整路径。</summary>
        public required string ImagePath { get; init; }
        /// <summary>视频文件的完整路径。</summary>
        public required string VideoPath { get; init; }
        /// <summary>匹配依据（用于日志和调试）。</summary>
        public required MatchSource Source { get; init; }
    }

    /// <summary>匹配来源。</summary>
    public enum MatchSource
    {
        /// <summary>通过 Apple ContentIdentifier UUID 精确匹配。</summary>
        ContentIdentifier,
        /// <summary>通过拍摄日期 ±2 秒容差匹配。</summary>
        CreateDate
    }

    /// <summary>元数据匹配器的完整输出。</summary>
    public sealed class MetadataMatchOutput
    {
        /// <summary>通过元数据额外匹配到的照片/视频对。</summary>
        public required IReadOnlyList<MetadataPair> Pairs { get; init; }
        /// <summary>匹配后仍剩余的照片路径数。</summary>
        public required int RemainingImages { get; init; }
        /// <summary>匹配后仍剩余的视频路径数。</summary>
        public required int RemainingVideos { get; init; }
    }

    /// <summary>
    /// 实况照片元数据匹配引擎。
    /// 提供两级降级匹配策略：
    ///   1. ContentIdentifier（UUID）精确匹配 — 零歧义
    ///   2. 拍摄日期 ±2 秒容差匹配 — 时区感知
    ///
    /// 两种调用路径：
    ///   - MatchAsync：Merge 页面使用，内部启动 exiftool 提取元数据
    ///   - MatchFromAnalysis：Repair 页面使用，复用已有的 RepairAnalysisResult
    /// </summary>
    public static partial class LivePhotoMetadataMatcher
    {
        /// <summary>日期匹配的容差（秒）。Apple 实况照片的照片和视频在秒级一致，±2 秒足以覆盖微小偏差。</summary>
        private const double DateMatchToleranceSeconds = 2.0;

        // ──────────────────────────────────────────────
        //  Merge 页面路径：内部运行 exiftool 提取元数据
        // ──────────────────────────────────────────────

        /// <summary>
        /// 对未匹配的照片和视频列表运行元数据匹配。
        /// 内部启动 PersistentExifTool 批量查询 ContentIdentifier 和 CreateDate。
        /// </summary>
        /// <param name="unmatchedImagePaths">文件名匹配后未配对的照片路径</param>
        /// <param name="unmatchedVideoPaths">文件名匹配后未配对的视频路径</param>
        /// <param name="exifToolPath">exiftool.exe 的完整路径</param>
        /// <param name="token">取消令牌</param>
        /// <returns>额外匹配到的配对 + 剩余未匹配计数</returns>
        public static async Task<MetadataMatchOutput> MatchAsync(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths,
            string exifToolPath,
            CancellationToken token,
            bool enableDateMatching = false)
        {
            if (unmatchedImagePaths.Count == 0 || unmatchedVideoPaths.Count == 0)
            {
                return new MetadataMatchOutput
                {
                    Pairs = Array.Empty<MetadataPair>(),
                    RemainingImages = unmatchedImagePaths.Count,
                    RemainingVideos = unmatchedVideoPaths.Count
                };
            }

            // 合并所有待查询文件，用 PersistentExifTool 批量提取 ContentIdentifier 和 CreateDate
            var allPaths = new List<string>(unmatchedImagePaths.Count + unmatchedVideoPaths.Count);
            allPaths.AddRange(unmatchedImagePaths);
            allPaths.AddRange(unmatchedVideoPaths);

            // 构建文件路径 → 元数据的映射
            var contentIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dateMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            // 快速判断是否为图片文件（用于日期解析时区分 EXIF 本地时间 vs QuickTime UTC）
            var imagePathSet = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);

            using var exifTool = new PersistentExifTool(exifToolPath);
            foreach (var filePath in allPaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // 查询 ContentIdentifier、日期、以及 EXIF 时区偏移（仅对图片有效）
                    string output = await exifTool.SendCommandAsync(token,
                        "-j", "-ContentIdentifier", "-DateTimeOriginal", "-CreateDate",
                        "-OffsetTimeOriginal", "-OffsetTimeDigitized", filePath);
                    if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                        continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var root = doc.RootElement[0];

                    string cid = GetJsonValueAsString(root, "ContentIdentifier");
                    if (!string.IsNullOrWhiteSpace(cid))
                        contentIdMap[filePath] = cid;

                    // 优先用 DateTimeOriginal，其次 CreateDate
                    string dtoStr = GetJsonValueAsString(root, "DateTimeOriginal");
                    string cdStr = GetJsonValueAsString(root, "CreateDate");
                    string dateStr = !string.IsNullOrWhiteSpace(dtoStr) ? dtoStr : cdStr;

                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        string? offsetStr = imagePathSet.Contains(filePath)
                            ? GetJsonValueAsString(root, "OffsetTimeOriginal")
                            : null;
                        if (string.IsNullOrWhiteSpace(offsetStr))
                            offsetStr = imagePathSet.Contains(filePath)
                                ? GetJsonValueAsString(root, "OffsetTimeDigitized") : null;

                        DateTime? utcDate = ParseExifDateToUtc(dateStr, offsetStr);
                        if (utcDate.HasValue)
                            dateMap[filePath] = utcDate.Value;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Scan($"MetadataMatch: exiftool read failed for {Path.GetFileName(filePath)}: {ex.Message}", LogLevel.Warning);
                }
            }

            return MatchFromMaps(unmatchedImagePaths, unmatchedVideoPaths, contentIdMap, dateMap, enableDateMatching);
        }

        // ──────────────────────────────────────────────
        //  Repair 页面路径：复用已有的 RepairAnalysisResult
        // ──────────────────────────────────────────────

        /// <summary>
        /// 使用已有的 RepairAnalysisResult 进行元数据匹配（Repair 页面专用）。
        /// 不需要额外启动 exiftool — 分析数据已在扫描阶段提取。
        /// </summary>
        /// <param name="images">独立照片（路径 + 分析结果）</param>
        /// <param name="videos">独立视频（路径 + 分析结果）</param>
        /// <returns>额外匹配到的配对 + 剩余未匹配计数</returns>
        public static MetadataMatchOutput MatchFromAnalysis(
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> images,
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> videos,
            bool enableDateMatching = false)
        {
            // 构建 ContentIdentifier 和日期映射
            var contentIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dateMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var (path, analysis) in images)
            {
                if (!string.IsNullOrWhiteSpace(analysis.ContentIdentifier))
                    contentIdMap[path] = analysis.ContentIdentifier;

                string imgDateStr = !string.IsNullOrWhiteSpace(analysis.DateTimeOriginal)
                    ? analysis.DateTimeOriginal : analysis.CreateDate;
                DateTime? utcDate = ParseExifDateToUtc(imgDateStr, analysis.OffsetTimeOriginal);
                if (utcDate.HasValue)
                    dateMap[path] = utcDate.Value;
            }

            foreach (var (path, analysis) in videos)
            {
                if (!string.IsNullOrWhiteSpace(analysis.ContentIdentifier))
                    contentIdMap[path] = analysis.ContentIdentifier;

                string vidDateStr = !string.IsNullOrWhiteSpace(analysis.DateTimeOriginal)
                    ? analysis.DateTimeOriginal : analysis.CreateDate;
                DateTime? utcDate = ParseExifDateToUtc(vidDateStr, offsetString: null);
                if (utcDate.HasValue)
                    dateMap[path] = utcDate.Value;
            }

            var imagePaths = images.Select(x => x.path).ToList();
            var videoPaths = videos.Select(x => x.path).ToList();
            return MatchFromMaps(imagePaths, videoPaths, contentIdMap, dateMap, enableDateMatching);
        }

        // ──────────────────────────────────────────────
        //  核心匹配引擎
        // ──────────────────────────────────────────────

        /// <summary>
        /// 根据已有的元数据映射进行两级匹配。
        /// Pass 1: ContentIdentifier UUID 精确匹配
        /// Pass 2: 拍摄日期 UTC ±2 秒容差匹配
        /// </summary>
        private static MetadataMatchOutput MatchFromMaps(
            IReadOnlyList<string> imagePaths,
            IReadOnlyList<string> videoPaths,
            Dictionary<string, string> contentIdMap,
            Dictionary<string, DateTime> dateMap,
            bool enableDateMatching)
        {
            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(imagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(videoPaths, StringComparer.OrdinalIgnoreCase);

            // ── Pass 1: ContentIdentifier 精确匹配 ──
            // 构建 ContentIdentifier → 照片路径 的索引（只纳入有 UUID 的照片）
            var cidToImage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imgPath in remainingImages.ToList())
            {
                if (contentIdMap.TryGetValue(imgPath, out var cid) && !string.IsNullOrWhiteSpace(cid))
                {
                    // 同一 UUID 若有多张照片，只保留第一张
                    if (!cidToImage.ContainsKey(cid))
                        cidToImage[cid] = imgPath;
                }
            }

            foreach (var vidPath in remainingVideos.ToList())
            {
                if (contentIdMap.TryGetValue(vidPath, out var vidCid)
                    && !string.IsNullOrWhiteSpace(vidCid)
                    && cidToImage.TryGetValue(vidCid, out var matchedImgPath))
                {
                    pairs.Add(new MetadataPair
                    {
                        ImagePath = matchedImgPath,
                        VideoPath = vidPath,
                        Source = MatchSource.ContentIdentifier
                    });
                    remainingImages.Remove(matchedImgPath);
                    remainingVideos.Remove(vidPath);
                    cidToImage.Remove(vidCid);

                    LogService.Scan($"MetadataMatch: paired by ContentIdentifier — " +
                        $"{Path.GetFileName(matchedImgPath)} ↔ {Path.GetFileName(vidPath)} (CID={vidCid})");
                }
            }

            // ── Pass 2: 拍摄日期 ±2 秒容差匹配（仅在开启时执行）──
            if (enableDateMatching)
            {
            var imgDateEntries = remainingImages
                .Where(p => dateMap.ContainsKey(p))
                .Select(p => (path: p, date: dateMap[p]))
                .OrderBy(x => x.date)
                .ToList();

            var vidDateEntries = remainingVideos
                .Where(p => dateMap.ContainsKey(p))
                .Select(p => (path: p, date: dateMap[p]))
                .OrderBy(x => x.date)
                .ToList();

            var matchedVidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (imgPath, imgDate) in imgDateEntries)
            {
                if (!remainingImages.Contains(imgPath)) continue;

                // 在视频中找日期最接近且 ±2 秒内的匹配
                string? bestVidPath = null;
                double bestDiff = double.MaxValue;

                foreach (var (vidPath, vidDate) in vidDateEntries)
                {
                    if (matchedVidPaths.Contains(vidPath)) continue;
                    if (!remainingVideos.Contains(vidPath)) continue;

                    double diff = Math.Abs((imgDate - vidDate).TotalSeconds);
                    if (diff <= DateMatchToleranceSeconds && diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestVidPath = vidPath;
                    }
                }

                if (bestVidPath != null)
                {
                    pairs.Add(new MetadataPair
                    {
                        ImagePath = imgPath,
                        VideoPath = bestVidPath,
                        Source = MatchSource.CreateDate
                    });
                    remainingImages.Remove(imgPath);
                    remainingVideos.Remove(bestVidPath);
                    matchedVidPaths.Add(bestVidPath);

                    LogService.Scan($"MetadataMatch: paired by CreateDate ({bestDiff:F1}s diff) — " +
                        $"{Path.GetFileName(imgPath)} ↔ {Path.GetFileName(bestVidPath)} " +
                        $"(img={imgDate:O}, vid={dateMap[bestVidPath]:O})");
                }
            }
            } // end if enableDateMatching

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }

        // ──────────────────────────────────────────────
        //  日期解析工具
        // ──────────────────────────────────────────────

        /// <summary>
        /// 解析 exiftool 日期字符串并转换为 UTC DateTime。
        /// exiftool -j 输出示例：
        ///   - 照片 EXIF（本地时间，无偏移）: "2026:06:20 12:26:26"（需结合 OffsetTimeOriginal）
        ///   - 视频 QuickTime（UTC，无偏移）:  "2026:06:20 04:26:26"
        ///
        /// 策略：
        ///   1. 若 offsetString 有值 → 追加到日期后
        ///   2. 若日期含显式时区偏移 (+HH:MM / Z) → 直接用偏移解析
        ///   3. 若无显式偏移 → 假定 UTC（QuickTime 标准）
        ///      （照片应通过 offsetString 参数提供 OffsetTimeOriginal）
        /// </summary>
        /// <param name="exifDateString">exiftool 返回的日期字符串</param>
        /// <param name="offsetString">可选 — EXIF OffsetTimeOriginal 值，如 "+08:00"</param>
        private static DateTime? ParseExifDateToUtc(string? exifDateString, string? offsetString = null)
        {
            if (string.IsNullOrWhiteSpace(exifDateString))
                return null;

            try
            {
                string normalized = ExifDateRegex().Replace(exifDateString.Trim(), "$1-$2-$3");

                // 如果提供了 OffsetTimeOriginal 且日期字符串不含时区，则追加
                if (!string.IsNullOrWhiteSpace(offsetString))
                {
                    string cleanOffset = offsetString.Trim();
                    if (!cleanOffset.StartsWith("+") && !cleanOffset.StartsWith("-"))
                        cleanOffset = "+" + cleanOffset;

                    if (!HasExplicitOffset(normalized))
                        normalized += cleanOffset;
                }

                // 检测是否含有显式时区偏移
                if (HasExplicitOffset(normalized))
                {
                    // 有时区偏移 → 精确解析
                    if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dtoWithOffset))
                        return dtoWithOffset.UtcDateTime;
                }
                else
                {
                    // 无显式时区偏移 → 假定 UTC（QuickTime 视频标准）
                    if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var dtoUtc))
                        return dtoUtc.UtcDateTime;
                }

                return null;
            }
            catch (Exception ex)
            {
                LogService.Scan($"MetadataMatch: failed to parse date '{exifDateString}': {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>检测日期字符串是否以 +HH:MM / -HH:MM / Z 结尾。</summary>
        private static bool HasExplicitOffset(string normalizedDate)
        {
            // 匹配 "+08:00", "-05:30", "Z" 等
            return System.Text.RegularExpressions.Regex.IsMatch(
                normalizedDate, @"[+-]\d{2}:\d{2}\s*$|Z\s*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// 从 exiftool JSON 元素安全读取字符串值（兼容 string 和 number 类型）。
        /// </summary>
        private static string GetJsonValueAsString(System.Text.Json.JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return "";

            return prop.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => prop.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => prop.GetRawText(),
                _ => prop.ToString()
            };
        }

        /// <summary>
        /// 匹配 exiftool 日期格式 "YYYY:MM:DD" 的开头，替换为 "YYYY-MM-DD"。
        /// 编译一次，避免每次调用时重新编译。
        /// </summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{4}):(\d{2}):(\d{2})")]
        private static partial Regex ExifDateRegex();
    }
}

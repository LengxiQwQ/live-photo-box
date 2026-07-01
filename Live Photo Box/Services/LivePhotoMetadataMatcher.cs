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
    // 元数据匹配结果 — 一组通过 ContentIdentifier 或拍摄日期匹配到的照片/视频对。
    public sealed class MetadataPair
    {
        // 照片文件的完整路径。
        public required string ImagePath { get; init; }
        // 视频文件的完整路径。
        public required string VideoPath { get; init; }
        // 匹配依据（用于日志和调试）。
        public required MatchSource Source { get; init; }
    }

    // 匹配来源。
    public enum MatchSource
    {
        // 通过 Apple ContentIdentifier UUID 精确匹配。
        ContentIdentifier,
        // 通过元数据组合（日期 + GPS + 设备 + iOS 版本全部满足）匹配。
        MetadataCombined
    }

    // 元数据匹配器的完整输出。
    public sealed class MetadataMatchOutput
    {
        // 通过元数据额外匹配到的照片/视频对。
        public required IReadOnlyList<MetadataPair> Pairs { get; init; }
        // 匹配后仍剩余的照片路径数。
        public required int RemainingImages { get; init; }
        // 匹配后仍剩余的视频路径数。
        public required int RemainingVideos { get; init; }
    }

    // 实况照片元数据匹配引擎。
    // 提供两级降级匹配策略：
    // 1. ContentIdentifier（UUID）精确匹配 — 零歧义
    // 2. 拍摄日期 ±2 秒容差匹配 — 时区感知
    // 两种调用路径：
    // - MatchAsync：Merge 页面使用，内部启动 exiftool 提取元数据
    // - MatchFromAnalysis：Repair 页面使用，复用已有的 RepairAnalysisResult
    public static partial class LivePhotoMetadataMatcher
    {
        // 日期匹配的容差（秒）。Apple 实况照片的照片和视频在秒级一致，±2 秒足以覆盖微小偏差。
        private const double DateMatchToleranceSeconds = 2.0;

        // GPS 坐标匹配的容差（度）。5 组真实样本偏差 3~6 米，0.0001° ≈ 11m 可覆盖且有安全余量。
        private const double GpsMatchToleranceDegrees = 0.0001;

        // 设备信息匹配的分隔符，用于组合 Make + Model + Software 为匹配键。
        private const string DeviceKeySeparator = "||";

        // Apple 设备检测：至少需要满足的条件数（1 = 满足任一即可，2 = 至少两个）。
        public const int AppleDeviceMinConditions = 1;

        // Apple 设备元数据特征值。
        private const string AppleMake = "Apple";
        private const string AppleModelPrefix = "iPhone";
        private const string AppleModelPrefixIpad = "iPad";

        // ──────────────────────────────────────────────
        //  Merge 页面路径：内部运行 exiftool 提取元数据
        // ──────────────────────────────────────────────

        // 对未匹配的照片和视频列表运行元数据匹配。
        // 内部启动 PersistentExifTool 批量查询 ContentIdentifier 和 CreateDate。
        // unmatchedImagePaths: 文件名匹配后未配对的照片路径
        // unmatchedVideoPaths: 文件名匹配后未配对的视频路径
        // exifToolPath: exiftool.exe 的完整路径
        // token: 取消令牌
        // è¿å: 额外匹配到的配对 + 剩余未匹配计数
        public static async Task<MetadataMatchOutput> MatchAsync(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths,
            string exifToolPath,
            CancellationToken token,
            bool enableCombinedMatching = false,
            bool runContentIdentifier = true)
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
            var gpsMap = new Dictionary<string, (double lat, double lon)>(StringComparer.OrdinalIgnoreCase);
            var deviceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 快速判断是否为图片文件（用于日期解析时区分 EXIF 本地时间 vs QuickTime UTC）
            var imagePathSet = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);

            using var exifTool = new PersistentExifTool(exifToolPath);
            foreach (var filePath in allPaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // 基础查询：ContentIdentifier + 日期（所有模式都需要）
                    var args = new List<string> { "-j", "-ContentIdentifier",
                        "-DateTimeOriginal", "-CreateDate", "-CreationDate",
                        "-OffsetTimeOriginal", "-OffsetTimeDigitized" };

                    // 组合匹配时额外查询 GPS + 设备信息
                    if (enableCombinedMatching)
                    {
                        args.Add("-GPSLatitude");
                        args.Add("-GPSLongitude");
                        args.Add("-GPSLatitudeRef");
                        args.Add("-GPSLongitudeRef");
                        args.Add("-Make");
                        args.Add("-Model");
                        args.Add("-Software");
                    }

                    args.Add(filePath);
                    string output = await exifTool.SendCommandAsync(token, args.ToArray());
                    if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                        continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var root = doc.RootElement[0];

                    // ── ContentIdentifier ──
                    string cid = GetJsonValueAsString(root, "ContentIdentifier");
                    if (!string.IsNullOrWhiteSpace(cid))
                        contentIdMap[filePath] = cid;

                    // ── 日期提取 ──
                    // 对于 MOV 文件：优先用 CreationDate（带时区，偏差 <1s），
                    //   QuickTime:CreateDate 可能偏差 3s+（Bug 修复）
                    // 对于 JPG 文件：优先用 DateTimeOriginal + OffsetTimeOriginal
                    string dtoStr = GetJsonValueAsString(root, "DateTimeOriginal");
                    string cdStr = GetJsonValueAsString(root, "CreateDate");
                    string creationDateStr = GetJsonValueAsString(root, "CreationDate");

                    string? dateStr = null;
                    string? offsetStr = null;
                    bool isImage = imagePathSet.Contains(filePath);

                    if (!string.IsNullOrWhiteSpace(creationDateStr))
                    {
                        // CreationDate 已自带时区偏移（如 "2025:12:19 18:14:56+07:00"）
                        dateStr = creationDateStr;
                        // 不需要额外 offsetStr，CreationDate 自带偏移
                    }
                    else if (isImage && !string.IsNullOrWhiteSpace(dtoStr))
                    {
                        dateStr = dtoStr;
                        offsetStr = GetJsonValueAsString(root, "OffsetTimeOriginal");
                        if (string.IsNullOrWhiteSpace(offsetStr))
                            offsetStr = GetJsonValueAsString(root, "OffsetTimeDigitized");
                    }
                    else if (!string.IsNullOrWhiteSpace(cdStr))
                    {
                        // 回退：JPG CreateDate 或无 CreationDate 的 MOV
                        dateStr = cdStr;
                        if (isImage)
                        {
                            offsetStr = GetJsonValueAsString(root, "OffsetTimeOriginal");
                            if (string.IsNullOrWhiteSpace(offsetStr))
                                offsetStr = GetJsonValueAsString(root, "OffsetTimeDigitized");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(dateStr))
                    {
                        DateTime? utcDate = ParseExifDateToUtc(dateStr, offsetStr);
                        if (utcDate.HasValue)
                            dateMap[filePath] = utcDate.Value;
                    }

                    // ── GPS 坐标提取 ──
                    if (enableCombinedMatching)
                    {
                        string? gpsLatStr = GetJsonValueAsString(root, "GPSLatitude");
                        string? gpsLonStr = GetJsonValueAsString(root, "GPSLongitude");
                        if (!string.IsNullOrWhiteSpace(gpsLatStr) && !string.IsNullOrWhiteSpace(gpsLonStr))
                        {
                            string gpsLatRef = GetJsonValueAsString(root, "GPSLatitudeRef");
                            string gpsLonRef = GetJsonValueAsString(root, "GPSLongitudeRef");
                            double? lat = ParseDmsToDecimalDegrees(gpsLatStr, gpsLatRef);
                            double? lon = ParseDmsToDecimalDegrees(gpsLonStr, gpsLonRef);
                            if (lat.HasValue && lon.HasValue)
                                gpsMap[filePath] = (lat.Value, lon.Value);
                        }

                        // ── 设备信息提取 ──
                        string make = GetJsonValueAsString(root, "Make")?.Trim() ?? "";
                        string model = GetJsonValueAsString(root, "Model")?.Trim() ?? "";
                        string software = GetJsonValueAsString(root, "Software")?.Trim() ?? "";
                        string deviceKey = BuildDeviceKey(make, model, software);
                        if (!string.IsNullOrWhiteSpace(deviceKey))
                            deviceMap[filePath] = deviceKey;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Scan($"MetadataMatch: exiftool read failed for {Path.GetFileName(filePath)}: {ex.Message}", LogLevel.Warning);
                }
            }

            return MatchFromMaps(unmatchedImagePaths, unmatchedVideoPaths,
                contentIdMap, dateMap, gpsMap, deviceMap, enableCombinedMatching, runContentIdentifier);
        }

        // ──────────────────────────────────────────────
        //  Repair 页面路径：复用已有的 RepairAnalysisResult
        // ──────────────────────────────────────────────

        // 使用已有的 RepairAnalysisResult 进行元数据匹配（Repair 页面专用）。
        // 不需要额外启动 exiftool — 分析数据已在扫描阶段提取。
        // images: 独立照片（路径 + 分析结果）
        // videos: 独立视频（路径 + 分析结果）
        // è¿å: 额外匹配到的配对 + 剩余未匹配计数
        public static MetadataMatchOutput MatchFromAnalysis(
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> images,
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> videos,
            bool enableCombinedMatching = false,
            bool runContentIdentifier = true)
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
            // Repair 页面暂不支持 GPS/设备匹配（Phase 2），传空映射
            return MatchFromMaps(imagePaths, videoPaths, contentIdMap, dateMap,
                new Dictionary<string, (double lat, double lon)>(),
                new Dictionary<string, string>(),
                enableCombinedMatching, runContentIdentifier);
        }

        // ──────────────────────────────────────────────
        //  核心匹配引擎
        // ──────────────────────────────────────────────

        // 根据已有的元数据映射进行两级匹配。
        // Pass 1: ContentIdentifier UUID 精确匹配
        // Pass 2: 拍摄日期 UTC ±2 秒容差匹配
        private static MetadataMatchOutput MatchFromMaps(
            IReadOnlyList<string> imagePaths,
            IReadOnlyList<string> videoPaths,
            Dictionary<string, string> contentIdMap,
            Dictionary<string, DateTime> dateMap,
            Dictionary<string, (double lat, double lon)> gpsMap,
            Dictionary<string, string> deviceMap,
            bool enableCombinedMatching,
            bool runContentIdentifier)
        {
            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(imagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(videoPaths, StringComparer.OrdinalIgnoreCase);

            // ── Pass 1: ContentIdentifier 精确匹配 ──
            if (runContentIdentifier)
            {
            var cidToImage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imgPath in remainingImages.ToList())
            {
                if (contentIdMap.TryGetValue(imgPath, out var cid) && !string.IsNullOrWhiteSpace(cid))
                {
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
            } // end if runContentIdentifier

            // ── 以下 Pass 仅在启用组合匹配时执行 ──
            if (!enableCombinedMatching)
            {
                return new MetadataMatchOutput
                {
                    Pairs = pairs,
                    RemainingImages = remainingImages.Count,
                    RemainingVideos = remainingVideos.Count
                };
            }

            // ── Pass 2: 元数据组合匹配（全部条件同时满足才算通过）──
            // 日期 ±2s 且 GPS ±0.0001° 且 设备型号 且 iOS 版本
            {
            var matchedVidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var imgPath in remainingImages.ToList())
            {
                if (!remainingImages.Contains(imgPath)) continue;

                bool imgHasDate = dateMap.TryGetValue(imgPath, out var imgDate);
                bool imgHasGps = gpsMap.TryGetValue(imgPath, out var imgCoord);
                bool imgHasDevice = deviceMap.TryGetValue(imgPath, out var imgDevKey);

                string? bestVidPath = null;
                double bestScore = double.MaxValue;
                int bestPassed = 0, bestTotal = 0;

                foreach (var vidPath in remainingVideos)
                {
                    if (matchedVidPaths.Contains(vidPath)) continue;

                    // ── 逐项检查，全部满足才算通过 ──
                    int passed = 0;
                    int total = 0;
                    double score = 0;

                    // 1) 拍摄日期
                    bool vidHasDate = dateMap.TryGetValue(vidPath, out var vidDate);
                    if (imgHasDate && vidHasDate)
                    {
                        total++;
                        double diff = Math.Abs((imgDate - vidDate).TotalSeconds);
                        if (diff <= DateMatchToleranceSeconds)
                        {
                            passed++;
                            score += diff;  // 越小越好
                        }
                    }

                    // 2) GPS 坐标
                    bool vidHasGps = gpsMap.TryGetValue(vidPath, out var vidCoord);
                    if (imgHasGps && vidHasGps)
                    {
                        total++;
                        double dLat = Math.Abs(imgCoord.lat - vidCoord.lat);
                        double dLon = Math.Abs(imgCoord.lon - vidCoord.lon);
                        if (dLat <= GpsMatchToleranceDegrees && dLon <= GpsMatchToleranceDegrees)
                        {
                            passed++;
                            score += Math.Sqrt(dLat * dLat + dLon * dLon) * 111320;  // 转为米
                        }
                    }

                    // 3) 设备信息（Make + Model + Software）
                    bool vidHasDevice = deviceMap.TryGetValue(vidPath, out var vidDevKey);
                    if (imgHasDevice && vidHasDevice)
                    {
                        total++;
                        if (string.Equals(imgDevKey, vidDevKey, StringComparison.OrdinalIgnoreCase))
                            passed++;
                    }

                    // 全部可用条件都通过，且至少要有 2 个条件参与判断（防误配）
                    if (total >= 2 && passed == total && score < bestScore)
                    {
                        bestScore = score;
                        bestVidPath = vidPath;
                        bestPassed = passed;
                        bestTotal = total;
                    }
                }

                if (bestVidPath != null)
                {
                    pairs.Add(new MetadataPair
                    {
                        ImagePath = imgPath,
                        VideoPath = bestVidPath,
                        Source = MatchSource.MetadataCombined
                    });
                    remainingImages.Remove(imgPath);
                    remainingVideos.Remove(bestVidPath);
                    matchedVidPaths.Add(bestVidPath);

                    LogService.Scan($"MetadataMatch: paired by Combined ({bestPassed}/{bestTotal} criteria) — " +
                        $"{Path.GetFileName(imgPath)} ↔ {Path.GetFileName(bestVidPath)}");
                }
            }
            } // end Pass 2 (Combined Metadata)

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }

        // ──────────────────────────────────────────────
        //  GPS 坐标解析工具
        // ──────────────────────────────────────────────

        // 解析 exiftool DMS 格式坐标字符串为十进制角度。
        // 支持的格式：
        //   "13 deg 44' 56.81\" N"  — exiftool Composite 格式（含方向）
        //   "13 deg 44' 56.81\""    — EXIF 原始格式（无方向，需 refStr）
        //   "13.749114"             — 已是十进制
        // dmsStr: 度分秒坐标字符串
        // refStr: 方向参考（"N"/"S"/"E"/"W"），dmsStr 不含方向时使用
        private static double? ParseDmsToDecimalDegrees(string dmsStr, string refStr)
        {
            if (string.IsNullOrWhiteSpace(dmsStr))
                return null;

            dmsStr = dmsStr.Trim();

            // 尝试直接解析为小数（已是十进制）
            if (double.TryParse(dmsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double decimalDegrees))
            {
                // 应用方向符号
                if (!string.IsNullOrWhiteSpace(refStr))
                {
                    refStr = refStr.Trim().ToUpperInvariant();
                    if (refStr == "S" || refStr == "W")
                        decimalDegrees = -decimalDegrees;
                }
                return decimalDegrees;
            }

            try
            {
                // 匹配 DMS 格式："DD deg MM' SS.SS\" 方向"
                // 例如 "13 deg 44' 56.81\" N" 或 "13 deg 44' 56.81\""
                var match = System.Text.RegularExpressions.Regex.Match(dmsStr,
                    @"(\d+)\s*deg\s+(\d+)\s*'\s*([\d.]+)\s*""\s*([NSEWnsew]?)",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                if (match.Success)
                {
                    double degrees = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    double minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

                    double result = degrees + minutes / 60.0 + seconds / 3600.0;

                    // 确定方向：优先用 dmsStr 中的方向字母，其次用 refStr
                    string direction = match.Groups[4].Value;
                    if (string.IsNullOrWhiteSpace(direction))
                        direction = refStr?.Trim() ?? "";

                    direction = direction.ToUpperInvariant();
                    if (direction == "S" || direction == "W")
                        result = -result;

                    return result;
                }
            }
            catch (Exception ex)
            {
                LogService.Scan($"GPS parse failed for '{dmsStr}': {ex.Message}", LogLevel.Warning);
            }

            return null;
        }

        // 构建设备匹配键：Make + Model + Software 组合。
        // 返回标准化后的键字符串，若全部为空则返回空字符串。
        private static string BuildDeviceKey(string make, string model, string software)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(make))
                parts.Add(make.Trim());
            if (!string.IsNullOrWhiteSpace(model))
                parts.Add(model.Trim());
            // Software（iOS 版本）作为可选的第三级区分
            // 注意：不在 matches 中对 software 做过多依赖，因为同一设备可能有不同 iOS 版本
            if (!string.IsNullOrWhiteSpace(software))
                parts.Add(software.Trim());

            return parts.Count > 0 ? string.Join(DeviceKeySeparator, parts) : "";
        }

        // ──────────────────────────────────────────────
        //  Apple 设备检测
        // ──────────────────────────────────────────────

        // 判断文件元数据是否来自 Apple 设备（iPhone/iPad）。
        // 五个独立条件，任一满足即判定为 Apple 设备：
        //   1. Make == "Apple"
        //   2. Model 以 "iPhone" 或 "iPad" 开头
        //   3. Software 匹配 iOS 版本格式（如 "18.3.1"）
        //   4. ContentIdentifier 存在（Apple 实况照片特有 UUID）
        //   5. LivePhotoAuto 标记存在（QuickTime 实况照片标记）
        public static bool IsAppleDevice(string? make, string? model, string? software, string? contentIdentifier, string? livePhotoAuto)
        {
            int conditionsMet = 0;

            // 条件 1：Make == "Apple"
            if (!string.IsNullOrWhiteSpace(make) &&
                string.Equals(make.Trim(), AppleMake, StringComparison.OrdinalIgnoreCase))
            {
                conditionsMet++;
            }

            // 条件 2：Model 以 "iPhone" 或 "iPad" 开头
            if (!string.IsNullOrWhiteSpace(model))
            {
                string m = model.Trim();
                if (m.StartsWith(AppleModelPrefix, StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith(AppleModelPrefixIpad, StringComparison.OrdinalIgnoreCase))
                {
                    conditionsMet++;
                }
            }

            // 条件 3：Software 匹配 iOS 版本格式（如 "18.3.1"）
            if (!string.IsNullOrWhiteSpace(software))
            {
                if (IosVersionRegex().IsMatch(software.Trim()))
                    conditionsMet++;
            }

            // 条件 4：ContentIdentifier 存在（Apple 实况照片标识符）
            if (!string.IsNullOrWhiteSpace(contentIdentifier))
            {
                conditionsMet++;
            }

            // 条件 5：LivePhotoAuto 标记存在（QuickTime 实况照片标记）
            if (!string.IsNullOrWhiteSpace(livePhotoAuto))
            {
                conditionsMet++;
            }

            return conditionsMet >= AppleDeviceMinConditions;
        }

        // 匹配 iOS 版本字符串：可选的 "iOS " 前缀 + 数字.数字(.数字)?
        [System.Text.RegularExpressions.GeneratedRegex(@"^(iOS\s*)?\d+\.\d+(\.\d+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
        private static partial Regex IosVersionRegex();

        // 批量查询文件是否为 Apple 设备（通过 exiftool 查询 Make/Model/Software/ContentIdentifier）。
        // 返回 Apple 设备的文件路径集合。
        public static async Task<HashSet<string>> FilterAppleDevicesAsync(
            IReadOnlyList<string> filePaths,
            PersistentExifTool exifTool,
            CancellationToken token)
        {
            var appleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in filePaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string output = await exifTool.SendCommandAsync(token,
                        "-j", "-Make", "-Model", "-Software", "-ContentIdentifier", "-LivePhotoAuto", filePath);
                    if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                        continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var root = doc.RootElement[0];

                    string make = GetJsonValueAsString(root, "Make");
                    string model = GetJsonValueAsString(root, "Model");
                    string software = GetJsonValueAsString(root, "Software");
                    string cid = GetJsonValueAsString(root, "ContentIdentifier");
                    string lpa = GetJsonValueAsString(root, "LivePhotoAuto");

                    if (IsAppleDevice(make, model, software, cid, lpa))
                        appleFiles.Add(filePath);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 单个文件查询失败不影响整体 */ }
            }

            return appleFiles;
        }

        // ──────────────────────────────────────────────
        //  日期解析工具
        // ──────────────────────────────────────────────

        // 解析 exiftool 日期字符串并转换为 UTC DateTime。
        // exiftool -j 输出示例：
        // - 照片 EXIF（本地时间，无偏移）: "2026:06:20 12:26:26"（需结合 OffsetTimeOriginal）
        // - 视频 QuickTime（UTC，无偏移）:  "2026:06:20 04:26:26"
        // 策略：
        // 1. 若 offsetString 有值 → 追加到日期后
        // 2. 若日期含显式时区偏移 (+HH:MM / Z) → 直接用偏移解析
        // 3. 若无显式偏移 → 假定 UTC（QuickTime 标准）
        // （照片应通过 offsetString 参数提供 OffsetTimeOriginal）
        // exifDateString: exiftool 返回的日期字符串
        // offsetString: 可选 — EXIF OffsetTimeOriginal 值，如 "+08:00"
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

        // 检测日期字符串是否以 +HH:MM / -HH:MM / Z 结尾。
        private static bool HasExplicitOffset(string normalizedDate)
        {
            // 匹配 "+08:00", "-05:30", "Z" 等
            return System.Text.RegularExpressions.Regex.IsMatch(
                normalizedDate, @"[+-]\d{2}:\d{2}\s*$|Z\s*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        // 从 exiftool JSON 元素安全读取字符串值（兼容 string 和 number 类型）。
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

        // 匹配 exiftool 日期格式 "YYYY:MM:DD" 的开头，替换为 "YYYY-MM-DD"。
        // 编译一次，避免每次调用时重新编译。
        [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{4}):(\d{2}):(\d{2})")]
        private static partial Regex ExifDateRegex();
    }
}

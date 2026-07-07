using LivePhotoBox.Models;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LivePhotoBox.Services
{
    // 拆分扫描时识别出的潜在实况照片文件信息。
    public sealed class LivePhotoSplitFileInfo
    {
        // 源文件完整路径。
        public required string SourcePath { get; init; }
        // 文件字节大小。
        public required long FileSizeBytes { get; init; }
        // 追加在 JPEG 尾部的 MP4 视频段字节数（0 = 未解析或无视频）。
        public long AppendedVideoLength { get; init; }
    }

    // 拆分扫描结果。
    public sealed class LivePhotoSplitScanResult
    {
        // 扫描出的实况照片文件列表。
        public required IReadOnlyList<LivePhotoSplitFileInfo> Files { get; init; }
        // 识别为实况照片的文件数。
        public required int RecognizedCount { get; init; }
        // 跳过的普通图片文件数。
        public required int SkippedCount { get; init; }
    }

    // 实况照片拆分扫描服务。
    // 遍历目录中的 JPEG 文件，通过在文件头部搜索厂商 XMP 属性名/元素名
    // 来识别嵌有视频尾部的实况照片（Android 标准）。
    // 检测标记覆盖：Google Camera V1/V2 (Pixel)、Samsung Galaxy、
    //   OPPO/OnePlus O-Live、Xiaomi Mi Motion Photo、本应用自产文件。
    public static class LivePhotoSplitScanService
    {
        // 统一与 SplitService 相同的 1MB 探测深度，避免遗漏包含较大 EXIF 的实况照片
        private const int MetadataProbeBytes = 1024 * 1024;
        private const int MetadataCheckInterval = 4;

        // 最小的合法 JPEG 体积（包含 SOI/EOI 及必要元数据），低于此值不可能是实况照片
        private const long MinImageBytes = 4 * 1024;
        // 视频流最小体积，低于此值也不可能是合法的实况照片
        private const long MinVideoBytes = 4 * 1024;

        // ===========================================================================
        // 实况照片 XMP 特征标记（字节级搜索，按命中率从高到低排列）
        // ===========================================================================
        // 第一组：XMP 属性 / 元素名（短字符串，兼容性最好，经过实战验证）
        //   这些是 XMP RDF 中的属性名和元素名。短小精悍，不受命名空间 URI
        //   格式差异（空格、引号、斜杠、https/http 等）的影响。
        //
        // 第二组：厂商属性名前缀（更精确，覆盖 OPPO / 小米私有协议）
        //
        // 第三组：命名空间 URI（最精确但受格式差异影响，作为兜底补充）
        //   仅包含本应用自产文件的 LivePhotoBox URI——这是我们自己写的，
        //   格式完全确定，不存在兼容性问题。
        // ===========================================================================
        private static readonly byte[][] MetadataMarkers =
        [
            // ━━ 第一组：Google 标准属性名（MicroVideo V1 / MotionPhoto V2）━━
            Encoding.ASCII.GetBytes("GCamera:MotionPhoto"),
            Encoding.ASCII.GetBytes("GCamera:MicroVideo"),
            Encoding.ASCII.GetBytes("MicroVideoOffset"),
            Encoding.ASCII.GetBytes("Container:Directory"),
            Encoding.ASCII.GetBytes("MotionPhoto"),

            // ━━ 第二组：OPPO / 小米 私有属性名 ━━━━━━━━━━━━━━━━━━━━━━━━━
            Encoding.ASCII.GetBytes("OpCamera:VideoLength"),
            Encoding.ASCII.GetBytes("OpCamera:MotionPhotoOwner"),
            Encoding.ASCII.GetBytes("MiCamera:VideoLength"),

            // ━━ 第三组：本应用自产标记（命名空间 URI，格式完全可控）━━━━
            Encoding.ASCII.GetBytes("xmlns:LivePhotoBox=\"https://github.com/LengxiQwQ/live-photo-box\""),
        ];

        // ===========================================================================
        // 视频偏移量正则（覆盖全部已知厂商格式）
        // ===========================================================================
        private static readonly Regex MicroVideoOffsetRegex = new(
            "GCamera:MicroVideoOffset=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoLengthRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Length=\"(?<value>\\d+)\"|Item:Length=\"(?<value>\\d+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        private static readonly Regex OppoVideoLengthRegex = new(
            "OpCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MiCameraVideoLengthRegex = new(
            "MiCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // 扫描指定目录，检测所有 JPEG 文件中的实况照片特征。
        // 先快速枚举所有图片文件，再逐个通过字节流匹配 XMP 属性名进行检测。
        // 支持递归扫描与流式进度报告。
        // inputDirectory: 要扫描的输入目录。
        // cancellationToken: 取消令牌。抛出 OperationCanceledException 表示扫描被取消。
        // progress: 批量进度报告（total, completed, recognized, skipped）。
        // itemProgress: 单个文件识别报告（每次发现实况照片时触发）。
        public static LivePhotoSplitScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null,
            IProgress<LivePhotoSplitFileInfo>? itemProgress = null)
        {
            LogService.Scan($"Split scan started. Directory: {inputDirectory}");
            progress?.Report(new WorkProgressSnapshot(0, 0));

            var candidates = new List<string>();
            int enumerated = 0;
            try
            {
                bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.*", searchOption))
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
                LogService.Scan($"Access denied to directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (IOException ex)
            {
                LogService.Scan($"IO error scanning directory: {inputDirectory}", LogLevel.Error, ex);
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }
            catch (OperationCanceledException)
            {
                LogService.Scan("Split scan cancelled");
                throw;
            }

            int total = candidates.Count;
            if (total == 0)
            {
                LogService.Scan($"No image files found in directory: {inputDirectory}");
                progress?.Report(new WorkProgressSnapshot(0, enumerated));
                return new LivePhotoSplitScanResult { Files = [], RecognizedCount = 0, SkippedCount = 0 };
            }

            LogService.Scan($"Found {total} image files, starting LivePhoto detection");

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
                    // 顺便解析视频段长度，存入扫描结果，避免灯箱重复读取
                    long videoLen = 0;
                    try
                    {
                        var metadataText = LivePhotoSplitService.ReadMetadataTextSync(path);
                        videoLen = LivePhotoSplitService.GetAppendedVideoLength(metadataText);
                    }
                    catch { videoLen = 0; }

                    var info = new LivePhotoSplitFileInfo
                    {
                        SourcePath = path,
                        FileSizeBytes = fileInfo.Length,
                        AppendedVideoLength = videoLen > 0 ? videoLen : 0
                    };
                    files.Add(info);
                    itemProgress?.Report(info);
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

            LogService.Scan($"Split scan completed. Found {recognizedCount} LivePhotos, skipped {skippedCount} regular images");

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

        // 判断一个 JPEG 文件是否嵌有实况照片视频尾部。
        //
        // 判定策略：字节级标记匹配（扫描阶段"宽进"原则）
        // 在文件头部 1MB 中搜索厂商 XMP 属性名/元素名（短字符串），
        // 命中任一标记即视为实况照片候选。偏移量解析仅用于日志，不阻塞。
        //
        // 注意：扫描阶段的职责是列出候选文件；拆分阶段 SplitService 会做
        // 精确的偏移量提取和校验，所以这里不需要严格验证偏移量。
        //
        // ── 设计决策：为什么用短属性名而不是命名空间 URI ──────────────────
        //
        // 曾尝试用完整的 XMP 命名空间 URI 做标记（如 xmlns:GCamera="http://...")，
        // 理论上更精确，但实测失败：不同厂商/设备/Android 版本写出的 XMP 中，
        // URI 的格式存在细微差异（空格、引号变体、斜杠、http/https 等），
        // 导致 62 字节的精确匹配大量漏判。只有本应用自产文件（LivePhotoBox URI）
        // 因为格式完全可控而能稳定匹配。
        //
        // 相比之下，短属性名（GCamera:MotionPhoto、Container:Directory 等）
        // 是 Google XMP 规范规定的字段名，Android ExifInterface 自身也用它们
        // 判定实况照片。跨厂商、跨版本兼容性最好，经实战验证。
        //
        // TODO: 如果以后需要更精确的厂商识别，可以在命中标记后尝试解析
        // 命名空间 URI 做二次确认（用 Regex 而非精确字节匹配，容忍格式差异）。
        // 目前直接放行即可，SplitService 会做最终校验。
        // ───────────────────────────────────────────────────────────────────
        public static bool IsLikelyLivePhoto(string path, long fileSize)
        {
            // ── 第一步：基础体积过滤 ──────────────────────────────────────
            if (fileSize <= MinImageBytes + MinVideoBytes) return false;

            int probeSize = (int)Math.Min(fileSize, MetadataProbeBytes);
            byte[] headBuffer = ArrayPool<byte>.Shared.Rent(probeSize);
            int headRead = 0;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

                headRead = stream.Read(headBuffer, 0, probeSize);
                if (headRead <= 0) return false;

                // ── 第二步：短属性名标记匹配（核心判定）───────────────────
                var headData = new ReadOnlySpan<byte>(headBuffer, 0, headRead);
                bool hasMarker = false;
                foreach (var marker in MetadataMarkers)
                {
                    if (headData.IndexOf(marker) >= 0)
                    {
                        hasMarker = true;
                        break;
                    }
                }

                if (!hasMarker) return false;

                // ── 第三步：偏移量复查（仅日志，不阻塞）───────────────────
                // 尝试解析视频偏移量。成功→记录合法/非法；失败→标记已命中，照常放行
                string metadataText = Encoding.UTF8.GetString(headBuffer, 0, headRead);
                long? parsedOffset = TryParseVideoOffset(metadataText);

                if (parsedOffset.HasValue && parsedOffset.Value > 0)
                {
                    long videoLen = parsedOffset.Value;
                    if (videoLen < MinVideoBytes || videoLen >= fileSize
                        || (fileSize - videoLen) < MinImageBytes)
                    {
                        LogService.Scan(
                            $"Marker matched but offset looks invalid for '{Path.GetFileName(path)}' " +
                            $"(offset={videoLen}, fileSize={fileSize}) — passing through anyway.",
                            LogLevel.Debug);
                    }
                }
                else
                {
                    LogService.Scan(
                        $"Marker matched but offset unparsed for '{Path.GetFileName(path)}' — " +
                        "passing through (marker-only match).",
                        LogLevel.Debug);
                }

                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Exception ex)
            {
                LogService.Scan($"Unexpected error checking LivePhoto candidate: {Path.GetFileName(path)}", LogLevel.Debug, ex);
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headBuffer);
            }
        }

        // 依次尝试所有已知厂商的实况照片视频偏移量格式：
        //   MicroVideo V1 → MotionPhoto V2 → OPPO O-Live → 小米
        // 返回第一个成功解析的有效偏移量（字节），或 null。
        private static long? TryParseVideoOffset(string metadataText)
        {
            if (string.IsNullOrEmpty(metadataText)) return null;

            // Google MicroVideo V1: GCamera:MicroVideoOffset="12345"
            var microMatch = MicroVideoOffsetRegex.Match(metadataText);
            if (microMatch.Success && long.TryParse(microMatch.Groups["value"].Value, out long microOffset) && microOffset > 0)
            {
                return microOffset;
            }

            // Google MotionPhoto V2: Item:Semantic="MotionPhoto" ... Item:Length="12345"
            var motionMatch = MotionPhotoLengthRegex.Match(metadataText);
            if (motionMatch.Success && long.TryParse(motionMatch.Groups["value"].Value, out long motionOffset) && motionOffset > 0)
            {
                return motionOffset;
            }

            // OPPO / OnePlus O-Live Photo: OpCamera:VideoLength="12345"
            var oppoMatch = OppoVideoLengthRegex.Match(metadataText);
            if (oppoMatch.Success && long.TryParse(oppoMatch.Groups["value"].Value, out long oppoOffset) && oppoOffset > 0)
            {
                return oppoOffset;
            }

            // 小米 Mi Motion Photo: MiCamera:VideoLength="12345"
            var miMatch = MiCameraVideoLengthRegex.Match(metadataText);
            if (miMatch.Success && long.TryParse(miMatch.Groups["value"].Value, out long miOffset) && miOffset > 0)
            {
                return miOffset;
            }

            return null;
        }
    }
}

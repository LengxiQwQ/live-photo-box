using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
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
        // 通过 vivo JPEG 尾部 JSON / MP4 uuid box (com.android.camera.livephoto ID) 匹配。
        VivoLivePhoto
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
    // 仅通过唯一标识符精确匹配，无日期/GPS 兜底：
    //   - ContentIdentifier UUID: Apple Live Photo 配对
    //   - com.android.camera.livephoto ID: vivo 双文件配对
    // 两种调用路径：
    //   - MatchAsync: Merge 页面，使用 Native SourceInspector 提取 ContentIdentifier
    //   - MatchFromAnalysis: Repair 页面，复用已有的 RepairAnalysisResult
    //   - MatchVivo: Merge 页面，纯文件 I/O 解析 vivo JSON 尾部
    public static partial class LivePhotoMetadataMatcher
    {
        /// <summary>
        /// Validates one dual-file candidate from metadata stored in both files.
        /// A matching filename is never protocol evidence: vivo requires identical
        /// com.android.camera.livephoto IDs, and Apple requires identical non-empty
        /// ContentIdentifier values.
        /// </summary>
        public static async Task<LivePhotoProtocolType> DetectDualFileProtocolAsync(
            string imagePath,
            string videoPath,
            CancellationToken token = default)
        {
            if (!File.Exists(imagePath) || !File.Exists(videoPath))
                return LivePhotoProtocolType.Unknown;

            string imageBaseName = Path.GetFileNameWithoutExtension(imagePath);
            string videoBaseName = Path.GetFileNameWithoutExtension(videoPath);
            if (!imageBaseName.Equals(videoBaseName, StringComparison.OrdinalIgnoreCase))
                return LivePhotoProtocolType.Unknown;

            token.ThrowIfCancellationRequested();

            try
            {
                var inspector = new SourceInspector();
                SourceMediaFacts facts = await inspector.InspectAsync(imagePath, videoPath, token).ConfigureAwait(false);
                if (facts.Protocol == SourceProtocol.AppleLivePhoto)
                    return LivePhotoProtocolType.Apple;
                if (facts.Protocol == SourceProtocol.VivoLegacyDualFile)
                    return LivePhotoProtocolType.Vivo;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fallback to Unknown */ }

            return LivePhotoProtocolType.Unknown;
        }

        // ── CID 匹配（Apple Live Photo）──
        // 使用 Native SourceInspector 查询 ContentIdentifier。
        // unmatchedImagePaths: 文件名匹配后未配对的照片路径
        // unmatchedVideoPaths: 文件名匹配后未配对的视频路径
        // token: 取消令牌
        // 返回: 额外匹配到的配对 + 剩余未匹配计数
        // ContentIdentifier UUID 精确匹配 — Apple Live Photo 专用。
        // 查询所有未配对的图片和视频的 ContentIdentifier 字段，UUID 一致则配对。
        public static async Task<MetadataMatchOutput> MatchAsync(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths,
            CancellationToken token = default,
            Action<int>? onFileProcessed = null)
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

            var allPaths = new List<string>(unmatchedImagePaths.Count + unmatchedVideoPaths.Count);
            allPaths.AddRange(unmatchedImagePaths);
            allPaths.AddRange(unmatchedVideoPaths);

            var contentIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inspector = new SourceInspector();

            int processed = 0;
            foreach (var filePath in allPaths)
            {
                token.ThrowIfCancellationRequested();
                onFileProcessed?.Invoke(++processed);
                try
                {
                    SourceMediaFacts facts = await inspector.InspectAsync(filePath, null, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(facts.PairingIdentifier))
                        contentIdMap[filePath] = facts.PairingIdentifier;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Scan($"CID match: native inspect failed for {Path.GetFileName(filePath)}: {ex.Message}", LogLevel.Warning);
                }
            }

            // Match by UUID
            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(unmatchedVideoPaths, StringComparer.OrdinalIgnoreCase);

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
                    try
                    {
                        SourceMediaFacts dualFacts = await inspector.InspectAsync(matchedImgPath, vidPath, token).ConfigureAwait(false);
                        if (dualFacts.Protocol == SourceProtocol.AppleLivePhoto)
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
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Candidate dual file did not validate as Apple Live Photo, skip pairing as ContentIdentifier
                    }
                }
            }

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }

        // ──────────────────────────────────────────────
        //  Repair 页面路径：复用已有的 RepairAnalysisResult
        // ──────────────────────────────────────────────

        // 使用已有的 RepairAnalysisResult 进行元数据匹配（Repair 页面专用）。
        // 不需要额外启动 exiftool — 分析数据已在扫描阶段提取。
        // images: 独立照片（路径 + 分析结果）
        // videos: 独立视频（路径 + 分析结果）
        // 返回: 额外匹配到的配对 + 剩余未匹配计数
        // Repair: ContentIdentifier UUID exact match only.
        public static MetadataMatchOutput MatchFromAnalysis(
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> images,
            IReadOnlyList<(string path, RepairAnalysisResult analysis)> videos)
        {
            var contentIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (path, analysis) in images)
            {
                if (!string.IsNullOrWhiteSpace(analysis.ContentIdentifier))
                    contentIdMap[path] = analysis.ContentIdentifier;
            }

            foreach (var (path, analysis) in videos)
            {
                if (!string.IsNullOrWhiteSpace(analysis.ContentIdentifier))
                    contentIdMap[path] = analysis.ContentIdentifier;
            }

            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(
                images.Select(x => x.path), StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(
                videos.Select(x => x.path), StringComparer.OrdinalIgnoreCase);

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
                }
            }

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }
        // ── vivo 双文件配对 ─────────────────────────────────────────

        /// <summary>
        /// Match unmatched photos and videos by vivo live photo pairing ID.
        /// Extracts pairing identifier facts using SourceInspector and pairs files
        /// with matching IDs after confirming the candidate pair via dual-file inspection.
        /// </summary>
        public static MetadataMatchOutput MatchVivo(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths,
            Action<int>? onFileProcessed = null)
        {
            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(unmatchedVideoPaths, StringComparer.OrdinalIgnoreCase);

            if (remainingImages.Count == 0 || remainingVideos.Count == 0)
            {
                return new MetadataMatchOutput
                {
                    Pairs = pairs,
                    RemainingImages = remainingImages.Count,
                    RemainingVideos = remainingVideos.Count
                };
            }

            var inspector = new SourceInspector();
            var imgIdToPath = new Dictionary<string, string>(StringComparer.Ordinal);
            int processed = 0;

            foreach (var imgPath in remainingImages.ToList())
            {
                onFileProcessed?.Invoke(++processed);
                string ext = Path.GetExtension(imgPath).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg") continue;

                try
                {
                    SourceMediaFacts facts = inspector.InspectAsync(imgPath).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(facts.PairingIdentifier) && facts.PairingIdentifier.Length > 8)
                    {
                        if (!imgIdToPath.ContainsKey(facts.PairingIdentifier))
                            imgIdToPath[facts.PairingIdentifier] = imgPath;
                    }
                }
                catch
                {
                    // Skip if inspection fails
                }
            }

            if (imgIdToPath.Count == 0)
            {
                return new MetadataMatchOutput
                {
                    Pairs = pairs,
                    RemainingImages = remainingImages.Count,
                    RemainingVideos = remainingVideos.Count
                };
            }

            foreach (var vidPath in remainingVideos.ToList())
            {
                onFileProcessed?.Invoke(++processed);
                string ext = Path.GetExtension(vidPath).ToLowerInvariant();
                if (ext != ".mp4") continue;

                try
                {
                    SourceMediaFacts facts = inspector.InspectAsync(vidPath).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(facts.PairingIdentifier) &&
                        imgIdToPath.TryGetValue(facts.PairingIdentifier, out var matchedImg))
                    {
                        try
                        {
                            SourceMediaFacts dualFacts = inspector.InspectAsync(matchedImg, vidPath).GetAwaiter().GetResult();
                            if (dualFacts.Protocol == SourceProtocol.VivoLegacyDualFile)
                            {
                                pairs.Add(new MetadataPair
                                {
                                    ImagePath = matchedImg,
                                    VideoPath = vidPath,
                                    Source = MatchSource.VivoLivePhoto
                                });
                                remainingImages.Remove(matchedImg);
                                remainingVideos.Remove(vidPath);
                                imgIdToPath.Remove(facts.PairingIdentifier);
                            }
                        }
                        catch
                        {
                            // Dual inspection rejected candidate
                        }
                    }
                }
                catch
                {
                    // Skip
                }
            }

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────

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

        // Apple live photo detection — used by Repair page for filtering.
        // An Apple Live Photo is identified by its ContentIdentifier UUID (present in both the
        // still image and the paired video), not the Make tag — Make can be stripped or rewritten,
        // and ordinary non-live Apple photos also carry Make=Apple.
        public static async Task<HashSet<string>> FilterAppleDevicesAsync(
            IReadOnlyList<string> filePaths, CancellationToken token = default)
        {
            var appleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inspector = new SourceInspector();
            foreach (var path in filePaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    SourceMediaFacts facts = await inspector.InspectAsync(path, null, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(facts.PairingIdentifier) || facts.Protocol == SourceProtocol.AppleLivePhoto)
                        appleFiles.Add(path);
                }
                catch { /* skip */ }
            }
            return appleFiles;
        }
    }
}

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
    // 调用路径：
    //   - MatchAsync: 使用 Native SourceInspector 提取 ContentIdentifier
    //   - MatchVivo: 使用 Native SourceInspector 确认 vivo 双文件候选
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
            catch (SourceInspectionException ex) when (
                ex.Category == SourceInspectionFailureCategory.Unsupported &&
                ex.Stage is SourceInspectionStage.Protocol or SourceInspectionStage.Pairing)
            {
                // An explicitly rejected candidate is not a pair.  All
                // malformed, ambiguous, and I/O failures remain observable.
            }

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
                catch (SourceInspectionException) { throw; }
                catch (IOException) { throw; }
            }

            // Match by UUID
            var pairs = new List<MetadataPair>();
            var remainingImages = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(unmatchedVideoPaths, StringComparer.OrdinalIgnoreCase);

            var imageGroups = remainingImages
                .Where(path => contentIdMap.ContainsKey(path))
                .GroupBy(path => contentIdMap[path], StringComparer.OrdinalIgnoreCase)
                .ToList();
            var videoGroups = remainingVideos
                .Where(path => contentIdMap.ContainsKey(path))
                .GroupBy(path => contentIdMap[path], StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (imageGroups.Any(group => group.Count() > 1) || videoGroups.Any(group => group.Count() > 1))
            {
                throw new SourceInspectionException(
                    SourceInspectionFailureCategory.Ambiguous,
                    SourceInspectionStage.Pairing,
                    LivePhotoBox.Interop.NativeRuntime.AppleCapability,
                    "Multiple image or video candidates share the same ContentIdentifier.");
            }

            var imageById = imageGroups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            var videoById = videoGroups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

            foreach (var (cid, matchedImgPath) in imageById)
            {
                if (videoById.TryGetValue(cid, out var vidPath))
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
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (SourceInspectionException ex) when (
                        ex.Category == SourceInspectionFailureCategory.Unsupported &&
                        ex.Stage is SourceInspectionStage.Protocol or SourceInspectionStage.Pairing)
                    {
                        // Explicit candidate rejection is not a pairing authority.
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
            var remainingImages = new HashSet<string>(unmatchedImagePaths, StringComparer.OrdinalIgnoreCase);
            var remainingVideos = new HashSet<string>(unmatchedVideoPaths, StringComparer.OrdinalIgnoreCase);

            if (remainingImages.Count == 0 || remainingVideos.Count == 0)
            {
                return new MetadataMatchOutput
                {
                    Pairs = Array.Empty<MetadataPair>(),
                    RemainingImages = remainingImages.Count,
                    RemainingVideos = remainingVideos.Count
                };
            }

            // The Native inspector owns vivo ID extraction and dual-file
            // confirmation.  A single orphan image intentionally fails closed,
            // so do not recover its ID with managed byte parsing here.  Probe
            // candidate pairs through Native and retain only unambiguous
            // one-to-one confirmations.
            var inspector = new SourceInspector();
            var candidates = new List<(string Image, string Video)>();
            int processed = 0;
            foreach (var imgPath in remainingImages)
            {
                string ext = Path.GetExtension(imgPath).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg") continue;
                foreach (var vidPath in remainingVideos)
                {
                    onFileProcessed?.Invoke(++processed);
                    try
                    {
                        SourceMediaFacts facts = inspector.InspectAsync(imgPath, vidPath).GetAwaiter().GetResult();
                        if (facts.Protocol == SourceProtocol.VivoLegacyDualFile)
                            candidates.Add((imgPath, vidPath));
                    }
                    catch (SourceInspectionException ex) when (
                        ex.Category == SourceInspectionFailureCategory.Unsupported &&
                        ex.Stage is SourceInspectionStage.Protocol or SourceInspectionStage.Pairing)
                    {
                        // A rejected candidate is not evidence of a match.
                    }
                    catch (IOException)
                    {
                        throw;
                    }
                }
            }

            var imageGroups = candidates
                .GroupBy(x => x.Image, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var videoGroups = candidates
                .GroupBy(x => x.Video, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (imageGroups.Any(group => group.Count() > 1) || videoGroups.Any(group => group.Count() > 1))
            {
                throw new SourceInspectionException(
                    SourceInspectionFailureCategory.Ambiguous,
                    SourceInspectionStage.Pairing,
                    LivePhotoBox.Interop.NativeRuntime.VivoLegacyCapability,
                    "Multiple vivo candidates share the same pairing identity.");
            }

            var onePerImage = imageGroups
                .SelectMany(group => group)
                .ToList();
            var onePerVideo = onePerImage;
            var pairs = new List<MetadataPair>();
            foreach (var candidate in onePerVideo)
            {
                pairs.Add(new MetadataPair
                {
                    ImagePath = candidate.Image,
                    VideoPath = candidate.Video,
                    Source = MatchSource.VivoLivePhoto
                });
                remainingImages.Remove(candidate.Image);
                remainingVideos.Remove(candidate.Video);
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
                catch (SourceInspectionException ex) when (
                    ex.Category == SourceInspectionFailureCategory.Unsupported &&
                    ex.Stage == SourceInspectionStage.Protocol)
                {
                    // A source that explicitly does not implement Apple
                    // pairing is simply outside this optional filter.  Other
                    // categories must fail closed.
                }
            }
            return appleFiles;
        }
    }
}

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
    //   - MatchAsync: Merge 页面，内部启动 exiftool 提取 ContentIdentifier
    //   - MatchFromAnalysis: Repair 页面，复用已有的 RepairAnalysisResult
    //   - MatchVivo: Merge 页面，纯文件 I/O 解析 vivo JSON 尾部
    public static partial class LivePhotoMetadataMatcher
    {
        // ── CID 匹配（Apple Live Photo）──
        // 内部启动 PersistentExifTool 批量查询 ContentIdentifier 和 CreateDate。
        // unmatchedImagePaths: 文件名匹配后未配对的照片路径
        // unmatchedVideoPaths: 文件名匹配后未配对的视频路径
        // exifToolPath: exiftool.exe 的完整路径
        // token: 取消令牌
        // è¿å: 额外匹配到的配对 + 剩余未匹配计数
        // ContentIdentifier UUID 精确匹配 — Apple Live Photo 专用。
        // 查询所有未配对的图片和视频的 ContentIdentifier 字段，UUID 一致则配对。
        public static async Task<MetadataMatchOutput> MatchAsync(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths,
            string exifToolPath,
            CancellationToken token)
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

            using var exifTool = new PersistentExifTool(exifToolPath);
            foreach (var filePath in allPaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string output = await exifTool.SendCommandAsync(token,
                        "-j", "-ContentIdentifier", filePath);
                    if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                        continue;

                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var root = doc.RootElement[0];

                    string cid = GetJsonValueAsString(root, "ContentIdentifier");
                    if (!string.IsNullOrWhiteSpace(cid))
                        contentIdMap[filePath] = cid;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogService.Scan($"CID match: exiftool read failed for {Path.GetFileName(filePath)}: {ex.Message}", LogLevel.Warning);
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

        // ──────────────────────────────────────────────
        //  Repair 页面路径：复用已有的 RepairAnalysisResult
        // ──────────────────────────────────────────────

        // 使用已有的 RepairAnalysisResult 进行元数据匹配（Repair 页面专用）。
        // 不需要额外启动 exiftool — 分析数据已在扫描阶段提取。
        // images: 独立照片（路径 + 分析结果）
        // videos: 独立视频（路径 + 分析结果）
        // è¿å: 额外匹配到的配对 + 剩余未匹配计数
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
        /// Extract the vivo live photo pairing ID from a JPEG or MP4 file.
        /// JPEG: reads the last 8KB, searches for vivo{JSON}cameralbum! pattern and
        ///        extracts the "com.android.camera.livephoto" field value.
        /// MP4:  searches for vivo{JSON} inside the file, typically inside a
        ///        uuid box with user type "vivoMediaExtInfo".
        /// Returns null if no vivo live photo ID is found.
        /// </summary>
        private static string? ExtractVivoLivePhotoId(string filePath)
        {
            try
            {
                const int tailSize = 8192;
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                long fileLen = fs.Length;
                if (fileLen < 64) return null;

                // vivo JSON sits at the tail of both JPEG and MP4
                long searchStart = Math.Max(0, fileLen - tailSize);
                int searchLen = (int)(fileLen - searchStart);

                byte[] buffer = new byte[searchLen];
                fs.Seek(searchStart, SeekOrigin.Begin);
                int bytesRead = fs.Read(buffer, 0, searchLen);

                // Search for "vivo{" (UTF-8 bytes)
                byte[] vivoMarker = "vivo{"u8.ToArray();
                int idx = IndexOfBytes(buffer.AsSpan(0, bytesRead), vivoMarker);
                if (idx < 0) return null;

                // Find the matching closing brace before "cameralbum!" or end of vivo JSON
                int jsonStart = idx + vivoMarker.Length;
                int braceDepth = 0;
                int jsonEnd = -1;
                for (int i = jsonStart; i < bytesRead; i++)
                {
                    if (buffer[i] == '{') braceDepth++;
                    else if (buffer[i] == '}')
                    {
                        if (braceDepth == 0) { jsonEnd = i + 1; break; }
                        braceDepth--;
                    }
                }
                if (jsonEnd < 0) return null;

                // Extract JSON bytes
                int jsonLen = jsonEnd - idx;
                string jsonText = System.Text.Encoding.UTF8.GetString(buffer, idx, jsonLen);

                // Parse out com.android.camera.livephoto value
                const string key = "\"com.android.camera.livephoto\":\"";
                int keyIdx = jsonText.IndexOf(key, StringComparison.Ordinal);
                if (keyIdx < 0) return null;

                int valStart = keyIdx + key.Length;
                int valEnd = jsonText.IndexOf('"', valStart);
                if (valEnd < 0) return null;

                return jsonText.Substring(valStart, valEnd - valStart);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Match unmatched photos and videos by vivo live photo pairing ID.
        /// Parses "vivo{JSON}" from JPEG tails and MP4 uuid boxes, extracts
        /// "com.android.camera.livephoto", and pairs files with matching IDs.
        /// Does NOT require exiftool — pure file I/O.
        /// </summary>
        public static MetadataMatchOutput MatchVivo(
            IReadOnlyList<string> unmatchedImagePaths,
            IReadOnlyList<string> unmatchedVideoPaths)
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

            // Extract vivo IDs from images (JPEG only — vivo dual-file always uses JPEG+MP4)
            var imgIdToPath = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var imgPath in remainingImages.ToList())
            {
                string ext = Path.GetExtension(imgPath).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg") continue;

                string? id = ExtractVivoLivePhotoId(imgPath);
                if (!string.IsNullOrWhiteSpace(id) && id.Length > 8) // meaningful IDs are ~30 chars
                {
                    if (!imgIdToPath.ContainsKey(id))
                        imgIdToPath[id] = imgPath;
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

            // Match videos by ID
            foreach (var vidPath in remainingVideos.ToList())
            {
                string ext = Path.GetExtension(vidPath).ToLowerInvariant();
                if (ext != ".mp4") continue;

                string? id = ExtractVivoLivePhotoId(vidPath);
                if (!string.IsNullOrWhiteSpace(id) && imgIdToPath.TryGetValue(id, out var matchedImg))
                {
                    pairs.Add(new MetadataPair
                    {
                        ImagePath = matchedImg,
                        VideoPath = vidPath,
                        Source = MatchSource.VivoLivePhoto
                    });
                    remainingImages.Remove(matchedImg);
                    remainingVideos.Remove(vidPath);
                    imgIdToPath.Remove(id);
                }
            }

            return new MetadataMatchOutput
            {
                Pairs = pairs,
                RemainingImages = remainingImages.Count,
                RemainingVideos = remainingVideos.Count
            };
        }

        private static int IndexOfBytes(ReadOnlySpan<byte> span, byte[] pattern)
        {
            int end = span.Length - pattern.Length;
            for (int i = 0; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (span[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
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

        // Apple device detection — used by Repair page for filtering.
        public static async Task<HashSet<string>> FilterAppleDevicesAsync(
            IReadOnlyList<string> filePaths, PersistentExifTool exifTool, CancellationToken token)
        {
            var appleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string output = await exifTool.SendCommandAsync(token, "-j", "-Make", path);
                    if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                        continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    string make = GetJsonValueAsString(doc.RootElement[0], "Make");
                    if (string.Equals(make?.Trim(), "Apple", StringComparison.OrdinalIgnoreCase))
                        appleFiles.Add(path);
                }
                catch { /* skip */ }
            }
            return appleFiles;
        }
    }
}

/*
 * LightboxItemSource.cs
 *
 * 灯箱条目源工具类。将各页面的 Task 列表转换为 LightboxItem 列表，
 * 自动填充 Live Photo 视频源信息，供 LightboxPreview 使用。
 *
 * 三种来源模式：
 * - FromMergeTasks：配对文件，直接用 MergeTask.VideoPath
 * - FromSplitTasks：单文件实况，解析 XMP 获取追加视频段长度，以及支持同名配对视频
 * - FromPathsAsync：通用回退，用 LivePhotoProtocolDetector 检测协议并填充
 *
 * 协议识别统一复用 LivePhotoProtocolDetector.Detect()（Edit 页面同款），
 * 保证所有协议（华为/荣耀/三星/融合/Google/OPPO/vivo/Apple）都能显示 LIVE 按钮。
 */

using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 将 Task 列表或文件路径列表转换为 LightboxItem 列表的静态工具类。
    /// </summary>
    public static class LightboxItemSource
    {

        /// <summary>
        /// 从 MergeTask 列表构造 LightboxItem（模式 A — 配对文件）。
        /// 直接使用 MergeTask 中已有的 VideoPath——扫描阶段已确认配对。
        /// </summary>
        public static List<LightboxItem> FromMergeTasks(IEnumerable<MergeTask> tasks)
        {
            return tasks.Select(t =>
            {
                // 空 VideoPath 必须传 null，否则灯箱底层的 IsLivePhoto 属性会判断失误
                string? videoPath = string.IsNullOrWhiteSpace(t.VideoPath) ? null : t.VideoPath;
                // 检测协议：无内容标记时兜底 Apple（双文件最常见协议）
                var protocol = DetectProtocol(t.ImagePath, LivePhotoType.DualFile);
                if (protocol == LivePhotoProtocolType.Unknown)
                    protocol = LivePhotoProtocolType.Apple;
                return new LightboxItem
                {
                    ImagePath = t.ImagePath,
                    VideoPath = videoPath,
                    AppendedVideoLength = 0,
                    LivePhotoType = LivePhotoType.DualFile,
                    DetectedProtocol = protocol,
                };
            }).ToList();
        }

        /// <summary>
        /// 从 RepairTask 列表构造 LightboxItem。
        /// 配对任务直接复用扫描阶段的 File1+File2 配对信息，零 I/O。
        /// </summary>
        public static List<LightboxItem> FromRepairTasks(IReadOnlyList<RepairTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            foreach (var t in tasks)
            {
                // 跳过分组标题
                if (t.IsGroupHeader) continue;

                string imagePath = t.File1Path;
                string? videoPath = null;
                var protocol = LivePhotoProtocolType.Unknown;
                var type = LivePhotoType.None;

                if (t.IsPaired)
                {
                    // 配对任务：照片=ImagePath，对应的另一个=VideoPath
                    var e1 = t.File1Entry;
                    var e2 = t.File2Entry;
                    if (e1 != null && e2 != null)
                    {
                        imagePath = e1.IsImage ? e1.FilePath : e2.FilePath;
                        videoPath = e1.IsImage ? e2.FilePath : e1.FilePath;
                    }
                    type = LivePhotoType.DualFile;
                    protocol = DetectProtocol(imagePath, LivePhotoType.DualFile);
                    if (protocol == LivePhotoProtocolType.Unknown)
                        protocol = LivePhotoProtocolType.Apple;
                }

                items.Add(new LightboxItem
                {
                    ImagePath = imagePath,
                    VideoPath = videoPath,
                    LivePhotoType = type,
                    DetectedProtocol = protocol,
                });
            }
            return items;
        }

        /// <summary>
        /// 从 SplitTask 列表构造 LightboxItem（模式 B — 单文件实况 + 模式 A 苹果配对兜底）。
        /// 视频长度直接从 SplitTask.AppendedVideoLength 读取，扫描阶段已解析，零 I/O。
        /// 补充协议检测（华为/三星等无 XMP 长度的协议靠 Detect 识别）。
        /// </summary>
        public static List<LightboxItem> FromSplitTasks(IReadOnlyList<SplitTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            foreach (var t in tasks)
            {
                // 优先用扫描时已解析的视频段长度（零 I/O）
                long videoLen = t.AppendedVideoLength;

                // 兜底：苹果格式同名配对视频（仅当不是单文件实况时才查）
                string? videoPath = videoLen > 0 ? null : FindPairedVideo(t.SourcePath);

                // 检测协议：影像内容标记（XMP / 尾标）优先
                var protocol = DetectProtocol(t.SourcePath, LivePhotoType.SingleFileJpeg);

                // 单文件协议类型：有 XMP 长度 → JPEG；否则看协议（华为/三星等 → 单文件）
                var type = videoLen > 0
                    ? LivePhotoType.SingleFileJpeg
                    : protocol is LivePhotoProtocolType.Huawei
                          or LivePhotoProtocolType.Samsung
                          or LivePhotoProtocolType.Fusion
                      ? LivePhotoType.SingleFileJpeg
                      : LivePhotoType.None;

                items.Add(new LightboxItem
                {
                    ImagePath = t.SourcePath,
                    VideoPath = videoPath,
                    AppendedVideoLength = videoLen > 0 ? videoLen : 0,
                    LivePhotoType = type,
                    DetectedProtocol = protocol,
                });
            }
            return items;
        }

        /// <summary>
        /// 从文件路径列表构造 LightboxItem（通用回退）。
        /// 用信号量限制并发解码，防止多选文件时卡死 UI。
        /// </summary>
        public static async Task<List<LightboxItem>> FromPathsAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return new List<LightboxItem>();

            var items = new LightboxItem[paths.Count];
            using var semaphore = new SemaphoreSlim(System.Environment.ProcessorCount * 2);

            var loadTasks = paths.Select(async (path, index) =>
            {
                await semaphore.WaitAsync();
                try
                {
                    items[index] = await BuildItemAsync(path);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(loadTasks);
            return new List<LightboxItem>(items);
        }

        /// <summary>
        /// 对单个文件路径构造 LightboxItem（含协议检测）。
        /// 识别信号：GPU 文件名配对（双文件）→ 内容标记检测（Detect）→ 按协议分流填充。
        /// </summary>
        private static async Task<LightboxItem> BuildItemAsync(string path)
        {
            if (!File.Exists(path))
            {
                return new LightboxItem { ImagePath = path };
            }

            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            bool isImage = ext is ".jpg" or ".jpeg" or ".heic" or ".heif";

            // ── 双文件（Apple / vivo 旧格式）：优先找同名配对视频 ──
            string? videoPath = FindPairedVideo(path);
            bool dualFile = videoPath != null;

            var type = dualFile ? LivePhotoType.DualFile : LivePhotoType.None;

            // ── 单文件实况：读 XMP + 检测协议 ──
            long videoLen = 0;
            string xmpText = "";
            if (!dualFile && isImage)
            {
                try { xmpText = await LivePhotoSplitService.ReadMetadataFromFileAsync(path); }
                catch { xmpText = ""; }

                try { videoLen = LivePhotoSplitService.GetAppendedVideoLength(xmpText); }
                catch { videoLen = 0; }

                if (videoLen > 0)
                {
                    type = LivePhotoType.SingleFileJpeg;
                }
            }

            // ── 协议检测（内容标记优先于文件名配对） ──
            // 对 dualFile：Detect 内部先查 vivo 尾标、再查 XMP/尾标内容，最后靠 CID → Apple
            var protocol = DetectProtocol(path, type, xmpText);

            // 双文件 + Detect 未识别 → 兜底 Apple（最常见双文件协议）
            if (dualFile && protocol == LivePhotoProtocolType.Unknown)
                protocol = LivePhotoProtocolType.Apple;

            // 单文件 HEIC / JPEG 靠 Detect 识别但无 XMP 长度（华为/三星/融合）→ 类型为 SingleFile
            if (type == LivePhotoType.None && protocol != LivePhotoProtocolType.Unknown
                && protocol is LivePhotoProtocolType.Huawei
                    or LivePhotoProtocolType.Samsung
                    or LivePhotoProtocolType.Fusion
                    or LivePhotoProtocolType.OPPO
                    or LivePhotoProtocolType.Vivo
                    or LivePhotoProtocolType.GoogleV1
                    or LivePhotoProtocolType.GoogleV2)
            {
                type = ext is ".heic" or ".heif"
                    ? LivePhotoType.SingleFileHeic
                    : LivePhotoType.SingleFileJpeg;
            }

            return new LightboxItem
            {
                ImagePath = path,
                VideoPath = dualFile ? videoPath : null,
                AppendedVideoLength = videoLen,
                LivePhotoType = type,
                DetectedProtocol = protocol,
            };
        }

        /// <summary>调用 LivePhotoProtocolDetector.Detect 并捕获异常（失败返回 Unknown）</summary>
        private static LivePhotoProtocolType DetectProtocol(string path, LivePhotoType type, string? xmpText = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return LivePhotoProtocolType.Unknown;
            try
            {
                return LivePhotoProtocolDetector.Detect(path, type, contentIdentifier: null, xmpText: xmpText);
            }
            catch
            {
                return LivePhotoProtocolType.Unknown;
            }
        }

        /// <summary>
        /// 在同目录中查找与图片同名的视频文件（.mp4 / .mov）。
        /// </summary>
        private static string? FindPairedVideo(string imagePath)
        {
            string? dir = Path.GetDirectoryName(imagePath);
            if (dir == null) return null;
            string baseName = Path.GetFileNameWithoutExtension(imagePath);
            foreach (var ext in new[] { ".mp4", ".mov" })
            {
                string candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

    }
}
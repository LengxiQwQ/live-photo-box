/*
 * LightboxItemSource.cs
 *
 * 灯箱条目源工具类。将各页面的 Task 列表转换为 LightboxItem 列表，
 * 自动填充 Live Photo 视频源信息，供 LightboxPreview 使用。
 *
 * 两种来源模式：
 * - FromMergeTasks：配对文件，直接用 MergeTask.VideoPath
 * - FromSplitTasks：单文件实况，解析 XMP 获取追加视频段长度，以及支持同名配对视频
 * - FromPaths：通用回退，自动探测目录内配对视频 + 单文件 XMP
 */

using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
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
            return tasks.Select(t => new LightboxItem
            {
                ImagePath = t.ImagePath,

                // 空 VideoPath 必须传 null，否则灯箱底层的 IsLivePhoto 属性会判断失误
                VideoPath = string.IsNullOrWhiteSpace(t.VideoPath) ? null : t.VideoPath,

                AppendedVideoLength = 0
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
                long videoLen = 0;

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
                }
                else if (t.File1IsImage)
                {
                    // 独立图片：可能是单文件实况（OPPO/vivo/小米 XMP 尾部 / 华为/荣耀 LIVE_ 尾标）。
                    // 复用通用探测：读 XMP 头拿 videoLen，无则查尾部 LIVE_ 标记。
                    // 普通照片无任何标记 → videoLen=0 → 灯箱当普通图显示（无 LIVE 按钮）。
                    DetectSingleFileVideo(imagePath, out videoLen);
                }
                // 独立视频条目（!File1IsImage）：保持现状，不做任何探测（灯箱单独播放视频）

                items.Add(new LightboxItem
                {
                    ImagePath = imagePath,
                    VideoPath = videoPath,
                    AppendedVideoLength = videoLen > 0 ? videoLen : 0
                });
            }
            return items;
        }

        /// <summary>
        /// 从 SplitTask 列表构造 LightboxItem（模式 B — 单文件实况 + 模式 A 苹果配对兜底）。
        /// 视频长度直接从 SplitTask.AppendedVideoLength 读取，扫描阶段已解析，零 I/O。
        /// </summary>
        public static List<LightboxItem> FromSplitTasks(IReadOnlyList<SplitTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            foreach (var t in tasks)
            {
                // 单文件实况：优先用扫描时已解析的视频段长度（零 I/O）
                long videoLen = t.AppendedVideoLength;
                string? videoPath = null;

                // 兜底：苹果格式同名配对视频（仅当不是单文件实况时才查）
                videoPath = videoLen > 0 ? null : FindPairedVideo(t.SourcePath);

                items.Add(new LightboxItem
                {
                    ImagePath = t.SourcePath,
                    VideoPath = videoPath,
                    AppendedVideoLength = videoLen > 0 ? videoLen : 0
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
                    string? videoPath = null;
                    long videoLen = 0;

                    if (File.Exists(path))
                    {
                        if (IsImagePath(path))
                        {
                            try
                            {
                                var facts = await new SourceInspector().InspectAsync(path);
                                videoLen = facts.MotionVideo is { IsPresent: true } video
                                    ? video.ByteLength
                                    : 0;
                            }
                            catch { videoLen = 0; }
                        }
                    }

                    items[index] = new LightboxItem
                    {
                        ImagePath = path,
                        VideoPath = videoPath,
                        AppendedVideoLength = videoLen > 0 ? videoLen : 0
                    };
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

        private static bool IsImagePath(string path)
        {
            string ext = Path.GetExtension(path);
            return ext is ".jpg" or ".jpeg" or ".heic" or ".heif";
        }

        /// <summary>
        /// 检查文件尾部 4KB 是否包含华为/荣耀 LIVE_ 尾标。
        /// 轻量操作（仅读 4KB），不按扩展名筛选。
        /// </summary>
        private static bool HasLiveTailMarker(string filePath)
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
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 对单个图片文件执行实况视频探测：读 XMP 头获取 videoLen，无则查尾部 LIVE_ 标记。
        /// 覆盖所有单文件协议：Google V1/V2（XMP Item:Length/MicroVideoOffset）、
        /// OPPO/vivo/小米（XMP）、华为/荣耀（LIVE_ 尾标）。普通照片无任何标记 → videoLen=0。
        /// 供灯箱后台探测调用（internal）。
        /// </summary>
        internal static void DetectSingleFileVideo(string filePath, out long videoLen)
        {
            videoLen = 0;
            if (!File.Exists(filePath)) return;
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            if (ext is not ".jpg" and not ".jpeg" and not ".heic" and not ".heif") return;

            try
            {
                // 读头部 64KB 搜索 XMP 元数据（含 GetAppendedVideoLength 所需的所有标记）
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                int headRead = (int)Math.Min(fileSize, 64 * 1024);
                byte[] headBuf = new byte[headRead];
                fs.ReadExactly(headBuf, 0, headRead);
                string meta = System.Text.Encoding.UTF8.GetString(headBuf);

                // GetAppendedVideoLength 对无 XMP 标记的文件抛异常（不是返回 0），
                // 单独 try/catch 确保异常不阻断后续尾标探测
                try
                {
                    videoLen = LivePhotoSplitService.GetAppendedVideoLength(meta);
                }
                catch
                {
                    videoLen = 0;
                }
            }
            catch { videoLen = 0; }

            // XMP 解析不出 videoLen → 查尾部 LIVE_ 标记（华为/荣耀）
            // 此检查放在外层 try/catch 之后，确保不会被 XMP 异常阻断
            if (videoLen == 0 && HasLiveTailMarker(filePath))
            {
                try
                {
                    var hwRange = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(filePath);
                    if (hwRange.HasValue)
                    {
                        videoLen = hwRange.Value.videoLength;
                    }
                }
                catch { /* 非华为/荣耀文件静默跳过 */ }
            }
        }

    }
}

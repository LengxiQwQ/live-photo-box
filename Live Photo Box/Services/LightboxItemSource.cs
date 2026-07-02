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

                // ✅ 修复点：必须严格处理 VideoPath。如果是空的，必须传 null！
                // 否则灯箱底层的 IsLivePhoto 属性会判断失误
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

                items.Add(new LightboxItem
                {
                    ImagePath = imagePath,
                    VideoPath = videoPath
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
                // 优先用扫描时已解析的视频段长度（零 I/O）
                long videoLen = t.AppendedVideoLength;

                // 兜底：苹果格式同名配对视频（仅当不是单文件实况时才查）
                string? videoPath = videoLen > 0 ? null : FindPairedVideo(t.SourcePath);

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
        /// ✨ 修复：同样引入高并发机制，防止在多选文件时卡死 UI。
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
                        videoPath = FindPairedVideo(path);
                        if (videoPath == null)
                        {
                            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
                            if (ext == ".jpg" || ext == ".jpeg" || ext == ".heic")
                            {
                                try
                                {
                                    string meta = await LivePhotoSplitService.ReadMetadataFromFileAsync(path);
                                    videoLen = LivePhotoSplitService.GetAppendedVideoLength(meta);
                                }
                                catch { videoLen = 0; }
                            }
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

    }
}
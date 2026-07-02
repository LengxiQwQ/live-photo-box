/*
 * LightboxItemSource.cs
 *
 * 灯箱条目源工具类。将各页面的 Task 列表转换为 LightboxItem 列表，
 * 自动填充 Live Photo 视频源信息，供 LightboxPreview 使用。
 *
 * 两种来源模式：
 *   - FromMergeTasks：配对文件，直接用 MergeTask.VideoPath
 *   - FromSplitTasks：单文件实况，解析 XMP 获取追加视频段长度
 *   - FromPaths：通用回退，自动探测目录内配对视频 + 单文件 XMP
 */

using LivePhotoBox.Models;
using System.Collections.Generic;
using System.IO;
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
        public static List<LightboxItem> FromMergeTasks(IReadOnlyList<MergeTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            foreach (var t in tasks)
            {
                bool hasVideo = !string.IsNullOrWhiteSpace(t.VideoPath);
                bool videoExists = hasVideo && File.Exists(t.VideoPath);

                // 每次必输出：确认此方法被执行，且显示每个 Task 的视频状态
                LogService.Info(
                    $"Lightbox: MergeTask[{t.Index}] Img='{Path.GetFileName(t.ImagePath)}' " +
                    $"Vid='{(hasVideo ? Path.GetFileName(t.VideoPath!) : "NULL")}' " +
                    $"Exists={videoExists} → LIVE={(videoExists ? "YES" : "NO")}",
                    LogSource.Scan);

                items.Add(new LightboxItem
                {
                    ImagePath = t.ImagePath,
                    VideoPath = videoExists ? t.VideoPath : null
                });
            }
            return items;
        }

        /// <summary>
        /// 从 SplitTask 列表构造 LightboxItem（模式 B — 单文件实况）。
        /// 使用 LivePhotoSplitService 的成熟元数据读取逻辑，确保与拆分流程一致。
        /// </summary>
        public static async Task<List<LightboxItem>> FromSplitTasksAsync(IReadOnlyList<SplitTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            foreach (var t in tasks)
            {
                long videoLen = 0;
                try
                {
                    if (File.Exists(t.SourcePath))
                    {
                        string meta = await LivePhotoSplitService.ReadMetadataFromFileAsync(t.SourcePath);
                        videoLen = LivePhotoSplitService.GetAppendedVideoLength(meta);
                    }
                }
                catch { videoLen = 0; }

                items.Add(new LightboxItem
                {
                    ImagePath = t.SourcePath,
                    AppendedVideoLength = videoLen > 0 ? videoLen : 0
                });
            }
            return items;
        }

        /// <summary>
        /// 从文件路径列表构造 LightboxItem（通用回退）。
        /// 自动探测同目录配对视频 + 单文件实况 XMP 解析。
        /// 用于 RepairPage 等没有现成 Task 信息的场景。
        /// </summary>
        public static async Task<List<LightboxItem>> FromPathsAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return new List<LightboxItem>();

            var items = new List<LightboxItem>(paths.Count);
            foreach (var path in paths)
            {
                string? videoPath = null;
                long videoLen = 0;

                if (File.Exists(path))
                {
                    // 1. 先尝试同目录配对视频
                    videoPath = FindPairedVideo(path);

                    // 2. 无配对视频 → 尝试单文件实况解析（仅 JPEG）
                    if (videoPath == null)
                    {
                        string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
                        if (ext == ".jpg" || ext == ".jpeg")
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

                items.Add(new LightboxItem
                {
                    ImagePath = path,
                    VideoPath = videoPath,
                    AppendedVideoLength = videoLen > 0 ? videoLen : 0
                });
            }
            return items;
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

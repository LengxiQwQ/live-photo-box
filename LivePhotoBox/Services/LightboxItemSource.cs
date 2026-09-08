/*
 * LightboxItemSource.cs
 *
 * 灯箱条目源工具类。将各页面的 Task 列表转换为 LightboxItem 列表，
 * 自动填充 Live Photo 视频源信息，供 LightboxPreview 使用。
 *
 * 两种来源模式：
 * - FromMergeTasks：配对文件，必须由 Native SourceInspector 再确认
 * - FromSplitTasks：仅消费拆分扫描阶段由 Native Inspector 确认的单文件视频范围
 * - FromPaths：通用回退，消费 Native Inspector facts
 */

using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
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
        /// MergeTask 是 UI 数据，不是媒体协议 authority；视频路径必须在
        /// Native boundary 重新确认，避免 stale/unrelated paths 被挂载。
        /// </summary>
        public static List<LightboxItem> FromMergeTasks(IEnumerable<MergeTask> tasks)
        {
            var inspector = new SourceInspector();
            return tasks.Select(t => new LightboxItem
            {
                ImagePath = t.ImagePath,

                // 空 VideoPath 必须传 null，否则灯箱底层的 IsLivePhoto 属性会判断失误
                VideoPath = !string.IsNullOrWhiteSpace(t.VideoPath)
                    && TryConfirmNativePair(t.ImagePath, t.VideoPath, inspector)
                    ? t.VideoPath
                    : null,

                AppendedVideoLength = 0
            }).ToList();
        }

        /// <summary>
        /// 从 RepairTask 列表构造 LightboxItem。
        /// 配对任务仅在 Native SourceInspector 再次确认 metadata pairing 后挂载视频。
        /// </summary>
        public static List<LightboxItem> FromRepairTasks(IReadOnlyList<RepairTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            var inspector = new SourceInspector();
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
                        string candidateImage = e1.IsImage ? e1.FilePath : e2.FilePath;
                        string candidateVideo = e1.IsImage ? e2.FilePath : e1.FilePath;
                        if (TryConfirmNativePair(candidateImage, candidateVideo, inspector))
                        {
                            imagePath = candidateImage;
                            videoPath = candidateVideo;
                        }
                    }
                }
                else if (t.File1IsImage)
                {
                    // 独立图片只通过 Native SourceInspector 识别；任何无法确认的
                    // 结构都 fail closed，不把托管字节扫描结果当作 live-photo 事实。
                    videoLen = DetectSingleFileVideo(imagePath);
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
        /// 从 SplitTask 列表构造 LightboxItem（仅单文件实况）。
        /// 视频长度来自当前路径的 Native Inspector facts；SplitTask 中的
        /// persisted length 可能已经 stale，不能作为 authority。
        /// </summary>
        public static List<LightboxItem> FromSplitTasks(IReadOnlyList<SplitTask> tasks)
        {
            var items = new List<LightboxItem>(tasks.Count);
            var inspector = new SourceInspector();
            foreach (var t in tasks)
            {
                var facts = inspector.InspectAsync(t.SourcePath).GetAwaiter().GetResult();
                long videoLen = facts.Protocol is not (SourceProtocol.NonLive or SourceProtocol.Unknown)
                    && facts.MotionVideo is { IsPresent: true, SourceIndex: 0 } motionVideo
                    ? motionVideo.ByteLength
                    : 0;
                string? videoPath = null;

                // 不以 basename 作为配对 authority。正式双文件 pairing 必须在
                // discovery/metadata matcher 中由 Native SourceInspector 确认后传入。

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
                            var facts = await new SourceInspector().InspectAsync(path);
                            videoLen = facts.Protocol is not (SourceProtocol.NonLive or SourceProtocol.Unknown)
                                && facts.MotionVideo is { IsPresent: true, SourceIndex: 0 } video
                                ? video.ByteLength
                                : 0;
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

        private static bool IsImagePath(string path)
        {
            string ext = Path.GetExtension(path);
            return ext is ".jpg" or ".jpeg" or ".heic" or ".heif";
        }

        /// <summary>通过 Native SourceInspector 获取单文件实况的精确视频长度。</summary>
        internal static long DetectSingleFileVideo(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            if (ext is not ".jpg" and not ".jpeg" and not ".heic" and not ".heif") return 0;

            var facts = new SourceInspector().InspectAsync(filePath).GetAwaiter().GetResult();
            return facts.Protocol is not (SourceProtocol.NonLive or SourceProtocol.Unknown)
                && facts.MotionVideo is { IsPresent: true, SourceIndex: 0 } video
                ? video.ByteLength
                : 0;
        }

        private static bool TryConfirmNativePair(
            string imagePath, string videoPath, ISourceInspector inspector)
        {
            var facts = inspector.InspectAsync(imagePath, videoPath).GetAwaiter().GetResult();
            return facts.PrimaryImage.IsPresent
                && facts.Protocol is not (SourceProtocol.NonLive or SourceProtocol.Unknown)
                && facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };
        }

    }
}

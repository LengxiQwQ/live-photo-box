/*
 * LivePhotoDiscoveryModels.cs
 *
 * 统一实况照片发现服务的数据模型。
 * 为 MergePage、SplitPage、KeyPhoto 等所有需要扫描实况照片的页面
 * 提供统一的文件分类结果，避免各页面各自维护不兼容的数据结构。
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace LivePhotoBox.Models
{
    /// <summary>实况照片类型</summary>
    public enum LivePhotoType
    {
        /// <summary>普通文件，非实况照片</summary>
        None = 0,
        /// <summary>双文件实况照片：独立的图片 + 视频文件配对</summary>
        DualFile = 1,
        /// <summary>单文件 JPEG 实况照片：JPEG 尾部内嵌 MP4 视频段</summary>
        SingleFileJpeg = 2,
        /// <summary>单文件 HEIC 实况照片：HEIC 容器内嵌视频轨</summary>
        SingleFileHeic = 3,
    }

    /// <summary>实况照片检测方法</summary>
    public enum LivePhotoDetectionMethod
    {
        /// <summary>通过文件名基础部分配对</summary>
        FilenamePairing,
        /// <summary>通过 Apple ContentIdentifier UUID 匹配</summary>
        ContentIdentifier,
        /// <summary>通过组合元数据（日期+GPS+设备）匹配</summary>
        MetadataCombined,
        /// <summary>通过 JPEG 文件头 XMP 字节标记检测</summary>
        JpegByteMarkers,
        /// <summary>通过 HEIC 视频轨检测</summary>
        HeicVideoTrack,
    }

    /// <summary>扫描模式 — 控制要运行哪些检测 Pass</summary>
    [Flags]
    public enum DiscoveryScanMode
    {
        /// <summary>Pass 1: JPEG 字节标记检测（Google/OPPO/小米）</summary>
        JpegMarkers = 1 << 0,
        /// <summary>Pass 2: HEIC 视频轨检测（exiftool ContentIdentifier + MediaDuration）</summary>
        HeicTrack = 1 << 1,
        /// <summary>Pass 3: 文件名基础部分配对（图片+视频同名）</summary>
        FilenamePair = 1 << 2,
        /// <summary>Pass 4: ContentIdentifier UUID 匹配</summary>
        CidMatch = 1 << 3,
        /// <summary>Pass 5: 组合元数据匹配（日期+GPS+设备）</summary>
        MetadataMatch = 1 << 4,

        /// <summary>拆分页面专用：仅 Pass 1</summary>
        SplitOnly = JpegMarkers,
        /// <summary>合并页面专用：Pass 3 + 4 + 5</summary>
        MergeOnly = FilenamePair | CidMatch | MetadataMatch,
        /// <summary>资源浏览页面：全部 Pass</summary>
        All = JpegMarkers | HeicTrack | FilenamePair | CidMatch | MetadataMatch,
    }

    /// <summary>统一文件发现条目 — 每个被扫描到的文件对应一条</summary>
    public sealed class LivePhotoDiscoveryItem
    {
        /// <summary>文件完整路径</summary>
        public required string FilePath { get; init; }

        /// <summary>文件字节大小</summary>
        public required long FileSizeBytes { get; init; }

        /// <summary>文件最后修改时间</summary>
        public DateTime LastWriteTime { get; init; }

        /// <summary>实况照片类型（None = 普通文件）</summary>
        public LivePhotoType LivePhotoType { get; set; } = LivePhotoType.None;

        /// <summary>检测方法</summary>
        public LivePhotoDetectionMethod DetectionMethod { get; set; }

        /// <summary>双文件实况照片：配对图片路径（视频条目指向对应图片）</summary>
        public string? PairedImagePath { get; set; }

        /// <summary>双文件实况照片：配对视频路径（图片条目指向对应视频）</summary>
        public string? PairedVideoPath { get; set; }

        /// <summary>单文件 JPEG 实况照片：内嵌视频段字节数</summary>
        public long AppendedVideoLength { get; set; }

        /// <summary>Apple ContentIdentifier UUID（exiftool 查询结果，未查询则为 null）</summary>
        public string? ContentIdentifier { get; set; }

        // ── 计算属性 ──

        /// <summary>是否为实况照片</summary>
        public bool IsLivePhoto => LivePhotoType != LivePhotoType.None;

        /// <summary>是否为图片文件（.jpg/.jpeg/.heic/.heif）</summary>
        public bool IsImage => LivePhotoType == LivePhotoType.DualFile
            ? PairedVideoPath != null   // 双文件中的图片：有配对视频的才是图片
            : FilePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
              || FilePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
              || FilePath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
              || FilePath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为视频文件（.mov/.mp4）</summary>
        public bool IsVideo => FilePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                               || FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统一扫描结果</summary>
    public sealed class LivePhotoDiscoveryResult
    {
        /// <summary>所有被扫描到的文件（含普通文件和实况照片）</summary>
        public required IReadOnlyList<LivePhotoDiscoveryItem> Items { get; init; }

        /// <summary>文件总数</summary>
        public int TotalCount => Items.Count;

        /// <summary>实况照片总数（含双文件和单文件）</summary>
        public int LivePhotoCount => Items.Count(i => i.IsLivePhoto);

        /// <summary>双文件实况照片数量</summary>
        public int DualFileCount => Items.Count(i => i.LivePhotoType == LivePhotoType.DualFile);

        /// <summary>单文件 JPEG 实况照片数量</summary>
        public int SingleFileJpegCount => Items.Count(i => i.LivePhotoType == LivePhotoType.SingleFileJpeg);

        /// <summary>单文件 HEIC 实况照片数量</summary>
        public int SingleFileHeicCount => Items.Count(i => i.LivePhotoType == LivePhotoType.SingleFileHeic);

        /// <summary>普通文件数量</summary>
        public int RegularFileCount => Items.Count(i => i.LivePhotoType == LivePhotoType.None);

        /// <summary>双文件实况照片配对列表：按图片+视频分组（仅包含 DualFile 类型的完整配对）</summary>
        public IReadOnlyList<(LivePhotoDiscoveryItem Image, LivePhotoDiscoveryItem Video)> DualFilePairs
        {
            get
            {
                var imageItems = Items
                    .Where(i => i.LivePhotoType == LivePhotoType.DualFile && i.IsImage)
                    .ToList();
                var result = new List<(LivePhotoDiscoveryItem, LivePhotoDiscoveryItem)>();
                var pairedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var img in imageItems)
                {
                    if (img.PairedVideoPath == null) continue;
                    var vid = Items.FirstOrDefault(v =>
                        string.Equals(v.FilePath, img.PairedVideoPath, StringComparison.OrdinalIgnoreCase));
                    if (vid != null)
                    {
                        result.Add((img, vid));
                        pairedVideos.Add(vid.FilePath);
                    }
                }

                return result;
            }
        }

        /// <summary>获取所有未被配对的独立图片路径</summary>
        public IReadOnlyList<string> StandaloneImagePaths =>
            Items.Where(i => i.LivePhotoType == LivePhotoType.None && i.IsImage)
                 .Select(i => i.FilePath)
                 .ToList();

        /// <summary>获取所有未被配对的独立视频路径</summary>
        public IReadOnlyList<string> StandaloneVideoPaths =>
            Items.Where(i => i.LivePhotoType == LivePhotoType.None && i.IsVideo)
                 .Select(i => i.FilePath)
                 .ToList();
    }
}

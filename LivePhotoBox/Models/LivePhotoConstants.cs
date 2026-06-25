using System;
using System.Text.RegularExpressions;

namespace LivePhotoBox.Models
{
    // 实况照片配对方式。
    public enum MetadataMatchingMode
    {
        // 先文件名匹配，剩余用 ContentIdentifier 匹配（默认，不含拍摄日期）。
        Both = 0,
        // 先文件名匹配，再用 ContentIdentifier + 拍摄日期兜底。
        BothWithDate = 1,
        // 仅按文件名匹配（和现有行为一致）。
        FilenameOnly = 2,
        // 仅按 ContentIdentifier 匹配，忽略文件名（不含拍摄日期）。
        MetadataOnly = 3
    }

    // Shared constants for Live Photo detection / splitting.
    public static class LivePhotoConstants
    {
        // 元数据探针读取的字节数（1MB），用于快速检测实况照片标记。
        public const int MetadataProbeBytes = 1024 * 1024;

        // 用于从 HEIC/XMP 中提取 MicroVideoOffset 的正则表达式。
        public static readonly Regex MicroVideoOffsetRegex = new(
            @"GCamera:MicroVideoOffset=""(?<value>\d+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // <summary>Max video duration (seconds) for a MOV/MP4 to be considered a Live Photo.
        // iPhone Live Photos are typically 1–3s; 3.5s adds safety margin (~3.09s observed max).</summary>
        public const double MaxLivePhotoVideoDurationSeconds = 3.5;

        // 用于从 HEIC/XMP 中提取 MotionPhoto 数据长度的正则表达式。
        public static readonly Regex MotionPhotoLengthRegex = new(
            @"Item:Semantic=""MotionPhoto""[^>]*Item:Length=""(?<value>\d+)""|Item:Length=""(?<value>\d+)""[^>]*Item:Semantic=""MotionPhoto""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
    }
}

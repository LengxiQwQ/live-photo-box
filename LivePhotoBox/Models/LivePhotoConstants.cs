using System;
using System.Text.RegularExpressions;

namespace LivePhotoBox.Models
{
    /// <summary>实况照片配对方式。</summary>
    public enum MetadataMatchingMode
    {
        /// <summary>先文件名匹配，剩余用 ContentIdentifier 匹配（默认，不含拍摄日期）。</summary>
        Both = 0,
        /// <summary>先文件名匹配，再用 ContentIdentifier + 拍摄日期兜底。</summary>
        BothWithDate = 1,
        /// <summary>仅按文件名匹配（和现有行为一致）。</summary>
        FilenameOnly = 2,
        /// <summary>仅按 ContentIdentifier 匹配，忽略文件名（不含拍摄日期）。</summary>
        MetadataOnly = 3
    }

    /// <summary>Shared constants for Live Photo detection / splitting.</summary>
    public static class LivePhotoConstants
    {
        public const int MetadataProbeBytes = 1024 * 1024;

        public static readonly Regex MicroVideoOffsetRegex = new(
            @"GCamera:MicroVideoOffset=""(?<value>\d+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        /// <summary>Max video duration (seconds) for a MOV/MP4 to be considered a Live Photo.
        /// iPhone Live Photos are typically 1–3s; 3.5s adds safety margin (~3.09s observed max).</summary>
        public const double MaxLivePhotoVideoDurationSeconds = 3.5;

        public static readonly Regex MotionPhotoLengthRegex = new(
            @"Item:Semantic=""MotionPhoto""[^>]*Item:Length=""(?<value>\d+)""|Item:Length=""(?<value>\d+)""[^>]*Item:Semantic=""MotionPhoto""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
    }
}

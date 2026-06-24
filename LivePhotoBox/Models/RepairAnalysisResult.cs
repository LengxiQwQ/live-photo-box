namespace LivePhotoBox.Models
{
    public enum RepairIssueType
    {
        Perfect,       // 状况C：原生竖向且没有缩图（完美跳过）
        NeedsStrip,    // 状况B：底层正的，藏了缩略图（需要瘦身）
        NeedsRebuild,  // 状况A：底层歪了（需要重构并剥离）
        Error          // 读取出错
    }

    public class RepairAnalysisResult
    {
        public RepairIssueType IssueType { get; set; }
        public string IssueDescription { get; set; } = string.Empty;
        public int RotationAngle { get; set; } = 0;
        public bool NeedsRepair => IssueType == RepairIssueType.NeedsStrip || IssueType == RepairIssueType.NeedsRebuild;
        public bool HasThumbnail { get; set; } = false;
        /// <summary>HEIC: Original QuickTime:Rotation value preserved during repair (never cleared).</summary>
        public string HeicOriginalRotation { get; set; } = string.Empty;
        /// <summary>Whether this file is a video (MOV/MP4).</summary>
        public bool IsVideo { get; set; } = false;
        /// <summary>Video rotation angle from QuickTime Rotation tag (0/90/180/270).</summary>
        public int VideoRotationAngle { get; set; } = 0;
        /// <summary>Video codec identifier from exiftool CompressorID (e.g. "hvc1"=HEVC, "avc1"=H.264).</summary>
        public string VideoCodec { get; set; } = string.Empty;
        /// <summary>Original video bitrate in bps, parsed from exiftool AvgBitrate (e.g. "12.2 Mbps" → 12200000).</summary>
        public long VideoBitrateBps { get; set; } = 0;
        /// <summary>Video duration in seconds, parsed from exiftool MediaDuration (e.g. "2.35 s" → 2.35). 0 if unknown.</summary>
        public double VideoDurationSeconds { get; set; } = 0;
        /// <summary>Apple ContentIdentifier UUID linking photo to its paired video. Empty if not present.</summary>
        public string ContentIdentifier { get; set; } = string.Empty;
        /// <summary>True if this file has a ContentIdentifier (strong indicator of Live Photo).</summary>
        public bool HasContentIdentifier => !string.IsNullOrWhiteSpace(ContentIdentifier);
        /// <summary>EXIF DateTimeOriginal — 原始拍摄时间（精确到秒）。照片存本地时间，视频存 UTC。</summary>
        public string DateTimeOriginal { get; set; } = string.Empty;
        /// <summary>QuickTime / EXIF CreateDate — 创建时间。兜底字段，当 DateTimeOriginal 为空时使用。</summary>
        public string CreateDate { get; set; } = string.Empty;
        /// <summary>EXIF OffsetTimeOriginal — 拍摄时间的 UTC 偏移量（如 "+08:00"）。用于照片日期转 UTC。</summary>
        public string OffsetTimeOriginal { get; set; } = string.Empty;
    }
}

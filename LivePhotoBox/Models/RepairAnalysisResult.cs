namespace LivePhotoBox.Models
{
    // 表示照片/视频的诊断问题类型。
    public enum RepairIssueType
    {
        Perfect,       // 状况C：原生竖向且没有缩图（完美跳过）
        NeedsStrip,    // 状况B：底层正的，藏了缩略图（需要瘦身）
        NeedsRebuild,  // 状况A：底层歪了（需要重构并剥离）
        Error          // 读取出错
    }

    // 单张照片/视频的诊断分析结果。包含问题类型、旋转角度、缩略图信息及实况照片元数据。
    public class RepairAnalysisResult
    {
        // 诊断出的问题类型
        public RepairIssueType IssueType { get; set; }
        // 问题描述文本
        public string IssueDescription { get; set; } = string.Empty;
        // 检测到的旋转角度（0/90/180/270）
        public int RotationAngle { get; set; } = 0;
        // 是否需要修复（NeedsStrip 或 NeedsRebuild）
        public bool NeedsRepair => IssueType == RepairIssueType.NeedsStrip || IssueType == RepairIssueType.NeedsRebuild;
        // 是否存在内嵌缩略图
        public bool HasThumbnail { get; set; } = false;
        // HEIC: Original QuickTime:Rotation value preserved during repair (never cleared).
        public string HeicOriginalRotation { get; set; } = string.Empty;
        // Whether this file is a video (MOV/MP4).
        public bool IsVideo { get; set; } = false;
        // Video rotation angle from QuickTime Rotation tag (0/90/180/270).
        public int VideoRotationAngle { get; set; } = 0;
        // Video codec identifier from exiftool CompressorID (e.g. "hvc1"=HEVC, "avc1"=H.264).
        public string VideoCodec { get; set; } = string.Empty;
        // Original video bitrate in bps, parsed from exiftool AvgBitrate (e.g. "12.2 Mbps" → 12200000).
        public long VideoBitrateBps { get; set; } = 0;
        // Video duration in seconds, parsed from exiftool MediaDuration (e.g. "2.35 s" → 2.35). 0 if unknown.
        public double VideoDurationSeconds { get; set; } = 0;
        // Apple ContentIdentifier UUID linking photo to its paired video. Empty if not present.
        public string ContentIdentifier { get; set; } = string.Empty;
        // True if this file has a ContentIdentifier (strong indicator of Live Photo).
        public bool HasContentIdentifier => !string.IsNullOrWhiteSpace(ContentIdentifier);
        // EXIF DateTimeOriginal — 原始拍摄时间（精确到秒）。照片存本地时间，视频存 UTC。
        public string DateTimeOriginal { get; set; } = string.Empty;
        // QuickTime / EXIF CreateDate — 创建时间。兜底字段，当 DateTimeOriginal 为空时使用。
        public string CreateDate { get; set; } = string.Empty;
        // EXIF OffsetTimeOriginal — 拍摄时间的 UTC 偏移量（如 "+08:00"）。用于照片日期转 UTC。
        public string OffsetTimeOriginal { get; set; } = string.Empty;
    }
}

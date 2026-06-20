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
    }
}

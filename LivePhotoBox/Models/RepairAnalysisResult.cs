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
    }
}

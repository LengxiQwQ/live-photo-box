namespace LivePhotoBox.Models
{
    /// <summary>扫描或批处理任务的进度快照。</summary>
    public readonly record struct WorkProgressSnapshot(
        int Total,
        int Completed,
        int RecognizedCount = 0,
        int SkippedCount = 0);
}

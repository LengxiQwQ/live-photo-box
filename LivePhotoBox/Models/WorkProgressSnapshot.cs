namespace LivePhotoBox.Models
{
    // 扫描或批处理任务的进度快照。
    public readonly record struct WorkProgressSnapshot(
        // 文件总数
        int Total,
        // 已完成处理的数量
        int Completed,
        // 识别为有效文件的数量
        int RecognizedCount = 0,
        // 跳过的文件数量
        int SkippedCount = 0);
}

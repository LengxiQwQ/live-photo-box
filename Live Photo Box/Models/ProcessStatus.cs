namespace LivePhotoBox.Models
{
    // 表示任务的处理状态。
    public enum ProcessStatus
    {
        // 任务等待处理
        Pending,
        // 任务正在处理中
        Processing,
        // 任务处理成功
        Success,
        // 任务处理失败
        Failed,
        // 任务被用户取消
        Cancelled
    }
}

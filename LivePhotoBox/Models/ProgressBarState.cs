namespace LivePhotoBox.Models
{
    // 表示进度条的运行状态。
    public enum ProgressBarState
    {
        // 空闲 — 无任务运行
        Idle,
        // 正在扫描文件
        Scanning,
        // 正在处理任务
        Processing,
        // 正在暂停中（过渡状态）
        Pausing,
        // 已暂停
        Paused,
        // 已取消
        Cancelled,
        // 全部处理成功
        Success
    }
}
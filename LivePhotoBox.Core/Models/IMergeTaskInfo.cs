namespace LivePhotoBox.Models
{
    // 合并任务的核心数据接口。Core 中的跑腿服务依赖此接口，
    // GUI 的 MergeTask 和 CLI 的轻量 record 都实现它。
    public interface IMergeTaskInfo
    {
        int Index { get; }
        string ImagePath { get; }
        string VideoPath { get; }
        string BaseName { get; }
        ProcessStatus Status { get; set; }
        string Details { get; set; }
    }
}

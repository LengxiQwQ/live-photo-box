using LivePhotoBox.Models;

namespace LivePhotoBox.Cli.Models
{
    // CLI 批量模式的轻量级任务对象，实现 IMergeTaskInfo。
    // GUI 使用完整的 MergeTask（含 Thumbnail），CLI 只需要数据字段。
    internal sealed class CliMergeTask : IMergeTaskInfo
    {
        public int Index { get; set; }
        public string ImagePath { get; set; } = string.Empty;
        public string VideoPath { get; set; } = string.Empty;
        public string BaseName { get; set; } = string.Empty;
        public ProcessStatus Status { get; set; } = ProcessStatus.Pending;
        public string Details { get; set; } = string.Empty;
    }
}

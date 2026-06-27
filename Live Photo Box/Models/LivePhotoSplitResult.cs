namespace LivePhotoBox.Models
{
    // 实况照片拆分结果 — 包含拆分后输出的照片和视频文件路径。
    public sealed class LivePhotoSplitResult
    {
        // 拆分后输出的图片文件路径（HEIC/JPEG）
        public required string ImageOutputPath { get; init; }
        // 拆分后输出的视频文件路径（MOV/MP4）
        public required string VideoOutputPath { get; init; }
    }
}

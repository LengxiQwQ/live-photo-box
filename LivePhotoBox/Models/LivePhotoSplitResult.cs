namespace LivePhotoBox.Models
{
    public sealed class LivePhotoSplitResult
    {
        public required string ImageOutputPath { get; init; }
        public required string VideoOutputPath { get; init; }
    }
}

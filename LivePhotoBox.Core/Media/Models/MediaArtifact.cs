namespace LivePhotoBox.Media.Models;

/// <summary>
/// Immutable typed media artifact representation within a transaction workspace or source.
/// </summary>
public sealed record MediaArtifact
{
    public required string Path { get; init; }
    public required MediaArtifactKind Kind { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public ImageCodec ImageCodec { get; init; } = ImageCodec.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;
    public long ByteLength { get; init; }
    public long SourceOffset { get; init; }
    public string? Sha256 { get; init; }
}

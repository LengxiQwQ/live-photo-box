namespace LivePhotoBox.Media.Models;

/// <summary>
/// Represents a typed immutable media artifact stored in the workspace or source location.
/// </summary>
public sealed record MediaArtifact
{
    public required string Path { get; init; }
    public required MediaArtifactKind Kind { get; init; }
    public required string MimeType { get; init; }
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public ImageCodec ImageCodec { get; init; } = ImageCodec.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;
    public long ByteLength { get; init; }
    public (long Offset, long Length)? SourceRange { get; init; }
    public string? Sha256 { get; init; }
}

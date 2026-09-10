namespace LivePhotoBox.Media.Models;

/// <summary>
/// An Inspector-confirmed item in a HEIF derived-item dependency graph.
/// The range identifies the complete item payload/descriptor range; it is
/// never treated as a standalone artifact by itself.
/// </summary>
public sealed record HeifDependencyFacts
{
    public uint ItemId { get; init; }
    public string ItemType { get; init; } = string.Empty;
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
}

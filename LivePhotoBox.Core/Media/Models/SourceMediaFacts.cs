namespace LivePhotoBox.Media.Models;

/// <summary>
/// Immutable facts describing an inspected media source file or pair.
/// </summary>
public sealed record SourceMediaFacts
{
    public required SourceProtocol Protocol { get; init; }
    public required ImageFacts PrimaryImage { get; init; }
    public VideoFacts? MotionVideo { get; init; }
    public GainMapFacts? GainMap { get; init; }
    public TimingFacts Timing { get; init; } = new();
    public long ProtocolTailOffset { get; init; }
    public long ProtocolTailLength { get; init; }
    public string? PairingIdentifier { get; init; }
}

public sealed record ImageFacts
{
    public bool IsPresent { get; init; }
    public ImageContainer Container { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
}

public sealed record VideoFacts
{
    public bool IsPresent { get; init; }
    public VideoContainer Container { get; init; }
    public VideoCodec Codec { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public int RotationDegrees { get; init; }
    public double DurationSeconds { get; init; }
    public double Fps { get; init; }
    public bool HasAudio { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
}

public sealed record GainMapFacts
{
    public bool IsPresent { get; init; }
    public ImageContainer Container { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
}

public sealed record TimingFacts
{
    public long CoverTimestampUs { get; init; }
    public long PrimaryTimestampUs { get; init; }
    public int CoverFrameIndex { get; init; }
    public int TotalFrames { get; init; }
}

using System;
using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

public sealed record ImageFacts
{
    public required ImageContainer Container { get; init; }
    public required ImageCodec Codec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Orientation { get; init; } = 1;
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
    public required string FilePath { get; init; }
}

public sealed record VideoFacts
{
    public required VideoContainer Container { get; init; }
    public required VideoCodec Codec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int RotationDegrees { get; init; }
    public TimeSpan Duration { get; init; }
    public double FrameRate { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
    public required string FilePath { get; init; }
}

public sealed record AudioTrackFacts
{
    public required string Codec { get; init; }
    public int Channels { get; init; }
    public int SampleRate { get; init; }
}

public sealed record GainMapFacts
{
    public required bool IsPresent { get; init; }
    public string? Format { get; init; }
    public ImageContainer Container { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
}

public sealed record AuxiliaryItemFacts
{
    public required string Name { get; init; }
    public required string MimeType { get; init; }
    public long ByteOffset { get; init; }
    public long ByteLength { get; init; }
    public string? Semantic { get; init; }
}

public sealed record TimingFacts
{
    public long? CoverTimestampUs { get; init; }
    public double? StillImageTimeMs { get; init; }
    public int? CoverFrameIndex { get; init; }
    public int? TotalFrames { get; init; }
}

public sealed record SourceMediaFacts
{
    public required SourceProtocol Protocol { get; init; }
    public required string SourceFilePath { get; init; }
    public string? SecondaryFilePath { get; init; }
    public long SourceFileSizeBytes { get; init; }
    public string? SourceSha256 { get; init; }

    public ImageFacts? PrimaryImage { get; init; }
    public VideoFacts? MotionVideo { get; init; }
    public GainMapFacts? GainMap { get; init; }
    public IReadOnlyList<AuxiliaryItemFacts> AuxiliaryItems { get; init; } = [];
    public IReadOnlyList<AudioTrackFacts> AudioTracks { get; init; } = [];
    public TimingFacts Timing { get; init; } = new();

    public string? PairingIdentity { get; init; }
    public IReadOnlyDictionary<string, string> ExifTags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> XmpPackets { get; init; } = [];
    public IReadOnlyDictionary<string, string> VendorFacts { get; init; } = new Dictionary<string, string>();

    public bool IsLivePhoto => Protocol is not (SourceProtocol.NonLiveImage or SourceProtocol.NonLiveVideo or SourceProtocol.Unknown)
                               && PrimaryImage != null && MotionVideo != null;
}

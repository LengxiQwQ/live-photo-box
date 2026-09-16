using System;

namespace LivePhotoBox.Media.Models;

public enum VideoBackend
{
    Unknown = 0,
    ProjectIsoBmffRemux = 1,
    WindowsMediaFoundation = 2
}

public enum VideoHardwareMode
{
    NotApplicable = 0,
    SoftwareForced = 1,
    Unknown = 2
}

public sealed record VideoConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required VideoContainer TargetContainer { get; init; }
    public required VideoCodec TargetCodec { get; init; }
    public required string TargetDirectory { get; init; }
    public int Crf { get; init; } = 23;
    public int TargetFps { get; init; } = 0;
}

public sealed record VideoExecutionRecord
{
    public required VideoContainer InputContainer { get; init; }
    public required VideoCodec InputCodec { get; init; }
    public required VideoContainer RequestedContainer { get; init; }
    public required VideoCodec RequestedCodec { get; init; }
    public required VideoContainer OutputContainer { get; init; }
    public required VideoCodec OutputCodec { get; init; }
    public bool RemuxUsed { get; init; }
    public VideoBackend Backend { get; init; } = VideoBackend.Unknown;
    public VideoHardwareMode HardwareMode { get; init; } = VideoHardwareMode.Unknown;
    public string SelectedEncoder { get; init; } = string.Empty;
    public bool HardwareFallbackOccurred { get; init; }
    public string HardwareFallbackReason { get; init; } = string.Empty;
    public bool AudioPreserved { get; init; }
    public bool RotationPreserved { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record VideoConversionResult
{
    public bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required VideoExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

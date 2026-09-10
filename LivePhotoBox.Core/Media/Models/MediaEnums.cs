namespace LivePhotoBox.Media.Models;

/// <summary>
/// Image container format.
/// </summary>
public enum ImageContainer
{
    Unknown = 0,
    Jpeg = 1,
    Heic = 2
}

/// <summary>
/// Image codec format.
/// </summary>
public enum ImageCodec
{
    Unknown = 0,
    Jpeg = 1,
    Hevc = 2
}

/// <summary>
/// Video container format.
/// </summary>
public enum VideoContainer
{
    Unknown = 0,
    Mp4 = 1,
    Mov = 2
}

/// <summary>
/// Video codec format.
/// </summary>
public enum VideoCodec
{
    Unknown = 0,
    Copy = 1,
    H264 = 2,
    Hevc = 3
}

/// <summary>
/// Detected source protocol for a Live Photo.
/// </summary>
public enum SourceProtocol
{
    Unknown = 0,
    NonLive = 1,
    GoogleMicroVideoV1 = 2,
    GoogleMotionPhotoV2 = 3,
    OppoLivePhoto = 4,
    VivoLivePhoto = 5,
    VivoLegacyDualFile = 6,
    SamsungMotionPhotoJpeg = 7,
    SamsungMotionPhotoHeic = 8,
    HuaweiMovingPhoto = 9,
    HonorMovingPhoto = 10,
    AppleLivePhoto = 11
}

/// <summary>
/// Kind of media artifact in the workspace.
/// </summary>
public enum MediaArtifactKind
{
    PrimaryImage,
    MotionVideo,
    GainMap,
    AuxiliaryItem,
    SourceContainer
}

/// <summary>
/// Codec identity for an Inspector-confirmed auxiliary item.  Auxiliary
/// items are not assumed to be images merely because they live in an image
/// container; the codec is carried independently for preservation handoff.
/// </summary>
public enum AuxiliaryCodec
{
    Unknown = 0,
    Jpeg = 1,
    Hevc = 2,
    H264 = 3,
    Copy = 4
}

/// <summary>Typed source-container preservation carrier category.</summary>
public enum PreservationCarrierKind
{
    Unknown = 0,
    SamsungSef = 1,
    ProtocolTrailer = 2,
    ContainerMetadata = 3,
    OpaqueFragment = 4
}

/// <summary>
/// HDR GainMap preservation policy.
/// </summary>
public enum PreservationPolicy
{
    Strict,
    BestEffort,
    AllowDiscard
}

/// <summary>
/// Outcome of preservation handling.
/// </summary>
public enum PreservationOutcome
{
    Preserved,
    /// <summary>
    /// The media was re-encoded. This is not lossless, even when selected
    /// audio and rotation metadata were retained.
    /// </summary>
    Reencoded,
    TranscodedLossless,
    DegradedToSdr,
    DiscardedNotApplicable,
    PartiallyPreserved,
    Unsupported
}

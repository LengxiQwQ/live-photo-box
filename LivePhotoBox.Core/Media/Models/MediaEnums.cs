namespace LivePhotoBox.Media.Models;

public enum ImageContainer
{
    Unknown = 0,
    Jpeg,
    Heic,
    Png,
    WebP
}

public enum ImageCodec
{
    Unknown = 0,
    Jpeg,
    Hevc,
    Png,
    WebP
}

public enum VideoContainer
{
    Unknown = 0,
    Mp4,
    Mov
}

public enum VideoCodec
{
    Unknown = 0,
    Copy,
    H264,
    Hevc
}

public enum SourceProtocol
{
    Unknown = 0,
    GoogleMicroVideoV1,
    GoogleMotionPhotoV2,
    OppoLivePhoto,
    VivoLivePhoto,
    VivoLegacyDualFile,
    SamsungMotionPhoto,
    HuaweiMovingPhoto,
    HonorMovingPhoto,
    AppleLivePhoto,
    NonLiveImage,
    NonLiveVideo
}

public enum MediaArtifactKind
{
    PrimaryImage,
    MotionVideo,
    AuxiliaryImage,
    GainMap,
    TrailerData,
    MetadataPacket
}

public enum PreservationPolicy
{
    BestEffort = 0,
    RequirePreservation = 1
}

public enum PreservationOutcome
{
    NotApplicable = 0,
    Preserved = 1,
    Downgraded = 2,
    Dropped = 3
}

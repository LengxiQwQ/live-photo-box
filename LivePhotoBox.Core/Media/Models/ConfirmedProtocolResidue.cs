using System;
using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

public enum ResidueStructureKind
{
    XmpProperty,
    XmpContainerItem,
    ExifMakerNoteTag,
    QuickTimeMdtaKey,
    QuickTimeMetadataTrack,
    IsoBmffBox,
    HeifItem,
    HeifProperty,
    SefEntry,
    ProtocolTailRange,
    UuidBox
}

public enum CoordinateSpace
{
    OriginalSourceRange,
    ExtractedArtifactRange,
    StructuredSelector
}

public enum ResidueRemovalMode
{
    Delete,
    ZeroFill,
    RebuildContainer
}

/// <summary>
/// Machine-consumable cleanup authorization explicitly granted by Source Inspector evidence.
/// Cleaner may only modify structures that match an authorized residue.
/// </summary>
public sealed record ConfirmedProtocolResidue
{
    public required string Id { get; init; }
    public required SourceProtocol OwnerProtocol { get; init; }
    public required MediaArtifactKind ArtifactRole { get; init; }
    public required ResidueStructureKind StructureKind { get; init; }
    public required string Selector { get; init; }
    public string? ExpectedSemantic { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public CoordinateSpace CoordinateSpace { get; init; } = CoordinateSpace.StructuredSelector;
    public ResidueRemovalMode RemovalMode { get; init; } = ResidueRemovalMode.Delete;
    public bool RequiredAfterExtraction { get; init; } = true;
}

/// <summary>
/// Authoritative builder that derives field/tag/track level cleanup authorizations
/// from P1-confirmed inspection facts.
/// </summary>
public static class CleanupAuthorizationAuthority
{
    public static IReadOnlyList<ConfirmedProtocolResidue> ResolveAuthorizations(SourceMediaFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Protocol == SourceProtocol.NonLive || facts.Protocol == SourceProtocol.Unknown)
        {
            return Array.Empty<ConfirmedProtocolResidue>();
        }

        var list = new List<ConfirmedProtocolResidue>();

        switch (facts.Protocol)
        {
            case SourceProtocol.AppleLivePhoto:
                // Primary Image: Apple MakerNote tags
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-img-makernote-0011",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.ExifMakerNoteTag,
                    Selector = "0x0011",
                    ExpectedSemantic = "ContentIdentifier",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-img-makernote-0017",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.ExifMakerNoteTag,
                    Selector = "0x0017",
                    ExpectedSemantic = "LivePhotoEntry17",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-img-makernote-0025",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.ExifMakerNoteTag,
                    Selector = "0x0025",
                    ExpectedSemantic = "LivePhotoEntry25",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-img-makernote-002b",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.ExifMakerNoteTag,
                    Selector = "0x002b",
                    ExpectedSemantic = "LivePhotoEntry2B",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });

                // Motion Video: QuickTime MDTA keys and tracks
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-mdta-cid",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.apple.quicktime.content.identifier",
                    ExpectedSemantic = "ContentIdentifier",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-mdta-livephoto",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.apple.quicktime.live-photo",
                    ExpectedSemantic = "LivePhotoKey",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-track-livephoto-info",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMetadataTrack,
                    Selector = "com.apple.quicktime.live-photo-info",
                    ExpectedSemantic = "LivePhotoInfoTrack",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-track-still-image-time",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMetadataTrack,
                    Selector = "com.apple.quicktime.still-image-time",
                    ExpectedSemantic = "StillImageTimeTrack",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-track-transform",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMetadataTrack,
                    Selector = "com.apple.quicktime.live-photo-still-image-transform",
                    ExpectedSemantic = "StillImageTransformTrack",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "apple-vid-track-reference-dimensions",
                    OwnerProtocol = SourceProtocol.AppleLivePhoto,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMetadataTrack,
                    Selector = "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions",
                    ExpectedSemantic = "TransformReferenceDimensionsTrack",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.GoogleMicroVideoV1:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v1-xmp-microvideo",
                    OwnerProtocol = SourceProtocol.GoogleMicroVideoV1,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MicroVideo",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v1-xmp-version",
                    OwnerProtocol = SourceProtocol.GoogleMicroVideoV1,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MicroVideoVersion",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v1-xmp-offset",
                    OwnerProtocol = SourceProtocol.GoogleMicroVideoV1,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MicroVideoOffset",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v1-xmp-pts",
                    OwnerProtocol = SourceProtocol.GoogleMicroVideoV1,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MicroVideoPresentationTimestampUs",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.GoogleMotionPhotoV2:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v2-xmp-motionphoto",
                    OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v2-xmp-version",
                    OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MotionPhotoVersion",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v2-xmp-pts",
                    OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MotionPhotoPresentationTimestampUs",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "google-v2-container-item-motionphoto",
                    OwnerProtocol = SourceProtocol.GoogleMotionPhotoV2,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpContainerItem,
                    Selector = "Item:Semantic=MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.OppoLivePhoto:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-xmp-version",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "OLivePhotoVersion",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-xmp-videolength",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "VideoLength",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-xmp-owner",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "MotionPhotoOwner",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-xmp-pts",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "MotionPhotoPrimaryPresentationTimestampUs",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-xmp-enable",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "MotionPhotoEnable",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "oppo-container-item-motionphoto",
                    OwnerProtocol = SourceProtocol.OppoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpContainerItem,
                    Selector = "Item:Semantic=MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.VivoLivePhoto:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-xmp-version",
                    OwnerProtocol = SourceProtocol.VivoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "VMotionPhotoVersion",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-xmp-source",
                    OwnerProtocol = SourceProtocol.VivoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "VMotionPhotoSource",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-xmp-flags",
                    OwnerProtocol = SourceProtocol.VivoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "VMotionPhotoFlags",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-xmp-mediakit",
                    OwnerProtocol = SourceProtocol.VivoLivePhoto,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "VMediaKitVersion",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.VivoLegacyDualFile:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-legacy-vid-uuid",
                    OwnerProtocol = SourceProtocol.VivoLegacyDualFile,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.UuidBox,
                    Selector = "vivoMediaExtInfo",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-legacy-vid-mdta-livephoto",
                    OwnerProtocol = SourceProtocol.VivoLegacyDualFile,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.android.camera.livephoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-legacy-vid-mdta-imagetime",
                    OwnerProtocol = SourceProtocol.VivoLegacyDualFile,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.android.camera.imageTime",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "vivo-legacy-vid-mdta-gallery",
                    OwnerProtocol = SourceProtocol.VivoLegacyDualFile,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.vivo.gallery.livePhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.SamsungMotionPhotoJpeg:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-jpeg-sef-0a30",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoJpeg,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.SefEntry,
                    Selector = "0x0A30:MotionPhoto_Data",
                    ExpectedSemantic = "MotionPhoto_Data",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-jpeg-sef-0a31",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoJpeg,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.SefEntry,
                    Selector = "0x0A31:MotionPhoto_Version",
                    ExpectedSemantic = "MotionPhoto_Version",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.RebuildContainer
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-jpeg-xmp-motionphoto",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoJpeg,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.SamsungMotionPhotoHeic:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-heic-box-mpvd",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoHeic,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.IsoBmffBox,
                    Selector = "mpvd",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-heic-box-sefd-motion",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoHeic,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.IsoBmffBox,
                    Selector = "sefd:0x0A30",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-heic-xmp-motionphoto",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoHeic,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpProperty,
                    Selector = "GCamera:MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "samsung-heic-container-item-motionphoto",
                    OwnerProtocol = SourceProtocol.SamsungMotionPhotoHeic,
                    ArtifactRole = MediaArtifactKind.PrimaryImage,
                    StructureKind = ResidueStructureKind.XmpContainerItem,
                    Selector = "Item:Semantic=MotionPhoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;

            case SourceProtocol.HuaweiMovingPhoto:
            case SourceProtocol.HonorMovingPhoto:
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "huawei-vid-mdta-openharmony",
                    OwnerProtocol = facts.Protocol,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.openharmony.movingphoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "huawei-vid-mdta-huawei",
                    OwnerProtocol = facts.Protocol,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.huawei.movingphoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "huawei-vid-mdta-covertime",
                    OwnerProtocol = facts.Protocol,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMdtaKey,
                    Selector = "com.openharmony.covertime",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                list.Add(new ConfirmedProtocolResidue
                {
                    Id = "huawei-vid-track-movingphoto",
                    OwnerProtocol = facts.Protocol,
                    ArtifactRole = MediaArtifactKind.MotionVideo,
                    StructureKind = ResidueStructureKind.QuickTimeMetadataTrack,
                    Selector = "com.openharmony.timed_metadata.movingphoto",
                    CoordinateSpace = CoordinateSpace.StructuredSelector,
                    RemovalMode = ResidueRemovalMode.Delete
                });
                break;
        }

        return list;
    }
}

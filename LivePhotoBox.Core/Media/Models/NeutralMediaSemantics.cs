using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

/// <summary>
/// Evidence attached to a neutral media fact. Unknown means that the current
/// pipeline did not establish the fact; it must not be inferred from source
/// vendor, file location, or selected backend.
/// </summary>
public enum NeutralEvidenceState
{
    Unknown,
    Verified,
    VerifiedPreserved,
    VerifiedConverted,
    NotApplicable
}

public enum NeutralArtifactRole
{
    PrimaryImage,
    MotionVideo
}

/// <summary>Representation of an auxiliary semantic asset in the final bundle.</summary>
public enum NeutralAuxiliaryRepresentation
{
    Unknown,
    Embedded,
    Detached,
    Materialized,
    Both
}

public enum NeutralHdrState
{
    Unknown,
    NoGainMapObserved,
    GainMapPresent
}

public enum NeutralColorSpaceState
{
    Unknown,
    Verified
}

public enum NeutralReflection
{
    Unknown,
    None,
    Horizontal,
    Vertical
}

/// <summary>
/// Platform-, backend-, and source-protocol-neutral semantic projection of
/// the final media artifacts. Transport paths and transaction authority do
/// not belong in this graph.
/// </summary>
public sealed record NeutralMediaSemantics
{
    public required NeutralArtifactSemantics PrimaryImage { get; init; }
    public NeutralArtifactSemantics? MotionVideo { get; init; }
    public NeutralAuxiliarySemantics? GainMap { get; init; }
    public IReadOnlyList<NeutralAuxiliarySemantics> AuxiliaryMedia { get; init; } = [];
    public NeutralTimingSemantics Timing { get; init; } = new();
    public NeutralOrientationSemantics Orientation { get; init; } = new();
    public NeutralColorHdrSemantics ColorHdr { get; init; } = new();
    public NeutralPreservationSemantics Preservation { get; init; } = NeutralPreservationSemantics.Unknown;

    /// <summary>
    /// Compatibility default for callers constructing NeutralMediaBundle
    /// directly. NeutralMediaService always returns a populated projection.
    /// </summary>
    public static NeutralMediaSemantics Unknown { get; } = new()
    {
        PrimaryImage = new NeutralArtifactSemantics
        {
            Role = NeutralArtifactRole.PrimaryImage,
            EvidenceState = NeutralEvidenceState.Unknown
        },
        Timing = new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.Unknown },
        Orientation = new NeutralOrientationSemantics
        {
            PrimaryImage = new NeutralOrientationTransform(),
            MotionVideo = new NeutralOrientationTransform()
        },
        ColorHdr = new NeutralColorHdrSemantics(),
        Preservation = NeutralPreservationSemantics.Unknown
    };
}

/// <summary>
/// Semantic identity and format facts for a final materialized primary asset.
/// ContentIdentity is derived from its SHA-256 and contains no path or file ID.
/// </summary>
public sealed record NeutralArtifactSemantics
{
    public NeutralArtifactRole Role { get; init; }
    public string ContentIdentity { get; init; } = string.Empty;
    public string Semantic { get; init; } = string.Empty;
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public ImageCodec ImageCodec { get; init; } = ImageCodec.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;
    public PreservationOutcome? PreservationOutcome { get; init; }
    public NeutralEvidenceState EvidenceState { get; init; } = NeutralEvidenceState.Unknown;
}

/// <summary>Transport-independent identity and relationship for an auxiliary asset.</summary>
public sealed record NeutralAuxiliarySemantics
{
    public string StableIdentity { get; init; } = string.Empty;
    public string Semantic { get; init; } = string.Empty;
    public string OwnerIdentity { get; init; } = string.Empty;
    public string Relationship { get; init; } = string.Empty;
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public AuxiliaryCodec Codec { get; init; } = AuxiliaryCodec.Unknown;
    public NeutralAuxiliaryRepresentation Representation { get; init; } = NeutralAuxiliaryRepresentation.Unknown;
    public AuxiliaryOwnership Ownership { get; init; }
    public PreservationOutcome? PreservationOutcome { get; init; }
    public NeutralEvidenceState EvidenceState { get; init; } = NeutralEvidenceState.Unknown;
}

public sealed record NeutralTimingSemantics
{
    public long? CoverTimestampUs { get; init; }
    public long? PrimaryTimestampUs { get; init; }
    public int? CoverFrameIndex { get; init; }
    public int? TotalFrames { get; init; }
    public NeutralEvidenceState EvidenceState { get; init; } = NeutralEvidenceState.Unknown;
}

/// <summary>
/// A normalized visual transform. Rotation and reflection have separate
/// evidence because a rotation observation alone does not prove reflection.
/// </summary>
public sealed record NeutralOrientationTransform
{
    public int? ClockwiseRotationDegrees { get; init; }
    public NeutralReflection Reflection { get; init; } = NeutralReflection.Unknown;
    public NeutralEvidenceState RotationEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public NeutralEvidenceState ReflectionEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public ConversionComponentOutcome Preservation { get; init; } = ConversionComponentOutcome.NotEvaluated;
}

public sealed record NeutralOrientationSemantics
{
    public NeutralOrientationTransform PrimaryImage { get; init; } = new();
    public NeutralOrientationTransform? MotionVideo { get; init; }
}

/// <summary>
/// Color/HDR observations that the current authoritative inspection or
/// preservation/conversion evidence can support. Missing ICC/color details
/// remain Unknown; no assumption of SDR is made from GainMap absence alone.
/// </summary>
public sealed record NeutralColorHdrSemantics
{
    public bool? HasGainMap { get; init; }
    public NeutralHdrState HdrState { get; init; } = NeutralHdrState.Unknown;
    public NeutralEvidenceState GainMapEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public NeutralColorSpaceState ColorSpaceState { get; init; } = NeutralColorSpaceState.Unknown;
    public string? IccProfileSha256 { get; init; }
    public NeutralEvidenceState IccProfileEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public uint? EncodedBitDepth { get; init; }
    public uint? DecodedSignalBitDepth { get; init; }
    public uint? DecodedStorageBitDepth { get; init; }
    public ushort? NclxPrimaries { get; init; }
    public ushort? NclxTransfer { get; init; }
    public ushort? NclxMatrix { get; init; }
    public bool? IsHdrRelevant { get; init; }
    public NeutralEvidenceState HdrEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public double? HdrCapacityMin { get; init; }
    public double? HdrCapacityMax { get; init; }
    public NeutralEvidenceState GainMapMetadataEvidence { get; init; } = NeutralEvidenceState.Unknown;
    public ConversionComponentOutcome ColorIccPreservation { get; init; } = ConversionComponentOutcome.NotEvaluated;
    public ConversionComponentOutcome GainMapPreservation { get; init; } = ConversionComponentOutcome.NotEvaluated;
    public PreservationOutcome? PreservationOutcome { get; init; }
    public NeutralEvidenceState PreservationEvidence { get; init; } = NeutralEvidenceState.Unknown;
}

public sealed record NeutralArtifactPreservation
{
    public string SemanticIdentity { get; init; } = string.Empty;
    public PreservationOutcome? Outcome { get; init; }
    public NeutralEvidenceState EvidenceState { get; init; } = NeutralEvidenceState.Unknown;
}

public sealed record NeutralPreservationSemantics
{
    public NeutralArtifactPreservation PrimaryImage { get; init; } = new();
    public NeutralArtifactPreservation? MotionVideo { get; init; }
    public NeutralArtifactPreservation? GainMap { get; init; }
    public IReadOnlyList<NeutralArtifactPreservation> AuxiliaryMedia { get; init; } = [];

    public static NeutralPreservationSemantics Unknown { get; } = new();
}

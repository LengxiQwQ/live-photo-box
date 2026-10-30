using System;

namespace LivePhotoBox.Media.Models;

public enum UnspecifiedImageOrientationHandling { Reject = 0, UseStoredPixels = 1 }

/// <summary>Explicit user resolution, never evidence that the original direction was known.</summary>
public sealed record ImageOrientationResolutionEvidence(string SourceSha256,string OutputSha256,
    string MappedExifSha256,string ObservedExifSha256,ulong ByteLength,uint Width,uint Height)
{
    public string Scope => "UserSelectedStoredPixelsAndExactRemainingJpegBytes";
    public string OriginalDirectionState => "Unspecified";
    public bool PixelReencoded => false;
    public bool PixelRotated => false;
    public uint ProofKind => 3;
}

public sealed record ImageConversionRequest
{
    public required MediaArtifact SourceArtifact { get; init; }
    public required ImageContainer TargetContainer { get; init; }
    public required string TargetDirectory { get; init; }
    public int Quality { get; init; } = 92;
    public PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
    public ImageConversionSourceFacts? TrustedSourceFacts { get; init; }
    /// <summary>Required to enter the explicit R3 semantic HDR/GainMap route.</summary>
    public ImageHdrGainMapSourceBinding? TrustedHdrGainMapBinding { get; init; }
    /// <summary>Explicit vendor-neutral target representation for an R3 semantic route.</summary>
    public ImageHdrGainMapTargetSemantic TargetHdrGainMapSemantic { get; init; } = ImageHdrGainMapTargetSemantic.Unknown;
    /// <summary>HDR-specific output authority; AllowDiscard does not imply SDR degradation permission.</summary>
    public HdrOutputPolicy HdrOutputPolicy { get; init; } = HdrOutputPolicy.PreserveHdr;
    public ImageTransformKind Transform { get; init; } = ImageTransformKind.None;
}

public sealed record ImageExecutionRecord
{
    public required ImageContainer InputContainer { get; init; }
    public required ImageContainer OutputContainer { get; init; }
    public bool PixelReencoded { get; init; }
    public bool MetadataCopied { get; init; }
    public PreservationOutcome PreservationOutcome { get; init; }
    public TimeSpan Duration { get; init; }
    public ImageMetadataMappingEvidence? MetadataMapping { get; init; }
    public ImageOrdinaryConversionEvidence? OrdinaryConversionEvidence { get; init; }
    public ImageHdrConversionEvidence? HdrConversionEvidence { get; init; }
    public ConversionExecutionTruth Truth { get; init; } = new();
}

/// <summary>
/// Complete supported ordinary image inventory after authorized container and
/// physical direction mapping. Pixel reencoding remains explicit in execution
/// truth. Proprietary metadata byte carriage is not gallery editing acceptance.
/// </summary>
public sealed record ImageOrdinaryConversionEvidence(
    string SourceSha256,string OutputSha256,string MappedSourceMetadataSha256,string OutputMetadataSha256,
    uint SourceOrientation,uint OutputOrientation,uint SourceWidth,uint SourceHeight,uint OutputWidth,uint OutputHeight,
    uint MetadataCount,bool HasIcc)
{
    internal VerifiedImageOrientationMapping? NativeVerifiedOrientation { get; init; }
    public int ProofVersion => 1;
    public string Scope => "BoundOrdinaryImageInventoryAndPhysicalOrientation";
}

/// <summary>Both HDR image owners after mapped metadata and physical direction verification; pixels remain reencoded.</summary>
public sealed record ImageHdrConversionEvidence(
    string SourceSha256,string OutputSha256,string MappedSourceMetadataSha256,string OutputMetadataSha256,
    uint SourceOrientation,uint OutputOrientation,uint SourceWidth,uint SourceHeight,uint OutputWidth,uint OutputHeight,
    uint MetadataCount,bool HasIcc)
{
    internal VerifiedImageOrientationMapping? NativeVerifiedOrientation { get; init; }
    public int ProofVersion => 1;
    public uint ProofKind => 2;
    public string Scope => "BoundHdrOwnerImageInventoryAndPhysicalOrientation";
}

// Not serialized or constructible by external IImageConverter implementations.
// Public record cloning cannot change the independently frozen source/output
// binding. A DTO field alone is never authority to waive orientation checks.
internal sealed record VerifiedImageOrientationMapping(string SourceSha256,string OutputSha256,
    uint SourceOrientation,uint OutputOrientation);

/// <summary>Proof of ordinary primary XMP carriage, limited to the named mapping.</summary>
public sealed record ImageMetadataMappingEvidence(string SourceSha256,string OutputSha256)
{
    public int ProofVersion => 2;
    public uint MappingKind { get; init; } = 2;
    public string Scope => MappingKind switch
    {
        2 => "OrdinaryPrimaryXmpWithValidatedGainMapDescription",
        3 => "OrdinaryPrimaryXmpWithBoundHeifGainMapDirectoryMapping",
        _ => "Unknown"
    };
}

public sealed record ImageConversionResult
{
    public bool Success { get; init; }
    public MediaArtifact? OutputArtifact { get; init; }
    public required ImageExecutionRecord ExecutionRecord { get; init; }
    public string? ErrorMessage { get; init; }
}

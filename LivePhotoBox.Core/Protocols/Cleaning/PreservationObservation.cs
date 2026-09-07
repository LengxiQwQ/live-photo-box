using System;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Native-authoritative preservation facts for a single media artifact.
/// All binary media parsing is performed by LivePhotoBox.Native;
/// this is a pure value container for C# comparison and reporting.
/// </summary>
public sealed class PreservationObservation
{
    // Flags constants (mirroring LPB_POBS_* in native header)
    private const uint FlagHasExif        = 0x00000001u;
    private const uint FlagHasGps         = 0x00000002u;
    private const uint FlagHasIcc         = 0x00000004u;
    private const uint FlagHasMakerNote   = 0x00000008u;
    private const uint FlagHasXmp         = 0x00000010u;
    private const uint FlagHasExtXmp      = 0x00000020u;
    private const uint FlagHasHeicAux     = 0x00000040u;
    private const uint FlagHasVideoMdat   = 0x00000080u;
    private const uint FlagHasGainMapMeta = 0x00000100u;
    private const uint FlagExifError      = 0x00010000u;
    private const uint FlagIccError       = 0x00020000u;
    private const uint FlagMakerNoteMalf  = 0x00040000u;
    private const uint FlagXmpMalformed   = 0x00080000u;
    private const uint FlagHeicAuxAmbig   = 0x00100000u;
    private const uint FlagCodestreamErr  = 0x00200000u;

    public uint Flags { get; init; }

    // Presence
    public bool HasExif => (Flags & FlagHasExif) != 0;
    public bool HasGps => (Flags & FlagHasGps) != 0;
    public bool HasIcc => (Flags & FlagHasIcc) != 0;
    public bool HasMakerNote => (Flags & FlagHasMakerNote) != 0;
    public bool HasXmp => (Flags & FlagHasXmp) != 0;
    public bool HasExtendedXmp => (Flags & FlagHasExtXmp) != 0;
    public bool HasHeicAux => (Flags & FlagHasHeicAux) != 0;
    public bool HasVideoMdat => (Flags & FlagHasVideoMdat) != 0;
    public bool HasGainMapMeta => (Flags & FlagHasGainMapMeta) != 0;

    // Error flags
    public bool ExifParseError => (Flags & FlagExifError) != 0;
    public bool IccParseError => (Flags & FlagIccError) != 0;
    public bool MakerNoteMalformed => (Flags & FlagMakerNoteMalf) != 0;
    public bool XmpMalformed => (Flags & FlagXmpMalformed) != 0;
    public bool HeicAuxAmbiguous => (Flags & FlagHeicAuxAmbig) != 0;
    public bool CodestreamError => (Flags & FlagCodestreamErr) != 0;

    public string ImageCodestreamSha256 { get; init; } = "";
    public string ExifIfd0NonPtrSha256 { get; init; } = "";
    public string ExifExifIfdSha256 { get; init; } = "";
    public string DateTimeOriginal { get; init; } = "";
    public ushort Orientation { get; init; }
    public string GpsSha256 { get; init; } = "";
    public string IccSha256 { get; init; } = "";
    public string MakernoteNonliveSha256 { get; init; } = "";
    public string XmpNonprotocolSha256 { get; init; } = "";
    public string ExtendedXmpSha256 { get; init; } = "";
    public uint HeicPrimaryItemId { get; init; }
    public uint HeicAuxItemId { get; init; }
    public uint HeicAuxFromItemId { get; init; }
    public uint HeicAuxToItemId { get; init; }
    public string HeicAuxItemSha256 { get; init; } = "";
    public string HeicAuxType { get; init; } = "";
    public string VideoMdatSha256 { get; init; } = "";

    /// <summary>Maps a NativePreservationObservation to a managed PreservationObservation.</summary>
    internal static PreservationObservation FromNative(in Interop.NativePreservationObservation native) => new()
    {
        Flags = native.Flags,
        ImageCodestreamSha256 = native.ImageCodestreamSha256 ?? "",
        ExifIfd0NonPtrSha256 = native.ExifIfd0NonPtrSha256 ?? "",
        ExifExifIfdSha256 = native.ExifExifIfdSha256 ?? "",
        DateTimeOriginal = native.DatetimeOriginal ?? "",
        Orientation = native.Orientation,
        GpsSha256 = native.GpsSha256 ?? "",
        IccSha256 = native.IccSha256 ?? "",
        MakernoteNonliveSha256 = native.MakernoteNonliveSha256 ?? "",
        XmpNonprotocolSha256 = native.XmpNonprotocolSha256 ?? "",
        ExtendedXmpSha256 = native.ExtendedXmpSha256 ?? "",
        HeicPrimaryItemId = native.HeicPrimaryItemId,
        HeicAuxItemId = native.HeicAuxItemId,
        HeicAuxFromItemId = native.HeicAuxFromItemId,
        HeicAuxToItemId = native.HeicAuxToItemId,
        HeicAuxItemSha256 = native.HeicAuxItemSha256 ?? "",
        HeicAuxType = native.HeicAuxType ?? "",
        VideoMdatSha256 = native.VideoMdatSha256 ?? ""
    };
}

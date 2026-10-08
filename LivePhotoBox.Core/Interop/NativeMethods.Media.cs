using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_gainmap_metadata_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult InspectGainMapMetadataV1(nint context,
        string primaryImagePath, string? materializedGainMapPath, ref NativeGainMapMetadataV1 outMetadata);

    [LibraryImport(LibraryName, EntryPoint = "lpb_stage_hdr_gainmap_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult StageHdrGainMapV1(nint context,
        string sourcePath, string outputPath,
        ref NativeHdrGainMapConversionRequestV1 request,
        ref NativeHdrGainMapConversionResultV1 outResult);

    [LibraryImport(LibraryName, EntryPoint = "lpb_commit_hdr_gainmap_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CommitHdrGainMapV1(nint context, ulong transactionToken);

    [LibraryImport(LibraryName, EntryPoint = "lpb_abort_hdr_gainmap_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult AbortHdrGainMapV1(nint context, ulong transactionToken);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_inspect_hdr_gainmap_stage_v1", ExactSpelling = true)]
    internal static extern NativeResult InspectHdrGainMapStageV1(
        nint context,
        ulong transactionToken,
        ref NativeSourceMediaFacts outFacts,
        ref NativeGainMapMetadataV1 outMetadata,
        ref NativePreservationObservation outPreservation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_heic_image", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult InspectHeicImage(nint context, string inputImagePath,
        ref NativeHeicImageInfo outInfo);

    [LibraryImport(LibraryName, EntryPoint = "lpb_decode_heic_primary_image", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult DecodeHeicPrimaryImage(nint context, string inputImagePath,
        ref NativeHeicPrimaryDecodeInfo outInfo);

    [LibraryImport(LibraryName, EntryPoint = "lpb_decode_heic_auxiliary_image", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult DecodeHeicAuxiliaryImage(nint context, string inputImagePath,
        uint auxiliaryItemId, ref NativeHeicAuxiliaryInfo outInfo);

    [LibraryImport(LibraryName, EntryPoint = "lpb_encode_heic_primary_and_secondary_jpegs", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult EncodeHeicPrimaryAndSecondaryJpegs(nint context,
        string primaryJpegPath, string secondaryJpegPath, string outputHeicPath, int quality,
        ref NativeHeicEncodedImagesInfo outInfo);

    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_media", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult InspectMedia(
        nint context,
        string primaryPath,
        string? secondaryPath,
        ref NativeSourceMediaFacts outFacts);

    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_media_with_residues", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult InspectMediaWithResidues(
        nint context,
        string primaryPath,
        string? secondaryPath,
        ref NativeSourceMediaFacts outFacts,
        NativeConfirmedResidue* outResidues,
        nuint residuesCapacity,
        out nuint outResiduesCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_inspect_media_with_plan", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult InspectMediaWithPlan(
        nint context,
        string primaryPath,
        string? secondaryPath,
        ref NativeSourceMediaFacts outFacts,
        out nint extractionPlan,
        NativeConfirmedResidue* outResidues,
        nuint residuesCapacity,
        out nuint outResiduesCount,
        out ulong planGeneration);

    [LibraryImport(LibraryName, EntryPoint = "lpb_release_extraction_plan", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult ReleaseExtractionPlan(nint context, nint extractionPlan);

    [LibraryImport(LibraryName, EntryPoint = "lpb_claim_extraction_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult ClaimExtractionPlan(
        nint context,
        nint extractionPlan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_finish_extraction_plan")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult FinishExtractionPlan(
        nint context,
        nint extractionPlan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_extract_media_with_plan", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult ExtractMediaWithPlan(
        nint context,
        nint extractionPlan,
        string primaryPath,
        string? secondaryPath,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath);

    [LibraryImport(LibraryName, EntryPoint = "lpb_extract_media_with_plan_outputs", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ExtractMediaWithPlanOutputs(
        nint context,
        nint extractionPlan,
        string primaryPath,
        string? secondaryPath,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        NativeExtractionOutput* auxiliaryOutputs,
        nuint auxiliaryOutputCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_extract_media_with_plan_outputs_v2", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ExtractMediaWithPlanOutputsV2(
        nint context,
        nint extractionPlan,
        string primaryPath,
        string? secondaryPath,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        string? cleanupSourcePath,
        NativeExtractionOutput* auxiliaryOutputs,
        nuint auxiliaryOutputCount);

    [LibraryImport(LibraryName, EntryPoint = "lpb_rollback_extraction_outputs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult RollbackExtractionOutputs(
        nint context,
        nint extractionPlan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_verify_extraction_outputs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult VerifyExtractionOutputs(
        nint context,
        nint extractionPlan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_commit_extraction_outputs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CommitExtractionOutputs(
        nint context,
        nint extractionPlan,
        ulong generation);

    [LibraryImport(LibraryName, EntryPoint = "lpb_probe_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ProbeVideo(
        nint context,
        string videoPath,
        ref NativeVideoItemFacts outVideoFacts);

    [LibraryImport(LibraryName, EntryPoint = "lpb_remux_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult RemuxVideo(
        nint context,
        string inputVideoPath,
        string outputVideoPath,
        int targetContainer);

    [LibraryImport(LibraryName, EntryPoint = "lpb_convert_image", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult ConvertImage(
        nint context,
        string inputImagePath,
        string outputImagePath,
        int targetContainer,
        int quality,
        out int outReencoded);

    [LibraryImport(LibraryName, EntryPoint = "lpb_transform_jpeg_losslessly", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TransformJpegLosslessly(
        nint context,
        string inputImagePath,
        string outputImagePath,
        int transform);

    [LibraryImport(LibraryName, EntryPoint = "lpb_transcode_video", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult TranscodeVideo(
        nint context,
        string inputVideoPath,
        string outputVideoPath,
        int targetContainer,
        int targetCodec,
        int crf,
        byte* outEncoderUsed,
        nuint encoderBufLen);

    [LibraryImport(LibraryName, EntryPoint = "lpb_transcode_video_v2", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult TranscodeVideoV2(
        nint context,
        string inputVideoPath,
        string outputVideoPath,
        int targetContainer,
        int targetCodec,
        int crf,
        ref NativeVideoBackendDiagnostics outDiagnostics);

    [LibraryImport(LibraryName, EntryPoint = "lpb_reassemble_jpeg_gainmap", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult lpb_reassemble_jpeg_gainmap(
        nint context,
        string primaryJpegPath,
        string gainmapJpegPath,
        string outputPath,
        string expectedGainMapSha256);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_capture_preservation_observation", ExactSpelling = true)]
    internal static extern NativeResult lpb_capture_preservation_observation(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mediaPath,
        int protocolHint,
        int containerHint,
        ref NativePreservationObservation outObservation);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_observe_image_orientation_v1", ExactSpelling = true)]
    internal static extern NativeResult ObserveImageOrientationV1(
        nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string imagePath,
        int containerHint,
        ref NativeImageOrientationObservationV1 outObservation);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "lpb_verify_preservation", ExactSpelling = true)]
    internal static extern NativeResult lpb_verify_preservation(
        nint context,
        ref NativePreservationObservation pre,
        ref NativePreservationObservation post,
        int protocol,
        uint detachedGainmapState,
        [In, Out] NativePreservationVerdict[] outVerdicts,
        nuint maxVerdicts,
        out nuint outCount,
        out byte outOverallPassed);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativePreservationObservation
{
    public uint StructSize;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ImageCodestreamSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExifIfd0NonPtrSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExifExifIfdSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DatetimeOriginal;
    public ushort Orientation;
    public ushort Pad0;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string GpsSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string IccSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string MakernoteNonliveSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string XmpNonprotocolSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string ExtendedXmpSha256;
    public uint HeicPrimaryItemId;
    public uint HeicAuxItemId;
    public uint HeicAuxFromItemId;
    public uint HeicAuxToItemId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string HeicAuxItemSha256;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string HeicAuxType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
    public string VideoMdatSha256;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageOrientationObservationV1
{
    public uint StructSize;
    public uint ApiVersion;
    public NativeImageOrientationStatus Status;
    public int ClockwiseRotationDegrees;
    public NativeImageOrientationReflection Reflection;
    public NativeImageOrientationEvidence RotationEvidence;
    public NativeImageOrientationEvidence ReflectionEvidence;
    public uint Reserved0;
}

internal enum NativeImageOrientationStatus : uint
{
    Unknown = 0,
    Verified = 1
}

internal enum NativeImageOrientationReflection : uint
{
    None = 0,
    Horizontal = 1
}

internal enum NativeImageOrientationEvidence : uint
{
    Unknown = 0,
    Verified = 1
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativePreservationVerdict
{
    public uint Category;
    public uint Status;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Details;
}

[Flags]
internal enum NativeExtractorFault
{
    None = 0,
    DiskFull = 1,
    WriteFail = 2,
    PublishFail = 3,
    ShortRead = 4,
    FlushDiskFull = 5,
    FlushWriteFail = 6,
    TempPublishBarrier = 7,
    PostPublishBarrier = 8,
    CleanupFail = 0x80
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHeicImageInfo
{
    public uint StructSize;
    public uint PrimaryItemId;
    public uint Width;
    public uint Height;
    public uint SourceBitDepth;
    public uint AuxiliaryCount;
    public ushort NclxPrimaries;
    public ushort NclxTransfer;
    public ushort NclxMatrix;
    public byte HasAlpha;
    public byte HasIcc;
    public byte HasNclx;
    public byte IsHdrRelevant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHeicPrimaryDecodeInfo
{
    public uint StructSize;
    public uint PrimaryItemId;
    public uint Width;
    public uint Height;
    public uint SourceBitDepth;
    public uint DecodedSignalBitDepth;
    public uint DecodedStorageBitDepth;
    public ushort NclxPrimaries;
    public ushort NclxTransfer;
    public ushort NclxMatrix;
    public byte HasAlpha;
    public byte HasIcc;
    public byte HasNclx;
    public byte IsHdrRelevant;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeHeicAuxiliaryInfo
{
    public uint StructSize;
    public uint ItemId;
    public uint Width;
    public uint Height;
    public uint SourceBitDepth;
    public uint DecodedSignalBitDepth;
    public uint DecodedStorageBitDepth;
    public ushort NclxPrimaries;
    public ushort NclxTransfer;
    public ushort NclxMatrix;
    public byte HasAlpha;
    public byte HasIcc;
    public byte HasNclx;
    public byte IsHdrRelevant;

    public fixed byte AuxiliaryType[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHeicEncodedImagesInfo
{
    public uint StructSize;
    public uint PrimaryItemId;
    public uint SecondaryItemId;
    public uint PrimaryWidth;
    public uint PrimaryHeight;
    public uint SecondaryWidth;
    public uint SecondaryHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeGainMapMetadataV1
{
    public uint StructSize;
    public uint ApiVersion;
    public int Kind;
    public uint Reserved;
    public fixed double GainMapMin[3];
    public fixed double GainMapMax[3];
    public fixed double Gamma[3];
    public fixed double OffsetSdr[3];
    public fixed double OffsetHdr[3];
    public double HdrCapacityMin;
    public double HdrCapacityMax;
    public int BaseRenditionIsHdr;
    public int Reserved2;
    public double AppleMakerNote33;
    public double AppleMakerNote48;

    public double GetGainMapMin(int channel) { fixed (double* p = GainMapMin) return p[channel]; }
    public double GetGainMapMax(int channel) { fixed (double* p = GainMapMax) return p[channel]; }
    public double GetGamma(int channel) { fixed (double* p = Gamma) return p[channel]; }
    public double GetOffsetSdr(int channel) { fixed (double* p = OffsetSdr) return p[channel]; }
    public double GetOffsetHdr(int channel) { fixed (double* p = OffsetHdr) return p[channel]; }

}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct NativeHdrGainMapConversionRequestV1
{
    public uint StructSize;
    public uint ApiVersion;
    public int SourceContainer;
    public int TargetSemantic;
    public int Quality;
    public int HdrOutputPolicy;
    public uint ItemId;
    public uint AuxiliaryIndex;
    public int Representation;
    public int Ownership;
    public int OwnerArtifactRole;
    public uint Reserved;
    public NativeMediaRange SourceRange;
    public uint ExpectedVolumeSerial;
    public uint ExpectedLinkCount;
    public ulong ExpectedFileIndex;
    public ulong ExpectedFileSize;
    public fixed byte SourceSha256[32];
    public fixed byte PrimarySha256[32];
    public fixed byte GainMapSha256[32];
    public fixed byte StableIdentity[96];
    public fixed byte InspectedStableIdentity[96];
    public fixed byte Semantic[64];
    public fixed byte OwnerIdentity[96];
    public fixed byte InspectedOwnerIdentity[96];
    public fixed byte Relationship[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeHdrGainMapConversionResultV1
{
    public uint StructSize;
    public uint ApiVersion;
    public int TargetSemantic;
    public int ActualContainer;
    public int GainMapOutcome;
    public int MetadataComplete;
    public uint PrimaryWidth;
    public uint PrimaryHeight;
    public uint GainMapWidth;
    public uint GainMapHeight;
    public NativeMediaRange GainMapRange;
    public fixed byte OutputSha256[32];
    public fixed byte GainMapSha256[32];
    public double HdrCapacityMin;
    public double HdrCapacityMax;
    public fixed double GainMapMin[3];
    public fixed double GainMapMax[3];
    public fixed double Gamma[3];
    public fixed double OffsetSdr[3];
    public fixed double OffsetHdr[3];
    public ulong TransactionToken;
    public fixed byte StagingPath[1024];

    public double GetGainMapMin(int channel) { fixed (double* p = GainMapMin) return p[channel]; }
    public double GetGainMapMax(int channel) { fixed (double* p = GainMapMax) return p[channel]; }
    public double GetGamma(int channel) { fixed (double* p = Gamma) return p[channel]; }
    public double GetOffsetSdr(int channel) { fixed (double* p = OffsetSdr) return p[channel]; }
    public double GetOffsetHdr(int channel) { fixed (double* p = OffsetHdr) return p[channel]; }
    public string GetOutputSha256Hex() { fixed (byte* p = OutputSha256) return Convert.ToHexString(new ReadOnlySpan<byte>(p, 32)); }
    public string GetGainMapSha256Hex() { fixed (byte* p = GainMapSha256) return Convert.ToHexString(new ReadOnlySpan<byte>(p, 32)); }
    public string GetStagingPath()
    {
        fixed (byte* p = StagingPath)
        {
            ReadOnlySpan<byte> bytes = new(p, 1024);
            int length = bytes.IndexOf((byte)0);
            if (length <= 0) throw new InvalidDataException("Native HDR/GainMap staging path is missing or truncated.");
            return Encoding.UTF8.GetString(bytes[..length]);
        }
    }
}

internal sealed class NativeHdrGainMapConversionTransaction : IDisposable
{
    private NativeContext? _context;
    private bool _completed;

    internal NativeHdrGainMapConversionTransaction(
        NativeContext context,
        NativeHdrGainMapConversionResultV1 result,
        string stagingPath)
    {
        _context = context;
        Result = result;
        StagingPath = stagingPath;
    }

    internal NativeHdrGainMapConversionResultV1 Result { get; }
    internal string StagingPath { get; }

    internal (SourceMediaFacts Facts, NativeGainMapMetadataV1 Metadata, PreservationObservation Preservation) Inspect()
    {
        NativeContext context = _context ?? throw new ObjectDisposedException(nameof(NativeHdrGainMapConversionTransaction));
        if (_completed) throw new InvalidOperationException("HDR/GainMap conversion transaction is already finalized.");
        var facts = new NativeSourceMediaFacts
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeSourceMediaFacts>())
        };
        var metadata = new NativeGainMapMetadataV1
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeGainMapMetadataV1>()),
            ApiVersion = 1
        };
        var preservation = new NativePreservationObservation
        {
            StructSize = checked((uint)Marshal.SizeOf<NativePreservationObservation>())
        };
        using NativeContextLease lease = context.AcquireOperationLease();
        NativeResult result = NativeMethods.InspectHdrGainMapStageV1(
            lease.Handle, Result.TransactionToken, ref facts, ref metadata, ref preservation);
        context.ThrowIfFailed(result);
        return (
            NativeMediaService.MapFromNativeFacts(facts),
            metadata,
            PreservationObservation.FromNative(in preservation));
    }

    internal void Commit()
    {
        NativeContext context = _context ?? throw new ObjectDisposedException(nameof(NativeHdrGainMapConversionTransaction));
        if (_completed) throw new InvalidOperationException("HDR/GainMap conversion transaction is already finalized.");
        using NativeContextLease lease = context.AcquireOperationLease();
        NativeResult result = NativeMethods.CommitHdrGainMapV1(lease.Handle, Result.TransactionToken);
        context.ThrowIfFailed(result);
        _completed = true;
    }

    internal void Abort()
    {
        NativeContext context = _context ?? throw new ObjectDisposedException(nameof(NativeHdrGainMapConversionTransaction));
        if (_completed) return;
        using NativeContextLease lease = context.AcquireOperationLease();
        NativeResult result = NativeMethods.AbortHdrGainMapV1(lease.Handle, Result.TransactionToken);
        context.ThrowIfFailed(result);
        _completed = true;
    }

    public void Dispose()
    {
        NativeContext? context = _context;
        if (context is null) return;
        try
        {
            if (!_completed) Abort();
        }
        catch
        {
            // Context destruction releases any still-registered stage through
            // its Native-owned output handle; explicit callers use Abort() to
            // surface cleanup failure before reaching this safety path.
        }
        finally
        {
            _context = null;
            context.Dispose();
        }
    }
}

using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// R2 evidence adapter. It turns project-owned, before/after preservation
/// observations into converter truth without exposing Native observation types
/// in the public conversion contract.
/// </summary>
internal sealed record ImagePostValidation(
    ConversionComponentOutcome Metadata,
    ConversionComponentOutcome Orientation,
    ConversionComponentOutcome ColorIcc,
    PreservationOutcome PreservationOutcome)
{
    internal string DifferenceDetails { get; init; } = string.Empty;
}

internal sealed record ImageHdrGainMapPostValidation(
    ImagePostValidation Preservation,
    string OutputSha256,
    string GainMapSha256,
    SourceMediaFacts OutputFacts)
{
    internal ImageMetadataMappingEvidence? MetadataMapping { get; init; }
    internal ImageHdrConversionEvidence? HdrConversionEvidence { get; init; }
    internal WindowsFileIdentity? CreatingOutputIdentity { get; init; }
}

internal static class ImagePostValidator
{
    internal static async Task<ImagePostValidation> VerifyExactPassthroughAsync(
        string sourcePath,
        string outputPath,
        ImageContainer container,
        CancellationToken cancellationToken)
    {
        string sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        string outputHash = await ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sourceHash, outputHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Same-container passthrough was not byte-identical.");
        }

        return await VerifyPreservationAsync(sourcePath, outputPath, container, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the preservation-bearing components of a JPEG DCT transform.
    /// Unlike passthrough, transformed codestream bytes are deliberately not
    /// expected to be identical.
    /// </summary>
    internal static Task<ImagePostValidation> VerifyLosslessJpegTransformAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken) =>
        VerifyPreservationAsync(sourcePath, outputPath, ImageContainer.Jpeg, cancellationToken);

    /// <summary>
    /// Compares observable preservation-bearing components across a lossy
    /// container conversion.  A cross-container encode is never represented
    /// as a fully preserved artifact even when every applicable observer field
    /// happens to compare equal.
    /// </summary>
    internal static async Task<ImagePostValidation> VerifyLossyReencodeAsync(
        string sourcePath,
        ImageContainer sourceContainer,
        string outputPath,
        ImageContainer outputContainer,
        CancellationToken cancellationToken)
    {
        ImagePostValidation observed = await VerifyPreservationAsync(
            sourcePath, outputPath, sourceContainer, outputContainer, cancellationToken).ConfigureAwait(false);
        return observed with { PreservationOutcome = PreservationOutcome.PartiallyPreserved };
    }

    internal static async Task<ImageHdrGainMapPostValidation> VerifyHeicToJpegUltraHdrAsync(
        string sourcePath,
        NativeHdrGainMapConversionTransaction transaction,
        NativeHdrGainMapConversionResultV1 nativeResult,
        CancellationToken cancellationToken) =>
        await VerifyHdrGainMapAsync(sourcePath, transaction, nativeResult,
            ImageContainer.Heic, ImageContainer.Jpeg, ImageHdrGainMapTargetSemantic.JpegIsoGainMap,
            expectedRelationship: null, cancellationToken).ConfigureAwait(false);

    internal static async Task<ImageHdrGainMapPostValidation> VerifyJpegToHeicGainMapAsync(
        string sourcePath,
        NativeHdrGainMapConversionTransaction transaction,
        NativeHdrGainMapConversionResultV1 nativeResult,
        CancellationToken cancellationToken) =>
        await VerifyHdrGainMapAsync(sourcePath, transaction, nativeResult,
            ImageContainer.Jpeg, ImageContainer.Heic, ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary,
            "urn:com:photo:aux:hdrgainmap", cancellationToken).ConfigureAwait(false);

    private static async Task<ImageHdrGainMapPostValidation> VerifyHdrGainMapAsync(
        string sourcePath,
        NativeHdrGainMapConversionTransaction transaction,
        NativeHdrGainMapConversionResultV1 nativeResult,
        ImageContainer sourceContainer,
        ImageContainer targetContainer,
        ImageHdrGainMapTargetSemantic targetSemantic,
        string? expectedRelationship,
        CancellationToken cancellationToken)
    {
        if (nativeResult.TargetSemantic != (int)targetSemantic ||
            nativeResult.ActualContainer != (int)targetContainer ||
            nativeResult.GainMapOutcome != 1 || nativeResult.MetadataComplete != 1 ||
            nativeResult.PrimaryWidth == 0 || nativeResult.PrimaryHeight == 0 ||
            nativeResult.GainMapWidth == 0 || nativeResult.GainMapHeight == 0 ||
            nativeResult.GainMapRange.Length == 0)
            throw new InvalidDataException("Native did not report the requested complete HDR/GainMap target semantic.");

        (SourceMediaFacts outputFacts, NativeGainMapMetadataV1 metadata,
            PreservationObservation afterPreservation) = transaction.Inspect();
        string outputPath = transaction.StagingPath;
        GainMapFacts gainMap = outputFacts.GainMap
            ?? throw new InvalidDataException("Native post-inspection did not find the converted JPEG GainMap.");
        if (outputFacts.PrimaryImage.Container != targetContainer ||
            !gainMap.IsPresent || gainMap.Container != targetContainer ||
            gainMap.ByteOffset != checked((long)nativeResult.GainMapRange.Offset) ||
            gainMap.ByteLength != checked((long)nativeResult.GainMapRange.Length) ||
            gainMap.AuxiliaryIndex != 0 || outputFacts.AuxiliaryItems.Count != 1 ||
            (expectedRelationship is not null &&
                !string.Equals(gainMap.Relationship, expectedRelationship, StringComparison.Ordinal)))
            throw new InvalidDataException($"Converted {targetContainer} facts do not match the Native primary/GainMap target ranges " +
                $"(container={outputFacts.PrimaryImage.Container}, primarySize={outputFacts.PrimaryImage.Width}x{outputFacts.PrimaryImage.Height}, " +
                $"expectedPrimarySize={nativeResult.PrimaryWidth}x{nativeResult.PrimaryHeight}, mapContainer={gainMap.Container}, " +
                $"relationship={gainMap.Relationship}, expectedRelationship={expectedRelationship}, mapOffset={gainMap.ByteOffset}, " +
                $"expectedOffset={nativeResult.GainMapRange.Offset}, mapLength={gainMap.ByteLength}, " +
                $"expectedLength={nativeResult.GainMapRange.Length}, mapIndex={gainMap.AuxiliaryIndex}, " +
                $"auxiliaryCount={outputFacts.AuxiliaryItems.Count}).");

        AuxiliaryMediaFacts auxiliary = outputFacts.AuxiliaryItems[checked((int)gainMap.AuxiliaryIndex)];
        if (!auxiliary.IsPresent || auxiliary.Container != targetContainer ||
            auxiliary.Representation != AuxiliaryRepresentation.Embedded ||
            auxiliary.Ownership != AuxiliaryOwnership.Primary || auxiliary.ItemId != gainMap.ItemId ||
            !string.Equals(auxiliary.Semantic, "GainMap", StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Relationship, gainMap.Relationship, StringComparison.Ordinal) ||
            (expectedRelationship is not null &&
                !string.Equals(auxiliary.Relationship, expectedRelationship, StringComparison.Ordinal)))
            throw new InvalidDataException($"Converted {targetContainer} auxiliary graph is not one embedded Primary-owned GainMap.");

        string outputSha = await ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
        string mapSha = await ComputeRangeSha256Async(outputPath, gainMap.ByteOffset, gainMap.ByteLength, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(outputSha, nativeResult.GetOutputSha256Hex(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(mapSha, nativeResult.GetGainMapSha256Hex(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(auxiliary.Sha256, mapSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Converted JPEG or GainMap bytes do not match the Native result hashes.");

        if (metadata.Kind != 2 || metadata.BaseRenditionIsHdr is not (0 or 1) ||
            metadata.HdrCapacityMin != nativeResult.HdrCapacityMin ||
            metadata.HdrCapacityMax != nativeResult.HdrCapacityMax)
            throw new InvalidDataException($"Converted {targetContainer} GainMap XMP is incomplete or disagrees with Native mapping metadata.");
        for (int channel = 0; channel < 3; channel++)
        {
            if (metadata.GetGainMapMin(channel) != nativeResult.GetGainMapMin(channel) ||
                metadata.GetGainMapMax(channel) != nativeResult.GetGainMapMax(channel) ||
                metadata.GetGamma(channel) != nativeResult.GetGamma(channel) ||
                metadata.GetOffsetSdr(channel) != nativeResult.GetOffsetSdr(channel) ||
                metadata.GetOffsetHdr(channel) != nativeResult.GetOffsetHdr(channel))
                throw new InvalidDataException($"Converted {targetContainer} GainMap values disagree with the Native result mapping.");
        }

        PreservationObservation beforePreservation = await NativeMediaService
            .CapturePreservationObservationAsync(sourcePath, SourceProtocol.Unknown,
                sourceContainer, cancellationToken).ConfigureAwait(false);
        if(sourceContainer==ImageContainer.Jpeg&&targetContainer==ImageContainer.Heic)
        {
            var completeProof=transaction.GetCompleteHdrImageProof();
            string sourceSha=await ComputeSha256Async(sourcePath,cancellationToken).ConfigureAwait(false);
            using var staged=new FileStream(outputPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var actualIdentity=WindowsFileIdentity.Capture(staged.SafeFileHandle);
            var actual=new NativeMediaService.NativeRegisteredOutputEvidence(actualIdentity,staged.Length,outputSha);
            var complete=NativeHdrImageProofValidator.Validate(completeProof,sourceSha,(ulong)new FileInfo(sourcePath).Length,
                checked((uint)beforePreservation.Orientation),actual);
            if(complete.OutputWidth!=nativeResult.PrimaryWidth||complete.OutputHeight!=nativeResult.PrimaryHeight||
                outputFacts.PrimaryImage.Width!=complete.OutputWidth||outputFacts.PrimaryImage.Height!=complete.OutputHeight)
                throw new InvalidDataException("Complete HDR image proof disagrees with the actual staged image geometry.");
            return new ImageHdrGainMapPostValidation(new ImagePostValidation(ConversionComponentOutcome.Preserved,
                ConversionComponentOutcome.Preserved,complete.HasIcc?ConversionComponentOutcome.Preserved:ConversionComponentOutcome.NotApplicable,
                PreservationOutcome.PartiallyPreserved),outputSha,mapSha,outputFacts)
                {HdrConversionEvidence=complete,CreatingOutputIdentity=actualIdentity};
        }
        bool mappedOrdinaryXmp=false;
        var mappingProof=transaction.GetOrdinaryXmpProof();
        if(mappingProof is { } proof)
        {
            string currentSourceHash=await ComputeSha256Async(sourcePath,cancellationToken).ConfigureAwait(false);
            if(!string.Equals(proof.SourceSha256,currentSourceHash,StringComparison.OrdinalIgnoreCase)||
                !string.Equals(proof.OutputSha256,outputSha,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HDR ordinary XMP proof does not bind the actual source and exact owned stage.");
            mappedOrdinaryXmp=true;
        }
        ImagePostValidation preservation = ComparePreservationObservations(beforePreservation, afterPreservation,mappedOrdinaryXmp)
            with { PreservationOutcome = PreservationOutcome.PartiallyPreserved };
        if (preservation.ColorIcc is not (ConversionComponentOutcome.Preserved or ConversionComponentOutcome.NotApplicable))
            throw new InvalidDataException($"Converted {targetContainer} did not preserve the source ICC profile with traceable before/after evidence.");
        return new ImageHdrGainMapPostValidation(preservation, outputSha, mapSha, outputFacts)
            { MetadataMapping = mappingProof is { } mapped ? new(mapped.SourceSha256,mapped.OutputSha256) { MappingKind = mapped.Kind } : null };
    }

    private static Task<ImagePostValidation> VerifyPreservationAsync(
        string sourcePath,
        string outputPath,
        ImageContainer container,
        CancellationToken cancellationToken) =>
        VerifyPreservationAsync(sourcePath, outputPath, container, container, cancellationToken);

    private static async Task<ImagePostValidation> VerifyPreservationAsync(
        string sourcePath,
        string outputPath,
        ImageContainer sourceContainer,
        ImageContainer outputContainer,
        CancellationToken cancellationToken)
    {
        PreservationObservation before = await NativeMediaService
            .CapturePreservationObservationAsync(sourcePath, SourceProtocol.Unknown, sourceContainer, cancellationToken)
            .ConfigureAwait(false);
        PreservationObservation after = await NativeMediaService
            .CapturePreservationObservationAsync(outputPath, SourceProtocol.Unknown, outputContainer, cancellationToken)
            .ConfigureAwait(false);

        return ComparePreservationObservations(before, after);
    }

    private static ImagePostValidation ComparePreservationObservations(
        PreservationObservation before,
        PreservationObservation after,bool mappedOrdinaryXmp=false)
    {
        ConversionComponentOutcome metadata = MetadataOutcome(before, after,mappedOrdinaryXmp);
        ConversionComponentOutcome orientation = OrientationOutcome(before, after);
        ConversionComponentOutcome icc = IccOutcome(before, after);
        PreservationOutcome aggregate = metadata is ConversionComponentOutcome.NotEvaluated or ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
                                       orientation is ConversionComponentOutcome.NotEvaluated or ConversionComponentOutcome.Lost ||
                                       icc is ConversionComponentOutcome.NotEvaluated or ConversionComponentOutcome.Lost
            ? PreservationOutcome.PartiallyPreserved
            : PreservationOutcome.Preserved;

        var differences = new List<string>();
        if (before.HasExif != after.HasExif) differences.Add("ExifPresence");
        if (before.HasXmp != after.HasXmp) differences.Add("XmpPresence");
        if (before.HasExtendedXmp != after.HasExtendedXmp) differences.Add("ExtendedXmpPresence");
        if (before.HasMakerNote != after.HasMakerNote) differences.Add("MakerNotePresence");
        if (before.HasGps != after.HasGps) differences.Add("GpsPresence");
        if (before.ExifIfd0NonPtrSha256 != after.ExifIfd0NonPtrSha256) differences.Add("ExifIfd0");
        if (before.ExifExifIfdSha256 != after.ExifExifIfdSha256) differences.Add("ExifIfd");
        if (before.DateTimeOriginal != after.DateTimeOriginal) differences.Add("DateTimeOriginal");
        if (before.GpsSha256 != after.GpsSha256) differences.Add("Gps");
        if (before.MakernoteNonliveSha256 != after.MakernoteNonliveSha256) differences.Add("MakerNote");
        if (before.XmpNonprotocolSha256 != after.XmpNonprotocolSha256) differences.Add("OrdinaryXmp");
        if (before.ExtendedXmpSha256 != after.ExtendedXmpSha256) differences.Add("ExtendedXmp");
        return new ImagePostValidation(metadata, orientation, icc, aggregate)
            { DifferenceDetails = string.Join(",",differences) };
    }

    private static ConversionComponentOutcome MetadataOutcome(PreservationObservation before, PreservationObservation after,bool mappedOrdinaryXmp=false)
    {
        bool present = before.HasExif || before.HasXmp || before.HasExtendedXmp || before.HasMakerNote || before.HasGps ||
                       !string.IsNullOrEmpty(before.DateTimeOriginal);
        if (!present) return ConversionComponentOutcome.NotApplicable;
        if (before.ExifParseError || before.XmpMalformed || before.MakerNoteMalformed ||
            after.ExifParseError || after.XmpMalformed || after.MakerNoteMalformed)
            return ConversionComponentOutcome.NotEvaluated;

        bool equivalent = before.HasExif == after.HasExif &&
                          (mappedOrdinaryXmp || before.HasXmp == after.HasXmp) &&
                          before.HasExtendedXmp == after.HasExtendedXmp &&
                          before.HasMakerNote == after.HasMakerNote &&
                          before.HasGps == after.HasGps &&
                          string.Equals(before.ExifIfd0NonPtrSha256, after.ExifIfd0NonPtrSha256, StringComparison.Ordinal) &&
                          string.Equals(before.ExifExifIfdSha256, after.ExifExifIfdSha256, StringComparison.Ordinal) &&
                          string.Equals(before.DateTimeOriginal, after.DateTimeOriginal, StringComparison.Ordinal) &&
                          string.Equals(before.GpsSha256, after.GpsSha256, StringComparison.Ordinal) &&
                          string.Equals(before.MakernoteNonliveSha256, after.MakernoteNonliveSha256, StringComparison.Ordinal) &&
                          (mappedOrdinaryXmp || string.Equals(before.XmpNonprotocolSha256, after.XmpNonprotocolSha256, StringComparison.Ordinal)) &&
                          string.Equals(before.ExtendedXmpSha256, after.ExtendedXmpSha256, StringComparison.Ordinal);
        if (equivalent) return ConversionComponentOutcome.Preserved;

        bool anyTrackedMetadataRemains = after.HasExif || after.HasXmp || after.HasExtendedXmp || after.HasMakerNote ||
                                         after.HasGps || !string.IsNullOrEmpty(after.DateTimeOriginal);
        return anyTrackedMetadataRemains
            ? ConversionComponentOutcome.Degraded
            : ConversionComponentOutcome.Lost;
    }

    internal static ConversionComponentOutcome OrientationOutcome(PreservationObservation before, PreservationObservation after)
    {
        if (before.ExifParseError || after.ExifParseError) return ConversionComponentOutcome.NotEvaluated;
        if (before.Orientation == 0)
            return after.Orientation == 0
                ? ConversionComponentOutcome.NotApplicable
                : ConversionComponentOutcome.Lost;
        return before.Orientation == after.Orientation
            ? ConversionComponentOutcome.Preserved
            : ConversionComponentOutcome.Lost;
    }

    private static ConversionComponentOutcome IccOutcome(PreservationObservation before, PreservationObservation after)
    {
        if (!before.HasIcc) return ConversionComponentOutcome.NotApplicable;
        if (before.IccParseError || after.IccParseError) return ConversionComponentOutcome.NotEvaluated;
        return after.HasIcc && string.Equals(before.IccSha256, after.IccSha256, StringComparison.Ordinal)
            ? ConversionComponentOutcome.Preserved
            : ConversionComponentOutcome.Lost;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ComputeRangeSha256Async(string path, long offset, long length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length <= 0) throw new InvalidDataException("GainMap byte range is invalid.");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length || length > stream.Length - offset)
            throw new InvalidDataException("GainMap byte range exceeds the converted JPEG file.");
        stream.Position = offset;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("GainMap JPEG ended before its inspected byte range.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

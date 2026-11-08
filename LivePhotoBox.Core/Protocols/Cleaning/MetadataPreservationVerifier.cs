using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Verifies that non-protocol metadata, media payloads, and auxiliary streams
/// are preserved intact after source protocol cleaning.
/// Uses native authoritative observations and SHA-256 fingerprint comparison.
/// All binary media parsing is performed exclusively by LivePhotoBox.Native.
/// </summary>
public static class MetadataPreservationVerifier
{
    internal sealed record BaselineAttempt(PreservationBaseline? Baseline,Exception? Failure);
    public static async Task<PreservationBaseline> CaptureBaselineAsync(ExtractedMediaBundle bundle,CancellationToken cancellationToken=default)
    {
        var attempt=await TryCaptureBaselineAsync(bundle,cancellationToken).ConfigureAwait(false);
        if(attempt.Failure is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Baseline!;
    }

    internal static async Task<BaselineAttempt> TryCaptureBaselineAsync(
        ExtractedMediaBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(bundle.PrimaryImage);

        if(cancellationToken.IsCancellationRequested)return new(null,new OperationCanceledException(cancellationToken));

        var imageObs = await NativeMediaService.TryCapturePreservationObservationAsync(
            bundle.PrimaryImage.Path,
            bundle.SourceFacts.Protocol,
            bundle.PrimaryImage.ImageContainer,
            cancellationToken).ConfigureAwait(false);

        if(imageObs.Failure is { } imageFailure)return new(null,imageFailure);
        PreservationObservation? videoObs = null;
        long? videoLength = null;
        string? videoSha = null;
        if (bundle.MotionVideo != null && File.Exists(bundle.MotionVideo.Path))
        {
            videoLength = bundle.MotionVideo.ByteLength > 0
                ? bundle.MotionVideo.ByteLength
                : new FileInfo(bundle.MotionVideo.Path).Length;
            videoSha = bundle.MotionVideo.Sha256;

            var videoAttempt = await NativeMediaService.TryCapturePreservationObservationAsync(
                bundle.MotionVideo.Path,
                bundle.SourceFacts.Protocol,
                ImageContainer.Unknown,
                cancellationToken).ConfigureAwait(false);
            if(videoAttempt.Failure is { } videoFailure)return new(null,videoFailure);
            videoObs=videoAttempt.Observation;
        }

        long primaryImageLength = bundle.PrimaryImage.ByteLength > 0
            ? bundle.PrimaryImage.ByteLength
            : (File.Exists(bundle.PrimaryImage.Path) ? new FileInfo(bundle.PrimaryImage.Path).Length : 0);

        return new(new PreservationBaseline
        {
            PrimaryImagePath = bundle.PrimaryImage.Path,
            PrimaryImageLength = primaryImageLength,
            PrimaryImageSha256 = bundle.PrimaryImage.Sha256 ?? "",
            PrimaryImageContainer = bundle.PrimaryImage.ImageContainer,
            Protocol = bundle.SourceFacts.Protocol,
            ImageObservation = imageObs.Observation!,
            MotionVideoLength = videoLength,
            MotionVideoSha256 = videoSha,
            MotionVideoContainer = bundle.MotionVideo?.VideoContainer ?? VideoContainer.Unknown,
            MotionVideoPath = bundle.MotionVideo?.Path,
            VideoObservation = videoObs,
            GainMapSha256 = bundle.GainMap?.Sha256,
            GainMapExpected = bundle.GainMap != null,
            PreservationCarriers = bundle.PreservationCarriers,
            AuxiliaryMedia = bundle.AuxiliaryMedia.Select(item => item with { }).ToArray()
        },null);
    }

    public static async Task<PreservationReport> VerifyAsync(
        ExtractedMediaBundle preBundle,
        string stagedImagePath,
        string? stagedVideoPath,
        string? stagedGainMapPath = null,
        CancellationToken cancellationToken = default)
    {
        var baseline = await CaptureBaselineAsync(preBundle, cancellationToken).ConfigureAwait(false);
        return await VerifyAgainstBaselineAsync(
            baseline, 
            stagedImagePath, 
            stagedVideoPath, 
            stagedGainMapPath ?? preBundle.GainMap?.Path, 
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PreservationReport> VerifyAgainstBaselineAsync(
        PreservationBaseline baseline,
        string stagedImagePath,
        string? stagedVideoPath,
        string? stagedGainMapPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(stagedImagePath);

        if(cancellationToken.IsCancellationRequested)return ObservationFailure("Preservation verification cancelled.");

        if (!File.Exists(stagedImagePath) || new FileInfo(stagedImagePath).Length == 0)
        {
            return CreateReport(new List<PreservationReportItem>
            {
                new()
                {
                    Name = "MediaPayload",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Cleaned image is missing or empty."
                }
            }, allPassed: false);
        }

        var imageAttempt=await NativeMediaService.TryCapturePreservationObservationAsync(stagedImagePath,baseline.Protocol,
            baseline.PrimaryImageContainer,cancellationToken).ConfigureAwait(false);
        if(imageAttempt.Failure is { } imageFailure)return ObservationFailure("Failed to capture post-clean observation: "+imageFailure.Message);
        PreservationObservation postImage=imageAttempt.Observation!;
        PreservationObservation? preVideo=null,postVideo=null;
        if(baseline.MotionVideoLength!=null && stagedVideoPath!=null)
        {
            preVideo=baseline.VideoObservation;
            if(File.Exists(stagedVideoPath)&&new FileInfo(stagedVideoPath).Length>0)
            {
                var videoAttempt=await NativeMediaService.TryCapturePreservationObservationAsync(stagedVideoPath,baseline.Protocol,
                    ImageContainer.Unknown,cancellationToken).ConfigureAwait(false);
                if(videoAttempt.Failure is { } videoFailure)return ObservationFailure("Failed to capture post-clean video: "+videoFailure.Message);
                postVideo=videoAttempt.Observation;
            }
        }

        DetachedGainMapVerificationState detachedGainMapState;
        if (!baseline.GainMapExpected)
        {
            detachedGainMapState = DetachedGainMapVerificationState.NotExpected;
        }
        else if (!string.IsNullOrWhiteSpace(baseline.GainMapSha256) &&
                 !string.IsNullOrEmpty(stagedGainMapPath) &&
                 await VerifyGainMapIdentityAsync(
                     stagedGainMapPath, baseline.GainMapSha256, cancellationToken).ConfigureAwait(false))
        {
            detachedGainMapState = DetachedGainMapVerificationState.ExpectedAndVerified;
        }
        else
        {
            detachedGainMapState = DetachedGainMapVerificationState.ExpectedButMissingOrChanged;
        }
        var verdicts = NativeMediaService.VerifyPreservation(
            baseline.ImageObservation,
            postImage,
            preVideo,
            postVideo,
            baseline.Protocol,
            detachedGainMapState,
            out bool allPassed,
            cancellationToken,strict:false);

        var items = new List<PreservationReportItem>(verdicts.Count);
        foreach (var v in verdicts)
        {
            items.Add(new PreservationReportItem
            {
                Name = MapCategoryToName(v.Category),
                Status = MapNativeStatus(v.Status),
                Details = v.Details
            });
        }

        SourceMediaFacts? postCarrierFacts = null;
        if (baseline.PreservationCarriers.Any(carrier => carrier.Kind == PreservationCarrierKind.SamsungSef))
        {
            try
            {
                postCarrierFacts = await new SourceInspector().InspectAsync(
                    stagedImagePath, null, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The carrier-specific loop below records the typed failure;
                // the generic Native preservation verdicts remain intact.
            }
        }

        bool carriersPassed = true;
        foreach (PreservationCarrier carrier in baseline.PreservationCarriers)
        {
            if(LivePhotoBox.Media.OrdinaryMetadataPreservation.IsRecordCarrier(carrier))
            {
                if (carrier.Representation != AuxiliaryRepresentation.Materialized || carrier.MaterializedArtifact == null)
                    continue;
                try
                {
                    await LivePhotoBox.Media.OrdinaryMetadataPreservation.VerifyAsync(carrier,cancellationToken).ConfigureAwait(false);
                    items.Add(new PreservationReportItem{Name=$"Carrier:{carrier.StableIdentity}",Status=PreservationCheckStatus.VerifiedPreserved,
                        Details="Complete materialized ordinary record bytes match the original source hash and Native-created artifact identity."});
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
                catch(Exception failure)
                {
                    carriersPassed=false;items.Add(new PreservationReportItem{Name=$"Carrier:{carrier.StableIdentity}",Status=PreservationCheckStatus.Failed,Details=failure.Message});
                }
                continue;
            }
            if (carrier.Kind == PreservationCarrierKind.SamsungSef)
            {
                // A live-only SEF directory is needed as extraction/cleanup
                // transport authority, but has no ordinary metadata to retain.
                // Do not require its recreation after all authorized live
                // entries have been removed. Ordinary entries still follow
                // the full post-clean range verification below.
                if (carrier.StableIdentity == "samsung:sef:index-trailer" &&
                    carrier.Semantic == "SamsungSEFIndexTrailer" &&
                    !baseline.PreservationCarriers.Any(other =>
                        other.Kind == PreservationCarrierKind.SamsungSef &&
                        other.SourceIndex == carrier.SourceIndex &&
                        other.StableIdentity != carrier.StableIdentity) &&
                    postCarrierFacts?.Protocol == SourceProtocol.NonLive &&
                    !postCarrierFacts.PreservationCarriers.Any(other => other.Kind == PreservationCarrierKind.SamsungSef))
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = $"Carrier:{carrier.StableIdentity}",
                        Status = PreservationCheckStatus.NotApplicable,
                        Details = "The SEF directory indexed only authorized source-live entries; no ordinary SEF metadata required retention."
                    });
                    continue;
                }
                var matchingPostCarriers = postCarrierFacts?.PreservationCarriers
                    .Where(candidate =>
                        string.Equals(candidate.StableIdentity, carrier.StableIdentity, StringComparison.Ordinal) &&
                        candidate.Kind == carrier.Kind &&
                        candidate.ArtifactRole == carrier.ArtifactRole &&
                        string.Equals(candidate.Semantic, carrier.Semantic, StringComparison.Ordinal) &&
                        string.Equals(candidate.OwnerIdentity, carrier.OwnerIdentity, StringComparison.Ordinal) &&
                        string.Equals(candidate.Relationship, carrier.Relationship, StringComparison.Ordinal))
                    .ToList() ?? new List<PreservationCarrier>();
                PreservationCarrier? postCarrier = matchingPostCarriers.Count == 1
                    ? matchingPostCarriers[0]
                    : null;

                string? carrierPath = carrier.SourceIndex == 0 ? stagedImagePath : stagedVideoPath;
                bool structuredCarrierPresent = postCarrier != null && carrierPath != null &&
                    await VerifySourceRangeIdentityAsync(
                        carrierPath, postCarrier.SourceOffset, postCarrier.SourceLength, postCarrier.SourceSha256,
                        cancellationToken).ConfigureAwait(false);
                if (!structuredCarrierPresent)
                {
                    carriersPassed = false;
                    items.Add(new PreservationReportItem
                    {
                        Name = $"Carrier:{carrier.StableIdentity}",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Structured Samsung SEF carrier was not found with a verifiable post-clean source range."
                    });
                }
                else
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = $"Carrier:{carrier.StableIdentity}",
                        Status = PreservationCheckStatus.VerifiedPreserved,
                        Details = $"Structured Samsung SEF carrier preserved; source SHA-256={carrier.SourceSha256}, post-clean range SHA-256={postCarrier!.SourceSha256}."
                    });
                }
                continue;
            }

            string? sourceCarrierPath = carrier.SourceIndex == 0
                ? stagedImagePath
                : stagedVideoPath;
            if (sourceCarrierPath is null ||
                !await VerifySourceRangeIdentityAsync(
                    sourceCarrierPath, carrier.SourceOffset, carrier.SourceLength, carrier.SourceSha256,
                    cancellationToken).ConfigureAwait(false))
            {
                carriersPassed = false;
                items.Add(new PreservationReportItem
                {
                    Name = $"Carrier:{carrier.StableIdentity}",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Preservation carrier range/hash could not be verified after cleaning: offset={carrier.SourceOffset}, length={carrier.SourceLength}."
                });
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = $"Carrier:{carrier.StableIdentity}",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"Preservation carrier SHA-256 verified for source range [{carrier.SourceOffset},{carrier.SourceOffset + carrier.SourceLength})."
                });
            }
        }

        bool auxiliaryMediaPassed = await VerifyMiCameraAuxiliaryPreservationAsync(
            baseline.AuxiliaryMedia, stagedImagePath, items, cancellationToken).ConfigureAwait(false);

        return CreateReport(items, allPassed && carriersPassed && auxiliaryMediaPassed);
    }

    private static async Task<bool> VerifyMiCameraAuxiliaryPreservationAsync(
        IReadOnlyList<AuxiliaryMediaDescriptor> baselineAuxiliaryMedia,
        string stagedImagePath,
        List<PreservationReportItem> reportItems,
        CancellationToken cancellationToken)
    {
        static bool IsMiCameraDescriptor(AuxiliaryMediaDescriptor descriptor) =>
            descriptor.StableIdentity is "micamera:xmpmeta:subimage" or "micamera:xmpmeta:lenswatermark" ||
            descriptor.Semantic is "MiCameraSubimage" or "MiCameraLensWatermark" ||
            descriptor.Relationship is "micamera-xmpmeta-subimage" or "micamera-xmpmeta-lenswatermark";

        static bool HasExpectedMiCameraIdentity(AuxiliaryMediaDescriptor descriptor) =>
            (descriptor.StableIdentity == "micamera:xmpmeta:subimage" &&
             descriptor.Semantic == "MiCameraSubimage" &&
             descriptor.Relationship == "micamera-xmpmeta-subimage") ||
            (descriptor.StableIdentity == "micamera:xmpmeta:lenswatermark" &&
             descriptor.Semantic == "MiCameraLensWatermark" &&
             descriptor.Relationship == "micamera-xmpmeta-lenswatermark");

        AuxiliaryMediaDescriptor[] micameraItems = baselineAuxiliaryMedia
            .Where(IsMiCameraDescriptor)
            .ToArray();
        if (micameraItems.Length == 0)
        {
            return true;
        }

        SourceMediaFacts? postCleanFacts = null;
        string? inspectionFailure = null;
        try
        {
            postCleanFacts = await new SourceInspector().InspectAsync(
                stagedImagePath, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            inspectionFailure = $"Fresh post-clean inspection failed: {exception.Message}";
        }

        bool allPassed = true;
        bool baselineShapeValid = micameraItems.Length == 2 &&
            micameraItems.Count(HasExpectedMiCameraIdentity) == 2 &&
            micameraItems.Select(item => item.Semantic).Distinct(StringComparer.Ordinal).Count() == 2 &&
            micameraItems.All(item =>
                item.ArtifactRole == MediaArtifactKind.AuxiliaryItem &&
                item.OwnerIdentity == "primary:0" &&
                item.SourceIndex == 0 &&
                item.SourceLength > 0 &&
                !string.IsNullOrWhiteSpace(item.SourceSha256) &&
                item.ImageContainer == ImageContainer.Jpeg &&
                item.Codec == (item.StableIdentity == "micamera:xmpmeta:subimage"
                    ? AuxiliaryCodec.Jpeg
                    : AuxiliaryCodec.Copy) &&
                item.Representation == AuxiliaryRepresentation.Embedded &&
                item.Ownership == AuxiliaryOwnership.Primary &&
                item.MaterializedArtifact is null);

        foreach (AuxiliaryMediaDescriptor descriptor in micameraItems)
        {
            string itemName = $"Auxiliary:{descriptor.Semantic}";
            var matchingItems = postCleanFacts?.AuxiliaryItems
                .Where(candidate =>
                    candidate.IsPresent &&
                    string.Equals(candidate.StableIdentity, descriptor.StableIdentity, StringComparison.Ordinal) &&
                    string.Equals(candidate.Semantic, descriptor.Semantic, StringComparison.Ordinal) &&
                    string.Equals(candidate.Relationship, descriptor.Relationship, StringComparison.Ordinal) &&
                    string.Equals(candidate.OwnerIdentity, descriptor.OwnerIdentity, StringComparison.Ordinal) &&
                    candidate.SourceIndex == descriptor.SourceIndex &&
                    candidate.ByteLength == descriptor.SourceLength &&
                    string.Equals(candidate.Sha256, descriptor.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Container == descriptor.ImageContainer &&
                    candidate.Codec == descriptor.Codec &&
                    candidate.Representation == descriptor.Representation &&
                    candidate.Ownership == descriptor.Ownership)
                .ToArray() ?? [];

            bool descriptorValid = baselineShapeValid && HasExpectedMiCameraIdentity(descriptor);
            AuxiliaryMediaFacts? rebound = matchingItems.Length == 1 ? matchingItems[0] : null;
            bool rangeAndHashVerified = descriptorValid && rebound is not null &&
                await VerifySourceRangeIdentityAsync(
                    stagedImagePath,
                    rebound.ByteOffset,
                    descriptor.SourceLength,
                    descriptor.SourceSha256,
                    cancellationToken).ConfigureAwait(false);

            if (!rangeAndHashVerified)
            {
                allPassed = false;
                string details = !descriptorValid
                    ? "Frozen MiCamera auxiliary baseline is incomplete, duplicated, or has unexpected ownership/representation facts."
                    : inspectionFailure ?? (matchingItems.Length != 1
                        ? $"Fresh post-clean Inspector returned {matchingItems.Length} exact MiCamera semantic/content identity matches; expected exactly one."
                        : "Fresh MiCamera range was out of bounds or its post-clean SHA-256 did not match the frozen baseline.");
                reportItems.Add(new PreservationReportItem
                {
                    Name = itemName,
                    Status = PreservationCheckStatus.Failed,
                    Details = details
                });
                continue;
            }

            reportItems.Add(new PreservationReportItem
            {
                Name = itemName,
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"Fresh Inspector rebound the embedded MiCamera item at offset {rebound!.ByteOffset}; length and SHA-256 match the frozen pre-clean descriptor."
            });
        }

        return allPassed;
    }

    private static string MapCategoryToName(uint category) => category switch
    {
        1 => "MediaPayload",
        2 => "Exif",
        3 => "Orientation",
        4 => "Gps",
        5 => "Icc",
        6 => "MakerNote",
        7 => "XmpNonTarget",
        8 => "ExtendedXmp",
        9 => "Hdr",
        10 => "GainMap",
        11 => "VideoStreams",
        12 => "AudioStreams",
        13 => "Timing",
        _ => $"UnknownCategory_{category}"
    };

    private static PreservationCheckStatus MapNativeStatus(uint nativeStatus) => nativeStatus switch
    {
        0 => PreservationCheckStatus.VerifiedPreserved,
        1 => PreservationCheckStatus.Failed,
        2 => PreservationCheckStatus.UnableToVerify,
        3 => PreservationCheckStatus.NotApplicable,
        4 => PreservationCheckStatus.SemanticallyPreserved,
        _ => PreservationCheckStatus.UnableToVerify
    };

    /// <summary>
    /// Re-verifies a GainMap working artifact's SHA-256 identity at preservation time.
    /// Returns <c>true</c> only when the file exists and its hash matches <paramref name="expectedSha256"/>.
    /// Any discrepancy is treated as "artifact absent" so that the Native preservation verdict
    /// can produce a DegradedToSdr failure rather than silently accepting a tampered file.
    /// </summary>
    private static async Task<bool> VerifyGainMapIdentityAsync(
        string gainMapPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(gainMapPath) || !File.Exists(gainMapPath))
            return false;

        if (new FileInfo(gainMapPath).Length == 0)
            return false;

        var hash=await MediaFileHashAttempt.ComputeAsync(gainMapPath,cancellationToken).ConfigureAwait(false);
        return hash.Failure is null && string.Equals(hash.Sha256,expectedSha256,StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> VerifySourceRangeIdentityAsync(
        string path,
        long offset,
        long length,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length <= 0 || string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path)) return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (offset > stream.Length || length > stream.Length - offset) return false;
            stream.Position = offset;
            using var sha = SHA256.Create();
            byte[] buffer = new byte[64 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read <= 0) return false;
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                remaining -= read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return string.Equals(Convert.ToHexString(sha.Hash!), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static PreservationReport ObservationFailure(string message) => CreateReport(
        new[]{new PreservationReportItem {Name="MediaPayload",Status=PreservationCheckStatus.Failed,Details=message}},false);

    private static PreservationReport CreateReport(IReadOnlyList<PreservationReportItem> items, bool allPassed)
    {
        PreservationOutcome outcome = PreservationOutcome.Preserved;
        if (!allPassed)
        {
            if (items.Any(i => (i.Name == "Hdr" || i.Name == "GainMap") && i.Status == PreservationCheckStatus.Failed))
            {
                outcome = PreservationOutcome.DegradedToSdr;
            }
            else
            {
                outcome = PreservationOutcome.PartiallyPreserved;
            }
        }

        string failedDetails = string.Join("; ", items.Where(i => i.Status is PreservationCheckStatus.Failed or PreservationCheckStatus.UnableToVerify).Select(i => $"{i.Name} ({i.Status}): {i.Details}"));
        return new PreservationReport
        {
            OverallOutcome = outcome,
            Items = items,
            Summary = allPassed
                ? "All applicable preservation checks verified intact."
                : (outcome == PreservationOutcome.DegradedToSdr
                    ? $"Preservation check failed: HDR/GainMap was lost (DegradedToSdr). Details: {failedDetails}"
                    : $"Preservation check failed: One or more non-protocol metadata or media items were lost or altered: {failedDetails}")
        };
    }

    public static bool TryLocateHeicExifItem(byte[] data, out long offset, out long length, out string? error)
    {
        return NativeHeifBoxParser.TryLocateExifItem(data, out offset, out length, out error);
    }

    public static bool TryLocateHeicXmpItem(byte[] data, out long offset, out long length, out string? error)
    {
        return NativeHeifBoxParser.TryLocateXmpItem(data, out offset, out length, out error);
    }
}

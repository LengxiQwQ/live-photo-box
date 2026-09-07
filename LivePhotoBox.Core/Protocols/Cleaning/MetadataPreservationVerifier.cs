using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
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
    public static async Task<PreservationBaseline> CaptureBaselineAsync(
        ExtractedMediaBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(bundle.PrimaryImage);

        cancellationToken.ThrowIfCancellationRequested();

        var imageObs = await NativeMediaService.CapturePreservationObservationAsync(
            bundle.PrimaryImage.Path,
            bundle.SourceFacts.Protocol,
            bundle.PrimaryImage.ImageContainer,
            cancellationToken).ConfigureAwait(false);

        PreservationObservation? videoObs = null;
        long? videoLength = null;
        string? videoSha = null;
        if (bundle.MotionVideo != null && File.Exists(bundle.MotionVideo.Path))
        {
            videoLength = bundle.MotionVideo.ByteLength > 0
                ? bundle.MotionVideo.ByteLength
                : new FileInfo(bundle.MotionVideo.Path).Length;
            videoSha = bundle.MotionVideo.Sha256;

            videoObs = await NativeMediaService.CapturePreservationObservationAsync(
                bundle.MotionVideo.Path,
                bundle.SourceFacts.Protocol,
                ImageContainer.Unknown,
                cancellationToken).ConfigureAwait(false);
        }

        long primaryImageLength = bundle.PrimaryImage.ByteLength > 0
            ? bundle.PrimaryImage.ByteLength
            : (File.Exists(bundle.PrimaryImage.Path) ? new FileInfo(bundle.PrimaryImage.Path).Length : 0);

        return new PreservationBaseline
        {
            PrimaryImagePath = bundle.PrimaryImage.Path,
            PrimaryImageLength = primaryImageLength,
            PrimaryImageSha256 = bundle.PrimaryImage.Sha256 ?? "",
            PrimaryImageContainer = bundle.PrimaryImage.ImageContainer,
            Protocol = bundle.SourceFacts.Protocol,
            ImageObservation = imageObs,
            MotionVideoLength = videoLength,
            MotionVideoSha256 = videoSha,
            MotionVideoContainer = bundle.MotionVideo?.VideoContainer ?? VideoContainer.Unknown,
            MotionVideoPath = bundle.MotionVideo?.Path,
            VideoObservation = videoObs,
            GainMapSha256 = bundle.GainMap?.Sha256
        };
    }

    public static async Task<PreservationReport> VerifyAsync(
        ExtractedMediaBundle preBundle,
        string stagedImagePath,
        string? stagedVideoPath,
        CancellationToken cancellationToken = default)
    {
        var baseline = await CaptureBaselineAsync(preBundle, cancellationToken).ConfigureAwait(false);
        return await VerifyAgainstBaselineAsync(baseline, stagedImagePath, stagedVideoPath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PreservationReport> VerifyAgainstBaselineAsync(
        PreservationBaseline baseline,
        string stagedImagePath,
        string? stagedVideoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(stagedImagePath);

        cancellationToken.ThrowIfCancellationRequested();

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

        PreservationObservation postImage;
        try
        {
            postImage = await NativeMediaService.CapturePreservationObservationAsync(
                stagedImagePath,
                baseline.Protocol,
                baseline.PrimaryImageContainer,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CreateReport(new List<PreservationReportItem>
            {
                new()
                {
                    Name = "MediaPayload",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Failed to capture post-clean observation: {ex.Message}"
                }
            }, allPassed: false);
        }

        PreservationObservation? preVideo = null;
        PreservationObservation? postVideo = null;
        if (baseline.MotionVideoLength != null && stagedVideoPath != null)
        {
            preVideo = baseline.VideoObservation;
            if (File.Exists(stagedVideoPath) && new FileInfo(stagedVideoPath).Length > 0)
            {
                try
                {
                    postVideo = await NativeMediaService.CapturePreservationObservationAsync(
                        stagedVideoPath,
                        baseline.Protocol,
                        ImageContainer.Unknown,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // postVideo remains null
                }
            }
        }

        bool hasDetachedGainmap = baseline.GainMapSha256 != null;
        var verdicts = NativeMediaService.VerifyPreservation(
            baseline.ImageObservation,
            postImage,
            preVideo,
            postVideo,
            baseline.Protocol,
            hasDetachedGainmap,
            out bool allPassed,
            cancellationToken);

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

        return CreateReport(items, allPassed);
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

        string failedDetails = string.Join("; ", items.Where(i => i.Status == PreservationCheckStatus.Failed).Select(i => $"{i.Name}: {i.Details}"));
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

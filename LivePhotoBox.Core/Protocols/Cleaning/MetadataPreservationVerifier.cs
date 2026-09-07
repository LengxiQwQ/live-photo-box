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

        var items = new List<PreservationReportItem>();
        bool allPassed = true;

        if (!File.Exists(stagedImagePath) || new FileInfo(stagedImagePath).Length == 0)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = "Cleaned image is missing or empty."
            });
            allPassed = false;
            return CreateReport(items, allPassed);
        }

        PreservationObservation pre = baseline.ImageObservation;
        PreservationObservation post;
        try
        {
            post = await NativeMediaService.CapturePreservationObservationAsync(
                stagedImagePath,
                baseline.Protocol,
                baseline.PrimaryImageContainer,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = $"Failed to capture post-clean observation: {ex.Message}"
            });
            allPassed = false;
            return CreateReport(items, allPassed);
        }

        // 1. Media Payload (Image)
        if (string.IsNullOrEmpty(pre.ImageCodestreamSha256) || pre.CodestreamError)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.UnableToVerify,
                Details = "Could not extract image codestream for bitwise verification (unrecognized container or corrupt codestream)."
            });
            allPassed = false;
        }
        else if (post.CodestreamError)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = "Image payload codestream extraction failed on cleaned artifact."
            });
            allPassed = false;
        }
        else if (ShasEqual(pre.ImageCodestreamSha256, post.ImageCodestreamSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"Image payload codestream bitwise verified identical (SHA-256: {pre.ImageCodestreamSha256})."
            });
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = $"Image payload codestream SHA-256 mismatch (pre: {pre.ImageCodestreamSha256}, post: {post.ImageCodestreamSha256})."
            });
            allPassed = false;
        }

        // 2. Exif / TIFF
        if (!pre.HasExif)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Exif segment in source artifact."
            });
        }
        else if (pre.ExifParseError || post.ExifParseError)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.UnableToVerify,
                Details = "Exif metadata parse error encountered."
            });
            allPassed = false;
        }
        else if (!post.HasExif)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.Failed,
                Details = "TIFF / Exif metadata was present in source but missing in cleaned artifact."
            });
            allPassed = false;
        }
        else if (!ShasEqual(pre.ExifIfd0NonPtrSha256, post.ExifIfd0NonPtrSha256) ||
                 !ShasEqual(pre.ExifExifIfdSha256, post.ExifExifIfdSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.Failed,
                Details = "Non-protocol Exif tags altered or dropped."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "All non-protocol TIFF/Exif tags bitwise verified preserved."
            });
        }

        // 3. Orientation
        if (pre.Orientation == 0)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Orientation",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Exif Orientation tag in input."
            });
        }
        else if (post.Orientation == 0)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Orientation",
                Status = PreservationCheckStatus.Failed,
                Details = "Exif Orientation tag was lost."
            });
            allPassed = false;
        }
        else if (pre.Orientation != post.Orientation)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Orientation",
                Status = PreservationCheckStatus.Failed,
                Details = $"Exif Orientation changed from {pre.Orientation} to {post.Orientation}."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Orientation",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"Orientation tag ({pre.Orientation}) verified preserved."
            });
        }

        // 4. GPS
        if (!pre.HasGps)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Gps",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No GPS metadata in input artifact."
            });
        }
        else if (!post.HasGps)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Gps",
                Status = PreservationCheckStatus.Failed,
                Details = "GPS metadata was present in source but completely lost."
            });
            allPassed = false;
        }
        else if (!ShasEqual(pre.GpsSha256, post.GpsSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "Gps",
                Status = PreservationCheckStatus.Failed,
                Details = "GPS metadata tags altered or partially dropped."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Gps",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "All GPS tags verified preserved."
            });
        }

        // 5. ICC Profile / Color Space
        if (!pre.HasIcc)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No ICC profile in input artifact."
            });
        }
        else if (pre.IccParseError || post.IccParseError)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.UnableToVerify,
                Details = "ICC color profile parsing error encountered."
            });
            allPassed = false;
        }
        else if (!post.HasIcc)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.Failed,
                Details = "ICC color profile was present in input artifact but lost after cleaning."
            });
            allPassed = false;
        }
        else if (!ShasEqual(pre.IccSha256, post.IccSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.Failed,
                Details = "ICC color profile binary payload was modified after cleaning."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "ICC color profile binary exact match."
            });
        }

        // 6. MakerNote
        if (!pre.HasMakerNote)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.NotApplicable,
                Details = baseline.Protocol == SourceProtocol.AppleLivePhoto
                    ? "No Apple MakerNote in source."
                    : "No applicable camera MakerNote requiring preservation."
            });
        }
        else if (pre.MakerNoteMalformed || post.MakerNoteMalformed)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.UnableToVerify,
                Details = "Camera MakerNote malformed or unparseable."
            });
            allPassed = false;
        }
        else if (!post.HasMakerNote)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.Failed,
                Details = baseline.Protocol == SourceProtocol.AppleLivePhoto
                    ? "Apple MakerNote container was lost completely."
                    : "Camera MakerNote was lost on non-Apple source."
            });
            allPassed = false;
        }
        else if (!ShasEqual(pre.MakernoteNonliveSha256, post.MakernoteNonliveSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.Failed,
                Details = baseline.Protocol == SourceProtocol.AppleLivePhoto
                    ? "Non-live Apple MakerNote data was modified or dropped."
                    : "Camera MakerNote altered on non-Apple source."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = baseline.Protocol == SourceProtocol.AppleLivePhoto
                    ? "Apple MakerNote verified intact: non-live tags preserved, live tags stripped."
                    : "Camera MakerNote preserved."
            });
        }

        // 7. XMP Non-Target Namespaces
        if (!pre.HasXmp || string.IsNullOrEmpty(pre.XmpNonprotocolSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No non-protocol XMP properties present in input."
            });
        }
        else if (pre.XmpMalformed || post.XmpMalformed)
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.UnableToVerify,
                Details = "XMP payload malformed or unparseable."
            });
            allPassed = false;
        }
        else if (!post.HasXmp)
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.Failed,
                Details = "XMP metadata was present in source but missing in cleaned artifact."
            });
            allPassed = false;
        }
        else if (!ShasEqual(pre.XmpNonprotocolSha256, post.XmpNonprotocolSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.Failed,
                Details = $"Non-target XMP properties modified or dropped (pre: '{pre.XmpNonprotocolSha256}', post: '{post.XmpNonprotocolSha256}')."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "All non-protocol XMP properties verified preserved."
            });
        }

        // 7.5 Extended XMP Preservation
        if (!pre.HasExtendedXmp)
        {
            items.Add(new PreservationReportItem
            {
                Name = "ExtendedXmp",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Extended XMP present in source."
            });
        }
        else if (!post.HasExtendedXmp || !ShasEqual(pre.ExtendedXmpSha256, post.ExtendedXmpSha256))
        {
            items.Add(new PreservationReportItem
            {
                Name = "ExtendedXmp",
                Status = PreservationCheckStatus.Failed,
                Details = $"Extended XMP segments altered or dropped (pre: {pre.ExtendedXmpSha256}, post: {post.ExtendedXmpSha256})."
            });
            allPassed = false;
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "ExtendedXmp",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"Extended XMP segments bitwise verified preserved (SHA-256: {pre.ExtendedXmpSha256})."
            });
        }

        // 8. HDR & GainMap
        bool hasHdrIndicator = false;
        bool hdrFailed = false;
        string hdrFailReason = "";

        if (pre.HeicAuxAmbiguous || post.HeicAuxAmbiguous)
        {
            hasHdrIndicator = true;
            hdrFailed = true;
            hdrFailReason = "Ambiguous or duplicate HEIC auxiliary relationship detected (fail closed).";
        }
        else if (pre.HasHeicAux)
        {
            hasHdrIndicator = true;
            if (!post.HasHeicAux)
            {
                hdrFailed = true;
                hdrFailReason = "HEIC GainMap auxl relationship or auxiliary item was dropped after cleaning.";
            }
            else if (pre.HeicPrimaryItemId != post.HeicPrimaryItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap primary item ID mismatch (pre: {pre.HeicPrimaryItemId}, post: {post.HeicPrimaryItemId}).";
            }
            else if (pre.HeicAuxItemId != post.HeicAuxItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxiliary item ID mismatch (pre: {pre.HeicAuxItemId}, post: {post.HeicAuxItemId}).";
            }
            else if (pre.HeicAuxFromItemId != post.HeicAuxFromItemId || pre.HeicAuxToItemId != post.HeicAuxToItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxl association direction/IDs tampered (pre: {pre.HeicAuxFromItemId}->{pre.HeicAuxToItemId}, post: {post.HeicAuxFromItemId}->{post.HeicAuxToItemId}).";
            }
            else if (!ShasEqual(pre.HeicAuxItemSha256, post.HeicAuxItemSha256))
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxl payload altered (pre: {pre.HeicAuxItemSha256}, post: {post.HeicAuxItemSha256}).";
            }
        }

        if (pre.HasGainMapMeta)
        {
            hasHdrIndicator = true;
            if (!hdrFailed && !post.HasGainMapMeta)
            {
                hdrFailed = true;
                hdrFailReason = "GainMap / HDR metadata was present in source but dropped after cleaning.";
            }
        }

        if (hdrFailed)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.Failed,
                Details = hdrFailReason
            });
            allPassed = false;
        }
        else if (hasHdrIndicator)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "GainMap / HDR payloads and metadata verified preserved."
            });
        }
        else if (baseline.GainMapSha256 != null)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.SemanticallyPreserved,
                Details = "Primary image contains no embedded HDR metadata; GainMap is tracked as detached artifact."
            });
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No HDR / GainMap metadata present in source."
            });
        }

        // 9. Detached GainMap
        if (baseline.GainMapSha256 != null)
        {
            items.Add(new PreservationReportItem
            {
                Name = "GainMap",
                Status = PreservationCheckStatus.SemanticallyPreserved,
                Details = $"Detached GainMap is not processed as destructive target; tracked as detached artifact ({baseline.GainMapSha256})."
            });
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "GainMap",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No detached GainMap artifact present."
            });
        }

        // 10. VideoStreams & AudioStreams
        if (baseline.MotionVideoLength != null && stagedVideoPath != null)
        {
            try
            {
                if (!File.Exists(stagedVideoPath) || new FileInfo(stagedVideoPath).Length == 0)
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "VideoStreams",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Cleaned video artifact is missing or empty."
                    });
                    items.Add(new PreservationReportItem
                    {
                        Name = "AudioStreams",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Cleaned video artifact is missing or empty."
                    });
                    allPassed = false;
                }
                else
                {
                    PreservationObservation? postVideo = null;
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
                        // handled below
                    }

                    string preMdatSha = baseline.VideoObservation?.VideoMdatSha256 ?? "";
                    string postMdatSha = postVideo?.VideoMdatSha256 ?? "";

                    if (!string.IsNullOrEmpty(preMdatSha) && !string.IsNullOrEmpty(postMdatSha))
                    {
                        if (!string.Equals(preMdatSha, postMdatSha, StringComparison.OrdinalIgnoreCase))
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "VideoStreams",
                                Status = PreservationCheckStatus.Failed,
                                Details = $"Video mdat sample payload SHA-256 mismatch (pre: {preMdatSha}, post: {postMdatSha})."
                            });
                            items.Add(new PreservationReportItem
                            {
                                Name = "AudioStreams",
                                Status = PreservationCheckStatus.Failed,
                                Details = $"Audio mdat sample payload SHA-256 mismatch (pre: {preMdatSha}, post: {postMdatSha})."
                            });
                            allPassed = false;
                        }
                        else
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "VideoStreams",
                                Status = PreservationCheckStatus.VerifiedPreserved,
                                Details = $"Video sample payload bitwise verified identical (mdat SHA-256: {preMdatSha})."
                            });
                            items.Add(new PreservationReportItem
                            {
                                Name = "AudioStreams",
                                Status = PreservationCheckStatus.VerifiedPreserved,
                                Details = $"Audio sample payload bitwise verified identical (mdat SHA-256: {preMdatSha})."
                            });
                        }
                    }
                    else
                    {
                        var probed = await NativeMediaService.ProbeVideoAsync(stagedVideoPath, cancellationToken).ConfigureAwait(false);
                        items.Add(new PreservationReportItem
                        {
                            Name = "VideoStreams",
                            Status = PreservationCheckStatus.UnableToVerify,
                            Details = $"Video stream probed at {probed.Width}x{probed.Height} ({probed.Codec}), but mdat payload fingerprint could not be established."
                        });
                        items.Add(new PreservationReportItem
                        {
                            Name = "AudioStreams",
                            Status = PreservationCheckStatus.UnableToVerify,
                            Details = $"Audio stream probed (HasAudio={probed.HasAudio}), but mdat payload fingerprint could not be established."
                        });
                        allPassed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "VideoStreams",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Video probe failed: {ex.Message}"
                });
                items.Add(new PreservationReportItem
                {
                    Name = "AudioStreams",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Audio probe failed: {ex.Message}"
                });
                allPassed = false;
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "VideoStreams",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No motion video artifact in bundle."
            });
            items.Add(new PreservationReportItem
            {
                Name = "AudioStreams",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No motion video artifact in bundle."
            });
        }

        // 11. Timing
        if (!string.IsNullOrEmpty(pre.DateTimeOriginal))
        {
            if (string.IsNullOrEmpty(post.DateTimeOriginal) ||
                !string.Equals(pre.DateTimeOriginal, post.DateTimeOriginal, StringComparison.Ordinal))
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Timing",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Capture timestamp altered from '{pre.DateTimeOriginal}' to '{post.DateTimeOriginal}'."
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Timing",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"Capture timestamp ({pre.DateTimeOriginal}) verified preserved."
                });
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Timing",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No capture timestamp present in source media."
            });
        }

        return CreateReport(items, allPassed);
    }

    private static bool ShasEqual(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static PreservationReport CreateReport(List<PreservationReportItem> items, bool allPassed)
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

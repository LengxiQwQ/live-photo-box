using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services.Protocols;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Verifies that non-protocol metadata, media payloads, and auxiliary streams
/// are preserved intact after source protocol cleaning.
/// Uses mathematical certainty, binary structure analysis, and exact metadata truth comparison.
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

        byte[] imageBytes = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path, cancellationToken).ConfigureAwait(false);
        string imageSha = Convert.ToHexString(SHA256.HashData(imageBytes));

        string? videoSha = null;
        long? videoLength = null;
        string? preMdatSha = null;
        if (bundle.MotionVideo != null && File.Exists(bundle.MotionVideo.Path))
        {
            videoLength = bundle.MotionVideo.ByteLength > 0
                ? bundle.MotionVideo.ByteLength
                : new FileInfo(bundle.MotionVideo.Path).Length;

            if (!string.IsNullOrEmpty(bundle.MotionVideo.Sha256))
            {
                videoSha = bundle.MotionVideo.Sha256;
            }
            else
            {
                using var stream = File.OpenRead(bundle.MotionVideo.Path);
                using var sha = SHA256.Create();
                videoSha = Convert.ToHexString(sha.ComputeHash(stream));
            }

            preMdatSha = await ExtractMdatPayloadSha256Async(bundle.MotionVideo.Path, cancellationToken).ConfigureAwait(false);
        }

        byte[]? gainMapBytes = null;
        string? gainMapSha = null;
        if (bundle.GainMap != null && File.Exists(bundle.GainMap.Path))
        {
            gainMapBytes = await File.ReadAllBytesAsync(bundle.GainMap.Path, cancellationToken).ConfigureAwait(false);
            gainMapSha = Convert.ToHexString(SHA256.HashData(gainMapBytes));
        }

        string? preCodestreamSha = bundle.PrimaryImage.ImageContainer == ImageContainer.Jpeg
            ? ExtractJpegEntropyScanSha256(imageBytes)
            : ExtractHeicPrimaryItemSha256(imageBytes);

        byte[]? preTiffBytes = ExtractTiff(imageBytes, bundle.PrimaryImage.Path);
        TiffMetadata? preTiff = preTiffBytes != null ? ParseTiff(preTiffBytes) : null;
        byte[]? preIcc = ExtractIcc(imageBytes, bundle.PrimaryImage.Path);

        return new PreservationBaseline
        {
            PrimaryImageBytes = imageBytes,
            PrimaryImageLength = imageBytes.Length,
            PrimaryImageSha256 = imageSha,
            PrimaryImageContainer = bundle.PrimaryImage.ImageContainer,
            PrimaryImagePath = bundle.PrimaryImage.Path,
            Protocol = bundle.SourceFacts.Protocol,
            MotionVideoBytes = null,
            MotionVideoLength = videoLength,
            MotionVideoSha256 = videoSha,
            MotionVideoContainer = bundle.MotionVideo?.VideoContainer ?? VideoContainer.Unknown,
            MotionVideoPath = bundle.MotionVideo?.Path,
            PreMdatSha = preMdatSha,
            GainMapBytes = gainMapBytes,
            GainMapLength = gainMapBytes?.Length,
            GainMapSha256 = gainMapSha,
            PreCodestreamSha = preCodestreamSha,
            PreTiff = preTiff,
            PreIcc = preIcc
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

        byte[] preBytes = baseline.PrimaryImageBytes;
        byte[] postBytes = File.Exists(stagedImagePath)
            ? await File.ReadAllBytesAsync(stagedImagePath, cancellationToken).ConfigureAwait(false)
            : Array.Empty<byte>();

        // 1. Media Payload (Image)
        try
        {
            if (postBytes.Length == 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "MediaPayload",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Cleaned image is missing or empty."
                });
                allPassed = false;
            }
            else
            {
                string? preCodestreamSha = baseline.PreCodestreamSha;

                string? postCodestreamSha = baseline.PrimaryImageContainer == ImageContainer.Jpeg
                    ? ExtractJpegEntropyScanSha256(postBytes)
                    : ExtractHeicPrimaryItemSha256(postBytes);

                if (preCodestreamSha != null && postCodestreamSha != null)
                {
                    if (!string.Equals(preCodestreamSha, postCodestreamSha, StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "MediaPayload",
                            Status = PreservationCheckStatus.Failed,
                            Details = $"Image payload codestream SHA-256 mismatch (pre: {preCodestreamSha}, post: {postCodestreamSha})."
                        });
                        allPassed = false;
                    }
                    else
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "MediaPayload",
                            Status = PreservationCheckStatus.VerifiedPreserved,
                            Details = $"Image payload codestream bitwise verified identical (SHA-256: {preCodestreamSha})."
                        });
                    }
                }
                else
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "MediaPayload",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Could not extract image codestream for bitwise verification (unrecognized container or corrupt codestream)."
                    });
                    allPassed = false;
                }
            }
        }
        catch (Exception ex)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = $"Image codestream extraction error: {ex.Message}"
            });
            allPassed = false;
        }

        // 2. Binary TIFF / Exif Parsing
        byte[]? preTiffBytes = ExtractTiff(preBytes, baseline.PrimaryImagePath);
        byte[]? postTiffBytes = ExtractTiff(postBytes, stagedImagePath);
        TiffMetadata? preTiff = baseline.PreTiff ?? (preTiffBytes != null ? ParseTiff(preTiffBytes) : null);
        TiffMetadata? postTiff = postTiffBytes != null ? ParseTiff(postTiffBytes) : null;

        if (preTiff != null && preTiff.IsValid)
        {
            if (postTiff == null || !postTiff.IsValid)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Exif",
                    Status = PreservationCheckStatus.Failed,
                    Details = "TIFF / Exif metadata was present in source but missing in cleaned artifact."
                });
                allPassed = false;
            }
            else
            {
                bool allTagsPreserved = true;
                string diffDetail = "";

                foreach (var (tag, preVal) in preTiff.Ifd0Tags)
                {
                    if (tag is 0x8769 or 0x8825) continue;
                    if (!postTiff.Ifd0Tags.TryGetValue(tag, out var postVal) || !preVal.SequenceEqual(postVal))
                    {
                        allTagsPreserved = false;
                        diffDetail = $"IFD0 tag 0x{tag:X4} modified or dropped.";
                        break;
                    }
                }

                if (allTagsPreserved)
                {
                    foreach (var (tag, preVal) in preTiff.ExifTags)
                    {
                        if (tag == 0x927C) continue;
                        if (!postTiff.ExifTags.TryGetValue(tag, out var postVal) || !preVal.SequenceEqual(postVal))
                        {
                            allTagsPreserved = false;
                            diffDetail = $"Exif tag 0x{tag:X4} modified or dropped.";
                            break;
                        }
                    }
                }

                if (!allTagsPreserved)
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "Exif",
                        Status = PreservationCheckStatus.Failed,
                        Details = $"Non-protocol Exif tags altered: {diffDetail}"
                    });
                    allPassed = false;
                }
                else
                {
                    int totalChecked = preTiff.Ifd0Tags.Count(t => t.Key is not (0x8769 or 0x8825)) +
                                       preTiff.ExifTags.Count(t => t.Key != 0x927C);
                    items.Add(new PreservationReportItem
                    {
                        Name = "Exif",
                        Status = PreservationCheckStatus.VerifiedPreserved,
                        Details = $"All {totalChecked} non-protocol TIFF/Exif tags bitwise verified preserved."
                    });
                }
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Exif",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Exif segment in source artifact."
            });
        }

        // 3. Orientation
        if (preTiff?.Orientation != null)
        {
            if (postTiff?.Orientation == null)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Orientation",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Exif Orientation tag was lost."
                });
                allPassed = false;
            }
            else if (postTiff.Orientation != preTiff.Orientation)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Orientation",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Exif Orientation changed from {preTiff.Orientation} to {postTiff.Orientation}."
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Orientation",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"Orientation tag ({preTiff.Orientation}) verified preserved."
                });
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Orientation",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Exif Orientation tag in input."
            });
        }

        // 4. GPS
        if (preTiff != null && preTiff.GpsTags.Count > 0)
        {
            if (postTiff == null || postTiff.GpsTags.Count == 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Gps",
                    Status = PreservationCheckStatus.Failed,
                    Details = "GPS metadata was present in source but completely lost."
                });
                allPassed = false;
            }
            else
            {
                bool gpsMatches = true;
                foreach (var (tag, expectedBytes) in preTiff.GpsTags)
                {
                    if (!postTiff.GpsTags.TryGetValue(tag, out var actualBytes) ||
                        !actualBytes.SequenceEqual(expectedBytes))
                    {
                        gpsMatches = false;
                        break;
                    }
                }

                if (!gpsMatches)
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
                        Details = $"All {preTiff.GpsTags.Count} GPS tags verified preserved."
                    });
                }
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Gps",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No GPS metadata in input artifact."
            });
        }

        // 5. ICC Profile / Color Space
        byte[]? preIcc = baseline.PreIcc ?? ExtractIcc(preBytes, baseline.PrimaryImagePath);
        byte[]? postIcc = ExtractIcc(postBytes, stagedImagePath);

        if (preIcc != null && preIcc.Length > 0)
        {
            if (postIcc == null || postIcc.Length == 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Icc",
                    Status = PreservationCheckStatus.Failed,
                    Details = "ICC color profile was present in input artifact but lost after cleaning."
                });
                allPassed = false;
            }
            else if (!preIcc.SequenceEqual(postIcc))
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
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Icc",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No ICC profile in input artifact."
            });
        }

        // 6. MakerNote
        if (baseline.Protocol == SourceProtocol.AppleLivePhoto)
        {
            var preAppleEntries = ParseAppleMakerNote(preTiff?.MakerNote);
            if (preAppleEntries.Count > 0)
            {
                if (postTiff?.MakerNote == null || postTiff.MakerNote.Length == 0)
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "MakerNote",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Apple MakerNote container was lost completely."
                    });
                    allPassed = false;
                }
                else
                {
                    var postAppleEntries = ParseAppleMakerNote(postTiff.MakerNote);
                    var postAppleDict = postAppleEntries.ToDictionary(e => e.Tag);
                    var liveTags = new HashSet<ushort> { 0x0011, 0x0017, 0x0025, 0x002b };
                    bool allNonLivePreserved = true;
                    string failDetail = "";

                    foreach (var preEntry in preAppleEntries)
                    {
                        if (liveTags.Contains(preEntry.Tag))
                        {
                            // Live tags are stripped by design
                            continue;
                        }

                        if (!postAppleDict.TryGetValue(preEntry.Tag, out var postEntry))
                        {
                            allNonLivePreserved = false;
                            failDetail = $"Non-live Apple MakerNote tag 0x{preEntry.Tag:X4} was dropped.";
                            break;
                        }

                        if (!preEntry.ValueBytes.SequenceEqual(postEntry.ValueBytes))
                        {
                            allNonLivePreserved = false;
                            failDetail = $"Non-live Apple MakerNote tag 0x{preEntry.Tag:X4} data was modified.";
                            break;
                        }
                    }

                    if (!allNonLivePreserved)
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "MakerNote",
                            Status = PreservationCheckStatus.Failed,
                            Details = failDetail
                        });
                        allPassed = false;
                    }
                    else
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "MakerNote",
                            Status = PreservationCheckStatus.VerifiedPreserved,
                            Details = "Apple MakerNote verified intact: non-live tags preserved, live tags stripped."
                        });
                    }
                }
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = PreservationCheckStatus.NotApplicable,
                    Details = "No Apple MakerNote in source."
                });
            }
        }
        else if (preTiff?.MakerNote != null && preTiff.MakerNote.Length > 0)
        {
            if (postTiff?.MakerNote == null || postTiff.MakerNote.Length == 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Camera MakerNote was lost on non-Apple source."
                });
                allPassed = false;
            }
            else
            {
                bool mnEqual = preTiff.MakerNote.SequenceEqual(postTiff.MakerNote);
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = mnEqual
                        ? PreservationCheckStatus.VerifiedPreserved
                        : PreservationCheckStatus.Failed,
                    Details = mnEqual
                        ? "Camera MakerNote preserved."
                        : "Camera MakerNote altered on non-Apple source."
                });
                if (!mnEqual)
                {
                    allPassed = false;
                }
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No applicable camera MakerNote requiring preservation."
            });
        }

        // 7. XMP Non-Target Namespaces
        string preXmp = ExtractXmp(preBytes, baseline.PrimaryImagePath);
        string postXmp = ExtractXmp(postBytes, stagedImagePath);

        var preNonTarget = ExtractNonTargetXmpProperties(preXmp);
        var postNonTarget = ExtractNonTargetXmpProperties(postXmp);

        if (preNonTarget.Count > 0)
        {
            var missingOrChanged = new List<string>();
            foreach (var (key, expectedValue) in preNonTarget)
            {
                if (!postNonTarget.TryGetValue(key, out var actualValue))
                {
                    missingOrChanged.Add($"{key} (missing)");
                }
                else if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
                {
                    missingOrChanged.Add($"{key} (value mismatch: expected '{expectedValue}', got '{actualValue}')");
                }
            }

            if (missingOrChanged.Count > 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "XmpNonTarget",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Non-target XMP properties modified or dropped: {string.Join(", ", missingOrChanged)}"
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "XmpNonTarget",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"All {preNonTarget.Count} non-protocol XMP properties verified preserved."
                });
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "XmpNonTarget",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No XMP present in input."
            });
        }

        // 7.5. Extended XMP Preservation
        string? preExtXmpSha = ExtractExtendedXmpSha256(preBytes);
        string? postExtXmpSha = ExtractExtendedXmpSha256(postBytes);
        if (preExtXmpSha != null)
        {
            if (postExtXmpSha == null || !string.Equals(preExtXmpSha, postExtXmpSha, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new PreservationReportItem
                {
                    Name = "ExtendedXmp",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Extended XMP segments altered or dropped (pre: {preExtXmpSha}, post: {postExtXmpSha})."
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "ExtendedXmp",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"Extended XMP segments bitwise verified preserved (SHA-256: {preExtXmpSha})."
                });
            }
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "ExtendedXmp",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No Extended XMP present in source."
            });
        }

        // 8. HDR & GainMap
        bool hasHdrIndicator = false;
        bool hdrFailed = false;
        string hdrFailReason = "";

        // Check A: HEIC auxl item relationship and payload
        HeicAuxRelationSnapshot? preAuxRel = ExtractHeicAuxRelationSnapshot(preBytes);
        if (preAuxRel != null)
        {
            hasHdrIndicator = true;
            HeicAuxRelationSnapshot? postAuxRel = ExtractHeicAuxRelationSnapshot(postBytes);
            if (postAuxRel == null)
            {
                hdrFailed = true;
                hdrFailReason = "HEIC GainMap auxl relationship or auxiliary item was dropped after cleaning.";
            }
            else if (preAuxRel.PrimaryItemId != postAuxRel.PrimaryItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap primary item ID mismatch (pre: {preAuxRel.PrimaryItemId}, post: {postAuxRel.PrimaryItemId}).";
            }
            else if (preAuxRel.AuxiliaryItemId != postAuxRel.AuxiliaryItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxiliary item ID mismatch (pre: {preAuxRel.AuxiliaryItemId}, post: {postAuxRel.AuxiliaryItemId}).";
            }
            else if (preAuxRel.FromItemId != postAuxRel.FromItemId || preAuxRel.ToItemId != postAuxRel.ToItemId)
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxl association direction/IDs tampered (pre: {preAuxRel.FromItemId}->{preAuxRel.ToItemId}, post: {postAuxRel.FromItemId}->{postAuxRel.ToItemId}).";
            }
            else if (!string.Equals(preAuxRel.AuxiliaryPayloadSha256, postAuxRel.AuxiliaryPayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                hdrFailed = true;
                hdrFailReason = $"HEIC GainMap auxl payload altered (pre: {preAuxRel.AuxiliaryPayloadSha256}, post: {postAuxRel.AuxiliaryPayloadSha256}).";
            }
        }

        // Check B: Structured XMP GainMap / hdrgm properties
        var preGainMapProps = preNonTarget.Where(kvp => kvp.Key.Contains("hdrgm", StringComparison.OrdinalIgnoreCase) ||
                                                        kvp.Key.Contains("GainMap", StringComparison.OrdinalIgnoreCase)).ToList();
        if (preGainMapProps.Count > 0)
        {
            hasHdrIndicator = true;
            if (!hdrFailed)
            {
                foreach (var prop in preGainMapProps)
                {
                    if (!postNonTarget.TryGetValue(prop.Key, out var postVal) || !string.Equals(prop.Value, postVal, StringComparison.Ordinal))
                    {
                        hdrFailed = true;
                        hdrFailReason = $"HDR/GainMap property '{prop.Key}' changed or lost.";
                        break;
                    }
                }
            }
        }

        // Check C: Raw fallback if raw bytes contained GainMap / hdrgm marker
        bool preHadRawGainMap = preBytes.AsSpan().IndexOf("GainMap"u8) >= 0 || preBytes.AsSpan().IndexOf("hdrgm"u8) >= 0;
        bool postHasRawGainMap = postBytes.AsSpan().IndexOf("GainMap"u8) >= 0 || postBytes.AsSpan().IndexOf("hdrgm"u8) >= 0;
        if (preHadRawGainMap)
        {
            if (!hdrFailed && !postHasRawGainMap)
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
        else if (baseline.GainMapBytes != null)
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
        if (baseline.GainMapBytes != null)
        {
            if (baseline.GainMapBytes.Length == 0)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "GainMap",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Declared GainMap baseline is empty."
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "GainMap",
                    Status = PreservationCheckStatus.SemanticallyPreserved,
                    Details = $"Detached GainMap is not processed as destructive target; tracked as detached artifact ({baseline.GainMapSha256})."
                });
            }
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
                    string? preMdatSha = baseline.PreMdatSha;
                    string? postMdatSha = await ExtractMdatPayloadSha256Async(stagedVideoPath, cancellationToken).ConfigureAwait(false);

                    if (preMdatSha != null && postMdatSha != null)
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
        if (!string.IsNullOrEmpty(preTiff?.DateTimeOriginal))
        {
            if (postTiff?.DateTimeOriginal == null || !string.Equals(preTiff.DateTimeOriginal, postTiff.DateTimeOriginal, StringComparison.Ordinal))
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Timing",
                    Status = PreservationCheckStatus.Failed,
                    Details = $"Capture timestamp altered from '{preTiff.DateTimeOriginal}' to '{postTiff?.DateTimeOriginal}'."
                });
                allPassed = false;
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Timing",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = $"Capture timestamp ({preTiff.DateTimeOriginal}) verified preserved."
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

        // 12. Determine Final Outcome
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

    #region Binary TIFF / Exif Parsing

    public sealed class TiffMetadata
    {
        public bool IsValid { get; set; }
        public ushort? Orientation { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string? DateTimeOriginal { get; set; }
        public Dictionary<ushort, byte[]> Ifd0Tags { get; } = new();
        public Dictionary<ushort, byte[]> ExifTags { get; } = new();
        public Dictionary<ushort, byte[]> GpsTags { get; } = new();
        public byte[]? MakerNote { get; set; }
    }

    public static TiffMetadata ParseTiff(byte[] tiffBytes)
    {
        var meta = new TiffMetadata();
        if (tiffBytes == null || tiffBytes.Length < 8) return meta;

        bool isLittleEndian;
        if (tiffBytes[0] == 0x49 && tiffBytes[1] == 0x49 && tiffBytes[2] == 0x2A && tiffBytes[3] == 0x00)
        {
            isLittleEndian = true;
        }
        else if (tiffBytes[0] == 0x4D && tiffBytes[1] == 0x4D && tiffBytes[2] == 0x00 && tiffBytes[3] == 0x2A)
        {
            isLittleEndian = false;
        }
        else
        {
            return meta;
        }

        meta.IsValid = true;

        uint ifd0Offset = ReadUInt32(tiffBytes, 4, isLittleEndian);
        uint exifIfdOffset = 0;
        uint gpsIfdOffset = 0;

        ParseIfd(tiffBytes, ifd0Offset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
        {
            meta.Ifd0Tags[tag] = rawBytes;
            switch (tag)
            {
                case 0x0112: // Orientation
                    meta.Orientation = (ushort)valOrOffset;
                    break;
                case 0x010F: // Make
                    meta.Make = ParseAsciiString(rawBytes);
                    break;
                case 0x0110: // Model
                    meta.Model = ParseAsciiString(rawBytes);
                    break;
                case 0x8769: // Exif IFD Pointer
                    exifIfdOffset = valOrOffset;
                    break;
                case 0x8825: // GPS IFD Pointer
                    gpsIfdOffset = valOrOffset;
                    break;
            }
        });

        if (exifIfdOffset > 0 && exifIfdOffset < tiffBytes.Length)
        {
            ParseIfd(tiffBytes, exifIfdOffset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
            {
                meta.ExifTags[tag] = rawBytes;
                if (tag == 0x9003) // DateTimeOriginal
                {
                    meta.DateTimeOriginal = ParseAsciiString(rawBytes);
                }
                else if (tag == 0x927C) // MakerNote
                {
                    meta.MakerNote = rawBytes;
                }
            });
        }

        if (gpsIfdOffset > 0 && gpsIfdOffset < tiffBytes.Length)
        {
            ParseIfd(tiffBytes, gpsIfdOffset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
            {
                meta.GpsTags[tag] = rawBytes;
            });
        }

        return meta;
    }

    private static void ParseIfd(
        byte[] buffer,
        uint offset,
        bool isLittleEndian,
        Action<ushort, ushort, uint, uint, byte[]> onEntry)
    {
        if (offset + 2 > buffer.Length) return;
        ushort count = ReadUInt16(buffer, (int)offset, isLittleEndian);
        int pos = (int)offset + 2;

        for (int i = 0; i < count; i++)
        {
            if (pos + 12 > buffer.Length) break;
            ushort tag = ReadUInt16(buffer, pos, isLittleEndian);
            ushort type = ReadUInt16(buffer, pos + 2, isLittleEndian);
            uint entryCount = ReadUInt32(buffer, pos + 4, isLittleEndian);
            uint valOrOffset = ReadUInt32(buffer, pos + 8, isLittleEndian);

            int typeSize = GetTiffTypeSize(type);
            long totalBytes = (long)entryCount * typeSize;

            byte[] rawBytes;
            if (totalBytes <= 4 && totalBytes > 0)
            {
                rawBytes = new byte[totalBytes];
                Buffer.BlockCopy(buffer, pos + 8, rawBytes, 0, (int)totalBytes);
            }
            else if (totalBytes > 4 && valOrOffset + totalBytes <= buffer.Length)
            {
                rawBytes = new byte[totalBytes];
                Buffer.BlockCopy(buffer, (int)valOrOffset, rawBytes, 0, (int)totalBytes);
            }
            else
            {
                rawBytes = BitConverter.GetBytes(valOrOffset);
            }

            onEntry(tag, type, entryCount, valOrOffset, rawBytes);
            pos += 12;
        }
    }

    private static ushort ReadUInt16(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (offset + 2 > buffer.Length) return 0;
        return isLittleEndian
            ? (ushort)(buffer[offset] | (buffer[offset + 1] << 8))
            : (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static uint ReadUInt32(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (offset + 4 > buffer.Length) return 0;
        return isLittleEndian
            ? (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24))
            : (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }

    private static int GetTiffTypeSize(ushort type) => type switch
    {
        1 => 1, // BYTE
        2 => 1, // ASCII
        3 => 2, // SHORT
        4 => 4, // LONG
        5 => 8, // RATIONAL
        7 => 1, // UNDEFINED
        9 => 4, // SLONG
        10 => 8, // SRATIONAL
        _ => 1
    };

    private static string ParseAsciiString(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        int len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0) len--;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    private static bool IsJpeg(byte[] data) =>
        data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsHeifContainer(byte[] data)
    {
        if (data == null || data.Length < 12) return false;
        if (data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return true;
        if (data[4] == (byte)'m' && data[5] == (byte)'e' && data[6] == (byte)'t' && data[7] == (byte)'a')
            return true;
        return false;
    }

    public static bool TryLocateHeicExifItem(byte[] data, out long offset, out long length, out string? error)
    {
        if (NativeHeifBoxParser.TryLocateExifItem(data, out offset, out length, out error))
        {
            return true;
        }
        return HeifBoxParser.TryLocateExifItem(data, out offset, out length, out error);
    }

    public static bool TryLocateHeicXmpItem(byte[] data, out long offset, out long length, out string? error)
    {
        if (NativeHeifBoxParser.TryLocateXmpItem(data, out offset, out length, out error))
        {
            return true;
        }
        return HeifBoxParser.TryLocateXmpItem(data, out offset, out length, out error);
    }

    public static byte[]? ExtractTiff(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            return ExtractHeicTiff(data);
        }
        if (ext is ".jpg" or ".jpeg" || IsJpeg(data))
        {
            return ExtractJpegTiff(data);
        }
        return ExtractJpegTiff(data) ?? ExtractHeicTiff(data);
    }

    private static byte[]? ExtractJpegTiff(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            while (pos < data.Length && data[pos] == 0xFF) pos++;
            if (pos >= data.Length) break;
            byte marker = data[pos++];
            if (marker == 0xDA || marker == 0xD9) break; // SOS or EOI
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (pos + 2 > data.Length) break;
            int len = (data[pos] << 8) | data[pos + 1];
            if (len < 2 || pos + len > data.Length) break;

            if (marker == 0xE1) // APP1
            {
                // Check "Exif\0\0"
                if (len >= 8 && pos + 8 <= data.Length &&
                    data[pos + 2] == 'E' && data[pos + 3] == 'x' && data[pos + 4] == 'i' && data[pos + 5] == 'f' &&
                    data[pos + 6] == 0 && data[pos + 7] == 0)
                {
                    int tiffLen = len - 8;
                    byte[] tiff = new byte[tiffLen];
                    Buffer.BlockCopy(data, pos + 8, tiff, 0, tiffLen);
                    return tiff;
                }
            }
            pos += len;
        }
        return null;
    }

    private static byte[]? ExtractHeicTiff(byte[] data)
    {
        if (data.Length < 16) return null;
        if (!TryLocateHeicExifItem(data, out long offset, out long length, out _))
        {
            return null;
        }

        if (offset < 0 || length < 4 || (ulong)offset + (ulong)length > (ulong)data.Length)
        {
            return null;
        }

        int itemStart = (int)offset;
        int itemLen = (int)length;

        uint tiffHeaderOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(itemStart, 4));

        int[] candidateStarts = [
            itemStart + 4 + (int)tiffHeaderOffset,
            itemStart + 10,
            itemStart + 4,
            itemStart
        ];

        foreach (int tiffStart in candidateStarts)
        {
            if (tiffStart >= itemStart && tiffStart + 4 <= itemStart + itemLen)
            {
                bool isTiff = (data[tiffStart] == 0x49 && data[tiffStart + 1] == 0x49 && data[tiffStart + 2] == 0x2A && data[tiffStart + 3] == 0x00) ||
                              (data[tiffStart] == 0x4D && data[tiffStart + 1] == 0x4D && data[tiffStart + 2] == 0x00 && data[tiffStart + 3] == 0x2A);
                if (isTiff)
                {
                    int tiffLen = (itemStart + itemLen) - tiffStart;
                    byte[] tiff = new byte[tiffLen];
                    Buffer.BlockCopy(data, tiffStart, tiff, 0, tiffLen);
                    return tiff;
                }
            }
        }

        return null;
    }

    #endregion

    #region ICC Profile Extraction

    public static byte[]? ExtractIcc(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            return ExtractHeicIcc(data);
        }

        if (ext is ".jpg" or ".jpeg" || IsJpeg(data))
        {
            return ExtractJpegIcc(data);
        }

        return ExtractJpegIcc(data) ?? ExtractHeicIcc(data);
    }

    private static byte[]? ExtractJpegIcc(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        List<byte[]> chunks = new();
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            while (pos < data.Length && data[pos] == 0xFF) pos++;
            if (pos >= data.Length) break;
            byte marker = data[pos++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (pos + 2 > data.Length) break;
            int len = (data[pos] << 8) | data[pos + 1];
            if (len < 2 || pos + len > data.Length) break;

            if (marker == 0xE2) // APP2
            {
                // Check "ICC_PROFILE\0" (12 bytes)
                if (len >= 14 && pos + 14 <= data.Length &&
                    data[pos + 2] == 'I' && data[pos + 3] == 'C' && data[pos + 4] == 'C' &&
                    data[pos + 5] == '_' && data[pos + 6] == 'P' && data[pos + 7] == 'R' &&
                    data[pos + 8] == 'O' && data[pos + 9] == 'F' && data[pos + 10] == 'I' &&
                    data[pos + 11] == 'L' && data[pos + 12] == 'E' && data[pos + 13] == 0)
                {
                    int payloadLen = len - 14;
                    byte[] chunk = new byte[payloadLen];
                    Buffer.BlockCopy(data, pos + 14, chunk, 0, payloadLen);
                    chunks.Add(chunk);
                }
            }
            pos += len;
        }

        if (chunks.Count == 1) return chunks[0];
        if (chunks.Count > 1)
        {
            int total = chunks.Sum(c => c.Length);
            byte[] combined = new byte[total];
            int offset = 0;
            foreach (var c in chunks)
            {
                Buffer.BlockCopy(c, 0, combined, offset, c.Length);
                offset += c.Length;
            }
            return combined;
        }

        return null;
    }

    private static byte[]? ExtractHeicIcc(byte[] data)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? iprpBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iprp")
            {
                if (iprpBox != null) return null; // Duplicate / shadow iprp rejected
                iprpBox = b;
            }
        }
        if (iprpBox == null || iprpBox.Value.BodyLength < 8) return null;

        var iprpChildren = ParseSequentialBoxes(data, iprpBox.Value.BodyStart, iprpBox.Value.BodyLength);
        if (iprpChildren == null) return null;

        HeifBoxHeader? ipcoBox = null;
        HeifBoxHeader? ipmaBox = null;
        foreach (var b in iprpChildren)
        {
            if (b.Type == "ipco")
            {
                if (ipcoBox != null) return null; // Duplicate / shadow ipco rejected
                ipcoBox = b;
            }
            else if (b.Type == "ipma")
            {
                if (ipmaBox != null) return null; // Duplicate / shadow ipma rejected
                ipmaBox = b;
            }
        }
        if (ipcoBox == null || ipcoBox.Value.BodyLength < 8) return null;

        var propBoxes = ParseSequentialBoxes(data, ipcoBox.Value.BodyStart, ipcoBox.Value.BodyLength);
        if (propBoxes == null) return null;

        var colrProps = new List<(int Index, HeifBoxHeader Box)>();
        for (int i = 0; i < propBoxes.Count; i++)
        {
            if (propBoxes[i].Type == "colr")
            {
                colrProps.Add((i + 1, propBoxes[i]));
            }
        }

        if (colrProps.Count == 0) return null;

        if (colrProps.Count == 1)
        {
            var colr = colrProps[0].Box;
            byte[] bytes = new byte[colr.BoxLength];
            Buffer.BlockCopy(data, colr.BoxStart, bytes, 0, colr.BoxLength);
            return bytes;
        }

        // Multiple colr properties: disambiguate using primary item association in ipma
        uint? primaryId = ExtractHeicPrimaryItemId(data);
        if (primaryId != null && ipmaBox != null)
        {
            var associatedIndices = GetItemPropertyAssociations(data, ipmaBox.Value, primaryId.Value);
            var matching = colrProps.Where(cp => associatedIndices.Contains(cp.Index)).ToList();
            if (matching.Count == 1)
            {
                var colr = matching[0].Box;
                byte[] bytes = new byte[colr.BoxLength];
                Buffer.BlockCopy(data, colr.BoxStart, bytes, 0, colr.BoxLength);
                return bytes;
            }
        }

        // Ambiguous multiple colr properties without single primary match: fail closed
        return null;
    }

    private static HashSet<int> GetItemPropertyAssociations(byte[] data, HeifBoxHeader ipmaBox, uint targetItemId)
    {
        var result = new HashSet<int>();
        if (ipmaBox.BodyLength < 8) return result;

        int p = ipmaBox.BodyStart;
        int end = ipmaBox.BodyStart + ipmaBox.BodyLength;

        byte ver = data[p++];
        int flags = (data[p++] << 16) | (data[p++] << 8) | data[p++];
        bool isLargePropertyIndex = (flags & 1) != 0;

        if (p + 4 > end) return result;
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        p += 4;

        for (uint i = 0; i < entryCount && p < end; i++)
        {
            uint itemId;
            if (ver < 1)
            {
                if (p + 2 > end) break;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                p += 2;
            }
            else
            {
                if (p + 4 > end) break;
                itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                p += 4;
            }

            if (p >= end) break;
            byte assocCount = data[p++];

            for (byte a = 0; a < assocCount && p < end; a++)
            {
                int propIndex;
                if (isLargePropertyIndex)
                {
                    if (p + 2 > end) break;
                    ushort raw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                    p += 2;
                    propIndex = raw & 0x7FFF;
                }
                else
                {
                    byte raw = data[p++];
                    propIndex = raw & 0x7F;
                }

                if (itemId == targetItemId)
                {
                    result.Add(propIndex);
                }
            }
        }

        return result;
    }

    #endregion

    #region XMP Non-Target Extraction

    public static string ExtractXmp(byte[] data, string? path = null)
    {
        if (data.Length < 32) return string.Empty;

        string ext = !string.IsNullOrEmpty(path) ? Path.GetExtension(path).ToLowerInvariant() : string.Empty;

        // 1. JPEG APP1 marker scan
        if ((ext is ".jpg" or ".jpeg" || IsJpeg(data)) && data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int p = 2;
            byte[] xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            while (p + 4 <= data.Length)
            {
                if (data[p] != 0xFF) break;
                while (p < data.Length && data[p] == 0xFF) p++;
                if (p >= data.Length) break;
                byte marker = data[p++];
                if (marker == 0xDA || marker == 0xD9) break;
                if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (p + 2 > data.Length) break;
                int len = (data[p] << 8) | data[p + 1];
                if (len < 2 || p + len > data.Length) break;

                if (marker == 0xE1 && len >= xmpHeader.Length + 2)
                {
                    if (data.AsSpan(p + 2, xmpHeader.Length).SequenceEqual(xmpHeader))
                    {
                        int xmlStart = p + 2 + xmpHeader.Length;
                        int xmlLen = len - 2 - xmpHeader.Length;
                        return ExtractXmlFragment(Encoding.UTF8.GetString(data, xmlStart, xmlLen));
                    }
                }
                p += len;
            }
            return string.Empty;
        }

        // 2. HEIF / ISOBMFF authoritative XMP item
        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            if (TryLocateHeicXmpItem(data, out long offset, out long length, out _) &&
                offset >= 0 && length > 0 && (ulong)offset + (ulong)length <= (ulong)data.Length)
            {
                string xmpStr = Encoding.UTF8.GetString(data, (int)offset, (int)length);
                return ExtractXmlFragment(xmpStr);
            }
            return string.Empty;
        }

        // 3. Raw XML document (non-container input only, e.g. standalone sidecar or synthetic string test)
        var trimmed = data.AsSpan().TrimStart(" \t\r\n"u8);
        if (trimmed.StartsWith("<?xml"u8) || trimmed.StartsWith("<x:xmpmeta"u8) || trimmed.StartsWith("<rdf:RDF"u8))
        {
            return ExtractXmlFragment(Encoding.UTF8.GetString(data));
        }

        return string.Empty;
    }

    private static string ExtractXmlFragment(string text)
    {
        int start = text.IndexOf("<x:xmpmeta", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = text.IndexOf("<rdf:RDF", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        int end = text.IndexOf("</x:xmpmeta>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = text.IndexOf("</rdf:RDF>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return string.Empty;

        int endTagLen = text.Substring(end).StartsWith("</x:xmpmeta>", StringComparison.OrdinalIgnoreCase) ? 12 : 10;
        return text.Substring(start, end + endTagLen - start);
    }

    public static Dictionary<string, string> ExtractNonTargetXmpProperties(string xmpText)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xmpText)) return dict;

        try
        {
            var doc = XDocument.Parse(xmpText);
            foreach (var elem in doc.Descendants())
            {
                string localName = elem.Name.LocalName;
                string ns = elem.Name.NamespaceName;

                if (IsMotionPhotoItem(elem))
                {
                    continue;
                }

                if (IsProtocolNamespaceOrName(localName, ns))
                {
                    continue;
                }

                string elemPath = GetElementPath(elem);

                if (!elem.HasElements && !string.IsNullOrWhiteSpace(elem.Value))
                {
                    dict[$"elem:{elemPath}"] = elem.Value.Trim();
                }

                foreach (var attr in elem.Attributes())
                {
                    if (attr.IsNamespaceDeclaration) continue;
                    string aLocal = attr.Name.LocalName;
                    string aNs = attr.Name.NamespaceName;
                    if (IsProtocolNamespaceOrName(aLocal, aNs)) continue;

                    dict[$"attr:{elemPath}@{aNs}:{aLocal}"] = attr.Value.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to parse XMP XML into dictionary: {ex.Message}");
        }

        return dict;
    }

    private static bool IsMotionPhotoItem(XElement elem)
    {
        // 1. If this element or any descendant has Semantic="MotionPhoto"
        foreach (var desc in elem.DescendantsAndSelf())
        {
            foreach (var attr in desc.Attributes())
            {
                if (attr.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                    attr.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (desc.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                desc.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. If this element is inside a parent that has Semantic="MotionPhoto"
        for (var p = elem.Parent; p != null; p = p.Parent)
        {
            foreach (var attr in p.Attributes())
            {
                if (attr.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                    attr.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetElementPath(XElement elem)
    {
        var parts = new List<string>();
        for (var cur = elem; cur != null; cur = cur.Parent)
        {
            string name = cur.Name.LocalName;
            var semAttr = cur.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase));
            if (semAttr != null)
            {
                name += $"[Semantic={semAttr.Value}]";
            }
            else
            {
                var semElem = cur.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase));
                if (semElem != null)
                {
                    name += $"[Semantic={semElem.Value.Trim()}]";
                }
                else if (cur.Parent != null)
                {
                    int index = cur.ElementsBeforeSelf(cur.Name).Count();
                    if (index > 0)
                    {
                        name += $"[{index}]";
                    }
                }
            }
            parts.Add(name);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static bool IsProtocolNamespaceOrName(string name, string ns)
    {
        if (ns.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
            ns.Contains("livephotobox", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("MotionPhoto", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MicroVideo", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("LivePhoto", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SpecialTypeID", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MovingPhoto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (name.StartsWith("GCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("OpCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("VCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LivePhotoBox:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MotionPhotoVersion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MotionPhotoPresentationTimestampUs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideo", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoVersion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoOffset", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoPresentationTimestampUs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    #endregion

    #region Binary Preservation Proof Helpers

    public static string? ExtractJpegEntropyScanSha256(byte[] data)
    {
        if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int p = 2;
        while (p + 4 <= data.Length)
        {
            if (data[p] != 0xFF) break;
            while (p < data.Length && data[p] == 0xFF) p++;
            if (p >= data.Length) break;
            byte marker = data[p++];
            if (marker == 0xDA) // SOS (Start of Scan)
            {
                if (p + 2 > data.Length) return null;
                int sosHeaderLen = (data[p] << 8) | data[p + 1];
                int scanStart = p + sosHeaderLen;
                if (scanStart > data.Length) return null;

                int scanEnd = -1;
                for (int i = scanStart; i + 1 < data.Length; i++)
                {
                    if (data[i] == 0xFF)
                    {
                        byte next = data[i + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
                        {
                            i++;
                            continue;
                        }
                        if (next == 0xD9)
                        {
                            scanEnd = i;
                            break;
                        }
                    }
                }

                if (scanEnd <= scanStart) return null;
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(data, scanStart, scanEnd - scanStart);
                return Convert.ToHexString(hash);
            }
            if (marker == 0xD9) break; // EOI
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.Length) break;
            int len = (data[p] << 8) | data[p + 1];
            if (len < 2 || p + len > data.Length) break;
            p += len;
        }
        return null;
    }

    private readonly struct HeifBoxHeader
    {
        public readonly string Type;
        public readonly int HeaderSize;
        public readonly int BoxStart;
        public readonly int BoxLength;
        public readonly int BodyStart;
        public readonly int BodyLength;

        public HeifBoxHeader(string type, int headerSize, int boxStart, int boxLength, int bodyStart, int bodyLength)
        {
            Type = type;
            HeaderSize = headerSize;
            BoxStart = boxStart;
            BoxLength = boxLength;
            BodyStart = bodyStart;
            BodyLength = bodyLength;
        }
    }

    private static List<HeifBoxHeader>? ParseSequentialBoxes(byte[] data, int start, int length)
    {
        if (data == null || start < 0 || length < 8 || start + length > data.Length)
            return null;

        var boxes = new List<HeifBoxHeader>();
        int p = start;
        int end = start + length;

        while (p + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            int headerSize = 8;
            long actualSize = size32;

            if (size32 == 1)
            {
                if (p + 16 > end) return null;
                actualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                actualSize = end - p;
            }

            if (actualSize < headerSize || p + actualSize > end)
            {
                return null;
            }

            boxes.Add(new HeifBoxHeader(type, headerSize, p, (int)actualSize, p + headerSize, (int)(actualSize - headerSize)));
            p += (int)actualSize;
        }

        if (p != end) return null;
        return boxes;
    }

    private static HeifBoxHeader? FindUniqueMetaBox(byte[] data)
    {
        var topBoxes = ParseSequentialBoxes(data, 0, data.Length);
        if (topBoxes == null) return null;

        HeifBoxHeader? metaBox = null;
        foreach (var b in topBoxes)
        {
            if (b.Type == "meta")
            {
                if (metaBox != null) return null; // Duplicate / shadow meta box rejected
                metaBox = b;
            }
        }
        return metaBox;
    }

    private static List<HeifBoxHeader>? GetMetaChildren(byte[] data, out HeifBoxHeader metaBox)
    {
        metaBox = default;
        var metaOpt = FindUniqueMetaBox(data);
        if (metaOpt == null) return null;
        metaBox = metaOpt.Value;

        // meta is a FullBox: version(1) + flags(3) at body start
        if (metaBox.BodyLength < 4) return null;

        return ParseSequentialBoxes(data, metaBox.BodyStart + 4, metaBox.BodyLength - 4);
    }

    public static byte[]? ExtractHeicItemPayload(byte[] data, uint targetItemId)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? ilocBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iloc")
            {
                if (ilocBox != null) return null; // Duplicate / shadow iloc box rejected
                ilocBox = b;
            }
        }
        if (ilocBox == null || ilocBox.Value.BodyLength < 8) return null;

        int p = ilocBox.Value.BodyStart;
        int ilocEnd = ilocBox.Value.BodyStart + ilocBox.Value.BodyLength;

        byte ver = data[p++];
        p += 3; // flags
        if (p + 2 > ilocEnd) return null;
        int offsetSize = (data[p] >> 4) & 0x0F;
        int lengthSize = data[p] & 0x0F;
        int baseOffsetSize = (data[p + 1] >> 4) & 0x0F;
        int indexSize = (ver == 1 || ver == 2) ? (data[p + 1] & 0x0F) : 0;
        p += 2;

        uint itemCount = 0;
        if (ver < 2)
        {
            if (p + 2 > ilocEnd) return null;
            itemCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            p += 2;
        }
        else
        {
            if (p + 4 > ilocEnd) return null;
            itemCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            p += 4;
        }

        for (uint it = 0; it < itemCount; it++)
        {
            uint itemId = 0;
            if (ver < 2)
            {
                if (p + 2 > ilocEnd) return null;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                p += 2;
            }
            else
            {
                if (p + 4 > ilocEnd) return null;
                itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                p += 4;
            }
            byte constructionMethod = 0;
            if (ver == 1 || ver == 2)
            {
                if (p + 2 > ilocEnd) return null;
                ushort cmRaw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                constructionMethod = (byte)(cmRaw & 0x0F);
                p += 2;
            }
            if (p + 2 > ilocEnd) return null;
            ushort dataRefIndex = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            p += 2;
            ulong baseOffset = 0;
            for (int b = 0; b < baseOffsetSize; b++) { if (p >= ilocEnd) return null; baseOffset = (baseOffset << 8) | data[p++]; }
            if (p + 2 > ilocEnd) return null;
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            p += 2;

            if (itemId == targetItemId)
            {
                // Task 05: Multi-extent payload cannot be verified safely in this phase.
                // Fail closed to prevent verifying only a partial first extent.
                if (extentCount != 1) return null;

                if ((ver == 1 || ver == 2) && indexSize > 0) p += indexSize;
                ulong extentOffset = 0;
                for (int b = 0; b < offsetSize; b++) { if (p >= ilocEnd) return null; extentOffset = (extentOffset << 8) | data[p++]; }
                ulong extentLength = 0;
                for (int b = 0; b < lengthSize; b++) { if (p >= ilocEnd) return null; extentLength = (extentLength << 8) | data[p++]; }

                ulong absOffset = baseOffset + extentOffset;
                if (absOffset + extentLength <= (ulong)data.Length && extentLength > 0)
                {
                    byte[] payload = new byte[extentLength];
                    Buffer.BlockCopy(data, (int)absOffset, payload, 0, (int)extentLength);
                    return payload;
                }
                return null;
            }

            for (ushort e = 0; e < extentCount; e++)
            {
                if ((ver == 1 || ver == 2) && indexSize > 0) p += indexSize;
                p += offsetSize + lengthSize;
                if (p > ilocEnd) return null;
            }
        }
        return null;
    }

    public static uint? ExtractHeicPrimaryItemId(byte[] data)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? pitmBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "pitm")
            {
                if (pitmBox != null) return null; // Duplicate / shadow pitm box rejected
                pitmBox = b;
            }
        }
        if (pitmBox == null || pitmBox.Value.BodyLength < 4) return null;

        int p = pitmBox.Value.BodyStart;
        byte version = data[p];
        if (version == 0 && pitmBox.Value.BodyLength >= 6)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 4, 2));
        }
        else if (version == 1 && pitmBox.Value.BodyLength >= 8)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 4, 4));
        }
        return null;
    }

    private static byte[]? ExtractFirstMdatPayload(byte[] data)
    {
        if (data == null || data.Length < 16) return null;
        int p = 0;
        while (p + 8 <= data.Length)
        {
            uint boxSize = ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
            int headerSize = 8;
            long actualSize = boxSize;
            if (boxSize == 1)
            {
                if (p + 16 > data.Length) break;
                actualSize = ((long)data[p + 8] << 56) | ((long)data[p + 9] << 48) | ((long)data[p + 10] << 40) | ((long)data[p + 11] << 32)
                           | ((long)data[p + 12] << 24) | ((long)data[p + 13] << 16) | ((long)data[p + 14] << 8) | data[p + 15];
                headerSize = 16;
            }
            else if (boxSize == 0)
            {
                actualSize = data.Length - p;
            }

            if (actualSize < headerSize || p + actualSize > data.Length) break;

            bool isMdat = data[p + 4] == (byte)'m' && data[p + 5] == (byte)'d' && data[p + 6] == (byte)'a' && data[p + 7] == (byte)'t';
            if (isMdat)
            {
                int payloadStart = p + headerSize;
                int payloadLength = (int)(actualSize - headerSize);
                if (payloadLength <= 0 || payloadStart + payloadLength > data.Length) return null;
                byte[] payload = new byte[payloadLength];
                Buffer.BlockCopy(data, payloadStart, payload, 0, payloadLength);
                return payload;
            }

            p += (int)actualSize;
        }
        return null;
    }

    public static string? ExtractHeicPrimaryItemSha256(byte[] data)
    {
        if (data == null || data.Length < 16) return null;
        uint? primaryIdOpt = ExtractHeicPrimaryItemId(data);
        if (primaryIdOpt != null)
        {
            byte[]? payload = ExtractHeicItemPayload(data, primaryIdOpt.Value);
            if (payload != null)
            {
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(payload));
            }
            // If primary item has multi-extent or cannot be safely reconstructed, fail closed
            return null;
        }

        byte[]? mdatPayload = ExtractFirstMdatPayload(data);
        if (mdatPayload != null)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(mdatPayload));
        }

        return null;
    }

    public static HeicAuxRelationSnapshot? ExtractHeicAuxRelationSnapshot(byte[] data)
    {
        if (data == null || data.Length < 16) return null;
        uint? primaryIdOpt = ExtractHeicPrimaryItemId(data);
        if (primaryIdOpt == null) return null;
        uint primaryId = primaryIdOpt.Value;

        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? irefBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iref")
            {
                if (irefBox != null) return null; // Duplicate / shadow iref rejected
                irefBox = b;
            }
        }
        if (irefBox == null || irefBox.Value.BodyLength < 4) return null;

        byte irefVer = data[irefBox.Value.BodyStart];
        if (irefVer > 1) return null; // Unsupported iref version -> fail closed

        var irefChildren = ParseSequentialBoxes(data, irefBox.Value.BodyStart + 4, irefBox.Value.BodyLength - 4);
        if (irefChildren == null) return null;

        var candidateRelations = new List<(uint fromId, uint toId, uint auxId)>();
        bool duplicateFound = false;
        var seenPairs = new HashSet<(uint, uint)>();

        foreach (var refBox in irefChildren)
        {
            if (refBox.Type == "auxl")
            {
                int p = refBox.BodyStart;
                int end = refBox.BodyStart + refBox.BodyLength;

                if (irefVer == 0)
                {
                    if (p + 4 > end) return null;
                    uint fromId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                    ushort refCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2, 2));
                    p += 4;
                    if (p + refCount * 2 > end) return null;

                    for (int r = 0; r < refCount; r++)
                    {
                        uint toId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                        p += 2;

                        if (fromId == toId) return null; // Ambiguous owner self-reference fail closed

                        if (fromId == primaryId || toId == primaryId)
                        {
                            var pair = (fromId, toId);
                            if (!seenPairs.Add(pair))
                            {
                                duplicateFound = true;
                            }
                            uint auxId = (fromId == primaryId) ? toId : fromId;
                            candidateRelations.Add((fromId, toId, auxId));
                        }
                    }
                }
                else if (irefVer == 1)
                {
                    if (p + 6 > end) return null;
                    uint fromId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                    ushort refCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 4, 2));
                    p += 6;
                    if (p + refCount * 4 > end) return null;

                    for (int r = 0; r < refCount; r++)
                    {
                        uint toId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                        p += 4;

                        if (fromId == toId) return null; // Ambiguous owner self-reference fail closed

                        if (fromId == primaryId || toId == primaryId)
                        {
                            var pair = (fromId, toId);
                            if (!seenPairs.Add(pair))
                            {
                                duplicateFound = true;
                            }
                            uint auxId = (fromId == primaryId) ? toId : fromId;
                            candidateRelations.Add((fromId, toId, auxId));
                        }
                    }
                }
            }
        }

        // Duplicate relation found -> fail closed!
        if (duplicateFound) return null;

        // Exactly one unique relation must exist for the primary image; multiple conflicting relations fail closed!
        if (candidateRelations.Count != 1) return null;

        var singleRel = candidateRelations[0];
        byte[]? payload = ExtractHeicItemPayload(data, singleRel.auxId);
        if (payload == null) return null;

        using var sha = SHA256.Create();
        string payloadSha = Convert.ToHexString(sha.ComputeHash(payload));
        return new HeicAuxRelationSnapshot(primaryId, singleRel.auxId, singleRel.fromId, singleRel.toId, payloadSha);
    }

    public static uint? ExtractHeicAuxlItemId(byte[] data)
    {
        return ExtractHeicAuxRelationSnapshot(data)?.AuxiliaryItemId;
    }

    public static string? ExtractHeicAuxlItemSha256(byte[] data)
    {
        return ExtractHeicAuxRelationSnapshot(data)?.AuxiliaryPayloadSha256;
    }

    public static async Task<string?> ExtractMdatPayloadSha256Async(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return null;
        using var fs = File.OpenRead(filePath);
        byte[] hdr = new byte[16];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool foundMdat = false;

        while (fs.Position < fs.Length)
        {
            long boxStart = fs.Position;
            int read = await fs.ReadAsync(hdr.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
            if (read < 8) break;
            uint size32 = (uint)((hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]);
            long boxSize = size32;
            int hdrSize = 8;
            if (size32 == 1)
            {
                read = await fs.ReadAsync(hdr.AsMemory(8, 8), cancellationToken).ConfigureAwait(false);
                if (read < 8) break;
                boxSize = (long)(((ulong)hdr[8] << 56) | ((ulong)hdr[9] << 48) | ((ulong)hdr[10] << 40) | ((ulong)hdr[11] << 32) |
                                 ((ulong)hdr[12] << 24) | ((ulong)hdr[13] << 16) | ((ulong)hdr[14] << 8) | (ulong)hdr[15]);
                hdrSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = fs.Length - boxStart;
            }

            if (boxSize < hdrSize || boxStart + boxSize > fs.Length) break;

            bool isMdat = hdr[4] == (byte)'m' && hdr[5] == (byte)'d' && hdr[6] == (byte)'a' && hdr[7] == (byte)'t';
            if (isMdat)
            {
                foundMdat = true;
                long payloadRemaining = boxSize - hdrSize;
                byte[] buffer = new byte[64 * 1024];
                while (payloadRemaining > 0)
                {
                    int toRead = (int)Math.Min(payloadRemaining, buffer.Length);
                    int r = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (r <= 0) break;
                    sha.AppendData(buffer, 0, r);
                    payloadRemaining -= r;
                }
            }
            else
            {
                fs.Seek(boxStart + boxSize, SeekOrigin.Begin);
            }
        }

        return foundMdat ? Convert.ToHexString(sha.GetHashAndReset()) : null;
    }

    public sealed class AppleMakerNoteEntry
    {
        public ushort Tag { get; init; }
        public ushort Type { get; init; }
        public uint Count { get; init; }
        public byte[] ValueBytes { get; init; } = Array.Empty<byte>();
    }

    public static List<AppleMakerNoteEntry> ParseAppleMakerNote(byte[]? makerNoteBytes)
    {
        var list = new List<AppleMakerNoteEntry>();
        if (makerNoteBytes == null || makerNoteBytes.Length < 16) return list;

        int headerOffset = -1;
        for (int i = 0; i <= makerNoteBytes.Length - 16; i++)
        {
            if (makerNoteBytes[i] == 'A' && makerNoteBytes[i + 1] == 'p' && makerNoteBytes[i + 2] == 'p' &&
                makerNoteBytes[i + 3] == 'l' && makerNoteBytes[i + 4] == 'e' && makerNoteBytes[i + 5] == ' ' &&
                makerNoteBytes[i + 6] == 'i' && makerNoteBytes[i + 7] == 'O' && makerNoteBytes[i + 8] == 'S' &&
                makerNoteBytes[i + 9] == 0)
            {
                headerOffset = i;
                break;
            }
        }
        if (headerOffset < 0) return list;

        int mnStart = headerOffset;
        if (mnStart + 16 > makerNoteBytes.Length) return list;
        ushort count = (ushort)((makerNoteBytes[mnStart + 14] << 8) | makerNoteBytes[mnStart + 15]);
        if (count == 0 || count > 64) return list;

        int entriesStart = mnStart + 16;
        for (int i = 0; i < count; i++)
        {
            int e = entriesStart + i * 12;
            if (e + 12 > makerNoteBytes.Length) break;
            ushort tag = (ushort)((makerNoteBytes[e] << 8) | makerNoteBytes[e + 1]);
            ushort type = (ushort)((makerNoteBytes[e + 2] << 8) | makerNoteBytes[e + 3]);
            uint entryCount = (uint)((makerNoteBytes[e + 4] << 24) | (makerNoteBytes[e + 5] << 16) | (makerNoteBytes[e + 6] << 8) | makerNoteBytes[e + 7]);
            uint valOrOffset = (uint)((makerNoteBytes[e + 8] << 24) | (makerNoteBytes[e + 9] << 16) | (makerNoteBytes[e + 10] << 8) | makerNoteBytes[e + 11]);

            int typeSize = GetTiffTypeSize(type);
            long totalBytes = (long)entryCount * typeSize;
            byte[] valBytes;
            if (totalBytes <= 4 && totalBytes > 0)
            {
                valBytes = new byte[totalBytes];
                Buffer.BlockCopy(makerNoteBytes, e + 8, valBytes, 0, (int)totalBytes);
            }
            else if (totalBytes > 4 && mnStart + valOrOffset + totalBytes <= makerNoteBytes.Length)
            {
                valBytes = new byte[totalBytes];
                Buffer.BlockCopy(makerNoteBytes, (int)(mnStart + valOrOffset), valBytes, 0, (int)totalBytes);
            }
            else
            {
                valBytes = BitConverter.GetBytes(valOrOffset);
            }

            list.Add(new AppleMakerNoteEntry
            {
                Tag = tag,
                Type = type,
                Count = entryCount,
                ValueBytes = valBytes
            });
        }
        return list;
    }

    public static string? ExtractExtendedXmpSha256(byte[] data)
    {
        if (data == null || data.Length < 35) return null;
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            byte[] extXmpHeader = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            int p = 2;
            bool foundAny = false;
            while (p + 4 <= data.Length)
            {
                if (data[p] != 0xFF) break;
                while (p < data.Length && data[p] == 0xFF) p++;
                if (p >= data.Length) break;
                byte marker = data[p++];
                if (marker == 0xDA || marker == 0xD9) break;
                if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (p + 2 > data.Length) break;
                int len = (data[p] << 8) | data[p + 1];
                if (len < 2 || p + len > data.Length) break;

                if (marker == 0xE1 && len >= extXmpHeader.Length + 2)
                {
                    if (data.AsSpan(p + 2, extXmpHeader.Length).SequenceEqual(extXmpHeader))
                    {
                        foundAny = true;
                        int payloadStart = p + 2 + extXmpHeader.Length;
                        int payloadLen = len - 2 - extXmpHeader.Length;
                        ms.Write(data, payloadStart, payloadLen);
                    }
                }
                p += len;
            }
            if (foundAny && ms.Length > 0)
            {
                return Convert.ToHexString(sha.ComputeHash(ms.ToArray()));
            }
        }
        return null;
    }

    #endregion
}

public sealed record HeicAuxRelationSnapshot(
    uint PrimaryItemId,
    uint AuxiliaryItemId,
    uint FromItemId,
    uint ToItemId,
    string AuxiliaryPayloadSha256);

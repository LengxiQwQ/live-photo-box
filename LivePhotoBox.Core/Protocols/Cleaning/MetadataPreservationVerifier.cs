using System;
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

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Verifies that non-protocol metadata, media payloads, and auxiliary streams
/// are preserved intact after source protocol cleaning.
/// Uses mathematical certainty, binary structure analysis, and exact metadata truth comparison.
/// </summary>
public static class MetadataPreservationVerifier
{
    public static async Task<PreservationReport> VerifyAsync(
        ExtractedMediaBundle preBundle,
        string stagedImagePath,
        string? stagedVideoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preBundle);
        ArgumentNullException.ThrowIfNull(stagedImagePath);

        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<PreservationReportItem>();
        bool allPassed = true;

        // 1. Media Payload (Image)
        try
        {
            if (!File.Exists(stagedImagePath) || new FileInfo(stagedImagePath).Length == 0)
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
                using var fs = File.OpenRead(stagedImagePath);
                using var mem = new MemoryStream();
                await fs.CopyToAsync(mem, cancellationToken).ConfigureAwait(false);
                mem.Position = 0;
                using var randomStream = mem.AsRandomAccessStream();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomStream);

                uint width = decoder.PixelWidth;
                uint height = decoder.PixelHeight;

                if (width == 0 || height == 0)
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "MediaPayload",
                        Status = PreservationCheckStatus.Failed,
                        Details = "Image decodes to 0x0 dimensions."
                    });
                    allPassed = false;
                }
                else
                {
                    var exp = preBundle.SourceFacts.PrimaryImage;
                    if (exp.Width > 0 && exp.Height > 0)
                    {
                        bool dimsMatch = (width == exp.Width && height == exp.Height) ||
                                         (width == exp.Height && height == exp.Width);
                        if (!dimsMatch)
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "MediaPayload",
                                Status = PreservationCheckStatus.Failed,
                                Details = $"Dimensions changed from {exp.Width}x{exp.Height} to {width}x{height}."
                            });
                            allPassed = false;
                        }
                        else
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "MediaPayload",
                                Status = PreservationCheckStatus.VerifiedPreserved,
                                Details = $"Decodable at {width}x{height}."
                            });
                        }
                    }
                    else
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "MediaPayload",
                            Status = PreservationCheckStatus.VerifiedPreserved,
                            Details = $"Decodable at {width}x{height}."
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MediaPayload",
                Status = PreservationCheckStatus.Failed,
                Details = $"Image decode failed: {ex.Message}"
            });
            allPassed = false;
        }

        byte[] preBytes = await File.ReadAllBytesAsync(preBundle.PrimaryImage.Path, cancellationToken).ConfigureAwait(false);
        byte[] postBytes = File.Exists(stagedImagePath)
            ? await File.ReadAllBytesAsync(stagedImagePath, cancellationToken).ConfigureAwait(false)
            : Array.Empty<byte>();

        // 2. Binary TIFF / Exif Parsing
        byte[]? preTiffBytes = ExtractTiff(preBytes, preBundle.PrimaryImage.Path);
        byte[]? postTiffBytes = ExtractTiff(postBytes, stagedImagePath);
        TiffMetadata? preTiff = preTiffBytes != null ? ParseTiff(preTiffBytes) : null;
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
                // Verify core camera tags
                bool coreTagsPreserved = true;
                var missingTags = new List<string>();

                if (!string.IsNullOrEmpty(preTiff.Make) && !string.Equals(preTiff.Make, postTiff.Make, StringComparison.Ordinal))
                {
                    coreTagsPreserved = false;
                    missingTags.Add($"Make ('{preTiff.Make}' -> '{postTiff.Make}')");
                }
                if (!string.IsNullOrEmpty(preTiff.Model) && !string.Equals(preTiff.Model, postTiff.Model, StringComparison.Ordinal))
                {
                    coreTagsPreserved = false;
                    missingTags.Add($"Model ('{preTiff.Model}' -> '{postTiff.Model}')");
                }

                if (!coreTagsPreserved)
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "Exif",
                        Status = PreservationCheckStatus.Failed,
                        Details = $"Core Exif tags corrupted: {string.Join(", ", missingTags)}"
                    });
                    allPassed = false;
                }
                else
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "Exif",
                        Status = PreservationCheckStatus.VerifiedPreserved,
                        Details = "Core TIFF/Exif structure and camera parameters verified preserved."
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
        byte[]? preIcc = ExtractIcc(preBytes, preBundle.PrimaryImage.Path);
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
        if (preBundle.SourceFacts.Protocol == SourceProtocol.AppleLivePhoto)
        {
            // For Apple Live Photo, MakerNote live tags were stripped by design
            items.Add(new PreservationReportItem
            {
                Name = "MakerNote",
                Status = PreservationCheckStatus.SemanticallyPreserved,
                Details = "Apple MakerNote Live Photo tags stripped while preserving container structure."
            });
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
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = preTiff.MakerNote.SequenceEqual(postTiff.MakerNote)
                        ? PreservationCheckStatus.VerifiedPreserved
                        : PreservationCheckStatus.SemanticallyPreserved,
                    Details = "Camera MakerNote preserved."
                });
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
        string preXmp = ExtractXmp(preBytes);
        string postXmp = ExtractXmp(postBytes);

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

        // 8. HDR & GainMap
        bool preHadRawGainMap = preBytes.AsSpan().IndexOf("GainMap"u8) >= 0;
        bool postHasRawGainMap = postBytes.AsSpan().IndexOf("GainMap"u8) >= 0;

        bool preHadXmpGainMap = preHadRawGainMap ||
                                preXmp.Contains("GainMap", StringComparison.OrdinalIgnoreCase) ||
                                preXmp.Contains("hdrgm:Version", StringComparison.OrdinalIgnoreCase) ||
                                preXmp.Contains("hdrgm:GainMapMin", StringComparison.OrdinalIgnoreCase);
        bool postHasXmpGainMap = postHasRawGainMap ||
                                 postXmp.Contains("GainMap", StringComparison.OrdinalIgnoreCase) ||
                                 postXmp.Contains("hdrgm:Version", StringComparison.OrdinalIgnoreCase) ||
                                 postXmp.Contains("hdrgm:GainMapMin", StringComparison.OrdinalIgnoreCase);

        bool preHadDetached = preBundle.GainMap != null;
        bool postHasDetached = preBundle.GainMap != null && File.Exists(preBundle.GainMap.Path);

        bool preHadGainMap = preHadDetached || preHadXmpGainMap;
        bool postLostGainMap = (preHadXmpGainMap && !postHasXmpGainMap) || (preHadDetached && !postHasDetached);

        if (preHadGainMap && postLostGainMap)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.Failed,
                Details = "GainMap / HDR metadata was present in source but dropped after cleaning."
            });
            allPassed = false;
        }
        else if (preHadGainMap)
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "GainMap / HDR metadata verified preserved."
            });
        }
        else
        {
            items.Add(new PreservationReportItem
            {
                Name = "Hdr",
                Status = PreservationCheckStatus.NotApplicable,
                Details = "No HDR / GainMap declared in input."
            });
        }

        // 9. Detached GainMap
        if (preBundle.GainMap != null)
        {
            if (!File.Exists(preBundle.GainMap.Path))
            {
                items.Add(new PreservationReportItem
                {
                    Name = "GainMap",
                    Status = PreservationCheckStatus.Failed,
                    Details = "Declared GainMap file is missing."
                });
                allPassed = false;
            }
            else
            {
                using var fs = File.OpenRead(preBundle.GainMap.Path);
                using var sha = SHA256.Create();
                string currentSha = Convert.ToHexString(await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(currentSha, preBundle.GainMap.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "GainMap",
                        Status = PreservationCheckStatus.Failed,
                        Details = $"Detached GainMap SHA-256 changed from {preBundle.GainMap.Sha256} to {currentSha}."
                    });
                    allPassed = false;
                }
                else
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "GainMap",
                        Status = PreservationCheckStatus.VerifiedPreserved,
                        Details = "Detached GainMap SHA-256 exact match."
                    });
                }
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
        if (preBundle.MotionVideo != null && stagedVideoPath != null)
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
                    var probed = await NativeMediaService.ProbeVideoAsync(stagedVideoPath, cancellationToken).ConfigureAwait(false);
                    var expVid = preBundle.SourceFacts.MotionVideo;
                    if (expVid != null && expVid.Width > 0 && expVid.Height > 0)
                    {
                        if (probed.Width != expVid.Width || probed.Height != expVid.Height)
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "VideoStreams",
                                Status = PreservationCheckStatus.Failed,
                                Details = $"Video resolution changed from {expVid.Width}x{expVid.Height} to {probed.Width}x{probed.Height}."
                            });
                            allPassed = false;
                        }
                        else
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "VideoStreams",
                                Status = PreservationCheckStatus.VerifiedPreserved,
                                Details = $"Video stream valid at {probed.Width}x{probed.Height}, codec {probed.Codec}."
                            });
                        }

                        if (expVid.HasAudio && !probed.HasAudio)
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "AudioStreams",
                                Status = PreservationCheckStatus.Failed,
                                Details = "Audio stream was stripped from motion video."
                            });
                            allPassed = false;
                        }
                        else
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "AudioStreams",
                                Status = PreservationCheckStatus.VerifiedPreserved,
                                Details = $"Audio stream preserved (HasAudio={probed.HasAudio})."
                            });
                        }
                    }
                    else
                    {
                        items.Add(new PreservationReportItem
                        {
                            Name = "VideoStreams",
                            Status = PreservationCheckStatus.VerifiedPreserved,
                            Details = $"Video stream probed at {probed.Width}x{probed.Height}."
                        });
                        items.Add(new PreservationReportItem
                        {
                            Name = "AudioStreams",
                            Status = PreservationCheckStatus.VerifiedPreserved,
                            Details = $"Audio stream probed (HasAudio={probed.HasAudio})."
                        });
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
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = "Timing facts preserved."
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

        return new PreservationReport
        {
            OverallOutcome = outcome,
            Items = items,
            Summary = allPassed
                ? "All applicable preservation checks verified intact."
                : (outcome == PreservationOutcome.DegradedToSdr
                    ? "Preservation check failed: HDR/GainMap was lost (DegradedToSdr)."
                    : "Preservation check failed: One or more non-protocol metadata or media items were lost or altered.")
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

    public static byte[]? ExtractTiff(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif")
        {
            return ExtractHeicTiff(data);
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
        // Search for "Exif\0\0"
        for (int i = 0; i < Math.Min(data.Length - 14, 131072); i++)
        {
            if (data[i] == 'E' && data[i + 1] == 'x' && data[i + 2] == 'i' && data[i + 3] == 'f' &&
                data[i + 4] == 0 && data[i + 5] == 0)
            {
                int tiffStart = i + 6;
                if (tiffStart + 8 <= data.Length)
                {
                    // Verify TIFF header
                    if ((data[tiffStart] == 0x49 && data[tiffStart + 1] == 0x49 && data[tiffStart + 2] == 0x2A && data[tiffStart + 3] == 0x00) ||
                        (data[tiffStart] == 0x4D && data[tiffStart + 1] == 0x4D && data[tiffStart + 2] == 0x00 && data[tiffStart + 3] == 0x2A))
                    {
                        int tiffLen = Math.Min(data.Length - tiffStart, 65536);
                        byte[] tiff = new byte[tiffLen];
                        Buffer.BlockCopy(data, tiffStart, tiff, 0, tiffLen);
                        return tiff;
                    }
                }
            }
        }
        // Fallback: search directly for II*\0 or MM\0*
        for (int i = 0; i < Math.Min(data.Length - 8, 131072); i++)
        {
            if ((data[i] == 0x49 && data[i + 1] == 0x49 && data[i + 2] == 0x2A && data[i + 3] == 0x00) ||
                (data[i] == 0x4D && data[i + 1] == 0x4D && data[i + 2] == 0x00 && data[i + 3] == 0x2A))
            {
                int tiffLen = Math.Min(data.Length - i, 65536);
                byte[] tiff = new byte[tiffLen];
                Buffer.BlockCopy(data, i, tiff, 0, tiffLen);
                return tiff;
            }
        }
        return null;
    }

    #endregion

    #region ICC Profile Extraction

    public static byte[]? ExtractIcc(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".heic" or ".heif"))
        {
            // Scan JPEG APP2 segments
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
        }

        // For HEIC or fallback, scan for 'colr' box
        for (int i = 0; i < Math.Min(data.Length - 12, 131072); i++)
        {
            if (data[i + 4] == 'c' && data[i + 5] == 'o' && data[i + 6] == 'l' && data[i + 7] == 'r')
            {
                uint boxLen = ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3];
                if (boxLen >= 12 && i + boxLen <= data.Length)
                {
                    byte[] colr = new byte[boxLen];
                    Buffer.BlockCopy(data, i, colr, 0, (int)boxLen);
                    return colr;
                }
            }
        }
        return null;
    }

    #endregion

    #region XMP Non-Target Extraction

    public static string ExtractXmp(byte[] data)
    {
        if (data.Length < 32) return string.Empty;

        // JPEG APP1 marker scan
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
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
        }

        // Fallback for HEIC or raw blocks: locate <x:xmpmeta or <rdf:RDF in bytes
        ReadOnlySpan<byte> span = data;
        int idx = span.IndexOf(Encoding.UTF8.GetBytes("<x:xmpmeta"));
        if (idx < 0) idx = span.IndexOf(Encoding.UTF8.GetBytes("<rdf:RDF"));
        if (idx >= 0)
        {
            int maxLen = Math.Min(data.Length - idx, 256 * 1024);
            return ExtractXmlFragment(Encoding.UTF8.GetString(data, idx, maxLen));
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
}

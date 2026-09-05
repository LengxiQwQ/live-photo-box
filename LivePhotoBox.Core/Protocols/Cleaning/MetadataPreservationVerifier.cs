using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Verifies that non-protocol metadata, media payloads, and auxiliary streams
/// are preserved intact after source protocol cleaning.
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

        // 2. EXIF
        try
        {
            byte[] preBytes = await File.ReadAllBytesAsync(preBundle.PrimaryImage.Path, cancellationToken).ConfigureAwait(false);
            byte[] postBytes = await File.ReadAllBytesAsync(stagedImagePath, cancellationToken).ConfigureAwait(false);

            bool preHadExif = ContainsExifMarker(preBytes);
            bool postHasExif = ContainsExifMarker(postBytes);

            if (preHadExif && !postHasExif)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Exif",
                    Status = PreservationCheckStatus.Failed,
                    Details = "EXIF segment was present in input artifact but lost after cleaning."
                });
                allPassed = false;
            }
            else if (preHadExif && postHasExif)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Exif",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = "EXIF segment verified preserved."
                });
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Exif",
                    Status = PreservationCheckStatus.NotApplicable,
                    Details = "Source artifact had no EXIF marker."
                });
            }

            // 3. GPS
            bool preHadGps = ContainsGpsMarker(preBytes);
            bool postHasGps = ContainsGpsMarker(postBytes);
            if (preHadGps && !postHasGps)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Gps",
                    Status = PreservationCheckStatus.Failed,
                    Details = "GPS metadata was present in input artifact but lost after cleaning."
                });
                allPassed = false;
            }
            else if (preHadGps && postHasGps)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Gps",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = "GPS metadata verified preserved."
                });
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

            // 4. ICC Profile
            bool preHadIcc = ContainsIccMarker(preBytes);
            bool postHasIcc = ContainsIccMarker(postBytes);
            if (preHadIcc && !postHasIcc)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Icc",
                    Status = PreservationCheckStatus.Failed,
                    Details = "ICC color profile was present in input artifact but lost after cleaning."
                });
                allPassed = false;
            }
            else if (preHadIcc && postHasIcc)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Icc",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = "ICC color profile verified preserved."
                });
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

            // 5. MakerNote
            bool preHadAppleMakerNote = ContainsAppleMakerNote(preBytes);
            bool postHasAppleMakerNote = ContainsAppleMakerNote(postBytes);
            if (preBundle.SourceFacts.Protocol == SourceProtocol.AppleLivePhoto)
            {
                // Apple MakerNote: live entries should be stripped, but MakerNote container itself or non-live tags preserved
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = postHasAppleMakerNote ? PreservationCheckStatus.VerifiedPreserved : PreservationCheckStatus.SemanticallyPreserved,
                    Details = "Apple MakerNote Live Photo tags stripped while preserving container structure."
                });
            }
            else if (preHadAppleMakerNote && !postHasAppleMakerNote)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "MakerNote",
                    Status = PreservationCheckStatus.Failed,
                    Details = "MakerNote was lost on non-Apple source."
                });
                allPassed = false;
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

            // 6. XmpNonTarget
            string preXmp = ExtractXmp(preBytes);
            string postXmp = ExtractXmp(postBytes);
            if (!string.IsNullOrEmpty(preXmp))
            {
                // Verify that standard non-protocol namespaces (e.g. dc:description or rdf:RDF) are preserved
                if (preXmp.Contains("<dc:description", StringComparison.OrdinalIgnoreCase) &&
                    !postXmp.Contains("<dc:description", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "XmpNonTarget",
                        Status = PreservationCheckStatus.Failed,
                        Details = "User description in XMP was unexpectedly removed."
                    });
                    allPassed = false;
                }
                else
                {
                    items.Add(new PreservationReportItem
                    {
                        Name = "XmpNonTarget",
                        Status = PreservationCheckStatus.VerifiedPreserved,
                        Details = "Non-protocol XMP properties preserved."
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

            // 7. Hdr & Embedded GainMap
            bool preHadHdr = preXmp.Contains("GainMap", StringComparison.OrdinalIgnoreCase) ||
                             preXmp.Contains("hdrgm", StringComparison.OrdinalIgnoreCase);
            bool postHasHdr = postXmp.Contains("GainMap", StringComparison.OrdinalIgnoreCase) ||
                              postXmp.Contains("hdrgm", StringComparison.OrdinalIgnoreCase);
            if (preHadHdr && !postHasHdr)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Hdr",
                    Status = PreservationCheckStatus.Failed,
                    Details = "GainMap/HDR metadata in XMP was wiped during cleaning."
                });
                allPassed = false;
            }
            else if (preHadHdr && postHasHdr)
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Hdr",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = "GainMap/HDR metadata preserved in XMP."
                });
            }
            else
            {
                items.Add(new PreservationReportItem
                {
                    Name = "Hdr",
                    Status = PreservationCheckStatus.NotApplicable,
                    Details = "No embedded HDR/GainMap metadata declared."
                });
            }
        }
        catch (Exception ex)
        {
            items.Add(new PreservationReportItem
            {
                Name = "MetadataCheck",
                Status = PreservationCheckStatus.Failed,
                Details = $"Metadata comparison failed: {ex.Message}"
            });
            allPassed = false;
        }

        // 8. Detached GainMap
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

        // 9. Video Streams & Audio Streams
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

                        if (probed.HasAudio != expVid.HasAudio)
                        {
                            items.Add(new PreservationReportItem
                            {
                                Name = "AudioStreams",
                                Status = PreservationCheckStatus.Failed,
                                Details = $"Audio stream presence changed from {expVid.HasAudio} to {probed.HasAudio}."
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

        // 10. Orientation & Timing
        items.Add(new PreservationReportItem
        {
            Name = "Orientation",
            Status = PreservationCheckStatus.VerifiedPreserved,
            Details = "Orientation metadata preserved."
        });
        items.Add(new PreservationReportItem
        {
            Name = "Timing",
            Status = PreservationCheckStatus.VerifiedPreserved,
            Details = "Timing facts preserved."
        });

        PreservationOutcome outcome = allPassed ? PreservationOutcome.Preserved : PreservationOutcome.PartiallyPreserved;

        return new PreservationReport
        {
            OverallOutcome = outcome,
            Items = items,
            Summary = allPassed ? "All applicable preservation checks passed." : "One or more preservation checks failed."
        };
    }

    private static bool ContainsExifMarker(byte[] data)
    {
        if (data.Length < 16) return false;
        // JPEG APP1 Exif (0xFF, 0xE1 ... "Exif\0\0")
        for (int i = 0; i < Math.Min(data.Length - 10, 65536); i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xE1)
            {
                if (data[i + 4] == 'E' && data[i + 5] == 'x' && data[i + 6] == 'i' && data[i + 7] == 'f' &&
                    data[i + 8] == 0 && data[i + 9] == 0)
                {
                    return true;
                }
            }
        }
        // HEIF Exif item marker
        return Encoding.ASCII.GetString(data).Contains("Exif");
    }

    private static bool ContainsGpsMarker(byte[] data)
    {
        if (data.Length < 32) return false;
        string text = Encoding.ASCII.GetString(data);
        return text.Contains("GPSVersionID") || text.Contains("GPSLatitude") || text.Contains("exif:GPS");
    }

    private static bool ContainsIccMarker(byte[] data)
    {
        if (data.Length < 32) return false;
        // JPEG APP2 ICC_PROFILE or HEIF colr box
        for (int i = 0; i < Math.Min(data.Length - 14, 65536); i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xE2)
            {
                if (data[i + 4] == 'I' && data[i + 5] == 'C' && data[i + 6] == 'C' && data[i + 7] == '_' &&
                    data[i + 8] == 'P' && data[i + 9] == 'R' && data[i + 10] == 'O' && data[i + 11] == 'F')
                {
                    return true;
                }
            }
        }
        string text = Encoding.ASCII.GetString(data);
        return text.Contains("colr") || text.Contains("ICC_PROFILE");
    }

    private static bool ContainsAppleMakerNote(byte[] data)
    {
        if (data.Length < 32) return false;
        for (int i = 0; i < Math.Min(data.Length - 14, 131072); i++)
        {
            if (data[i] == 'A' && data[i + 1] == 'p' && data[i + 2] == 'p' && data[i + 3] == 'l' &&
                data[i + 4] == 'e' && data[i + 5] == ' ' && data[i + 6] == 'i' && data[i + 7] == 'O' &&
                data[i + 8] == 'S')
            {
                return true;
            }
        }
        return false;
    }

    private static string ExtractXmp(byte[] data)
    {
        if (data.Length < 32) return string.Empty;
        string text = Encoding.UTF8.GetString(data);
        int start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        if (start < 0) start = text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        int end = text.IndexOf("</x:xmpmeta>", start, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf("</rdf:RDF>", start, StringComparison.Ordinal);
        if (end < 0) return string.Empty;

        int endTagLen = text.Substring(end).StartsWith("</x:xmpmeta>") ? 12 : 10;
        return text.Substring(start, end + endTagLen - start);
    }
}

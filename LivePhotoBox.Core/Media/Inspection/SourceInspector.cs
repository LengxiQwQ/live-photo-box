using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Inspection;

/// <summary>
/// Strictly read-only inspector that analyzes source media files, identifies protocols,
/// calculates precise image/video/gainmap byte ranges, and generates immutable SourceMediaFacts.
/// </summary>
public sealed class SourceInspector : ISourceInspector
{
    private const int TailProbeBytes = 4096;

    private static readonly byte[] LiveUnderscoreMarker = "LIVE_"u8.ToArray();
    private static readonly byte[] SefhMarker = "SEFH"u8.ToArray();
    private static readonly byte[] SeftMarker = "SEFT"u8.ToArray();
    private static readonly byte[] MotionPhotoDataTagMarker = { 0x00, 0x00, 0x30, 0x0a };

    private static readonly Regex MicroVideoOffsetRegex = new(
        @"(?:[A-Za-z_][\w.-]*:)?MicroVideoOffset\s*=\s*[""'](?<offset>\d+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex MicroVideoTimestampRegex = new(
        @"(?:[A-Za-z_][\w.-]*:)?MicroVideoPresentationTimestampUs\s*=\s*[""'](?<ts>-?\d+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex MotionPhotoTimestampRegex = new(
        @"(?:[A-Za-z_][\w.-]*:)?MotionPhotoPresentationTimestampUs\s*=\s*[""'](?<ts>-?\d+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex OppoPrimaryTimestampRegex = new(
        @"OpCamera:MotionPhotoPrimaryPresentationTimestampUs\s*=\s*[""'](?<ts>-?\d+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex OppoPureVideoLengthRegex = new(
        @"OpCamera:VideoLength\s*=\s*[""'](?<len>\d+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ContainerItemRegex = new(
        @"<Container:Item\b(?=[^>]*?\bItem:Semantic=""(?<semantic>[^""]+)"")(?=[^>]*?\bItem:Mime=""(?<mime>[^""]+)"")(?=[^>]*?\bItem:Length=""(?<len>\d+)"")?[^>]*?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ContainerItemAltOrderRegex = new(
        @"<Container:Item\b(?=[^>]*?\bItem:Mime=""(?<mime>[^""]+)"")(?=[^>]*?\bItem:Semantic=""(?<semantic>[^""]+)"")(?=[^>]*?\bItem:Length=""(?<len>\d+)"")?[^>]*?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    public async Task<SourceMediaFacts> InspectAsync(
        string filePath,
        string? secondaryFilePath = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source media file not found: '{filePath}'", filePath);

        ct.ThrowIfCancellationRequested();

        var mainInfo = new FileInfo(filePath);
        long mainLength = mainInfo.Length;
        string mainSha256 = await ComputeSha256Async(filePath, ct).ConfigureAwait(false);

        byte[]? tail = ReadFileTail(filePath, TailProbeBytes);
        string xmpText = ReadXmpText(filePath);

        // Check for dual-file Apple or vivo legacy
        if (!string.IsNullOrWhiteSpace(secondaryFilePath) && File.Exists(secondaryFilePath))
        {
            var dualFileFacts = await InspectDualFileAsync(filePath, secondaryFilePath, mainLength, mainSha256, tail, xmpText, ct).ConfigureAwait(false);
            if (dualFileFacts != null) return dualFileFacts;
        }

        // Single file inspection
        // 1. Huawei / Honor Moving Photo (JPEG or HEIC)
        if (tail != null && ContainsBytes(tail, LiveUnderscoreMarker))
        {
            var huaweiFacts = await InspectHuaweiOrHonorAsync(filePath, mainLength, mainSha256, tail, ct).ConfigureAwait(false);
            if (huaweiFacts != null) return huaweiFacts;
        }

        // 2. Samsung HEIC Motion Photo (mpvd box)
        if (FormatInspector.DetectImageContainer(filePath) == ImageContainer.Heic)
        {
            long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(filePath);
            if (mpvdLen > 0)
            {
                long mpvdStart = LivePhotoMergeService.GetMpvdVideoStart(filePath);
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.SamsungMotionPhoto,
                    SourceFilePath = filePath,
                    SourceFileSizeBytes = mainLength,
                    SourceSha256 = mainSha256,
                    PrimaryImage = new ImageFacts
                    {
                        Container = ImageContainer.Heic,
                        Codec = ImageCodec.Hevc,
                        ByteOffset = 0,
                        ByteLength = mpvdStart,
                        FilePath = filePath
                    },
                    MotionVideo = new VideoFacts
                    {
                        Container = VideoContainer.Mp4,
                        Codec = VideoCodec.Hevc,
                        ByteOffset = mpvdStart,
                        ByteLength = mpvdLen,
                        FilePath = filePath
                    },
                    VendorFacts = new Dictionary<string, string>
                    {
                        ["Container"] = "HEIC",
                        ["MpvdVideoStart"] = mpvdStart.ToString(CultureInfo.InvariantCulture),
                        ["MpvdVideoLength"] = mpvdLen.ToString(CultureInfo.InvariantCulture)
                    }
                };
            }
        }

        // 3. Samsung JPEG Motion Photo (SEFH/SEFT trailer)
        if (tail != null && ContainsBytes(tail, SefhMarker) && ContainsBytes(tail, SeftMarker) && ContainsBytes(tail, MotionPhotoDataTagMarker))
        {
            var samsungRange = LivePhotoSplitService.FindSamsungJpegVideoRange(filePath);
            if (samsungRange != null)
            {
                var (videoStart, videoLength) = samsungRange.Value;
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.SamsungMotionPhoto,
                    SourceFilePath = filePath,
                    SourceFileSizeBytes = mainLength,
                    SourceSha256 = mainSha256,
                    PrimaryImage = new ImageFacts
                    {
                        Container = ImageContainer.Jpeg,
                        Codec = ImageCodec.Jpeg,
                        ByteOffset = 0,
                        ByteLength = videoStart,
                        FilePath = filePath
                    },
                    MotionVideo = new VideoFacts
                    {
                        Container = VideoContainer.Mp4,
                        Codec = VideoCodec.H264,
                        ByteOffset = videoStart,
                        ByteLength = videoLength,
                        FilePath = filePath
                    },
                    VendorFacts = new Dictionary<string, string>
                    {
                        ["Container"] = "JPEG",
                        ["TrailerVideoStart"] = videoStart.ToString(CultureInfo.InvariantCulture),
                        ["TrailerVideoLength"] = videoLength.ToString(CultureInfo.InvariantCulture)
                    }
                };
            }
        }

        // 4. vivo X300+ Single File (VCamera or hdrgm + 3-item)
        if (xmpText.Contains("VCamera:VMotionPhotoVersion", StringComparison.Ordinal) ||
            xmpText.Contains("VCamera:VMotionPhotoSource", StringComparison.Ordinal))
        {
            var vivoFacts = InspectVivoSingleFile(filePath, mainLength, mainSha256, xmpText);
            if (vivoFacts != null) return vivoFacts;
        }

        // 5. OPPO / OnePlus O-Live Photo
        if (xmpText.Contains("OpCamera:MotionPhotoOwner", StringComparison.Ordinal) ||
            xmpText.Contains("OpCamera:VideoLength", StringComparison.Ordinal) ||
            xmpText.Contains("OpCamera:OLivePhotoVersion", StringComparison.Ordinal) ||
            xmpText.Contains("oplus_", StringComparison.Ordinal))
        {
            var oppoFacts = InspectOppo(filePath, mainLength, mainSha256, xmpText);
            if (oppoFacts != null) return oppoFacts;
        }

        // 6. Google Motion Photo V2 / Xiaomi
        if (xmpText.Contains("GCamera:MotionPhoto=\"1\"", StringComparison.Ordinal) ||
            xmpText.Contains("MotionPhoto=\"1\"", StringComparison.Ordinal) ||
            xmpText.Contains("Semantic=\"MotionPhoto\"", StringComparison.OrdinalIgnoreCase))
        {
            var googleV2Facts = InspectGoogleV2(filePath, mainLength, mainSha256, xmpText);
            if (googleV2Facts != null) return googleV2Facts;
        }

        // 7. Google MicroVideo V1
        if (xmpText.Contains("MicroVideo=\"1\"", StringComparison.Ordinal) ||
            xmpText.Contains("MicroVideoOffset=", StringComparison.Ordinal))
        {
            var googleV1Facts = InspectGoogleV1(filePath, mainLength, mainSha256, xmpText);
            if (googleV1Facts != null) return googleV1Facts;
        }

        // 8. Standalone Image or Video
        ImageContainer imgContainer = FormatInspector.DetectImageContainer(filePath);
        if (imgContainer != ImageContainer.Unknown)
        {
            return new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLiveImage,
                SourceFilePath = filePath,
                SourceFileSizeBytes = mainLength,
                SourceSha256 = mainSha256,
                PrimaryImage = new ImageFacts
                {
                    Container = imgContainer,
                    Codec = imgContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteOffset = 0,
                    ByteLength = mainLength,
                    FilePath = filePath
                }
            };
        }

        VideoContainer vidContainer = FormatInspector.DetectVideoContainer(filePath);
        if (vidContainer != VideoContainer.Unknown)
        {
            var vidFacts = await FormatInspector.ProbeVideoFactsAsync(filePath, 0, mainLength, ct).ConfigureAwait(false);
            return new SourceMediaFacts
            {
                Protocol = SourceProtocol.NonLiveVideo,
                SourceFilePath = filePath,
                SourceFileSizeBytes = mainLength,
                SourceSha256 = mainSha256,
                MotionVideo = vidFacts ?? new VideoFacts
                {
                    Container = vidContainer,
                    Codec = VideoCodec.Unknown,
                    ByteOffset = 0,
                    ByteLength = mainLength,
                    FilePath = filePath
                }
            };
        }

        return new SourceMediaFacts
        {
            Protocol = SourceProtocol.Unknown,
            SourceFilePath = filePath,
            SourceFileSizeBytes = mainLength,
            SourceSha256 = mainSha256
        };
    }

    private static async Task<SourceMediaFacts?> InspectDualFileAsync(
        string primaryPath,
        string secondaryPath,
        long primaryLength,
        string primarySha256,
        byte[]? tail,
        string xmpText,
        CancellationToken ct)
    {
        long secondaryLength = new FileInfo(secondaryPath).Length;
        ImageContainer imgContainer = FormatInspector.DetectImageContainer(primaryPath);
        VideoContainer vidContainer = FormatInspector.DetectVideoContainer(secondaryPath);

        // Check vivo legacy dual-file
        if (tail != null)
        {
            string tailText = Encoding.UTF8.GetString(tail);
            if (tailText.Contains("com.android.camera.livephoto", StringComparison.Ordinal))
            {
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.VivoLegacyDualFile,
                    SourceFilePath = primaryPath,
                    SecondaryFilePath = secondaryPath,
                    SourceFileSizeBytes = primaryLength + secondaryLength,
                    SourceSha256 = primarySha256,
                    PrimaryImage = new ImageFacts
                    {
                        Container = ImageContainer.Jpeg,
                        Codec = ImageCodec.Jpeg,
                        ByteOffset = 0,
                        ByteLength = primaryLength,
                        FilePath = primaryPath
                    },
                    MotionVideo = new VideoFacts
                    {
                        Container = VideoContainer.Mp4,
                        Codec = VideoCodec.H264,
                        ByteOffset = 0,
                        ByteLength = secondaryLength,
                        FilePath = secondaryPath
                    }
                };
            }
        }

        // Apple Live Photo dual-file (HEIC/JPG + MOV)
        if (imgContainer is ImageContainer.Heic or ImageContainer.Jpeg && vidContainer == VideoContainer.Mov)
        {
            var vidFacts = await FormatInspector.ProbeVideoFactsAsync(secondaryPath, 0, secondaryLength, ct).ConfigureAwait(false);
            return new SourceMediaFacts
            {
                Protocol = SourceProtocol.AppleLivePhoto,
                SourceFilePath = primaryPath,
                SecondaryFilePath = secondaryPath,
                SourceFileSizeBytes = primaryLength + secondaryLength,
                SourceSha256 = primarySha256,
                PrimaryImage = new ImageFacts
                {
                    Container = imgContainer,
                    Codec = imgContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteOffset = 0,
                    ByteLength = primaryLength,
                    FilePath = primaryPath
                },
                MotionVideo = vidFacts ?? new VideoFacts
                {
                    Container = VideoContainer.Mov,
                    Codec = VideoCodec.Hevc,
                    ByteOffset = 0,
                    ByteLength = secondaryLength,
                    FilePath = secondaryPath
                }
            };
        }

        return null;
    }

    private static async Task<SourceMediaFacts?> InspectHuaweiOrHonorAsync(
        string filePath,
        long fileSize,
        string sha256,
        byte[] tail,
        CancellationToken ct)
    {
        var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(filePath);
        if (range == null) return null;

        var (videoStart, videoEnd, videoLength) = range.Value;
        ImageContainer imgContainer = FormatInspector.DetectImageContainer(filePath);

        string tailText = Encoding.ASCII.GetString(tail);
        bool isHonor = tailText.Contains("uuidextend_type_matrix", StringComparison.Ordinal) ||
                       tailText.Contains("v2_f", StringComparison.Ordinal);

        return new SourceMediaFacts
        {
            Protocol = isHonor ? SourceProtocol.HonorMovingPhoto : SourceProtocol.HuaweiMovingPhoto,
            SourceFilePath = filePath,
            SourceFileSizeBytes = fileSize,
            SourceSha256 = sha256,
            PrimaryImage = new ImageFacts
            {
                Container = imgContainer,
                Codec = imgContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteOffset = 0,
                ByteLength = videoStart,
                FilePath = filePath
            },
            MotionVideo = new VideoFacts
            {
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.H264, // Huawei/Honor embedded video is always H.264
                ByteOffset = videoStart,
                ByteLength = videoLength,
                FilePath = filePath
            },
            VendorFacts = new Dictionary<string, string>
            {
                ["VideoStart"] = videoStart.ToString(CultureInfo.InvariantCulture),
                ["VideoLength"] = videoLength.ToString(CultureInfo.InvariantCulture),
                ["IsHonor"] = isHonor.ToString()
            }
        };
    }

    private static SourceMediaFacts? InspectVivoSingleFile(
        string filePath,
        long fileSize,
        string sha256,
        string xmpText)
    {
        var (primaryLen, gainMapLen, videoLen) = ParseContainerDirectory(xmpText);
        if (videoLen <= 0) return null;

        long videoOffset = fileSize - videoLen;
        GainMapFacts? gainMapFacts = null;
        long primaryLenCalculated = videoOffset;

        if (gainMapLen > 0)
        {
            long gainMapOffset = fileSize - videoLen - gainMapLen;
            primaryLenCalculated = gainMapOffset;
            gainMapFacts = new GainMapFacts
            {
                IsPresent = true,
                Format = "vivo-hdrgm",
                Container = ImageContainer.Jpeg,
                ByteOffset = gainMapOffset,
                ByteLength = gainMapLen
            };
        }

        long? coverTs = ParseTimestamp(MotionPhotoTimestampRegex, xmpText);

        return new SourceMediaFacts
        {
            Protocol = SourceProtocol.VivoLivePhoto,
            SourceFilePath = filePath,
            SourceFileSizeBytes = fileSize,
            SourceSha256 = sha256,
            PrimaryImage = new ImageFacts
            {
                Container = ImageContainer.Jpeg,
                Codec = ImageCodec.Jpeg,
                ByteOffset = 0,
                ByteLength = primaryLenCalculated,
                FilePath = filePath
            },
            MotionVideo = new VideoFacts
            {
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.Hevc,
                ByteOffset = videoOffset,
                ByteLength = videoLen,
                FilePath = filePath
            },
            GainMap = gainMapFacts,
            Timing = new TimingFacts { CoverTimestampUs = coverTs }
        };
    }

    private static SourceMediaFacts? InspectOppo(
        string filePath,
        long fileSize,
        string sha256,
        string xmpText)
    {
        var (primaryLen, gainMapLen, videoItemLen) = ParseContainerDirectory(xmpText);
        long pureVideoLen = ParseLong(OppoPureVideoLengthRegex, xmpText);

        long effectiveVideoLen = pureVideoLen > 0 ? pureVideoLen : videoItemLen;
        long totalTailVideoLen = videoItemLen > 0 ? videoItemLen : effectiveVideoLen;
        if (effectiveVideoLen <= 0) return null;

        long videoOffset = fileSize - totalTailVideoLen;
        long? currentCoverTs = ParseTimestamp(MotionPhotoTimestampRegex, xmpText);
        long? primaryCoverTs = ParseTimestamp(OppoPrimaryTimestampRegex, xmpText);

        return new SourceMediaFacts
        {
            Protocol = SourceProtocol.OppoLivePhoto,
            SourceFilePath = filePath,
            SourceFileSizeBytes = fileSize,
            SourceSha256 = sha256,
            PrimaryImage = new ImageFacts
            {
                Container = ImageContainer.Jpeg,
                Codec = ImageCodec.Jpeg,
                ByteOffset = 0,
                ByteLength = videoOffset,
                FilePath = filePath
            },
            MotionVideo = new VideoFacts
            {
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.H264,
                ByteOffset = videoOffset,
                ByteLength = effectiveVideoLen,
                FilePath = filePath
            },
            Timing = new TimingFacts { CoverTimestampUs = currentCoverTs },
            VendorFacts = new Dictionary<string, string>
            {
                ["PureVideoLength"] = pureVideoLen.ToString(CultureInfo.InvariantCulture),
                ["ContainerItemVideoLength"] = videoItemLen.ToString(CultureInfo.InvariantCulture),
                ["PrimaryCoverTimestampUs"] = (primaryCoverTs ?? 0).ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private static SourceMediaFacts? InspectGoogleV2(
        string filePath,
        long fileSize,
        string sha256,
        string xmpText)
    {
        var (primaryLen, gainMapLen, videoLen) = ParseContainerDirectory(xmpText);
        if (videoLen <= 0) return null;

        long videoOffset = fileSize - videoLen;
        GainMapFacts? gainMapFacts = null;
        long primaryLenCalculated = videoOffset;

        if (gainMapLen > 0)
        {
            long gainMapOffset = fileSize - videoLen - gainMapLen;
            primaryLenCalculated = gainMapOffset;
            gainMapFacts = new GainMapFacts
            {
                IsPresent = true,
                Format = "UltraHDR",
                Container = ImageContainer.Jpeg,
                ByteOffset = gainMapOffset,
                ByteLength = gainMapLen
            };
        }

        long? coverTs = ParseTimestamp(MotionPhotoTimestampRegex, xmpText);
        ImageContainer imgContainer = FormatInspector.DetectImageContainer(filePath);

        return new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            SourceFilePath = filePath,
            SourceFileSizeBytes = fileSize,
            SourceSha256 = sha256,
            PrimaryImage = new ImageFacts
            {
                Container = imgContainer,
                Codec = imgContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteOffset = 0,
                ByteLength = primaryLenCalculated,
                FilePath = filePath
            },
            MotionVideo = new VideoFacts
            {
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.H264,
                ByteOffset = videoOffset,
                ByteLength = videoLen,
                FilePath = filePath
            },
            GainMap = gainMapFacts,
            Timing = new TimingFacts { CoverTimestampUs = coverTs }
        };
    }

    private static SourceMediaFacts? InspectGoogleV1(
        string filePath,
        long fileSize,
        string sha256,
        string xmpText)
    {
        long videoOffsetLen = ParseLong(MicroVideoOffsetRegex, xmpText);
        if (videoOffsetLen <= 0) return null;

        long videoOffset = fileSize - videoOffsetLen;
        long? coverTs = ParseTimestamp(MicroVideoTimestampRegex, xmpText);

        return new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            SourceFilePath = filePath,
            SourceFileSizeBytes = fileSize,
            SourceSha256 = sha256,
            PrimaryImage = new ImageFacts
            {
                Container = ImageContainer.Jpeg,
                Codec = ImageCodec.Jpeg,
                ByteOffset = 0,
                ByteLength = videoOffset,
                FilePath = filePath
            },
            MotionVideo = new VideoFacts
            {
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.H264,
                ByteOffset = videoOffset,
                ByteLength = videoOffsetLen,
                FilePath = filePath
            },
            Timing = new TimingFacts { CoverTimestampUs = coverTs }
        };
    }

    private static (long primaryLen, long gainMapLen, long videoLen) ParseContainerDirectory(string xmpText)
    {
        long primaryLen = 0;
        long gainMapLen = 0;
        long videoLen = 0;

        var matches = ContainerItemRegex.Matches(xmpText);
        if (matches.Count == 0)
            matches = ContainerItemAltOrderRegex.Matches(xmpText);

        foreach (Match m in matches)
        {
            string semantic = m.Groups["semantic"].Value;
            string lenStr = m.Groups["len"].Value;
            long.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long len);

            if (semantic.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                primaryLen = len;
            else if (semantic.Equals("GainMap", StringComparison.OrdinalIgnoreCase))
                gainMapLen = len;
            else if (semantic.Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
                videoLen = len;
        }

        return (primaryLen, gainMapLen, videoLen);
    }

    private static long ParseLong(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val))
            return val;
        return 0;
    }

    private static long? ParseTimestamp(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (m.Success && long.TryParse(m.Groups["ts"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val))
            return val;
        return null;
    }

    private static string ReadXmpText(string filePath)
    {
        return LivePhotoSplitService.ReadMetadataTextSync(filePath);
    }

    private static byte[]? ReadFileTail(string filePath, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileSize = fs.Length;
            int readSize = (int)Math.Min(fileSize, maxBytes);
            if (readSize <= 0) return null;

            byte[] buffer = new byte[readSize];
            fs.Seek(-readSize, SeekOrigin.End);
            int totalRead = 0;
            while (totalRead < readSize)
            {
                int n = fs.Read(buffer, totalRead, readSize - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsBytes(byte[] data, byte[] pattern)
    {
        if (pattern.Length == 0) return false;
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            int j;
            for (j = 0; j < pattern.Length; j++)
                if (data[i + j] != pattern[j]) break;
            if (j == pattern.Length) return true;
        }
        return false;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

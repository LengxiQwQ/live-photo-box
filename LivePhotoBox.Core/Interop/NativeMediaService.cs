using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

/// <summary>
/// Thin control plane service that invokes LivePhotoBox.Native execution plane media operations.
/// </summary>
public static class NativeMediaService
{
    public static Task<SourceMediaFacts> InspectMediaAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(primaryPath))
            throw new FileNotFoundException("Primary media file not found.", primaryPath);

        return Task.Run(() =>
        {
            unsafe
            {
                var nativeFacts = new NativeSourceMediaFacts
                {
                    StructSize = checked((uint)sizeof(NativeSourceMediaFacts)),
                    PrimaryImage = new NativeImageItemFacts { StructSize = checked((uint)sizeof(NativeImageItemFacts)) },
                    MotionVideo = new NativeVideoItemFacts { StructSize = checked((uint)sizeof(NativeVideoItemFacts)) },
                    GainMap = new NativeGainMapItemFacts { StructSize = checked((uint)sizeof(NativeGainMapItemFacts)) },
                    Timing = new NativeTimingFacts { StructSize = checked((uint)sizeof(NativeTimingFacts)) }
                };

                NativeResult res = NativeMethods.InspectMedia(nint.Zero, primaryPath, secondaryPath, ref nativeFacts);
                if (res != NativeResult.Ok)
                {
                    throw new InvalidOperationException($"Native source media inspection failed with result: {res}");
                }

                return MapFromNativeFacts(nativeFacts);
            }
        }, cancellationToken);
    }

    public static Task ExtractMediaAsync(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            NativeSourceMediaFacts nativeFacts = MapToNativeFacts(facts);

            NativeResult res = NativeMethods.ExtractMedia(
                nint.Zero,
                primaryPath,
                secondaryPath,
                in nativeFacts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath);

            if (res != NativeResult.Ok)
            {
                throw new InvalidOperationException($"Native media extraction failed with result: {res}");
            }
        }, cancellationToken);
    }

    public static Task<VideoFacts> ProbeVideoAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found for probe.", videoPath);

        return Task.Run(() =>
        {
            unsafe
            {
                var nativeFacts = new NativeVideoItemFacts
                {
                    StructSize = checked((uint)sizeof(NativeVideoItemFacts))
                };

                NativeResult res = NativeMethods.ProbeVideo(nint.Zero, videoPath, ref nativeFacts);
                if (res != NativeResult.Ok)
                {
                    throw new InvalidOperationException($"Native video probe failed with result: {res}");
                }

                return MapFromNativeVideoFacts(nativeFacts);
            }
        }, cancellationToken);
    }

    public static Task RemuxVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        VideoContainer targetContainer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            NativeResult res = NativeMethods.RemuxVideo(
                nint.Zero,
                inputVideoPath,
                outputVideoPath,
                (int)targetContainer);

            if (res != NativeResult.Ok)
            {
                throw new InvalidOperationException($"Native video remux failed with result: {res}");
            }
        }, cancellationToken);
    }

    public static Task<bool> ConvertImageAsync(
        string inputImagePath,
        string outputImagePath,
        ImageContainer targetContainer,
        int quality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            NativeResult res = NativeMethods.ConvertImage(
                nint.Zero,
                inputImagePath,
                outputImagePath,
                (int)targetContainer,
                quality,
                out int outReencoded);

            if (res != NativeResult.Ok)
            {
                throw new InvalidOperationException($"Native image conversion failed with result: {res}");
            }

            return outReencoded != 0;
        }, cancellationToken);
    }

    public static Task<string> TranscodeVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        VideoContainer targetContainer,
        VideoCodec targetCodec,
        int crf,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            Span<byte> encoderBuf = stackalloc byte[128];
            NativeResult res;
            unsafe
            {
                fixed (byte* pBuf = encoderBuf)
                {
                    res = NativeMethods.TranscodeVideo(
                        nint.Zero,
                        inputVideoPath,
                        outputVideoPath,
                        (int)targetContainer,
                        (int)targetCodec,
                        crf,
                        pBuf,
                        (nuint)encoderBuf.Length);
                }
            }

            if (res != NativeResult.Ok)
            {
                throw new InvalidOperationException($"Native video transcode failed with result: {res}");
            }

            int nullIdx = encoderBuf.IndexOf((byte)0);
            if (nullIdx < 0) nullIdx = encoderBuf.Length;
            return Encoding.UTF8.GetString(encoderBuf[..nullIdx]);
        }, cancellationToken);
    }

    private static unsafe SourceMediaFacts MapFromNativeFacts(in NativeSourceMediaFacts native)
    {
        string? pairingId = null;
        fixed (byte* p = native.PairingIdentifier)
        {
            int len = 0;
            while (len < 128 && p[len] != 0) len++;
            if (len > 0)
            {
                pairingId = Encoding.UTF8.GetString(p, len);
            }
        }

        return new SourceMediaFacts
        {
            Protocol = (SourceProtocol)native.Protocol,
            PrimaryImage = new ImageFacts
            {
                IsPresent = native.PrimaryImage.IsPresent != 0,
                Container = (ImageContainer)native.PrimaryImage.Container,
                Width = native.PrimaryImage.Width,
                Height = native.PrimaryImage.Height,
                ByteOffset = (long)native.PrimaryImage.FileRange.Offset,
                ByteLength = (long)native.PrimaryImage.FileRange.Length
            },
            MotionVideo = native.MotionVideo.IsPresent != 0 ? MapFromNativeVideoFacts(native.MotionVideo) : null,
            GainMap = native.GainMap.IsPresent != 0 ? new GainMapFacts
            {
                IsPresent = true,
                Container = (ImageContainer)native.GainMap.Container,
                ByteOffset = (long)native.GainMap.FileRange.Offset,
                ByteLength = (long)native.GainMap.FileRange.Length
            } : null,
            Timing = new TimingFacts
            {
                CoverTimestampUs = native.Timing.CoverTimestampUs,
                PrimaryTimestampUs = native.Timing.PrimaryTimestampUs,
                CoverFrameIndex = native.Timing.CoverFrameIndex,
                TotalFrames = native.Timing.TotalFrames
            },
            PairingIdentifier = pairingId
        };
    }

    private static VideoFacts MapFromNativeVideoFacts(in NativeVideoItemFacts native)
    {
        return new VideoFacts
        {
            IsPresent = native.IsPresent != 0,
            Container = (VideoContainer)native.Container,
            Codec = (VideoCodec)native.Codec,
            Width = native.Width,
            Height = native.Height,
            RotationDegrees = native.RotationDegrees,
            DurationSeconds = native.DurationSeconds,
            Fps = native.Fps,
            HasAudio = native.HasAudio != 0,
            ByteOffset = (long)native.FileRange.Offset,
            ByteLength = (long)native.FileRange.Length
        };
    }

    private static unsafe NativeSourceMediaFacts MapToNativeFacts(SourceMediaFacts facts)
    {
        var native = new NativeSourceMediaFacts
        {
            StructSize = checked((uint)sizeof(NativeSourceMediaFacts)),
            Protocol = (int)facts.Protocol,
            PrimaryImage = new NativeImageItemFacts
            {
                StructSize = checked((uint)sizeof(NativeImageItemFacts)),
                IsPresent = facts.PrimaryImage.IsPresent ? 1 : 0,
                Container = (int)facts.PrimaryImage.Container,
                Width = facts.PrimaryImage.Width,
                Height = facts.PrimaryImage.Height,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.PrimaryImage.ByteOffset,
                    Length = (ulong)facts.PrimaryImage.ByteLength
                }
            }
        };

        if (facts.MotionVideo != null)
        {
            native.MotionVideo = new NativeVideoItemFacts
            {
                StructSize = checked((uint)sizeof(NativeVideoItemFacts)),
                IsPresent = facts.MotionVideo.IsPresent ? 1 : 0,
                Container = (int)facts.MotionVideo.Container,
                Codec = (int)facts.MotionVideo.Codec,
                Width = facts.MotionVideo.Width,
                Height = facts.MotionVideo.Height,
                RotationDegrees = facts.MotionVideo.RotationDegrees,
                DurationSeconds = facts.MotionVideo.DurationSeconds,
                Fps = facts.MotionVideo.Fps,
                HasAudio = facts.MotionVideo.HasAudio ? 1 : 0,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.MotionVideo.ByteOffset,
                    Length = (ulong)facts.MotionVideo.ByteLength
                }
            };
        }

        if (facts.GainMap != null)
        {
            native.GainMap = new NativeGainMapItemFacts
            {
                StructSize = checked((uint)sizeof(NativeGainMapItemFacts)),
                IsPresent = facts.GainMap.IsPresent ? 1 : 0,
                Container = (int)facts.GainMap.Container,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.GainMap.ByteOffset,
                    Length = (ulong)facts.GainMap.ByteLength
                }
            };
        }

        native.Timing = new NativeTimingFacts
        {
            StructSize = checked((uint)sizeof(NativeTimingFacts)),
            CoverTimestampUs = facts.Timing.CoverTimestampUs,
            PrimaryTimestampUs = facts.Timing.PrimaryTimestampUs,
            CoverFrameIndex = facts.Timing.CoverFrameIndex,
            TotalFrames = facts.Timing.TotalFrames
        };

        return native;
    }
}

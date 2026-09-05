using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorExactnessTests
{
    private static async Task<string> ComputeSliceSha256Async(string filePath, long offset, long length)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    [Fact]
    public async Task Extract_SyntheticSingleFile_ExtractsByteExactSlicesWithSentinels()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "synthetic_source.jpg");

        // Layout:
        // [0 .. 1023]: Header sentinel (0xAA) + valid JPEG SOI (0xFF 0xD8) + dummy JPEG bytes
        // [1024 .. 3071]: Valid MP4 ftyp box + dummy video bytes (2048 bytes)
        // [3072 .. 4095]: Tail sentinel (0xBB) (1024 bytes)
        byte[] sourceBytes = new byte[4096];

        // Primary image: 1024 bytes
        sourceBytes[0] = 0xFF;
        sourceBytes[1] = 0xD8;
        for (int i = 2; i < 1024; i++) sourceBytes[i] = 0xAA;

        // Video: 2048 bytes (with ftyp box)
        int vidOffset = 1024;
        int vidLen = 2048;
        sourceBytes[vidOffset + 0] = 0x00;
        sourceBytes[vidOffset + 1] = 0x00;
        sourceBytes[vidOffset + 2] = 0x00;
        sourceBytes[vidOffset + 3] = 0x10;
        sourceBytes[vidOffset + 4] = (byte)'f';
        sourceBytes[vidOffset + 5] = (byte)'t';
        sourceBytes[vidOffset + 6] = (byte)'y';
        sourceBytes[vidOffset + 7] = (byte)'p';
        sourceBytes[vidOffset + 8] = (byte)'m';
        sourceBytes[vidOffset + 9] = (byte)'p';
        sourceBytes[vidOffset + 10] = (byte)'4';
        sourceBytes[vidOffset + 11] = (byte)'2';
        for (int i = 16; i < vidLen; i++) sourceBytes[vidOffset + i] = (byte)(i % 251);

        // Tail sentinel: 1024 bytes
        for (int i = 3072; i < 4096; i++) sourceBytes[i] = 0xBB;

        await File.WriteAllBytesAsync(dummySource, sourceBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = 0,
                ByteLength = 1024
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Container = VideoContainer.Mp4,
                ByteOffset = vidOffset,
                ByteLength = vidLen,
                SourceIndex = 0
            },
            ProtocolTailOffset = 3072,
            ProtocolTailLength = 1024
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, dummySource, null, workspace);

        // 1. Verify primary image output
        Assert.NotNull(bundle.PrimaryImage);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.Equal(1024, bundle.PrimaryImage.ByteLength);
        string expectedImgSha = await ComputeSliceSha256Async(dummySource, 0, 1024);
        Assert.Equal(expectedImgSha, bundle.PrimaryImage.Sha256);
        byte[] extractedImg = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        Assert.Equal(1024, extractedImg.Length);
        Assert.Equal(0xFF, extractedImg[0]);
        Assert.Equal(0xD8, extractedImg[1]);
        Assert.Equal(0xAA, extractedImg[1023]);

        // 2. Verify motion video output
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.Equal(vidLen, bundle.MotionVideo.ByteLength);
        string expectedVidSha = await ComputeSliceSha256Async(dummySource, vidOffset, vidLen);
        Assert.Equal(expectedVidSha, bundle.MotionVideo.Sha256);
        byte[] extractedVid = await File.ReadAllBytesAsync(bundle.MotionVideo.Path);
        Assert.Equal(vidLen, extractedVid.Length);
        Assert.Equal((byte)'f', extractedVid[4]);
        Assert.Equal((byte)'t', extractedVid[5]);
        Assert.Equal((byte)'y', extractedVid[6]);
        Assert.Equal((byte)'p', extractedVid[7]);

        // 3. Immutability of source
        byte[] postSourceBytes = await File.ReadAllBytesAsync(dummySource);
        Assert.Equal(sourceBytes, postSourceBytes);
    }

    [Fact]
    public async Task Extract_Synthetic3ItemFile_ExtractsImageGainMapAndVideoByteExact()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "synthetic_3item.jpg");

        int imgLen = 512;
        int gmOffset = 512;
        int gmLen = 256;
        int vidOffset = 768;
        int vidLen = 1024;
        int totalLen = 1792;

        byte[] sourceBytes = new byte[totalLen];

        sourceBytes[0] = 0xFF;
        sourceBytes[1] = 0xD8;
        for (int i = 2; i < imgLen; i++) sourceBytes[i] = 0x11;

        sourceBytes[gmOffset] = 0xFF;
        sourceBytes[gmOffset + 1] = 0xD8;
        for (int i = 2; i < gmLen; i++) sourceBytes[gmOffset + i] = 0x22;

        sourceBytes[vidOffset + 4] = (byte)'f';
        sourceBytes[vidOffset + 5] = (byte)'t';
        sourceBytes[vidOffset + 6] = (byte)'y';
        sourceBytes[vidOffset + 7] = (byte)'p';
        for (int i = 8; i < vidLen; i++) sourceBytes[vidOffset + i] = 0x33;

        await File.WriteAllBytesAsync(dummySource, sourceBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.VivoLivePhoto,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = 0,
                ByteLength = imgLen
            },
            GainMap = new GainMapFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = gmOffset,
                ByteLength = gmLen
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Container = VideoContainer.Mp4,
                ByteOffset = vidOffset,
                ByteLength = vidLen,
                SourceIndex = 0
            }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, dummySource, null, workspace);

        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.GainMap);
        Assert.NotNull(bundle.MotionVideo);

        string expectedImgSha = await ComputeSliceSha256Async(dummySource, 0, imgLen);
        string expectedGmSha = await ComputeSliceSha256Async(dummySource, gmOffset, gmLen);
        string expectedVidSha = await ComputeSliceSha256Async(dummySource, vidOffset, vidLen);

        Assert.Equal(expectedImgSha, bundle.PrimaryImage.Sha256);
        Assert.Equal(expectedGmSha, bundle.GainMap.Sha256);
        Assert.Equal(expectedVidSha, bundle.MotionVideo.Sha256);

        Assert.Equal(sourceBytes[..imgLen], await File.ReadAllBytesAsync(bundle.PrimaryImage.Path));
        Assert.Equal(sourceBytes[gmOffset..(gmOffset + gmLen)], await File.ReadAllBytesAsync(bundle.GainMap.Path));
        Assert.Equal(sourceBytes[vidOffset..(vidOffset + vidLen)], await File.ReadAllBytesAsync(bundle.MotionVideo.Path));
    }

    [Fact]
    public async Task Extract_LargeFileStreaming_PreservesByteExactnessAcross64KbBoundaries()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "large_source.jpg");

        int totalSize = 512 * 1024;
        byte[] data = new byte[totalSize];
        var rng = new Random(42);
        rng.NextBytes(data);
        data[0] = 0xFF;
        data[1] = 0xD8;

        long vidOffset = 73521; // Non-aligned offset
        long vidLen = 215432;  // Multiple 64KB blocks + partial block
        data[vidOffset + 4] = (byte)'f';
        data[vidOffset + 5] = (byte)'t';
        data[vidOffset + 6] = (byte)'y';
        data[vidOffset + 7] = (byte)'p';

        await File.WriteAllBytesAsync(dummySource, data);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = 0,
                ByteLength = vidOffset
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Container = VideoContainer.Mp4,
                ByteOffset = vidOffset,
                ByteLength = vidLen,
                SourceIndex = 0
            }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, dummySource, null, workspace);

        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(vidLen, bundle.MotionVideo.ByteLength);

        string expectedVidSha = await ComputeSliceSha256Async(dummySource, vidOffset, vidLen);
        Assert.Equal(expectedVidSha, bundle.MotionVideo.Sha256);

        byte[] extractedVideoBytes = await File.ReadAllBytesAsync(bundle.MotionVideo.Path);
        Assert.Equal((int)vidLen, extractedVideoBytes.Length);
        byte[] expectedSlice = new byte[vidLen];
        Array.Copy(data, vidOffset, expectedSlice, 0, vidLen);
        Assert.Equal(expectedSlice, extractedVideoBytes);
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; }

        public DisposableTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lpb_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch { }
        }
    }
}

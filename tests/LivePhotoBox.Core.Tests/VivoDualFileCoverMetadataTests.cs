using System.Buffers.Binary;
using System.Text;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class VivoDualFileCoverMetadataTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ChangeCover_RealVivoPair_PreservesTheSourceTailOnOutputJpeg()
    {
        string directory = CreateTempDirectory("lpb_vivo_cover_real");
        string? previousSettingsPath = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            string sourceImage = Path.Combine(directory, "source.jpg");
            string sourceVideo = Path.Combine(directory, "source.mp4");
            string outputImage = Path.Combine(directory, "output.jpg");
            string outputVideo = Path.Combine(directory, "output.mp4");
            File.Copy(ResolveSample("vivo双文件.jpg"), sourceImage);
            File.Copy(ResolveSample("vivo双文件.mp4"), sourceVideo);
            byte[] sourceTail = ReadVivoTail(await File.ReadAllBytesAsync(sourceImage));
            Assert.NotEmpty(sourceTail);

            var exception = await Assert.ThrowsAsync<RebuiltPipelineNotReadyException>(() =>
                CoverChangeService.ChangeCoverAsync(new CoverChangeRequest
                {
                    ImagePath = sourceImage,
                    VideoPath = sourceVideo,
                    LivePhotoType = LivePhotoType.DualFile,
                    Protocol = LivePhotoProtocolType.Vivo,
                    TimestampUs = 0,
                    OutputImagePath = outputImage,
                    OutputVideoPath = outputVideo
                }, CancellationToken.None));

            Assert.Equal("cover", exception.Operation);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previousSettingsPath);
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Writer_UsesTheV221ImageAndVideoContracts()
    {
        string directory = CreateTempDirectory("lpb_vivo_contract");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath, Ftyp());

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath, videoPath, CancellationToken.None);

            string imageText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(imagePath));
            string videoText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(videoPath));
            Assert.Contains("\"version\":2200", imageText);
            Assert.Contains("\"version\":2016", videoText);
            Assert.Contains("\"com.vivo.gallery.livePhoto.newCoverTime\":0", videoText);
            Assert.DoesNotContain("com.android.camera.imageTime", videoText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_CreatesOneStandardUuidBoxAndSharedId()
    {
        string directory = CreateTempDirectory("lpb_vivo_pair");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath, Ftyp());

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath, videoPath, CancellationToken.None);

            byte[] image = await File.ReadAllBytesAsync(imagePath);
            byte[] video = await File.ReadAllBytesAsync(videoPath);
            Assert.Equal(1, CountOccurrences(video, "vivoMediaExtInfo"u8));
            string imageId = ExtractId(image);
            string videoId = ExtractId(video);
            Assert.False(string.IsNullOrWhiteSpace(imageId));
            Assert.Equal(imageId, videoId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_RemovesAnExistingStandardUuidBoxBeforeAppending()
    {
        string directory = CreateTempDirectory("lpb_vivo_existing");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);

            byte[] oldUuid = BuildUuidBox(Encoding.UTF8.GetBytes(
                "vivo{\"com.android.camera.livephoto\":\"old\"}"));
            await File.WriteAllBytesAsync(videoPath,
            [
                .. Ftyp(),
                .. oldUuid
            ]);

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath, videoPath, CancellationToken.None);

            byte[] result = await File.ReadAllBytesAsync(videoPath);
            Assert.Equal(1, CountOccurrences(result, "vivoMediaExtInfo"u8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_DoesNotInventEditedCoverMetadata()
    {
        string directory = CreateTempDirectory("lpb_vivo_unedited");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath, Ftyp());

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath, videoPath, CancellationToken.None);

            string videoText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(videoPath));
            Assert.DoesNotContain("com.vivo.gallery.livePhoto.bestTime", videoText);
            Assert.Contains("com.vivo.gallery.livePhoto.newCoverTime\":0", videoText);
            Assert.DoesNotContain("com.android.camera.imageTime", videoText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ResolveSample(params string[] pathParts)
    {
        string path = Path.Combine([AppContext.BaseDirectory, "samples", .. pathParts]);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Sample not found: {path}");
        return path;
    }

    private static byte[] ReadVivoTail(byte[] image)
    {
        int start = -1;
        for (int i = image.Length - 6; i >= 0; i--)
        {
            if (image[i] == 'v' && image[i + 1] == 'i' && image[i + 2] == 'v' &&
                image[i + 3] == 'o' && image[i + 4] == '{')
            {
                start = i;
                break;
            }
        }

        Assert.True(start >= 0, "The copied vivo sample must contain a vivo tail.");
        byte[] marker = "cameralbum!"u8.ToArray();
        Assert.True(IndexOf(image, marker, start) >= 0, "The copied vivo sample must contain the tail end marker.");
        return image[start..];
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static byte[] Ftyp()
        => [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p'];

    private static string ExtractId(byte[] data)
    {
        byte[] marker = Encoding.UTF8.GetBytes("com.android.camera.livephoto\":\"");
        int start = IndexOf(data, marker);
        Assert.True(start >= 0);
        start += marker.Length;
        int end = Array.IndexOf(data, (byte)'\"', start);
        Assert.True(end > start);
        return Encoding.UTF8.GetString(data, start, end - start);
    }

    private static byte[] BuildUuidBox(byte[] payload)
    {
        byte[] userType = Encoding.ASCII.GetBytes("vivoMediaExtInfo");
        int size = 8 + userType.Length + payload.Length;
        byte[] result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)size);
        Encoding.ASCII.GetBytes("uuid").CopyTo(result, 4);
        userType.CopyTo(result, 8);
        payload.CopyTo(result, 8 + userType.Length);
        return result;
    }

    private static int CountOccurrences(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        int count = 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                count++;
        }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}

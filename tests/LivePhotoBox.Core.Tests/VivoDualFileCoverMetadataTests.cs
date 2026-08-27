using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class VivoDualFileCoverMetadataTests
{
    [Fact]
    public async Task RealDeviceMetadata_SeparatesOriginalFrameAndEditedCoverTime()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"lpb_vivo_real_metadata_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // 以 2026-08-27 iQOO 12 真机三份样本的原始 JSON 关键字段建立回归夹具。
            string originalPath = Path.Combine(directory, "original.mp4");
            string firstPath = Path.Combine(directory, "first.mp4");
            string lastPath = Path.Combine(directory, "last.mp4");
            await WriteVivoFixtureAsync(originalPath,
                "{\"com.android.camera.imageTime\":39,\"com.android.camera.livephoto\":\"original\",\"version\":2104,\"com.android.camera.faceInfo\":{}}");
            await WriteVivoFixtureAsync(firstPath,
                "{\"com.android.camera.imageTime\":39,\"com.vivo.gallery.livePhoto.bestTime\":0,\"com.android.camera.livephoto\":\"first\",\"version\":2200,\"com.android.camera.faceInfo\":{},\"com.vivo.gallery.livePhoto.newCoverTime\":0}");
            await WriteVivoFixtureAsync(lastPath,
                "{\"com.android.camera.imageTime\":39,\"com.vivo.gallery.livePhoto.bestTime\":0,\"com.android.camera.livephoto\":\"last\",\"version\":2200,\"com.android.camera.faceInfo\":{},\"com.vivo.gallery.livePhoto.newCoverTime\":2921}");

            VivoDualFileCoverInfo original = AssertCoverInfo(originalPath);
            Assert.Equal(39, original.OriginalFrameIndex);
            Assert.Null(original.CurrentCoverTimeMilliseconds);

            VivoDualFileCoverInfo first = AssertCoverInfo(firstPath);
            Assert.Equal(39, first.OriginalFrameIndex);
            Assert.Equal(0, first.CurrentCoverTimeMilliseconds);

            VivoDualFileCoverInfo last = AssertCoverInfo(lastPath);
            Assert.Equal(39, last.OriginalFrameIndex);
            Assert.Equal(2921, last.CurrentCoverTimeMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_StoresSingleUneditedCoverPosition()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"lpb_vivo_cover_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath,
                [0x00, 0x00, 0x00, 0x08, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath,
                videoPath,
                coverFrameIndex: 39,
                CancellationToken.None);

            VivoDualFileCoverInfo? infoResult =
                VivoDualFileMetadataWriter.ReadCoverInfo(videoPath);
            Assert.NotNull(infoResult);
            VivoDualFileCoverInfo info = infoResult!;
            Assert.Equal(39, info.OriginalFrameIndex);
            Assert.Null(info.CurrentCoverTimeMilliseconds);
            Assert.False(string.IsNullOrWhiteSpace(info.LivePhotoId));

            string tail = System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(videoPath));
            Assert.DoesNotContain("com.vivo.gallery.livePhoto.newCoverTime", tail);
            Assert.DoesNotContain("com.vivo.gallery.livePhoto.bestTime", tail);
            Assert.Contains("\"version\":2104", tail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_ReplacesUuidAfterExtendedSizeMdat()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"lpb_vivo_extended_mdat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);

            byte[] oldJson = System.Text.Encoding.UTF8.GetBytes(
                "vivo{\"com.android.camera.imageTime\":39," +
                "\"com.android.camera.livephoto\":\"old\"}");
            byte[] oldPayload = BuildTail(oldJson);
            byte[] oldUuid = BuildUuidBox(oldPayload);
            byte[] extendedMdat =
            [
                0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t',
                0, 0, 0, 0, 0, 0, 0, 20,
                1, 2, 3, 4
            ];
            await File.WriteAllBytesAsync(videoPath,
            [
                0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                .. extendedMdat,
                .. oldUuid
            ]);

            await VivoDualFileMetadataWriter.WritePairMetadataAsync(
                imagePath,
                videoPath,
                coverFrameIndex: 12,
                CancellationToken.None);

            byte[] result = await File.ReadAllBytesAsync(videoPath);
            Assert.Equal(1, CountOccurrences(result, "vivoMediaExtInfo"u8));
            VivoDualFileCoverInfo info = AssertCoverInfo(videoPath);
            Assert.Equal(12, info.OriginalFrameIndex);
            Assert.Null(info.CurrentCoverTimeMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EditedWriter_PreservesOriginalAndAddsCurrentCoverPosition()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"lpb_vivo_edited_cover_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourceImagePath = Path.Combine(directory, "source.jpg");
            string sourceVideoPath = Path.Combine(directory, "source.mp4");
            string outputImagePath = Path.Combine(directory, "output.jpg");
            string outputVideoPath = Path.Combine(directory, "output.mp4");

            byte[] sourceImageJson = System.Text.Encoding.UTF8.GetBytes(
                "vivo{\"com.android.camera.livephoto\":\"source\",\"version\":2104}");
            await File.WriteAllBytesAsync(sourceImagePath,
            [
                0xFF, 0xD8, 0xFF, 0xD9,
                .. BuildTail(sourceImageJson)
            ]);

            byte[] sourceVideoJson = System.Text.Encoding.UTF8.GetBytes(
                "vivo{\"com.android.camera.imageTime\":39," +
                "\"com.android.camera.livephoto\":\"source\",\"version\":2104}");
            await File.WriteAllBytesAsync(sourceVideoPath,
            [
                0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                .. BuildUuidBox(BuildTail(sourceVideoJson))
            ]);
            File.Copy(sourceImagePath, outputImagePath);
            File.Copy(sourceVideoPath, outputVideoPath);

            await VivoDualFileMetadataWriter.RewriteEditedPairMetadataAsync(
                sourceImagePath,
                sourceVideoPath,
                outputImagePath,
                outputVideoPath,
                currentCoverTimeMilliseconds: 456,
                CancellationToken.None);

            VivoDualFileCoverInfo info = AssertCoverInfo(outputVideoPath);
            Assert.Equal(39, info.OriginalFrameIndex);
            Assert.Equal(456, info.CurrentCoverTimeMilliseconds);

            string tail = System.Text.Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(outputVideoPath));
            Assert.Contains("com.vivo.gallery.livePhoto.bestTime\":0", tail);
            Assert.Contains("\"version\":2200", tail);
            Assert.Equal(1, CountOccurrences(
                await File.ReadAllBytesAsync(outputVideoPath), "vivoMediaExtInfo"u8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VivoDualFileCoverInfo AssertCoverInfo(string path)
    {
        VivoDualFileCoverInfo? result = VivoDualFileMetadataWriter.ReadCoverInfo(path);
        Assert.NotNull(result);
        return result!;
    }

    private static Task WriteVivoFixtureAsync(string path, string json)
    {
        byte[] prefix = [0x00, 0x00, 0x00, 0x08, (byte)'f', (byte)'t', (byte)'y', (byte)'p'];
        byte[] metadata = System.Text.Encoding.UTF8.GetBytes("vivo" + json);
        return File.WriteAllBytesAsync(path, [.. prefix, .. metadata]);
    }

    private static byte[] BuildTail(byte[] json)
        => [.. json, .. "cameralbum!"u8, 0xFF, 0xFF, 0xFF, 0xFF];

    private static byte[] BuildUuidBox(byte[] payload)
    {
        int size = 8 + 16 + payload.Length;
        byte[] result = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result, size);
        "uuid"u8.CopyTo(result.AsSpan(4));
        "vivoMediaExtInfo"u8.CopyTo(result.AsSpan(8));
        payload.CopyTo(result.AsSpan(24));
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
}

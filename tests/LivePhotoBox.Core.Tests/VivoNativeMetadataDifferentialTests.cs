using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class VivoNativeMetadataDifferentialTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes(
        "vivo{\"com.android.camera.imageTime\":39," +
        "\"com.android.camera.livephoto\":\"00112233445566778899aabbccdd\"," +
        "\"version\":2104}");

    [Fact]
    public void ImageAppend_IsByteIdenticalToLegacyWriter()
    {
        byte[] input = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        byte[] expected = [.. input, .. BuildLegacyTail(Json)];

        Assert.True(NativeVivoLegacyMetadata.TryRewriteImage(
            input, Json, replaceExisting: false, out byte[] actual, out string? error), error);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EditedImageReplacement_IsByteIdenticalToLegacyWriter()
    {
        byte[] prefix = [0xFF, 0xD8, 0x33, 0x44, 0xFF, 0xD9];
        byte[] oldJson = Encoding.UTF8.GetBytes(
            "vivo{\"com.android.camera.livephoto\":\"old-id\",\"version\":2104}");
        byte[] input = [.. prefix, .. BuildLegacyTail(oldJson)];
        byte[] expected = [.. prefix, .. BuildLegacyTail(Json)];

        Assert.True(NativeVivoLegacyMetadata.TryRewriteImage(
            input, Json, replaceExisting: true, out byte[] actual, out string? error), error);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EditedImageReplacement_DoesNotTruncateAnUnrelatedVivoMarker()
    {
        byte[] prefix = [0xFF, 0xD8, .. "ordinary vivo{ text"u8, 0xFF, 0xD9];
        byte[] input = [.. prefix];

        Assert.True(NativeVivoLegacyMetadata.TryRewriteImage(
            input, Json, replaceExisting: true, out byte[] actual, out string? error), error);
        Assert.Equal(input, actual.AsSpan(0, input.Length).ToArray());
        Assert.Equal(BuildLegacyTail(Json), actual.AsSpan(input.Length).ToArray());
    }

    [Fact]
    public void VideoUuidReplacementAndChunkOffset_IsByteIdenticalToLegacyWriter()
    {
        byte[] oldJson = Encoding.UTF8.GetBytes(
            "vivo{\"com.android.camera.livephoto\":\"old-id\",\"version\":2104}");
        byte[] oldUuid = BuildUuidBox(BuildLegacyTail(oldJson));
        byte[] ftyp = Box("ftyp", []);
        byte[] mdat = Box("mdat", [1, 2, 3, 4]);

        // stco points at the mdat payload in the original layout. The old uuid box
        // sits between moov and mdat, so both implementations must subtract it.
        byte[] placeholderMoov = BuildMoov(chunkOffset: 0);
        int chunkOffset = ftyp.Length + placeholderMoov.Length + oldUuid.Length + 8;
        byte[] moov = BuildMoov(chunkOffset);
        byte[] input = [.. ftyp, .. moov, .. oldUuid, .. mdat];

        string directory = Path.Combine(
            FindRepositoryRoot(), "ai-tmp", "test-runs", $"lpb_native_vivo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string legacyPath = Path.Combine(directory, "legacy.mp4");
        try
        {
            File.WriteAllBytes(legacyPath, input);
            Assert.True(Mp4MdtaKeyStripper.TryStripUuidBox(
                legacyPath, "vivoMediaExtInfo", out string? stripError), stripError);
            byte[] expected = [.. File.ReadAllBytes(legacyPath), .. BuildUuidBox(BuildLegacyTail(Json))];

            Assert.True(NativeVivoLegacyMetadata.TryRewriteVideo(
                input, Json, out byte[] actual, out string? error), error);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingLivePhotoId_IsRejectedWithoutOutput()
    {
        byte[] invalidJson = "vivo{\"version\":2104}"u8.ToArray();

        Assert.False(NativeVivoLegacyMetadata.TryRewriteImage(
            [0xFF, 0xD8, 0xFF, 0xD9], invalidJson, false,
            out byte[] output, out string? error));
        Assert.Empty(output);
        Assert.Contains("livephoto ID", error);
    }

    private static byte[] BuildLegacyTail(byte[] json)
    {
        byte[] key = "\"com.android.camera.livephoto\":\""u8.ToArray();
        int keyStart = json.AsSpan().IndexOf(key);
        Assert.True(keyStart >= 0);
        int idStart = keyStart + key.Length;
        int idEnd = json.AsSpan(idStart).IndexOf((byte)'"') + idStart;
        int idLength = idEnd - idStart;

        using var stream = new MemoryStream();
        stream.Write(json);
        WriteBe32(stream, json.Length - 4);
        stream.Write("cameralbum!"u8);
        WriteBe32(stream, 19 + idLength);
        stream.Write(json, idStart, idLength);
        stream.Write([0xFF, 0xFF, 0xFF, 0xFF]);
        stream.Write([0x1B, 0x2A, 0x39, 0x48, 0x57, 0x66, 0x75, 0x84, 0x93, 0xA2, 0xB3]);
        return stream.ToArray();
    }

    private static byte[] BuildUuidBox(byte[] payload)
    {
        byte[] result = new byte[24 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        "uuid"u8.CopyTo(result.AsSpan(4));
        "vivoMediaExtInfo"u8.CopyTo(result.AsSpan(8));
        payload.CopyTo(result.AsSpan(24));
        return result;
    }

    private static byte[] BuildMoov(int chunkOffset)
    {
        byte[] stcoPayload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(stcoPayload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32BigEndian(stcoPayload.AsSpan(8), chunkOffset);
        return Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", stcoPayload))))));
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static void WriteBe32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Live Photo Box.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Live Photo Box repository root was not found.");
    }
}

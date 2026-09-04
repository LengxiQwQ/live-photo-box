using System.Buffers.Binary;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

[Trait("Category", "RealSamples")]
public sealed class AppleSplitRegressionTests
{
    [Fact]
    public async Task RebuiltNeutralSplit_ChunkOffsetsPointIntoMediaData()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"lpb-apple-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
                ResolveSample("oppo.jpg"),
                outputDirectory,
                protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
                outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
                CancellationToken.None);

            byte[] mov = await File.ReadAllBytesAsync(result.VideoOutputPath);
            var boxes = EnumerateBoxes(mov, 0, mov.Length).ToArray();
            var mediaDataRanges = boxes
                .Where(x => x.Type == "mdat")
                .Select(x => (Start: x.Offset + 8L, End: x.Offset + x.Size))
                .ToArray();

            Assert.NotEmpty(mediaDataRanges);
            Assert.DoesNotContain(boxes, x => x.Type == "mebx");

            var chunkOffsets = boxes
                .Where(x => x.Type is "stco" or "co64")
                .SelectMany(x => ReadChunkOffsets(mov, x))
                .ToArray();

            Assert.NotEmpty(chunkOffsets);
            Assert.All(chunkOffsets, offset =>
                Assert.True(
                    mediaDataRanges.Any(range => offset >= range.Start && offset < range.End),
                    $"Chunk offset {offset} does not point into an mdat payload."));
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RebuiltSplit_RejectsTargetProtocolWriterOptions()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"lpb-neutral-split-{Guid.NewGuid():N}");

        try
        {
            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                LivePhotoSplitService.SplitAsync(
                    Path.Combine(outputDirectory, "missing.jpg"),
                    outputDirectory,
                    protocolIndex: ProtocolFormatMatrix.SplitProtocolApple,
                    outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
                    CancellationToken.None));

            Assert.Contains("protocol-free neutral media", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { }
        }
    }

    private static IEnumerable<long> ReadChunkOffsets(byte[] data, (string Type, int Offset, int Size) box)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Offset + 12, 4)));
        int cursor = box.Offset + 16;
        for (int i = 0; i < count; i++)
        {
            if (box.Type == "co64")
            {
                yield return BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(cursor, 8));
                cursor += 8;
            }
            else
            {
                yield return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4));
                cursor += 4;
            }
        }
    }

    private static IEnumerable<(string Type, int Offset, int Size)> EnumerateBoxes(byte[] data, int start, int end)
    {
        int offset = start;
        while (offset + 8 <= end)
        {
            long declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            int headerSize = 8;
            if (declaredSize == 1)
            {
                if (offset + 16 > end) yield break;
                declaredSize = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset + 8, 8));
                headerSize = 16;
            }
            else if (declaredSize == 0)
            {
                declaredSize = end - offset;
            }

            if (declaredSize < headerSize || declaredSize > end - offset)
                yield break;

            int size = checked((int)declaredSize);
            string type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            yield return (type, offset, size);

            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "stsd")
            {
                int childStart = offset + headerSize + (type == "stsd" ? 8 : 0);
                foreach (var child in EnumerateBoxes(data, childStart, offset + size))
                    yield return child;
            }

            offset += size;
        }
    }

    private static string ResolveSample(string fileName)
    {
        string directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "designs", "各个机型测试", fileName);
            if (File.Exists(candidate)) return candidate;
            string? parent = Directory.GetParent(directory)?.FullName;
            if (parent == null || parent == directory) break;
            directory = parent;
        }

        throw new FileNotFoundException($"Sample file '{fileName}' not found.");
    }
}

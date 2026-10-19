using System.Buffers.Binary;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorHeifStatusTests
{
    private const string GainMap = "urn:com:apple:photo:2020:aux:hdrgainmap";

    [Theory]
    [InlineData("duplicate-iloc", SourceInspectionFailureCategory.Ambiguous)]
    [InlineData("unknown-iloc", SourceInspectionFailureCategory.Malformed)]
    [InlineData("duplicate-infe", SourceInspectionFailureCategory.Ambiguous)]
    [InlineData("unknown-ipma", SourceInspectionFailureCategory.Malformed)]
    [InlineData("duplicate-ipma", SourceInspectionFailureCategory.Ambiguous)]
    [InlineData("data-reference", SourceInspectionFailureCategory.Unsupported)]
    [InlineData("field-width", SourceInspectionFailureCategory.Unsupported)]
    [InlineData("truncated-iloc", SourceInspectionFailureCategory.Malformed)]
    [InlineData("cycle", SourceInspectionFailureCategory.Ambiguous)]
    [InlineData("missing-tile", SourceInspectionFailureCategory.Malformed)]
    [InlineData("unsupported-derived", SourceInspectionFailureCategory.Unsupported)]
    [InlineData("relationship-capacity", SourceInspectionFailureCategory.Unsupported)]
    [InlineData("item-capacity", SourceInspectionFailureCategory.Unsupported)]
    [InlineData("deep-chain", SourceInspectionFailureCategory.Unsupported)]
    public async Task Inspect_HeifGraphFailure_IsTypedAndCannotIssuePlan(
        string variant, SourceInspectionFailureCategory expected)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath(variant, ".heic");
        byte[] bytes = Build(variant);
        await File.WriteAllBytesAsync(input, bytes);
        byte[] before = SHA256.HashData(bytes);
        var inspector = new SourceInspector();

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => inspector.InspectAsync(input));
        Assert.Equal(expected, error.Category);
        Assert.Equal(SourceInspectionStage.Container, error.Stage);
        // These generic HEIFs claim no vendor capability; do not invent an owner.
        Assert.Equal(0UL, error.Capability);
        SourceInspectionException planError = await Assert.ThrowsAsync<SourceInspectionException>(
            () => inspector.InspectWithPlanAsync(input));
        Assert.Equal(expected, planError.Category);
        Assert.Equal(SourceInspectionStage.Container, planError.Stage);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public unsafe void Inspect_ReusedNativeContext_DoesNotRetainFailureOrPartialGraph()
    {
        using var workspace = new TestFixtureWorkspace();
        using NativeContext context = NativeContext.Create();
        foreach (var (variant, expected) in new[]
        {
            ("valid", NativeInspectionFailureCategory.None),
            ("item-capacity", NativeInspectionFailureCategory.Unsupported),
            ("valid", NativeInspectionFailureCategory.None),
            ("cycle", NativeInspectionFailureCategory.Ambiguous),
            ("unknown-iloc", NativeInspectionFailureCategory.Malformed),
            ("valid", NativeInspectionFailureCategory.None)
        })
        {
            string input = workspace.AllocateFilePath(variant, ".heic");
            File.WriteAllBytes(input, Build(variant));
            var facts = new NativeSourceMediaFacts { StructSize = (uint)sizeof(NativeSourceMediaFacts) };
            NativeResult result = NativeMethods.InspectMedia(context.Handle, input, null, ref facts);
            var status = new NativeInspectionStatus { StructSize = (uint)sizeof(NativeInspectionStatus) };
            Assert.Equal(NativeResult.Ok, NativeMethods.GetLastInspectionStatus(context.Handle, ref status));
            Assert.Equal(expected, status.Category);
            if (expected == NativeInspectionFailureCategory.None)
            {
                Assert.Equal(NativeResult.Ok, result);
                Assert.Equal(1u, facts.AuxiliaryCount);
            }
            else
            {
                Assert.Equal(NativeResult.InvalidArgument, result);
                Assert.Equal(NativeInspectionStage.Container, status.Stage);
                Assert.Equal(0u, facts.AuxiliaryCount);
            }
        }
    }

    private static byte[] Build(string variant)
    {
        if (variant == "item-capacity")
        {
            var items = new List<W3SyntheticItem> { new(1, "hvc1", 0, 4) };
            var references = new List<W3SyntheticReference>();
            for (ushort id = 2; id <= 10; id++)
            {
                items.Add(new(id, "hvc1", (uint)(id - 1) * 4, 4, "urn:test:aux:" + id));
                references.Add(new("auxl", id, [1]));
            }
            return W3HeifSyntheticBuilder.Build(items, references);
        }
        if (variant == "deep-chain")
        {
            var items = new List<W3SyntheticItem> { new(1, "hvc1", 0, 4) };
            var references = new List<W3SyntheticReference> { new("auxl", 2, [1]) };
            for (ushort id = 2; id <= 80; id++)
            {
                items.Add(new(id, id == 80 ? "hvc1" : "grid", (uint)(id - 1) * 4, 4,
                    id == 2 ? GainMap : null));
                if (id < 80) references.Add(new("dimg", id, [(ushort)(id + 1)]));
            }
            return W3HeifSyntheticBuilder.Build(items, references);
        }
        var refs = new List<W3SyntheticReference> { new("auxl", 2, [1]) };
        if (variant == "cycle") { refs.Add(new("dimg", 2, [3])); refs.Add(new("dimg", 3, [2])); }
        if (variant == "missing-tile") refs.Add(new("dimg", 2, [99]));
        if (variant == "unsupported-derived") refs.Add(new("dimg", 2, [3]));
        byte[] bytes = W3HeifSyntheticBuilder.Build(
        [
            new(1, "hvc1", 0, 4),
            new(2, variant == "unsupported-derived" ? "zzzz" : "grid", 4, 4,
                variant == "relationship-capacity" ? new string('a', 120) : GainMap),
            new(3, "grid", 8, 4)
        ], refs);
        int iloc = FindBox(bytes, "meta", "iloc");
        int ipma = FindBox(bytes, "meta", "iprp", "ipma");
        switch (variant)
        {
            case "duplicate-iloc": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(iloc + 30), 1); break;
            case "unknown-iloc": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(iloc + 16), 99); break;
            case "data-reference": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(iloc + 18), 1); break;
            case "field-width": bytes[iloc + 12] = 0x34; break;
            case "unknown-ipma": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(ipma + 16), 99); break;
            case "duplicate-ipma":
                // Two associations to the same property, while retaining enclosing box sizes.
                bytes[ipma + 18] = 2;
                bytes = InsertInMeta(bytes, ipma + 20, [1], ipma,
                    FindBox(bytes, "meta", "iprp"));
                break;
            case "duplicate-infe":
                int iinf = FindBox(bytes, "meta", "iinf");
                int secondInfe = iinf + 14 + checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(iinf + 14)));
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(secondInfe + 12), 1);
                break;
            case "truncated-iloc":
                // Increase declared entries without enlarging the iloc; the next mdat is not table data.
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(iloc + 14), ushort.MaxValue);
                break;
        }
        return bytes;
    }

    private static byte[] InsertInMeta(byte[] bytes, int position, byte[] addition, params int[] ancestors)
    {
        byte[] result = [.. bytes[..position], .. addition, .. bytes[position..]];
        foreach (int parent in ancestors.Append(FindBox(bytes, "meta")))
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(parent),
                checked(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(parent)) + (uint)addition.Length));
        int iloc = FindBox(result, "meta", "iloc");
        int count = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(iloc + 14));
        for (int index = 0; index < count; index++)
        {
            int offset = iloc + 22 + index * 14;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset),
                checked(BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offset)) + (uint)addition.Length));
        }
        return result;
    }

    private static int FindBox(byte[] bytes, params string[] hierarchy)
    {
        int start = 0, end = bytes.Length;
        foreach (string type in hierarchy)
        {
            int current = start;
            while (current < end && !bytes.AsSpan(current + 4, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(type)))
                current += checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(current)));
            Assert.True(current < end, $"Missing synthetic {type} box.");
            end = current + checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(current)));
            start = current + (type == "meta" ? 12 : 8);
        }
        return start - (hierarchy[^1] == "meta" ? 12 : 8);
    }
}

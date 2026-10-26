using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R3XmpMergeTests
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Container = "http://ns.google.com/photos/1.0/container/";
    private const string Item = "http://ns.google.com/photos/1.0/container/item/";
    private const string Camera = "http://ns.google.com/photos/1.0/camera/";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryOnlyInventoryRetainsEveryOrdinaryByteAndOneDirectory(bool mov)
    {
        const string xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><r:Description xmlns:c=\"http://ns.google.com/photos/1.0/container/\" xmlns:i=\"http://ns.google.com/photos/1.0/container/item/\" xmlns:q=\"urn:ordinary\" q:Keep=\"A &amp; B\"><q:Unknown>retain exact text</q:Unknown><c:Directory><r:Seq><r:li r:parseType=\"Resource\"><c:Item i:Semantic=\"Primary\" i:Mime=\"image/jpeg\" q:Extra=\"unchanged\"/></r:li></r:Seq></c:Directory></r:Description></r:RDF></x:xmpmeta>";
        byte[] image = P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, xml);
        var output = await WriteAndCheck(image, nameof(PrimaryOnlyInventoryRetainsEveryOrdinaryByteAndOneDirectory) + mov, mov);
        string actual = SingleXmp(output);
        Assert.Contains("q:Keep=\"A &amp; B\"", actual);
        Assert.Contains("<q:Unknown>retain exact text</q:Unknown>", actual);
        Assert.Contains("q:Extra=\"unchanged\"", actual);
        XDocument tree = XDocument.Parse(actual);
        Assert.Single(tree.Descendants(XName.Get("Directory", Container)));
        Assert.Equal(new[] { "Primary", "MotionPhoto" }, tree.Descendants(XName.Get("Item", Container))
            .Select(n => (string?)n.Attribute(XName.Get("Semantic", Item))).ToArray());
        Assert.Single(tree.Descendants(XName.Get("MotionPhoto", Camera)));
        Assert.Single(tree.Descendants(XName.Get("Description", Rdf)));
    }

    [Fact]
    public async Task SelfClosingRdfExpandsUsingItsActualQualifiedName()
    {
        const string xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><different:RDF xmlns:different=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>";
        var output = await WriteAndCheck(P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, xml),
            nameof(SelfClosingRdfExpandsUsingItsActualQualifiedName), false);
        Assert.Single(XDocument.Parse(SingleXmp(output)).Descendants(XName.Get("RDF", Rdf)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRelease004OriginalInputPublishesUniqueTargetPacket(bool mov)
    {
        string root = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "..", "..", "..", "..", "..", ".."));
        string input = Path.Combine(root, ".ai-tmp", "workspace", "P8", "product-foundation", "R2", "inputs", "image.jpg");
        byte[] image = File.ReadAllBytes(input);
        Assert.Equal("05D8A889CAFD422C5FE0590DD38AA038E8BF8C5DAD202638A9174790990956DD", Convert.ToHexString(SHA256.HashData(image)));
        var output = await WriteAndCheck(image, nameof(ExactRelease004OriginalInputPublishesUniqueTargetPacket) + mov, mov);
        Assert.Single(XDocument.Parse(SingleXmp(output)).Descendants(XName.Get("Directory", Container)));
        Assert.Equal(image, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task V1UsesActualEofOffsetAndPreservesOrdinaryInventory(bool mov, bool exactOriginal)
    {
        byte[] image = P9TargetTestInputs.Image;
        if (exactOriginal)
        {
            string root = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "..", "..", "..", "..", "..", ".."));
            image = File.ReadAllBytes(Path.Combine(root, ".ai-tmp", "workspace", "P8", "product-foundation", "R2", "inputs", "image.jpg"));
        }
        var output = await WriteAndCheck(image, nameof(V1UsesActualEofOffsetAndPreservesOrdinaryInventory) + mov + exactOriginal,
            mov, TargetProtocol.GoogleMicroVideoV1);
        XDocument tree = XDocument.Parse(SingleXmp(output));
        Assert.Equal("1", Assert.Single(tree.Descendants(XName.Get("MicroVideo", Camera))).Value);
        Assert.Equal("1", Assert.Single(tree.Descendants(XName.Get("MicroVideoVersion", Camera))).Value);
        string videoName = mov ? "neutral.mov" : "neutral.mp4";
        Assert.Equal(new FileInfo(Path.Combine(P9TargetTestInputs.Root, videoName)).Length.ToString(),
            Assert.Single(tree.Descendants(XName.Get("MicroVideoOffset", Camera))).Value);
        Assert.Empty(tree.Descendants(XName.Get("MotionPhoto", Camera)));
        Assert.DoesNotContain(tree.Descendants(XName.Get("Item", Container)), n =>
            (string?)n.Attribute(XName.Get("Semantic", Item)) == "MotionPhoto");
    }

    [Fact]
    public async Task ExactRelease004OriginalInputPublishesOppoWithExplicitSyntheticOriginalTime()
    {
        string root = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "..", "..", "..", "..", "..", ".."));
        string input = Path.Combine(root, ".ai-tmp", "workspace", "P8", "product-foundation", "R2", "inputs", "image.jpg");
        byte[] image = File.ReadAllBytes(input);
        Assert.Equal("05D8A889CAFD422C5FE0590DD38AA038E8BF8C5DAD202638A9174790990956DD", Convert.ToHexString(SHA256.HashData(image)));
        var output = await WriteAndCheck(image, nameof(ExactRelease004OriginalInputPublishesOppoWithExplicitSyntheticOriginalTime),
            false, TargetProtocol.OppoLivePhoto);
        XDocument tree = XDocument.Parse(SingleXmp(output));
        Assert.Single(tree.Descendants(XName.Get("Directory", Container)));
        Assert.Equal("100000", Assert.Single(tree.Descendants(XName.Get("MotionPhotoPrimaryPresentationTimestampUs",
            "http://ns.oplus.com/photos/1.0/camera/"))).Value);
        Assert.Equal(image, File.ReadAllBytes(input));
    }

    [Fact]
    public async Task ExtendedOrdinaryPacketRemainsByteExactWithValidatedGuidReference()
    {
        const string extended = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><r:Description r:about=\"\" xmlns:q=\"urn:extended:ordinary\" q:Keep=\"all ordinary extended bytes\"/></r:RDF></x:xmpmeta>";
        byte[] extension = Encoding.UTF8.GetBytes(extended);
        string guid = Convert.ToHexString(MD5.HashData(extension));
        string standard = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><r:RDF xmlns:r=\"" + Rdf + "\"><r:Description r:about=\"\" xmlns:n=\"http://ns.adobe.com/xmp/note/\" n:HasExtendedXMP=\"" + guid + "\"/></r:RDF></x:xmpmeta>";
        byte[] image = P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, standard);
        image = AddExtended(image, extension, guid);
        var output = await WriteAndCheck(image, nameof(ExtendedOrdinaryPacketRemainsByteExactWithValidatedGuidReference), false);
        Assert.Contains(guid, SingleXmp(output));
        Assert.True(output.AsSpan().IndexOf(extension) >= 0);
    }

    [Fact]
    public async Task CrossPacketPropertyShadowCannotPublishOrAlterOrdinaryInput()
    {
        const string extensionXml = "<r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><r:Description r:about=\"\" xmlns:q=\"urn:ordinary\" q:Keep=\"second\"/></r:RDF>";
        byte[] extension = Encoding.UTF8.GetBytes(extensionXml);
        string guid = Convert.ToHexString(MD5.HashData(extension));
        string standard = "<r:RDF xmlns:r=\"" + Rdf + "\"><r:Description r:about=\"\" xmlns:q=\"urn:ordinary\" q:Keep=\"first\" xmlns:n=\"http://ns.adobe.com/xmp/note/\" n:HasExtendedXMP=\"" + guid + "\"/></r:RDF>";
        byte[] image = AddExtended(P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, standard), extension, guid);
        using var folder = new P9TargetTestInputs.Folder(nameof(CrossPacketPropertyShadowCannotPublishOrAlterOrdinaryInput));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, image: image);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4), final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Equal(image, File.ReadAllBytes(bundle.PrimaryImage.Path));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    private static byte[] AddExtended(byte[] image, byte[] xml, string guid)
    {
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0" + guid);
        byte[] payload = new byte[header.Length + 8 + xml.Length];
        header.CopyTo(payload, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(header.Length, 4), (uint)xml.Length);
        xml.CopyTo(payload, header.Length + 8);
        int length = payload.Length + 2;
        return new byte[] { 255, 216, 255, 225, (byte)(length >> 8), (byte)length }
            .Concat(payload).Concat(image.AsSpan(2).ToArray()).ToArray();
    }

    private static async Task<byte[]> WriteAndCheck(byte[] image, string name, bool mov,
        TargetProtocol target = TargetProtocol.GoogleMotionPhotoV2)
    {
        using var folder = new P9TargetTestInputs.Folder(name);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, image: image, mov: mov);
        // Explicit test input, not a claim about this real photo's capture history.
        if (target == TargetProtocol.OppoLivePhoto)
            bundle = bundle with { Semantics = bundle.Semantics with
            { Timing = bundle.Semantics.Timing with { PrimaryTimestampUs = 100000 } } };
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(target, mov ? OutputMediaFormat.JpegMov : OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None);
        byte[] output = File.ReadAllBytes(final);
        using var reader = File.OpenRead(final);
        var expected = new TargetValidationExpectations(target == TargetProtocol.GoogleMicroVideoV1 ? 1u : target == TargetProtocol.OppoLivePhoto ? 3u : 2u, mov ? 1u : 0, 2, 0)
        { UseV2 = target == TargetProtocol.OppoLivePhoto, PrimaryTimestampUs = target == TargetProtocol.OppoLivePhoto ? 100000 : null };
        var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(observed, expected);
        Assert.Equal(File.ReadAllBytes(bundle.MotionVideo!.Path), output.AsSpan(checked((int)observed.Observed.VideoOffset)).ToArray());
        byte[] actualPrimary = output.AsSpan(0, checked((int)observed.Observed.PrimaryLength)).ToArray();
        byte[] originalComparable = (byte[])image.Clone(), actualComparable = (byte[])actualPrimary.Clone();
        int? oldMpf = SingletonSizeField(image), newMpf = SingletonSizeField(actualPrimary);
        Assert.Equal(oldMpf.HasValue, newMpf.HasValue);
        if (oldMpf is int oldSize && newMpf is int newSize)
        {
            // The only non-XMP reflow change authorized here is this derived
            // MP Index self-size, independently observed as a one-image index.
            Assert.Equal((uint)actualPrimary.Length, Read32(actualPrimary, newSize));
            originalComparable.AsSpan(oldSize, 4).Clear(); actualComparable.AsSpan(newSize, 4).Clear();
        }
        Assert.Equal(WithoutXmp(originalComparable), WithoutXmp(actualComparable));
        string? evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R3_OUTPUTS");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllBytes(Path.Combine(evidence, name + ".jpg"), output);
        }
        return output;
    }

    // Independent APP1 walk: compare all non-XMP JPEG bytes, including the
    // complete entropy-coded tail. No production parser/writer helper is used.
    private static List<(int Start, int End)> Packets(byte[] bytes)
    {
        Assert.Equal(new byte[] { 255, 216 }, bytes.AsSpan(0, 2).ToArray());
        byte[] signature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var packets = new List<(int, int)>();
        for (int at = 2; at + 4 <= bytes.Length;)
        {
            Assert.Equal(255, bytes[at]);
            int marker = bytes[at + 1];
            if (marker is 0xda or 0xd9) break;
            int length = (bytes[at + 2] << 8) | bytes[at + 3];
            Assert.InRange(length, 2, bytes.Length - at - 2);
            if (marker == 0xe1 && bytes.AsSpan(at + 4, length - 2).StartsWith(signature)) packets.Add((at, at + 2 + length));
            at += length + 2;
        }
        return packets;
    }

    private static string SingleXmp(byte[] bytes)
    {
        var packet = Assert.Single(Packets(bytes));
        const int signatureLength = 29;
        return Encoding.UTF8.GetString(bytes, packet.Start + 4 + signatureLength, packet.End - packet.Start - 4 - signatureLength);
    }

    private static byte[] WithoutXmp(byte[] bytes)
    {
        using var result = new MemoryStream();
        int at = 0;
        foreach (var packet in Packets(bytes))
        {
            result.Write(bytes.AsSpan(at, packet.Start - at));
            at = packet.End;
        }
        result.Write(bytes.AsSpan(at));
        return result.ToArray();
    }

    private static uint Read32(byte[] bytes, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));

    private static int? SingletonSizeField(byte[] bytes)
    {
        int? result = null;
        for (int at = 2; at + 4 <= bytes.Length;)
        {
            int marker = bytes[at + 1]; if (marker is 0xda or 0xd9) break;
            int length = (bytes[at + 2] << 8) | bytes[at + 3];
            if (marker == 0xe2 && bytes.AsSpan(at + 4, length - 2).StartsWith("MPF\0"u8))
            {
                Assert.Null(result);
                int tiff = at + 8;
                Assert.Equal("MM\0*"u8.ToArray(), bytes.AsSpan(tiff, 4).ToArray());
                int ifd = checked(tiff + (int)Read32(bytes, tiff + 4));
                int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ifd, 2));
                int? table = null; uint? images = null;
                for (int i = 0; i < count; i++)
                {
                    int entry = ifd + 2 + 12 * i;
                    ushort tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry, 2));
                    if (tag == 0xb001) { Assert.Null(images); images = Read32(bytes, entry + 8); }
                    if (tag == 0xb002) { Assert.Null(table); Assert.Equal(16u, Read32(bytes, entry + 4)); table = checked(tiff + (int)Read32(bytes, entry + 8)); }
                }
                Assert.Equal(1u, images);
                int tableOffset = table ?? throw new InvalidDataException("Missing MPEntry table.");
                Assert.Equal(0x00030000u, Read32(bytes, tableOffset));
                Assert.Equal(0u, Read32(bytes, tableOffset + 8));
                Assert.Equal(0u, Read32(bytes, tableOffset + 12));
                result = tableOffset + 4;
            }
            at += length + 2;
        }
        return result;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorXmpScopeTests
{
    private const string Camera = "http://ns.google.com/photos/1.0/camera/";
    private const string Container = "http://ns.google.com/photos/1.0/container/";
    private const string Item = "http://ns.google.com/photos/1.0/container/item/";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_LocalContainerPrefixRebinding_UsesTheInnerScope(bool wrongInnerUri)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("prefix-scope", ".jpg");
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        string inner = wrongInnerUri ? "urn:unrelated:container" : Container;
        // The same prefix is declared at two lexical scopes. The inner URI is authoritative.
        string xml = Packet($"xmlns:g=\"{Camera}\" xmlns:dir=\"urn:outer:container\" xmlns:i=\"{Item}\" g:MotionPhoto=\"1\" g:MotionPhotoVersion=\"1\"",
            $"<dir:Directory xmlns:dir=\"{inner}\"><rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\"><dir:Item i:Semantic=\"Primary\" i:Mime=\"image/jpeg\" i:Length=\"0\"/></rdf:li>" +
            $"<rdf:li rdf:parseType=\"Resource\"><dir:Item i:Semantic=\"MotionPhoto\" i:Mime=\"video/mp4\" i:Length=\"{video.Length}\"/></rdf:li>" +
            "</rdf:Seq></dir:Directory>");
        byte[] bytes = [.. SyntheticProtocolFixtures.CreateJpegWithXmp(xml), .. video];
        await File.WriteAllBytesAsync(input, bytes);
        var inspector = new SourceInspector();
        if (wrongInnerUri)
        {
            var error = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(input));
            Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
            Assert.Equal(SourceInspectionStage.Protocol, error.Stage);
        }
        else
        {
            var facts = await inspector.InspectAsync(input);
            Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
            Assert.Equal(video.Length, facts.MotionVideo!.ByteLength);
        }
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(input)));
    }

    [Theory]
    [InlineData("unrelated-first", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    [InlineData("unrelated-last", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    [InlineData("duplicate-related", SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Metadata)]
    [InlineData("conflicting-related", SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Metadata)]
    [InlineData("version-only", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    [InlineData("timestamp-only", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    [InlineData("v1-version-only", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    [InlineData("v1-timestamp-only", SourceInspectionFailureCategory.Malformed, SourceInspectionStage.Protocol)]
    public async Task Inspect_MultipleHeifXmpItems_NeverHideRelevantDeclarations(
        string variant, SourceInspectionFailureCategory category, SourceInspectionStage stage)
    {
        string unrelated = Packet("xmlns:u=\"urn:unrelated\" u:Title=\"ordinary metadata\"", "");
        string related = Packet($"xmlns:g=\"{Camera}\" g:MotionPhoto=\"1\" g:MotionPhotoVersion=\"1\"", "");
        string other = variant == "conflicting-related"
            ? Packet($"xmlns:g=\"{Camera}\" g:MotionPhoto=\"0\" g:MotionPhotoVersion=\"1\"", "")
            : related;
        string[] packets = variant switch
        {
            "unrelated-first" => [unrelated, related],
            "unrelated-last" => [related, unrelated],
            "version-only" => [unrelated, Packet($"xmlns:g=\"{Camera}\" g:MotionPhotoVersion=\"1\"", "")],
            "timestamp-only" => [unrelated, Packet($"xmlns:g=\"{Camera}\" g:MotionPhotoPresentationTimestampUs=\"0\"", "")],
            "v1-version-only" => [unrelated, Packet($"xmlns:g=\"{Camera}\" g:MicroVideoVersion=\"1\"", "")],
            "v1-timestamp-only" => [unrelated, Packet($"xmlns:g=\"{Camera}\" g:MicroVideoPresentationTimestampUs=\"0\"", "")],
            _ => [related, other]
        };
        byte[] bytes = BuildHeif(packets);
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath(variant, ".heic");
        await File.WriteAllBytesAsync(input, bytes);
        var inspector = new SourceInspector();
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(input));
        Assert.Equal(category, error.Category);
        Assert.Equal(stage, error.Stage);
        var planError = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectWithPlanAsync(input));
        Assert.Equal(category, planError.Category);
        Assert.Equal(stage, planError.Stage);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Inspect_MultipleUnrelatedHeifXmpItems_PreservesNonLiveClassification()
    {
        byte[] bytes = BuildHeif(
        [
            Packet("xmlns:u=\"urn:unrelated\" u:Title=\"ordinary\"", ""),
            Packet("xmlns:g=\"urn:unrelated:camera\" g:MotionPhoto=\"1\"", "")
        ]);
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("unrelated-rdf-items", ".heic");
        await File.WriteAllBytesAsync(input, bytes);
        var facts = await new SourceInspector().InspectAsync(input);
        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.Null(facts.MotionVideo);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(input)));
    }

    private static string Packet(string attributes, string body) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        $"<rdf:Description rdf:about=\"\" {attributes}>{body}</rdf:Description></rdf:RDF></x:xmpmeta>";

    [Theory]
    [InlineData("http://ns.oplus.com/photos/1.0/camera/", "VideoLength", "99")]
    [InlineData("http://ns.oplus.com/photos/1.0/camera/", "MotionPhotoPrimaryPresentationTimestampUs", "0")]
    [InlineData("http://ns.oplus.com/photos/1.0/camera/", "MotionPhotoEnable", "True")]
    [InlineData("http://ns.vivo.com/photos/1.0/camera/", "VMotionPhotoVersion", "1")]
    [InlineData("http://ns.vivo.com/photos/1.0/camera/", "VMotionPhotoFlags", "0")]
    [InlineData("http://ns.vivo.com/photos/1.0/camera/", "VMotionPhotoSource", "1")]
    [InlineData("http://ns.vivo.com/photos/1.0/camera/", "VMediaKitVersion", "1.0")]
    public async Task Inspect_VendorOnlyHeifPacket_IsRelevantInEitherItemOrder(string uri, string local, string value)
    {
        string unrelated = Packet("xmlns:u=\"urn:ordinary\" u:Title=\"retained\"", "");
        string partial = Packet($"xmlns:p=\"{uri}\" p:{local}=\"{value}\"", "");
        using var workspace = new TestFixtureWorkspace();
        foreach (string[] packets in new[] { new[] { unrelated, partial }, new[] { partial, unrelated } })
        {
            string input = workspace.AllocateFilePath("vendor-packet", ".heic");
            await File.WriteAllBytesAsync(input, BuildHeif(packets));
            var error = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(input));
            Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
            Assert.Equal(SourceInspectionStage.Protocol, error.Stage);
        }
    }

    [Fact]
    public async Task Inspect_HeifMotionItemWithoutCameraDeclaration_IsNotNonLive()
    {
        string partial = Packet($"xmlns:dir=\"{Container}\" xmlns:i=\"{Item}\"",
            "<dir:Directory><rdf:Seq><rdf:li><dir:Item i:Semantic=\"MotionPhoto\" i:Mime=\"video/mp4\" i:Length=\"4\"/></rdf:li></rdf:Seq></dir:Directory>");
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("motion-item-only", ".heic");
        await File.WriteAllBytesAsync(input, BuildHeif([partial]));
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(input));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
        Assert.Equal(SourceInspectionStage.Protocol, error.Stage);
    }

    [Theory]
    [InlineData("MicroVideoVersion")]
    [InlineData("MicroVideoPresentationTimestampUs")]
    [InlineData("VMotionPhotoSource")]
    [InlineData("VMediaKitVersion")]
    [InlineData("OLivePhotoVersion")]
    public async Task Inspect_MalformedXmpWithPartialLiveProperty_IsNotNonLive(string local)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("malformed-partial", ".jpg");
        string xml = Packet($"xmlns:g=\"{Camera}\" g:{local}=\"1\"", "<unclosed>");
        await File.WriteAllBytesAsync(input, SyntheticProtocolFixtures.CreateJpegWithXmp(xml));
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(input));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, error.Category);
        Assert.Equal(SourceInspectionStage.Metadata, error.Stage);
    }

    [Fact]
    public async Task Inspect_ExtendedXmpHugeDeclaredSize_RejectsBeforeDeclaredSizeAllocation()
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("extended-xmp-capacity", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegExtendedXmpShadow(input);
        byte[] bytes = await File.ReadAllBytesAsync(input);
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
        bool changed = false;
        for (int position = 2; position + 4 <= bytes.Length;)
        {
            Assert.Equal(0xff, bytes[position]);
            byte marker = bytes[position + 1];
            if (marker is 0xda or 0xd9) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2));
            if (marker == 0xe1 && bytes.AsSpan(position + 4, Math.Min(header.Length, length - 2)).SequenceEqual(header))
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(position + 4 + header.Length + 32), uint.MaxValue);
                changed = true;
                break;
            }
            position += 2 + length;
        }
        Assert.True(changed);
        await File.WriteAllBytesAsync(input, bytes);
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(input));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
        Assert.Equal(SourceInspectionStage.Metadata, error.Stage);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(input)));
    }

    // Two formally owned mime items, each with its own iloc extent and cdsc edge.
    // This fixture has no image codec claim; P1 validates the HEIF item structure.
    private static byte[] BuildHeif(string[] packets)
    {
        byte[][] payloads = [[0x11, 0x22, 0x33, 0x44], .. packets.Select(Encoding.UTF8.GetBytes)];
        byte[] ftyp = Box("ftyp", [.. Encoding.ASCII.GetBytes("heic"), .. U32(0), .. Encoding.ASCII.GetBytes("mif1")]);
        byte[] Meta(uint payloadStart)
        {
            var infe = new List<byte[]>();
            var loc = new List<byte> { 0, 0, 0, 0, 0x44, 0 };
            loc.AddRange(U16((ushort)payloads.Length));
            var refs = new List<byte[]>();
            uint offset = payloadStart;
            for (ushort index = 0; index < payloads.Length; index++)
            {
                ushort id = (ushort)(index + 1);
                byte[] type = Encoding.ASCII.GetBytes(index == 0 ? "hvc1" : "mime");
                byte[] mime = index == 0 ? [] : [.. Encoding.ASCII.GetBytes("application/rdf+xml"), 0];
                infe.Add(Box("infe", [2, 0, 0, 0, .. U16(id), 0, 0, .. type, 0, .. mime]));
                loc.AddRange([.. U16(id), 0, 0, 0, 1, .. U32(offset), .. U32((uint)payloads[index].Length)]);
                offset += (uint)payloads[index].Length;
                if (index > 0) refs.Add(Box("cdsc", [.. U16(id), 0, 1, 0, 1]));
            }
            return Box("meta", [0, 0, 0, 0,
                .. Box("pitm", [0, 0, 0, 0, 0, 1]),
                .. Box("iinf", [0, 0, 0, 0, .. U16((ushort)infe.Count), .. infe.SelectMany(x => x)]),
                .. Box("iloc", [.. loc]),
                .. Box("iref", [0, 0, 0, 0, .. refs.SelectMany(x => x)])]);
        }
        byte[] meta = Meta(0);
        meta = Meta(checked((uint)(ftyp.Length + meta.Length + 8)));
        return [.. ftyp, .. meta, .. Box("mdat", [.. payloads.SelectMany(x => x)])];
    }

    private static byte[] Box(string type, byte[] body) =>
        [.. U32(checked((uint)(8 + body.Length))), .. Encoding.ASCII.GetBytes(type), .. body];
    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];
    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}

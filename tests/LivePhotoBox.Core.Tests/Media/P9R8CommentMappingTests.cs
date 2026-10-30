using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8CommentMappingTests
{
    private sealed record Comment(ushort Type, uint Count, byte[] Raw);
    private static readonly XNamespace P = "urn:livephotobox:preserved-exif:1";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.OppoLivePhoto)]
    [InlineData(TargetProtocol.VivoLivePhoto)]
    public async Task OrdinaryCommentIsMappedWithExactOriginalTuple(TargetProtocol target)
    {
        string primary = Cached("vivo双文件.jpg"), video = Cached("vivo双文件.mp4");
        string sourceHash = Hash(primary), videoHash = Hash(video);
        using var folder = new P9TargetTestInputs.Folder(nameof(OrdinaryCommentIsMappedWithExactOriginalTuple) + target);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(target, OutputMediaFormat.JpegMp4);
        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, video, workspace, profile.MediaRequirement, PreservationPolicy.Strict);
        var original = ReadComment(File.ReadAllBytes(bundle.PrimaryImage.Path));
        string neutralHash = Hash(bundle.PrimaryImage.Path);
        string output = Path.Combine(folder.Path, "mapped.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None,
            oppoInitialCoverPolicy: target == TargetProtocol.OppoLivePhoto ? OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover : OppoInitialCoverPolicy.RequireKnownOriginal);
        byte[] generated = File.ReadAllBytes(output);
        AssertMapping(generated, original);
        var marker = ReadComment(generated);
        Assert.Equal((ushort)7, marker.Type);
        byte[] required = target == TargetProtocol.OppoLivePhoto ? Encoding.ASCII.GetBytes("ASCII\0\0\0oplus_10485792") : Encoding.ASCII.GetBytes("multi-frame: 1;\nispap:1;\nmodule: photo;\0");
        Assert.Equal(required, marker.Raw);
        Assert.Equal((uint)required.Length, marker.Count);
        Assert.Equal(neutralHash, Hash(bundle.PrimaryImage.Path));
        Assert.Equal(sourceHash, Hash(primary)); Assert.Equal(videoHash, Hash(video));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.OppoLivePhoto)]
    [InlineData(TargetProtocol.VivoLivePhoto)]
    public async Task MappedCommentSurvivesTargetBackToNeutral(TargetProtocol target)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(MappedCommentSurvivesTargetBackToNeutral) + target);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(target, OutputMediaFormat.JpegMp4);
        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(Cached("vivo双文件.jpg"), Cached("vivo双文件.mp4"), workspace, profile.MediaRequirement, PreservationPolicy.Strict);
        var original = ReadComment(File.ReadAllBytes(bundle.PrimaryImage.Path));
        string output = Path.Combine(folder.Path, "mapped.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None,
            oppoInitialCoverPolicy: target == TargetProtocol.OppoLivePhoto ? OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover : OppoInitialCoverPolicy.RequireKnownOriginal);
        using var secondWorkspace = new MediaWorkspace();
        var neutral = await service.CreateNeutralBundleAsync(output, null, secondWorkspace, null, PreservationPolicy.Strict);
        AssertMapping(File.ReadAllBytes(neutral.PrimaryImage.Path), original);
        string otherTarget = Path.Combine(folder.Path, "mapped-v2.jpg");
        await StructuredTargetWriterService.WriteAsync(neutral, OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            otherTarget, secondWorkspace.RootDirectory, false, CancellationToken.None);
        AssertMapping(File.ReadAllBytes(otherTarget), original);
    }

    private static void AssertMapping(byte[] image, Comment original)
    {
        byte[] packet = Assert.Single(App1(image).Where(p => p.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)));
        var document = XDocument.Parse(Encoding.UTF8.GetString(packet, 29, packet.Length - 29));
        var record = Assert.Single(document.Descendants(P + "ExifUserComment"));
        Assert.Equal(Rdf + "Description", record.Parent!.Name);
        Assert.Equal("", record.Parent.Attribute(Rdf + "about")!.Value);
        Assert.Equal("Resource", record.Attribute(Rdf + "parseType")!.Value);
        Assert.Equal(8, record.Elements().Count());
        string Field(string name) => Assert.Single(record.Elements(P + name)).Value;
        Assert.Equal("1", Field("schemaVersion")); Assert.Equal("ExifIFD", Field("owner")); Assert.Equal("37510", Field("tag"));
        Assert.Equal(original.Type.ToString(), Field("tiffType")); Assert.Equal(original.Count.ToString(), Field("count"));
        Assert.Equal(original.Raw.Length.ToString(), Field("byteLength"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original.Raw)), Field("rawSha256"));
        Assert.Equal(Convert.ToHexString(original.Raw), Field("rawHex"));
    }
    // Test-only independent formal APP1/TIFF traversal; production keeps all
    // media parsing and writing in Native.
    private static Comment ReadComment(byte[] image)
    {
        byte[] exif = Assert.Single(App1(image).Where(p => p.AsSpan().StartsWith("Exif\0\0"u8)));
        bool big = exif[6] == 'M';
        ushort U16(int at) => big ? (ushort)((exif[at] << 8) | exif[at + 1]) : (ushort)(exif[at] | (exif[at + 1] << 8));
        uint U32(int at) => big ? ((uint)exif[at] << 24) | ((uint)exif[at + 1] << 16) | ((uint)exif[at + 2] << 8) | exif[at + 3] : exif[at] | ((uint)exif[at + 1] << 8) | ((uint)exif[at + 2] << 16) | ((uint)exif[at + 3] << 24);
        int Entry(int directory, ushort tag)
        {
            int count = U16(directory); var matches = Enumerable.Range(0, count).Select(i => directory + 2 + i * 12).Where(at => U16(at) == tag).ToArray();
            return Assert.Single(matches);
        }
        int root = checked(6 + (int)U32(10)), pointer = Entry(root, 0x8769);
        Assert.Equal((ushort)4, U16(pointer + 2)); Assert.Equal(1u, U32(pointer + 4));
        int entry = Entry(checked(6 + (int)U32(pointer + 8)), 0x9286);
        ushort type = U16(entry + 2); uint size = U32(entry + 4); Assert.Contains(type, new ushort[] { 2, 7 });
        int start = size <= 4 ? entry + 8 : checked(6 + (int)U32(entry + 8));
        return new(type, size, exif.AsSpan(start, checked((int)size)).ToArray());
    }
    private static List<byte[]> App1(byte[] image)
    {
        Assert.Equal(0xff, image[0]); Assert.Equal(0xd8, image[1]); var result = new List<byte[]>(); int at = 2;
        while (at < image.Length)
        {
            Assert.Equal(0xff, image[at++]); while (image[at] == 0xff) at++;
            byte marker = image[at++]; if (marker is 0xda or 0xd9) break;
            if (marker == 1) continue;
            int length = (image[at] << 8) | image[at + 1]; Assert.InRange(length, 2, image.Length - at);
            if (marker == 0xe1) result.Add(image.AsSpan(at + 2, length - 2).ToArray()); at += length;
        }
        return result;
    }
    private static string Cached(string name)
    {
        string path = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples", name));
        Assert.True(File.Exists(path), "Required RealSample is missing: " + path); return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}

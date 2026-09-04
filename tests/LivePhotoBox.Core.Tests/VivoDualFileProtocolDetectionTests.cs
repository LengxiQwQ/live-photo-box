using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "RealSamples")]
public sealed class VivoDualFileProtocolDetectionTests
{
    [Fact]
    public async Task DetectDualFileProtocol_RealVivoPair_ReturnsVivo()
    {
        LivePhotoProtocolType protocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
            ResolveSample("vivo双文件.jpg"),
            ResolveSample("vivo双文件.mp4"),
            token: CancellationToken.None);

        Assert.Equal(LivePhotoProtocolType.Vivo, protocol);
    }

    [Fact]
    public async Task DetectDualFileProtocol_RealApplePair_ReturnsApple()
    {
        LivePhotoProtocolType protocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
            ResolveSample("苹果双文件.HEIC"),
            ResolveSample("苹果双文件.MOV"),
            token: CancellationToken.None);

        Assert.Equal(LivePhotoProtocolType.Apple, protocol);
    }

    [Fact]
    public async Task DetectDualFileProtocol_MatchingAppleMetadataWithDifferentNames_ReturnsUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_dual_name_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string imagePath = Path.Combine(dir, "image-name.HEIC");
        string videoPath = Path.Combine(dir, "video-name.MOV");

        try
        {
            File.Copy(ResolveSample("苹果双文件.HEIC"), imagePath);
            File.Copy(ResolveSample("苹果双文件.MOV"), videoPath);

            LivePhotoProtocolType protocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
                imagePath,
                videoPath,
                token: CancellationToken.None);

            Assert.Equal(LivePhotoProtocolType.Unknown, protocol);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DetectDualFileProtocol_SameNameFilesWithoutMatchingMetadata_ReturnsUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_dual_detect_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string imagePath = Path.Combine(dir, "same-name.HEIC");
        string videoPath = Path.Combine(dir, "same-name.MOV");

        try
        {
            File.Copy(ResolveSample("苹果双文件.HEIC"), imagePath);
            File.Copy(ResolveSample("苹果-双文件.MOV"), videoPath);

            LivePhotoProtocolType protocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
                imagePath,
                videoPath,
                token: CancellationToken.None);

            Assert.Equal(LivePhotoProtocolType.Unknown, protocol);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Detect_SingleFileJpeg_WithHdrOnlyContainer_ReturnsUnknown()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_hdr_detect_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "hdr_only.jpg");

        try
        {
            const string xmp =
                "<x:xmpmeta><rdf:RDF><rdf:Description>" +
                "<Container:Directory><rdf:Seq>" +
                "<rdf:li><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>" +
                "<rdf:li><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"123\"/></rdf:li>" +
                "</rdf:Seq></Container:Directory>" +
                "</rdf:Description></rdf:RDF></x:xmpmeta>";
            File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);

            LivePhotoProtocolType protocol = LivePhotoProtocolDetector.Detect(
                path, LivePhotoType.SingleFileJpeg, contentIdentifier: null, xmpText: xmp);

            Assert.Equal(LivePhotoProtocolType.Unknown, protocol);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Detect_DualFileJpeg_WithHdrContainerXmp_PrefersVivoTail()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_vivo_detect_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "test.jpg");

        try
        {
            // HDR 增益图的 Container:Directory 会让旧的检测逻辑误判成 Google V2，
            // 但只要文件带 vivo 双文件尾标，就必须优先识别为 Vivo。
            const string xmp =
                "<x:xmpmeta><rdf:RDF><rdf:Description>" +
                "<Container:Directory><rdf:Seq>" +
                "<rdf:li><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>" +
                "<rdf:li><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"123\"/></rdf:li>" +
                "</rdf:Seq></Container:Directory>" +
                "</rdf:Description></rdf:RDF></x:xmpmeta>";

            byte[] xmpPayload = Encoding.UTF8.GetBytes(
                "http://ns.adobe.com/xap/1.0/\0" + xmp);

            using var ms = new MemoryStream();
            ms.Write([0xFF, 0xD8]); // SOI
            ms.Write([0xFF, 0xE1]); // APP1 XMP
            ms.Write(new byte[]
            {
                (byte)((xmpPayload.Length + 2) >> 8),
                (byte)((xmpPayload.Length + 2) & 0xFF)
            });
            ms.Write(xmpPayload);
            ms.Write([0xFF, 0xD9]); // EOI
            ms.Write(Encoding.UTF8.GetBytes(
                "vivo{\"com.android.camera.livephoto\":\"abcd1234abcd1234abcd1234abcd12\"}"));
            File.WriteAllBytes(path, ms.ToArray());

            LivePhotoProtocolType protocol = LivePhotoProtocolDetector.Detect(
                path, LivePhotoType.DualFile, contentIdentifier: null);

            Assert.Equal(LivePhotoProtocolType.Vivo, protocol);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string ResolveSample(params string[] pathParts)
    {
        string path = Path.Combine([AppContext.BaseDirectory, "samples", .. pathParts]);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Sample not found: {path}");
        return path;
    }
}

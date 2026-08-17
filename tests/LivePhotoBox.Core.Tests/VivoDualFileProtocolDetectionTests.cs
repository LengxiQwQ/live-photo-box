using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class VivoDualFileProtocolDetectionTests
{
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
}

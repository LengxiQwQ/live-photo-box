using System;
using System.IO;
using System.Text;

namespace LivePhotoBox.Core.Tests.Protocols;

internal static class SyntheticProtocolFixtures
{
    private static readonly byte[] MinimalJpegHeader = [
        0xFF, 0xD8, // SOI
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, // APP0 JFIF
    ];

    private static readonly byte[] MinimalJpegBody = [
        0xFF, 0xDB, 0x00, 0x43, 0x00, // DQT
        0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14,
        0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12, 0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A,
        0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C,
        0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00, // SOF0 16x16
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, // SOS
        0x7F, 0xFF, 0x00, 0x00, // Scan entropy data
        0xFF, 0xD9 // EOI
    ];

    public static byte[] CreateJpegWithApp1(byte[] app1Payload)
    {
        using var ms = new MemoryStream();
        ms.Write(MinimalJpegHeader);

        // Write APP1 segment
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE1);
        int segLen = app1Payload.Length + 2;
        ms.WriteByte((byte)(segLen >> 8));
        ms.WriteByte((byte)(segLen & 0xFF));
        ms.Write(app1Payload);

        ms.Write(MinimalJpegBody);
        return ms.ToArray();
    }

    public static byte[] CreateJpegWithXmp(string xmpContent)
    {
        byte[] xmpHeader = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        byte[] xmpBody = Encoding.UTF8.GetBytes(xmpContent);
        byte[] payload = new byte[xmpHeader.Length + xmpBody.Length];
        Buffer.BlockCopy(xmpHeader, 0, payload, 0, xmpHeader.Length);
        Buffer.BlockCopy(xmpBody, 0, payload, xmpHeader.Length, xmpBody.Length);
        return CreateJpegWithApp1(payload);
    }

    public static byte[] CreateMinimalMp4()
    {
        return [
            0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x08, (byte)'m', (byte)'d', (byte)'a', (byte)'t',
            0x00, 0x00, 0x00, 0x08, (byte)'m', (byte)'o', (byte)'o', (byte)'v'];
    }

    public static void CreateGoogleV1Jpeg(string outputPath)
    {
        byte[] dummyMp4 = CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MicroVideo=\"1\" GCamera:MicroVideoVersion=\"1\" GCamera:MicroVideoOffset=\"{dummyMp4.Length}\" /></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = CreateJpegWithXmp(xmp);

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
    }

    public static void CreateGoogleV2Jpeg(string outputPath, bool withGainMap = true)
    {
        byte[] dummyMp4 = CreateMinimalMp4();
        byte[] dummyGainMap = [0xFF, 0xD8, 0xFF, 0xD9];
        string gainMapItem = withGainMap
            ? $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"GainMap\" Item:Length=\"{dummyGainMap.Length}\" Item:Padding=\"0\" /></rdf:li>"
            : "";

        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\" /></rdf:li>{gainMapItem}<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" Item:Padding=\"0\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = CreateJpegWithXmp(xmp);

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        if (withGainMap) fs.Write(dummyGainMap);
        fs.Write(dummyMp4);
    }

    public static void CreateOppoJpeg(string outputPath)
    {
        byte[] dummyMp4 = CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:OpCamera=\"http://ns.oplus.com/photos/1.0/camera/\" OpCamera:OLivePhotoVersion=\"1\" OpCamera:VideoLength=\"{dummyMp4.Length}\" OpCamera:MotionPhotoOwner=\"oplus\" OpCamera:MotionPhotoPrimaryPresentationTimestampUs=\"123\" OpCamera:MotionPhotoEnable=\"True\" /></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = CreateJpegWithXmp(xmp);

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
    }

    public static void CreateGoogleV2JpegWithNormalMotionPhotoText(string outputPath)
    {
        byte[] dummyMp4 = CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:Camera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" xmlns:Other=\"urn:example:unrelated\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" GCamera:MotionPhoto=\"1\" Camera:MotionPhotoVersion=\"1\" Other:MotionPhoto=\"1\"><dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">A normal note mentioning MotionPhoto and LIVE_ must survive.</rdf:li></rdf:Alt></dc:description><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Other:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Other:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        xmp = xmp.Replace("<Container:Directory><rdf:Seq>", "<Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" /></rdf:li>");
        byte[] jpeg = CreateJpegWithXmp(xmp);
        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
    }

    public static void CreateWrongNamespaceMotionPhotoJpeg(string outputPath)
    {
        byte[] dummyMp4 = Encoding.UTF8.GetBytes("DUMMY_MP4_WRONG_NAMESPACE_25B");
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:Other=\"urn:example:unrelated\" xmlns:WrongContainer=\"urn:example:container\" xmlns:WrongItem=\"urn:example:item\" Other:MotionPhoto=\"1\"><WrongContainer:Directory><rdf:Seq><rdf:li><WrongContainer:Item WrongItem:Mime=\"video/mp4\" WrongItem:Semantic=\"MotionPhoto\" WrongItem:Length=\"{dummyMp4.Length}\" /></rdf:li></rdf:Seq></WrongContainer:Directory><dc:description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><rdf:li>MotionPhoto is ordinary text here.</rdf:li></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = CreateJpegWithXmp(xmp);
        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
    }

    public static void CreateGoogleV2JpegWithScopedPrefixRebinding(string outputPath)
    {
        byte[] dummyMp4 = CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" GCamera:MotionPhoto=\"1\"><note xmlns:GCamera=\"urn:example:unrelated\" GCamera:MotionPhoto=\"keep-this\" /><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        xmp = xmp.Replace("<Container:Directory><rdf:Seq>", "<Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" /></rdf:li>");
        byte[] jpeg = CreateJpegWithXmp(xmp);
        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
    }

    public static void CreateVivoX300Jpeg(string outputPath)
    {
        byte[] dummyGainMap = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] dummyMp4 = CreateMinimalMp4();
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:VCamera=\"http://ns.vivo.com/photos/1.0/camera/\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" VCamera:VMotionPhotoVersion=\"1\" VCamera:VMotionPhotoFlags=\"0\"><Container:Directory><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"GainMap\" Item:Length=\"{dummyGainMap.Length}\" Item:Padding=\"0\" /></rdf:li><rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{dummyMp4.Length}\" Item:Padding=\"0\" /></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = CreateJpegWithXmp(xmp);

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyGainMap);
        fs.Write(dummyMp4);
    }

    public static void CreateVivoLegacyDualJpeg(string outputPath)
    {
        byte[] jpeg = CreateJpegWithXmp("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");
        byte[] tail = Encoding.UTF8.GetBytes("vivo{\"com.android.camera.livephoto\":\"synthetic-vivo-id\"}cameralbum!");

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(tail);
    }

    public static void CreateVivoLegacyDualMp4(string outputPath)
    {
        using var ms = new MemoryStream();
        // ftyp box
        WriteBox(ms, "ftyp", Encoding.UTF8.GetBytes("isom\0\0\x02\0isommp41"));

        // top-level vivo uuid box
        byte[] vivoUuid = [
            0x76, 0x69, 0x76, 0x6F, 0x4D, 0x65, 0x64, 0x69,
            0x61, 0x45, 0x78, 0x74, 0x49, 0x6E, 0x66, 0x6F,
            0x7B, 0x22, 0x63, 0x6F, 0x6D, 0x2E, 0x61, 0x6E, 0x64, 0x72, 0x6F, 0x69, 0x64,
            0x2E, 0x63, 0x61, 0x6D, 0x65, 0x72, 0x61, 0x2E, 0x6C, 0x69, 0x76, 0x65, 0x70,
            0x68, 0x6F, 0x74, 0x6F, 0x22, 0x3A, 0x22, 0x73, 0x79, 0x6E, 0x74, 0x68, 0x65,
            0x74, 0x69, 0x63, 0x2D, 0x76, 0x69, 0x76, 0x6F, 0x2D, 0x69, 0x64, 0x22, 0x7D
        ];
        WriteBox(ms, "uuid", vivoUuid);

        // moov box
        using (var moovMs = new MemoryStream())
        {
            WriteBox(ms, "moov", moovMs.ToArray());
        }

        // mdat box
        WriteBox(ms, "mdat", Encoding.UTF8.GetBytes("DUMMY_VIVO_VIDEO_DATA"));
        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    public static void CreateSamsungSefJpeg(string outputPath, bool includeNonLiveTag = true)
    {
        byte[] jpeg = CreateJpegWithXmp("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");
        using var ms = new MemoryStream();
        ms.Write(jpeg);

        // SEF Payload 1: MotionPhoto_Data (0x0A30)
        byte[] motionVideo = CreateMinimalMp4();
        byte[] mpDataPayload = [0x00, 0x00, 0x30, 0x0A, 0x10, 0x00, 0x00, 0x00,
            (byte)'M', (byte)'o', (byte)'t', (byte)'i', (byte)'o', (byte)'n', (byte)'P', (byte)'h', (byte)'o', (byte)'t', (byte)'o', (byte)'_', (byte)'D', (byte)'a', (byte)'t', (byte)'a',
            .. motionVideo];
        uint mpDataLen = (uint)mpDataPayload.Length;

        // SEF Payload 2: Non-live DualCam (0x0A01)
        byte[] dualCamPayload = [0x00, 0x00, 0x01, 0x0A, 0x0C, 0x00, 0x00, 0x00,
            (byte)'D', (byte)'u', (byte)'a', (byte)'l', (byte)'C', (byte)'a', (byte)'m', (byte)'_', (byte)'D', (byte)'a', (byte)'t', (byte)'a',
            (byte)'N', (byte)'O', (byte)'N', (byte)'_', (byte)'L', (byte)'I', (byte)'V', (byte)'E', (byte)'_', (byte)'M', (byte)'E', (byte)'T', (byte)'A', (byte)'D', (byte)'A', (byte)'T', (byte)'A'];
        uint dualCamLen = (uint)dualCamPayload.Length;

        ms.Write(mpDataPayload);
        if (includeNonLiveTag)
        {
            ms.Write(dualCamPayload);
        }

        // SEFH
        uint entryCount = includeNonLiveTag ? 2u : 1u;
        ms.Write(Encoding.UTF8.GetBytes("SEFH"));
        WriteLe32(ms, 107); // Version
        WriteLe32(ms, entryCount);

        // Entry 1: 0x0A30
        uint mpOffset = includeNonLiveTag ? (mpDataLen + dualCamLen) : mpDataLen;
        WriteLe16(ms, 0);
        WriteLe16(ms, 0x0A30);
        WriteLe32(ms, mpOffset);
        WriteLe32(ms, mpDataLen);

        // Entry 2: 0x0A01
        if (includeNonLiveTag)
        {
            WriteLe16(ms, 0);
            WriteLe16(ms, 0x0A01);
            WriteLe32(ms, dualCamLen);
            WriteLe32(ms, dualCamLen);
        }

        // Total SEF size + SEFT
        uint totalSefSize = (uint)(12 + entryCount * 12);
        WriteLe32(ms, totalSefSize);
        ms.Write(Encoding.UTF8.GetBytes("SEFT"));

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    public static void CreateSamsungHeic(string outputPath)
    {
        using var ms = new MemoryStream();
        // ftyp box
        WriteBox(ms, "ftyp", Encoding.UTF8.GetBytes("heic\0\0\0\0mif1heic"));
        // meta box
        WriteBox(ms, "meta", new byte[16]);
        // Keep the still-image item before the private motion-photo boxes.
        WriteBox(ms, "mdat", Encoding.UTF8.GetBytes("DUMMY_IMAGE_ITEM_DATA"));

        // mpvd box (Samsung Motion Photo box)
        byte[] motionVideo = CreateMinimalMp4();
        long mpvdStart = ms.Position;
        WriteBox(ms, "mpvd", motionVideo);

        // sefd box with a pointer-style MotionPhoto_Data entry and a valid
        // SEFH/SEFT directory. The pointer is the absolute file offset of
        // the MP4 payload immediately after the mpvd header.
        using var sefd = new MemoryStream();
        sefd.WriteByte(0); sefd.WriteByte(0); // prefix
        WriteLe16(sefd, 0x0A30);
        WriteLe32(sefd, 16);
        sefd.Write(Encoding.ASCII.GetBytes("MotionPhoto_Data"));
        sefd.Write(Encoding.ASCII.GetBytes("mpv2"));
        WriteBe32(sefd, checked((uint)(mpvdStart + 8)));
        WriteBe32(sefd, checked((uint)motionVideo.Length));
        sefd.Write(Encoding.ASCII.GetBytes("SEFH"));
        WriteLe32(sefd, 107);
        WriteLe32(sefd, 1);
        WriteLe16(sefd, 0);
        WriteLe16(sefd, 0x0A30);
        WriteLe32(sefd, 36);
        WriteLe32(sefd, 36);
        WriteLe32(sefd, 24);
        sefd.Write(Encoding.ASCII.GetBytes("SEFT"));
        WriteBox(ms, "sefd", sefd.ToArray());

        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    public static void CreateHuaweiJpeg(string outputPath)
    {
        byte[] jpeg = CreateJpegWithXmp("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>");
        byte[] dummyMp4 = CreateMinimalMp4();
        
        byte[] liveTail = new byte[60];
        // 40 bytes of padding / timestamp
        Encoding.UTF8.GetBytes("0000000000000000000000000000000000000000").CopyTo(liveTail, 0);
        // LIVE_ marker at byte offset 40
        Encoding.UTF8.GetBytes("LIVE_").CopyTo(liveTail, 40);
        // mp4_len + 20 formatted into 15 chars
        string lenStr = (dummyMp4.Length + 20).ToString();
        Encoding.UTF8.GetBytes(lenStr).CopyTo(liveTail, 45);

        using var fs = File.Create(outputPath);
        fs.Write(jpeg);
        fs.Write(dummyMp4);
        fs.Write(liveTail);
    }

    public static void CreateAppleJpeg(string outputPath)
    {
        // MakerNote with tag 0x0011 (Live Photo ContentIdentifier) + non-live tag 0x0001
        using var mnMs = new MemoryStream();
        mnMs.Write(Encoding.UTF8.GetBytes("Apple iOS\0\0\x01MM")); // Signature + BE
        WriteBe16(mnMs, 2); // 2 entries

        // Entry 1: tag 0x0011 (Live Photo)
        WriteBe16(mnMs, 0x0011);
        WriteBe16(mnMs, 2); // ASCII
        WriteBe32(mnMs, 36); // length
        WriteBe32(mnMs, 40); // offset

        // Entry 2: tag 0x0001 (Non-live MakerNote tag e.g. Exposure)
        WriteBe16(mnMs, 0x0001);
        WriteBe16(mnMs, 4); // LONG
        WriteBe32(mnMs, 1);
        WriteBe32(mnMs, 100); // inline value

        // Payload for 0x0011
        mnMs.Write(Encoding.UTF8.GetBytes("12345678-ABCD-1234-ABCD-1234567890AB\0"));

        byte[] exifPayload = mnMs.ToArray();
        byte[] app1 = new byte[exifPayload.Length + 6];
        Encoding.UTF8.GetBytes("Exif\0\0").CopyTo(app1, 0);
        Buffer.BlockCopy(exifPayload, 0, app1, 6, exifPayload.Length);

        byte[] jpeg = CreateJpegWithApp1(app1);
        File.WriteAllBytes(outputPath, jpeg);
    }

    public static void CreateAppleMov(string outputPath)
    {
        using var ms = new MemoryStream();
        // ftyp box
        WriteBox(ms, "ftyp", Encoding.UTF8.GetBytes("qt  \0\0\0\0qt  "));

        // moov box with meta, keys, and ilst
        using (var moovMs = new MemoryStream())
        {
            using (var metaMs = new MemoryStream())
            {
                // hdlr box
                WriteBox(metaMs, "hdlr", Encoding.UTF8.GetBytes("\0\0\0\0\0\0\0\0mdirappl\0\0\0\0\0\0\0\0\0"));
                
                // keys box
                byte[] keysPayload;
                using (var keysMs = new MemoryStream())
                {
                    WriteBe32(keysMs, 0); // version/flags
                    WriteBe32(keysMs, 1); // 1 key
                    byte[] keyVal = Encoding.UTF8.GetBytes("com.apple.quicktime.content.identifier");
                    WriteBe32(keysMs, (uint)(keyVal.Length + 8));
                    keysMs.Write(Encoding.UTF8.GetBytes("mdta"));
                    keysMs.Write(keyVal);
                    keysPayload = keysMs.ToArray();
                }
                WriteBox(metaMs, "keys", keysPayload);

                // ilst box with 1-based index 1
                byte[] ilstPayload;
                using (var ilstMs = new MemoryStream())
                {
                    using (var item1Ms = new MemoryStream())
                    {
                        using (var dataMs = new MemoryStream())
                        {
                            WriteBe32(dataMs, 1); // type 1 = UTF8
                            WriteBe32(dataMs, 0); // locale
                            dataMs.Write(Encoding.UTF8.GetBytes("12345678-ABCD-1234-ABCD-1234567890AB"));
                            WriteBox(item1Ms, "data", dataMs.ToArray());
                        }
                        WriteBe32(ilstMs, (uint)(item1Ms.Length + 8));
                        WriteBe32(ilstMs, 1); // 1-based index 1
                        item1Ms.Position = 0;
                        item1Ms.CopyTo(ilstMs);
                    }
                    ilstPayload = ilstMs.ToArray();
                }
                WriteBox(metaMs, "ilst", ilstPayload);

                WriteBox(moovMs, "meta", metaMs.ToArray());
            }

            // trak with mebx metadata
            using (var trakMs = new MemoryStream())
            {
                using (var mdiaMs = new MemoryStream())
                {
                    // hdlr box with handler type 'meta'
                    WriteBox(mdiaMs, "hdlr", Encoding.UTF8.GetBytes("\0\0\0\0\0\0\0\0metaappl\0\0\0\0\0\0\0\0\0"));

                    using (var minfMs = new MemoryStream())
                    {
                        using (var stblMs = new MemoryStream())
                        {
                            using (var stsdMs = new MemoryStream())
                            {
                                WriteBe32(stsdMs, 0); // version/flags
                                WriteBe32(stsdMs, 1); // 1 entry
                                WriteBox(stsdMs, "mebx",
                                    Encoding.UTF8.GetBytes("com.apple.quicktime.live-photo-info"));
                                WriteBox(stblMs, "stsd", stsdMs.ToArray());
                            }
                            WriteBox(minfMs, "stbl", stblMs.ToArray());
                        }
                        WriteBox(mdiaMs, "minf", minfMs.ToArray());
                    }
                    WriteBox(trakMs, "mdia", mdiaMs.ToArray());
                }
                WriteBox(moovMs, "trak", trakMs.ToArray());
            }

            WriteBox(ms, "moov", moovMs.ToArray());
        }

        // mdat box
        WriteBox(ms, "mdat", Encoding.UTF8.GetBytes("DUMMY_APPLE_VIDEO_TRACK"));
        File.WriteAllBytes(outputPath, ms.ToArray());
    }

    private static void WriteBox(Stream s, string type, byte[] payload)
    {
        uint size = (uint)(payload.Length + 8);
        WriteBe32(s, size);
        s.Write(Encoding.UTF8.GetBytes(type));
        s.Write(payload);
    }

    private static void WriteBe16(Stream s, ushort val)
    {
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)(val & 0xFF));
    }

    private static void WriteBe32(Stream s, uint val)
    {
        s.WriteByte((byte)(val >> 24));
        s.WriteByte((byte)((val >> 16) & 0xFF));
        s.WriteByte((byte)((val >> 8) & 0xFF));
        s.WriteByte((byte)(val & 0xFF));
    }

    private static void WriteLe16(Stream s, ushort val)
    {
        s.WriteByte((byte)(val & 0xFF));
        s.WriteByte((byte)(val >> 8));
    }

    private static void WriteLe32(Stream s, uint val)
    {
        s.WriteByte((byte)(val & 0xFF));
        s.WriteByte((byte)((val >> 8) & 0xFF));
        s.WriteByte((byte)((val >> 16) & 0xFF));
        s.WriteByte((byte)(val >> 24));
    }
}

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class NativePreservationObservationAdversarialTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    [Fact]
    public async Task Observation_FakeExifInEntropyCodestream_IsIgnoredByNative()
    {
        string samplePath = ResolveSample("小米.jpg");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        // Baseline observation on valid image
        string validPath = workspace.AllocateFilePath("valid-orig", ".jpg");
        await File.WriteAllBytesAsync(validPath, rawBytes);
        var baselineObs = await NativeMediaService.CapturePreservationObservationAsync(
            validPath, SourceProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg);
        Assert.True(baselineObs.HasExif);

        // Append a fake APP1 Exif signature in entropy codestream (after SOS)
        // Find SOS (0xFF, 0xDA)
        int sosIdx = -1;
        for (int i = 0; i < rawBytes.Length - 1; i++)
        {
            if (rawBytes[i] == 0xFF && rawBytes[i + 1] == 0xDA)
            {
                sosIdx = i;
                break;
            }
        }
        Assert.True(sosIdx > 0);

        byte[] adversarialBytes = (byte[])rawBytes.Clone();
        byte[] fakeExif = Encoding.ASCII.GetBytes("Exif\0\0II*\0\x08\0\0\0\x01\0\x12\x01\x03\0\x01\0\0\0\x06\0\0\0\0\0\0\0");
        int injectPos = sosIdx + 50;
        if (injectPos + fakeExif.Length < adversarialBytes.Length - 2)
        {
            Buffer.BlockCopy(fakeExif, 0, adversarialBytes, injectPos, fakeExif.Length);
        }

        string advPath = workspace.AllocateFilePath("adv-exif", ".jpg");
        await File.WriteAllBytesAsync(advPath, adversarialBytes);

        var advObs = await NativeMediaService.CapturePreservationObservationAsync(
            advPath, SourceProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg);

        // Native authoritative Exif observation must match the true APP1 segment, NOT the fake data injected into entropy
        Assert.Equal(baselineObs.ExifIfd0NonPtrSha256, advObs.ExifIfd0NonPtrSha256);
        Assert.Equal(baselineObs.Orientation, advObs.Orientation);
    }

    [Fact]
    public async Task Observation_FakeXmpInTrailer_IsIgnoredByNative()
    {
        string samplePath = ResolveSample("小米.jpg");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        string validPath = workspace.AllocateFilePath("valid-xmp", ".jpg");
        await File.WriteAllBytesAsync(validPath, rawBytes);
        var baselineObs = await NativeMediaService.CapturePreservationObservationAsync(
            validPath, SourceProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg);

        // Append fake XMP trailer after EOI
        byte[] fakeXmpTrailer = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:fake=\"http://fake.org/\"><fake:Injected>adversarial</fake:Injected></rdf:Description></rdf:RDF></x:xmpmeta>");
        byte[] adversarialBytes = new byte[rawBytes.Length + fakeXmpTrailer.Length];
        Buffer.BlockCopy(rawBytes, 0, adversarialBytes, 0, rawBytes.Length);
        Buffer.BlockCopy(fakeXmpTrailer, 0, adversarialBytes, rawBytes.Length, fakeXmpTrailer.Length);

        string advPath = workspace.AllocateFilePath("adv-xmp", ".jpg");
        await File.WriteAllBytesAsync(advPath, adversarialBytes);

        var advObs = await NativeMediaService.CapturePreservationObservationAsync(
            advPath, SourceProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg);

        // The authoritative XMP segment is parsed from APP1, trailer noise is ignored
        Assert.Equal(baselineObs.XmpNonprotocolSha256, advObs.XmpNonprotocolSha256);
    }

    [Fact]
    public async Task Observation_FakeIccAfterEoi_IsIgnoredByNative()
    {
        string samplePath = ResolveSample("oppo.jpg");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        string validPath = workspace.AllocateFilePath("valid-icc", ".jpg");
        await File.WriteAllBytesAsync(validPath, rawBytes);
        var baselineObs = await NativeMediaService.CapturePreservationObservationAsync(
            validPath, SourceProtocol.OppoLivePhoto, ImageContainer.Jpeg);

        // Append fake ICC APP2 chunk after EOI
        byte[] fakeIcc = Encoding.ASCII.GetBytes("ICC_PROFILE\0\x01\x01FAKE_ICC_PAYLOAD_DATA_HERE");
        byte[] adversarialBytes = new byte[rawBytes.Length + fakeIcc.Length];
        Buffer.BlockCopy(rawBytes, 0, adversarialBytes, 0, rawBytes.Length);
        Buffer.BlockCopy(fakeIcc, 0, adversarialBytes, rawBytes.Length, fakeIcc.Length);

        string advPath = workspace.AllocateFilePath("adv-icc", ".jpg");
        await File.WriteAllBytesAsync(advPath, adversarialBytes);

        var advObs = await NativeMediaService.CapturePreservationObservationAsync(
            advPath, SourceProtocol.OppoLivePhoto, ImageContainer.Jpeg);

        Assert.Equal(baselineObs.IccSha256, advObs.IccSha256);
    }

    [Fact]
    public async Task Observation_AmbiguousHeicAuxRelationship_SetsAmbiguousFlag()
    {
        string samplePath = ResolveSample("三星.heic");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        // Tamper rawBytes by injecting a duplicate auxl box into iref
        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int irefPos = -1;
        for (int i = 0; i <= tamperedBytes.Length - 8; i++)
        {
            if (tamperedBytes[i + 4] == 'i' && tamperedBytes[i + 5] == 'r' && tamperedBytes[i + 6] == 'e' && tamperedBytes[i + 7] == 'f')
            {
                irefPos = i;
                break;
            }
        }
        Assert.True(irefPos > 0);

        int auxlPos = -1;
        for (int i = irefPos; i <= tamperedBytes.Length - 14; i++)
        {
            if (tamperedBytes[i + 4] == 'a' && tamperedBytes[i + 5] == 'u' && tamperedBytes[i + 6] == 'x' && tamperedBytes[i + 7] == 'l')
            {
                auxlPos = i;
                break;
            }
        }
        Assert.True(auxlPos > 0);

        // Expand ref_count by 1 and write duplicate reference
        ushort origRefCount = BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(auxlPos + 10, 2));
        BinaryPrimitives.WriteUInt16BigEndian(tamperedBytes.AsSpan(auxlPos + 10, 2), (ushort)(origRefCount + 1));
        uint origToId = BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(auxlPos + 12, 2));
        BinaryPrimitives.WriteUInt16BigEndian(tamperedBytes.AsSpan(auxlPos + 14, 2), (ushort)origToId);

        uint auxlSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(auxlPos, 4));
        BinaryPrimitives.WriteUInt32BigEndian(tamperedBytes.AsSpan(auxlPos, 4), auxlSize + 2);

        uint irefSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(irefPos, 4));
        BinaryPrimitives.WriteUInt32BigEndian(tamperedBytes.AsSpan(irefPos, 4), irefSize + 2);

        string tamperedPath = workspace.AllocateFilePath("samsung-ambig-aux", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        var obs = await NativeMediaService.CapturePreservationObservationAsync(
            tamperedPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);

        // Duplicate auxl relation must set the ambiguous error flag
        Assert.True(obs.HeicAuxAmbiguous, "Ambiguous HEIC aux relation must set HeicAuxAmbiguous flag.");
    }

    [Fact]
    public async Task Observation_UnsupportedMultiExtentHeic_SetsCodestreamError()
    {
        string samplePath = ResolveSample("三星.heic");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        uint? primaryIdOpt = PreservationTestHelpers.ExtractHeicPrimaryItemId(rawBytes);
        Assert.NotNull(primaryIdOpt);
        uint primaryId = primaryIdOpt.Value;

        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int ilocPos = -1;
        for (int i = 0; i <= tamperedBytes.Length - 8; i++)
        {
            if (tamperedBytes[i + 4] == 'i' && tamperedBytes[i + 5] == 'l' && tamperedBytes[i + 6] == 'o' && tamperedBytes[i + 7] == 'c')
            {
                ilocPos = i;
                break;
            }
        }
        Assert.True(ilocPos > 0);

        int p = ilocPos + 8;
        byte ver = tamperedBytes[p++];
        p += 3;
        int offsetSize = (tamperedBytes[p] >> 4) & 0x0F;
        int lengthSize = tamperedBytes[p] & 0x0F;
        int baseOffsetSize = (tamperedBytes[p + 1] >> 4) & 0x0F;
        int indexSize = (ver == 1 || ver == 2) ? (tamperedBytes[p + 1] & 0x0F) : 0;
        p += 2;

        uint itemCount = ver < 2
            ? BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(p, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(p, 4));
        p += (ver < 2) ? 2 : 4;

        for (uint it = 0; it < itemCount; it++)
        {
            uint itemId = ver < 2
                ? BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(p, 2))
                : BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(p, 4));
            p += (ver < 2) ? 2 : 4;
            if (ver == 1 || ver == 2) p += 2;
            p += 2;
            p += baseOffsetSize;
            int extentCountPos = p;
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(p, 2));
            p += 2;

            if (itemId == primaryId)
            {
                // Tamper extent_count to 2
                BinaryPrimitives.WriteUInt16BigEndian(tamperedBytes.AsSpan(extentCountPos, 2), 2);
                break;
            }

            for (ushort e = 0; e < extentCount; e++)
            {
                if ((ver == 1 || ver == 2) && indexSize > 0) p += indexSize;
                p += offsetSize + lengthSize;
            }
        }

        string tamperedPath = workspace.AllocateFilePath("samsung-multi-extent", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        var obs = await NativeMediaService.CapturePreservationObservationAsync(
            tamperedPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);

        // Multi-extent HEIF must fail closed with CodestreamError
        Assert.True(obs.CodestreamError, "Unsupported multi-extent HEIF must set CodestreamError flag.");
    }
}


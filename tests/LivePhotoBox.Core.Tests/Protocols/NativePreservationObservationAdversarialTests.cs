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

[Trait("Category", "RealSamples")]
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

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Observation_HeicMissingPitm_FailsClosedWithCodestreamError()
    {
        string samplePath = ResolveSample("三星.heic");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        // Baseline: untampered file should succeed
        string validPath = workspace.AllocateFilePath("valid-baseline", ".heic");
        await File.WriteAllBytesAsync(validPath, rawBytes);
        var baselineObs = await NativeMediaService.CapturePreservationObservationAsync(
            validPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);
        Assert.False(baselineObs.CodestreamError);
        Assert.NotEmpty(baselineObs.ImageCodestreamSha256);

        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int metaPos = -1;
        int p = 0;
        while (p <= tamperedBytes.Length - 8)
        {
            uint sz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(p, 4));
            if (sz < 8 || p + sz > tamperedBytes.Length) break;
            if (tamperedBytes[p + 4] == 'm' && tamperedBytes[p + 5] == 'e' &&
                tamperedBytes[p + 6] == 't' && tamperedBytes[p + 7] == 'a')
            {
                metaPos = p;
                break;
            }
            p += (int)sz;
        }
        Assert.True(metaPos >= 0);

        uint metaSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4));
        int metaEnd = metaPos + (int)metaSize;
        int pitmPos = -1;
        int cp = metaPos + 12;
        while (cp <= metaEnd - 8)
        {
            uint csz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(cp, 4));
            if (csz < 8 || cp + csz > metaEnd) break;
            if (tamperedBytes[cp + 4] == 'p' && tamperedBytes[cp + 5] == 'i' &&
                tamperedBytes[cp + 6] == 't' && tamperedBytes[cp + 7] == 'm')
            {
                pitmPos = cp;
                break;
            }
            cp += (int)csz;
        }
        Assert.True(pitmPos > metaPos);

        // Strip pitm box by renaming its fourcc to 'free'
        tamperedBytes[pitmPos + 4] = (byte)'f';
        tamperedBytes[pitmPos + 5] = (byte)'r';
        tamperedBytes[pitmPos + 6] = (byte)'e';
        tamperedBytes[pitmPos + 7] = (byte)'e';

        string tamperedPath = workspace.AllocateFilePath("samsung-no-pitm", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        var obs = await NativeMediaService.CapturePreservationObservationAsync(
            tamperedPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);

        // Must fail closed with CodestreamError, and must NOT hash the first mdat
        Assert.True(obs.CodestreamError, "Missing pitm must set CodestreamError flag.");
        Assert.Empty(obs.ImageCodestreamSha256);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Observation_HeicDuplicatePitm_FailsClosedWithCodestreamError()
    {
        string samplePath = ResolveSample("三星.heic");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int metaPos = -1;
        int p = 0;
        while (p <= tamperedBytes.Length - 8)
        {
            uint sz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(p, 4));
            if (sz < 8 || p + sz > tamperedBytes.Length) break;
            if (tamperedBytes[p + 4] == 'm' && tamperedBytes[p + 5] == 'e' &&
                tamperedBytes[p + 6] == 't' && tamperedBytes[p + 7] == 'a')
            {
                metaPos = p;
                break;
            }
            p += (int)sz;
        }
        Assert.True(metaPos >= 0);

        uint metaSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4));
        int metaEnd = metaPos + (int)metaSize;
        int pitmPos = -1;
        int cp = metaPos + 12;
        while (cp <= metaEnd - 8)
        {
            uint csz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(cp, 4));
            if (csz < 8 || cp + csz > metaEnd) break;
            if (tamperedBytes[cp + 4] == 'p' && tamperedBytes[cp + 5] == 'i' &&
                tamperedBytes[cp + 6] == 't' && tamperedBytes[cp + 7] == 'm')
            {
                pitmPos = cp;
                break;
            }
            cp += (int)csz;
        }
        Assert.True(pitmPos > metaPos);

        uint pitmSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(pitmPos, 4));

        uint freeSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(metaEnd, 4));
        Assert.True(freeSize > pitmSize + 8);

        // Copy pitm to metaEnd, expand meta by pitmSize, and shrink free by pitmSize
        byte[] pitmBox = new byte[pitmSize];
        Buffer.BlockCopy(tamperedBytes, pitmPos, pitmBox, 0, (int)pitmSize);

        BinaryPrimitives.WriteUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4), metaSize + pitmSize);
        Buffer.BlockCopy(pitmBox, 0, tamperedBytes, metaEnd, (int)pitmSize);

        int newFreePos = metaEnd + (int)pitmSize;
        uint newFreeSize = freeSize - pitmSize;
        BinaryPrimitives.WriteUInt32BigEndian(tamperedBytes.AsSpan(newFreePos, 4), newFreeSize);
        tamperedBytes[newFreePos + 4] = (byte)'f';
        tamperedBytes[newFreePos + 5] = (byte)'r';
        tamperedBytes[newFreePos + 6] = (byte)'e';
        tamperedBytes[newFreePos + 7] = (byte)'e';

        string tamperedPath = workspace.AllocateFilePath("samsung-dup-pitm", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        var obs = await NativeMediaService.CapturePreservationObservationAsync(
            tamperedPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);

        // Duplicate pitm must fail closed with CodestreamError and empty codestream hash
        Assert.True(obs.CodestreamError, "Duplicate pitm must set CodestreamError flag.");
        Assert.Empty(obs.ImageCodestreamSha256);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Observation_HeicColrWithoutIpmaLink_FailsClosedWithIccError()
    {
        string samplePath = ResolveSample("三星.heic");
        if (!File.Exists(samplePath)) return;

        byte[] rawBytes = await File.ReadAllBytesAsync(samplePath);
        using var workspace = new MediaWorkspace();

        // Baseline: untampered file has ICC
        string validPath = workspace.AllocateFilePath("valid-baseline-icc", ".heic");
        await File.WriteAllBytesAsync(validPath, rawBytes);
        var baselineObs = await NativeMediaService.CapturePreservationObservationAsync(
            validPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);
        Assert.True(baselineObs.HasIcc);
        Assert.False(baselineObs.IccParseError);
        Assert.NotEmpty(baselineObs.IccSha256);

        uint? primaryIdOpt = PreservationTestHelpers.ExtractHeicPrimaryItemId(rawBytes);
        Assert.NotNull(primaryIdOpt);
        uint primaryId = primaryIdOpt.Value;

        byte[] tamperedBytes = (byte[])rawBytes.Clone();
        int metaPos = -1;
        int p = 0;
        while (p <= tamperedBytes.Length - 8)
        {
            uint sz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(p, 4));
            if (sz < 8 || p + sz > tamperedBytes.Length) break;
            if (tamperedBytes[p + 4] == 'm' && tamperedBytes[p + 5] == 'e' &&
                tamperedBytes[p + 6] == 't' && tamperedBytes[p + 7] == 'a')
            {
                metaPos = p;
                break;
            }
            p += (int)sz;
        }
        Assert.True(metaPos >= 0);

        uint metaSize = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(metaPos, 4));
        int metaEnd = metaPos + (int)metaSize;
        int ipmaPos = -1;

        int cp = metaPos + 12;
        while (cp <= metaEnd - 8)
        {
            uint csz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(cp, 4));
            if (csz < 8 || cp + csz > metaEnd) break;
            if (tamperedBytes[cp + 4] == 'i' && tamperedBytes[cp + 5] == 'p' &&
                tamperedBytes[cp + 6] == 'm' && tamperedBytes[cp + 7] == 'a')
            {
                ipmaPos = cp;
                break;
            }
            if (tamperedBytes[cp + 4] == 'i' && tamperedBytes[cp + 5] == 'p' &&
                tamperedBytes[cp + 6] == 'r' && tamperedBytes[cp + 7] == 'p')
            {
                int iprpEnd = cp + (int)csz;
                int icp = cp + 8;
                while (icp <= iprpEnd - 8)
                {
                    uint isz = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(icp, 4));
                    if (isz < 8 || icp + isz > iprpEnd) break;
                    if (tamperedBytes[icp + 4] == 'i' && tamperedBytes[icp + 5] == 'p' &&
                        tamperedBytes[icp + 6] == 'm' && tamperedBytes[icp + 7] == 'a')
                    {
                        ipmaPos = icp;
                        break;
                    }
                    icp += (int)isz;
                }
            }
            if (ipmaPos >= 0) break;
            cp += (int)csz;
        }
        Assert.True(ipmaPos >= 0);

        int pp = ipmaPos + 8;
        byte ver = tamperedBytes[pp++];
        int flags = (tamperedBytes[pp] << 16) | (tamperedBytes[pp + 1] << 8) | tamperedBytes[pp + 2];
        pp += 3;
        bool isLarge = (flags & 1) != 0;

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(pp, 4));
        pp += 4;

        bool brokenLink = false;
        for (uint i = 0; i < entryCount; i++)
        {
            uint itemId = ver < 1
                ? BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(pp, 2))
                : BinaryPrimitives.ReadUInt32BigEndian(tamperedBytes.AsSpan(pp, 4));
            pp += ver < 1 ? 2 : 4;
            byte assocCount = tamperedBytes[pp++];

            for (byte a = 0; a < assocCount; a++)
            {
                int propPos = pp;
                int propIndex = isLarge
                    ? (BinaryPrimitives.ReadUInt16BigEndian(tamperedBytes.AsSpan(pp, 2)) & 0x7FFF)
                    : (tamperedBytes[pp] & 0x7F);
                pp += isLarge ? 2 : 1;

                // Samsung primary item uses colr property at index 1.
                // Mutate the association to a non-existent property index (e.g. 99) to break the link.
                if (itemId == primaryId && propIndex == 1)
                {
                    if (isLarge)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(tamperedBytes.AsSpan(propPos, 2), 99);
                    }
                    else
                    {
                        tamperedBytes[propPos] = 99;
                    }
                    brokenLink = true;
                }
            }
        }
        Assert.True(brokenLink, "Failed to locate and break colr link in ipma.");

        string tamperedPath = workspace.AllocateFilePath("samsung-broken-colr-link", ".heic");
        await File.WriteAllBytesAsync(tamperedPath, tamperedBytes);

        var obs = await NativeMediaService.CapturePreservationObservationAsync(
            tamperedPath, SourceProtocol.SamsungMotionPhotoHeic, ImageContainer.Heic);

        // Colr exists in file, but primary item has no ipma link to it -> must fail closed with IccParseError
        Assert.True(obs.IccParseError, "HEIC colr without ipma link must set IccParseError flag.");
        Assert.False(obs.HasIcc);
        Assert.Empty(obs.IccSha256);
    }
}


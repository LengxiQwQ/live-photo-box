using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
using System.Text;
using Xunit;
using System;
using System.IO;

namespace LivePhotoBox.Core.Tests;

public sealed class Mp4MdtaKeyStripperDifferentialTests
{
    private static byte[] BuildBox(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        box[4] = (byte)type[0];
        box[5] = (byte)type[1];
        box[6] = (byte)type[2];
        box[7] = (byte)type[3];
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] BuildFullBox(string type, byte version, uint flags, byte[] payload)
    {
        byte[] fullPayload = new byte[4 + payload.Length];
        fullPayload[0] = version;
        fullPayload[1] = (byte)(flags >> 16);
        fullPayload[2] = (byte)(flags >> 8);
        fullPayload[3] = (byte)flags;
        payload.CopyTo(fullPayload, 4);
        return BuildBox(type, fullPayload);
    }

    [Fact]
    public void StripStsdTracks_IsByteIdenticalToLegacyParser()
    {
        // Construct a mock stsd inside trak
        byte[] textFragment = Encoding.ASCII.GetBytes("com.apple.quicktime.live-photo-info... padding string");
        byte[] stsdPayload = [.. new byte[8], .. BuildBox("txt ", textFragment)]; // dummy stsd
        byte[] stsd = BuildFullBox("stsd", 0, 0, stsdPayload);
        byte[] stbl = BuildBox("stbl", stsd);
        
        // hdlr for meta
        byte[] hdlrPayload = new byte[20];
        hdlrPayload[4] = (byte)'m';
        hdlrPayload[5] = (byte)'e';
        hdlrPayload[6] = (byte)'t';
        hdlrPayload[7] = (byte)'a';
        byte[] hdlr = BuildFullBox("hdlr", 0, 0, hdlrPayload);

        byte[] minf = BuildBox("minf", stbl);
        byte[] mdia = BuildBox("mdia", [.. hdlr, .. minf]);
        byte[] trak = BuildBox("trak", mdia);

        // A second trak to keep
        byte[] hdlrPayloadVide = new byte[20];
        hdlrPayloadVide[4] = (byte)'v';
        hdlrPayloadVide[5] = (byte)'i';
        hdlrPayloadVide[6] = (byte)'d';
        hdlrPayloadVide[7] = (byte)'e';
        byte[] hdlrVide = BuildFullBox("hdlr", 0, 0, hdlrPayloadVide);
        byte[] mdiaVide = BuildBox("mdia", [.. hdlrVide, .. BuildBox("minf", [])]);
        byte[] trakKeep = BuildBox("trak", mdiaVide);

        byte[] moov = BuildBox("moov", [.. trakKeep, .. trak]);
        byte[] fileData = [.. BuildBox("ftyp", new byte[16]), .. moov, .. new byte[100]];

        bool nativeSuccess = Interop.NativeMp4MdtaKeyStripper.TryStripTracks(
            fileData, ["com.apple.quicktime.live-photo-info"], out byte[]? nativeResult, out string? nativeError);

        // We run the legacy manually by invoking it on a file and bypassing Native.
        // But since TryStripTracks currently tries Native first, we'll temporarly rename or just use reflection to call StripTracksWithKeys
        var legacyMethod = typeof(Mp4MdtaKeyStripper).GetMethod("StripTracksWithKeys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        byte[]? legacyResult = (byte[]?)legacyMethod!.Invoke(null, new object[] { fileData, new string[] { "com.apple.quicktime.live-photo-info" } });

        Assert.True(nativeSuccess, nativeError);
        Assert.NotNull(legacyResult);
        Assert.Equal(legacyResult, nativeResult);
    }
}

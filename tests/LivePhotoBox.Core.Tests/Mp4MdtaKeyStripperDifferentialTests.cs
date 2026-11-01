using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
using System.Text;
using Xunit;
using System;
using System.IO;

namespace LivePhotoBox.Core.Tests;

[Trait("Evidence", "HistoricalAlgorithm")]
public sealed class Mp4MdtaKeyStripperDifferentialTests
{
    private static byte[] BuildBox(byte[] type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        type.CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

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
        // Construct a valid metadata trak with tkhd and mebx
        byte[] keyName = Encoding.UTF8.GetBytes("com.apple.quicktime.live-photo-info");
        byte[] keydPayload = [.. Encoding.ASCII.GetBytes("mdta"), .. keyName];
        byte[] keyd = BuildBox("keyd", keydPayload);
        byte[] keyItem = BuildBox(new byte[] { 0, 0, 0, 1 }, keyd);
        byte[] keys = BuildBox("keys", keyItem);
        
        byte[] mebxPayload = [0, 0, 0, 0, 0, 0, 0, 1, .. keys];
        byte[] mebx = BuildBox("mebx", mebxPayload);
        
        byte[] stsdPayload = [0, 0, 0, 1, .. mebx];
        byte[] stsd = BuildFullBox("stsd", 0, 0, stsdPayload);
        byte[] stbl = BuildBox("stbl", stsd);
        
        byte[] hdlrPayload = new byte[20];
        hdlrPayload[4] = (byte)'m';
        hdlrPayload[5] = (byte)'e';
        hdlrPayload[6] = (byte)'t';
        hdlrPayload[7] = (byte)'a';
        byte[] hdlr = BuildFullBox("hdlr", 0, 0, hdlrPayload);

        byte[] minf = BuildBox("minf", stbl);
        byte[] mdia = BuildBox("mdia", [.. hdlr, .. minf]);
        
        byte[] tkhdPayload1 = new byte[92];
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload1.AsSpan(8), 1);
        byte[] tkhd1 = BuildFullBox("tkhd", 0, 0, tkhdPayload1);
        byte[] trak = BuildBox("trak", [.. tkhd1, .. mdia]);

        byte[] tkhdPayload2 = new byte[92];
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload2.AsSpan(8), 2);
        byte[] tkhd2 = BuildFullBox("tkhd", 0, 0, tkhdPayload2);
        
        byte[] hdlrPayloadVide = new byte[20];
        hdlrPayloadVide[4] = (byte)'v';
        hdlrPayloadVide[5] = (byte)'i';
        hdlrPayloadVide[6] = (byte)'d';
        hdlrPayloadVide[7] = (byte)'e';
        byte[] hdlrVide = BuildFullBox("hdlr", 0, 0, hdlrPayloadVide);
        byte[] mdiaVide = BuildBox("mdia", [.. hdlrVide, .. BuildBox("minf", [])]);
        byte[] trakKeep = BuildBox("trak", [.. tkhd2, .. mdiaVide]);

        byte[] moov = BuildBox("moov", [.. trakKeep, .. trak]);
        byte[] fileData = [.. BuildBox("ftyp", new byte[16]), .. moov, .. new byte[100]];

        bool nativeSuccess = Interop.NativeMp4MdtaKeyStripper.TryStripTracks(
            fileData, ["com.apple.quicktime.live-photo-info"], out byte[]? nativeResult, out string? nativeError);

        // We run the legacy manually by invoking it on a file and bypassing Native.
        // But since TryStripTracks currently tries Native first, we'll temporarly rename or just use reflection to call StripTracksWithKeys
        var legacyMethod = typeof(LegacyMp4MdtaKeyStripper).GetMethod("StripTracksWithKeys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        byte[]? legacyResult = (byte[]?)legacyMethod!.Invoke(null, new object[] { fileData, new string[] { "com.apple.quicktime.live-photo-info" } });

        Assert.True(nativeSuccess, nativeError);
        Assert.NotNull(legacyResult);
        Assert.Equal(legacyResult, nativeResult);
    }
}

using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMediaRange
{
    public ulong Offset;
    public ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public uint Width;
    public uint Height;
    public NativeMediaRange FileRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public int Codec;
    public uint Width;
    public uint Height;
    public int RotationDegrees;
    public double DurationSeconds;
    public double Fps;
    public int HasAudio;
    public NativeMediaRange FileRange;
    public int SourceIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGainMapItemFacts
{
    public uint StructSize;
    public int IsPresent;
    public int Container;
    public NativeMediaRange FileRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTimingFacts
{
    public uint StructSize;
    public long CoverTimestampUs;
    public long PrimaryTimestampUs;
    public int CoverFrameIndex;
    public int TotalFrames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSourceMediaFacts
{
    public uint StructSize;
    public int Protocol;
    public NativeImageItemFacts PrimaryImage;
    public NativeVideoItemFacts MotionVideo;
    public NativeGainMapItemFacts GainMap;
    public NativeTimingFacts Timing;
    public NativeMediaRange ProtocolTailRange;
    public fixed byte PairingIdentifier[128];
    public fixed byte PrimarySha256[32];
    public fixed byte SecondarySha256[32];
    public int HasSecondarySource;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeRemovedProtocolFact
{
    public uint StructSize;
    public fixed byte ProtocolName[64];
    public fixed byte Component[64];
    public fixed byte Description[128];
}

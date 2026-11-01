using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileIdentity
{
    public uint VolumeSerial;
    public ulong FileIndex;
    public ulong FileSize;
    public uint LinkCount;
}

using System.Runtime.InteropServices;

namespace LivePhotoBox.Core.Tests.Support;

// Independent OS query: no LivePhotoBox probe/helper supplies the expected result.
// COM vtable positions and GUIDs follow the installed Windows SDK mfobjects.h /
// mftransform.h. This proves activation, not encoding/type negotiation.
internal static unsafe class WindowsH264CapabilityOracle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MediaTypeFilter { public Guid Major; public Guid Subtype; }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags,
        ref MediaTypeFilter input, ref MediaTypeFilter output, out nint activations, out uint count);

    internal static bool IsAvailable(Action<string> report)
    {
        int com = CoInitializeEx(0, 0);
        report($"CoInitializeEx=0x{com:X8}");
        if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
        bool started = false;
        nint list = 0;
        uint count = 0;
        try
        {
            int startup = MFStartup(0x20070, 0);
            report($"MFStartup=0x{startup:X8}");
            Marshal.ThrowExceptionForHR(startup);
            started = true;
            Guid video = new("73646976-0000-0010-8000-00aa00389b71");
            var input = new MediaTypeFilter { Major = video, Subtype = new("3231564e-0000-0010-8000-00aa00389b71") };
            var output = new MediaTypeFilter { Major = video, Subtype = new("34363248-0000-0010-8000-00aa00389b71") };
            int enumeration = MFTEnumEx(new("f79eac7d-e545-4387-bdee-d647d7bde42a"), 1,
                ref input, ref output, out list, out count);
            report($"MFTEnumEx NV12/H264 SYNCMFT=0x{enumeration:X8}; count={count}");
            Marshal.ThrowExceptionForHR(enumeration);
            if (count > 0 && list == 0) throw new InvalidOperationException("MFTEnumEx returned a null list.");
            for (int i = 0; i < count; i++)
            {
                nint activation = Marshal.ReadIntPtr(list, checked(i * IntPtr.Size));
                if (activation == 0) throw new InvalidOperationException("Null MFT activation.");
                nint transform = 0, attributes = 0;
                try
                {
                    Guid iid = new("bf94c121-5b05-4e6f-8000-ba598961414d");
                    var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)activation)[33];
                    int hr = activate(activation, &iid, &transform);
                    report($"candidate[{i}] ActivateObject=0x{hr:X8}");
                    if (hr < 0 || transform == 0) continue;
                    var getAttributes = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)transform)[8];
                    hr = getAttributes(transform, &attributes);
                    report($"candidate[{i}] GetAttributes=0x{hr:X8}");
                    if (hr < 0 || attributes == 0) continue;
                    Guid asyncKey = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
                    uint asynchronous = 0;
                    var getUInt32 = (delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)(*(nint**)attributes)[7];
                    hr = getUInt32(attributes, &asyncKey, &asynchronous);
                    report($"candidate[{i}] MF_TRANSFORM_ASYNC=0x{hr:X8}; value={asynchronous}");
                    if ((hr >= 0 && asynchronous == 0) || hr == unchecked((int)0xC00D36E6)) return true;
                }
                finally
                {
                    if (attributes != 0) Marshal.Release(attributes);
                    if (transform != 0) Marshal.Release(transform);
                }
            }
            return false;
        }
        finally
        {
            if (list != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    nint activation = Marshal.ReadIntPtr(list, checked(i * IntPtr.Size));
                    if (activation != 0) Marshal.Release(activation);
                }
                Marshal.FreeCoTaskMem(list);
            }
            if (started) Marshal.ThrowExceptionForHR(MFShutdown());
            if (com >= 0) CoUninitialize();
        }
    }

    internal static void VerifySidecar()
    {
        nint module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "LivePhotoBox.Video.Libav.dll"));
        try
        {
            nint query = NativeLibrary.GetExport(module, "lpb_video_sidecar_query_abi_v1");
            _ = NativeLibrary.GetExport(module, "lpb_video_sidecar_transcode_v1");
            byte* info = stackalloc byte[80];
            new Span<byte>(info, 80).Clear();
            *(uint*)info = 80;
            int result = ((delegate* unmanaged[Cdecl]<byte*, int>)query)(info);
            if (result != 0 || *(uint*)info != 80 || *(uint*)(info + 4) != 1 || *(uint*)(info + 8) != 2 ||
                (*(uint*)(info + 12) & 0x15) != 0x15 || Marshal.PtrToStringUTF8((nint)(info + 48)) != "9.0.1")
                throw new InvalidOperationException("Independent video sidecar ABI/version/capability check failed.");
        }
        finally { NativeLibrary.Free(module); }
    }
}

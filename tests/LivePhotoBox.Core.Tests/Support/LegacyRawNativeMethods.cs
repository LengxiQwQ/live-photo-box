using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

// Raw positive algorithm fixtures must never bind the production library.
internal static class LegacyRawNativeMethods
{
    internal static NativeResult CreateContext(nint options, out nint context)
        => TestHarnessNativeMethods.CreateContext(options, out context);
    internal static void DestroyContext(nint context) => TestHarnessNativeMethods.DestroyContext(context);
    internal static NativeResult GetLastError(nint context, nint buffer, nuint size, out nuint required)
        => TestHarnessNativeMethods.GetLastError(context, buffer, size, out required);

    [DllImport(TestHarnessNativeMethods.LibraryName, EntryPoint = "lpb_vivo_rewrite_image_metadata", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe NativeResult RewriteVivoImageMetadata(nint context, byte* input, nuint inputSize,
        byte* json, nuint jsonSize, int replace, byte* output, nuint outputSize, out nuint required);

    [DllImport(TestHarnessNativeMethods.LibraryName, EntryPoint = "lpb_vivo_rewrite_video_metadata", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe NativeResult RewriteVivoVideoMetadata(nint context, byte* input, nuint inputSize,
        byte* json, nuint jsonSize, byte* output, nuint outputSize, out nuint required);
}

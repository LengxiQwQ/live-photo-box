using System;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop
{
    internal static partial class NativeAppleMebxWriter
    {
        [LibraryImport(NativeMethods.LibraryName)]
        internal static unsafe partial NativeResult lpb_apple_append_mebx_tracks(
            nint context,
            byte* data, nuint data_size,
            double cover_seconds,
            byte* output, nuint output_size,
            out nuint out_written);

        public static unsafe bool TryAppendStillImageTrack(byte[] data, double coverSeconds, out byte[]? output, out string? error)
        {
            output = null;
            error = null;
            nint context = nint.Zero;
            try
            {
                if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

                nuint outWritten = 0;
                NativeResult res;

                fixed (byte* pIn = data)
                {
                    res = lpb_apple_append_mebx_tracks(
                        context, pIn, (nuint)data.Length, 
                        coverSeconds, null, 0, out outWritten);

                    if (res == NativeResult.BufferTooSmall && outWritten > 0)
                    {
                        output = new byte[(int)outWritten];
                        fixed (byte* pOut = output)
                        {
                            res = lpb_apple_append_mebx_tracks(
                                context, pIn, (nuint)data.Length, 
                                coverSeconds, pOut, (nuint)output.Length, out outWritten);
                        }
                    }
                }

                if (res == NativeResult.Ok && output != null)
                {
                    return true;
                }
                else
                {
                    error = "Failed with result: " + res;
                    return false;
                }
            }
            finally
            {
                if (context != nint.Zero)
                    NativeMethods.DestroyContext(context);
            }
        }
    }
}

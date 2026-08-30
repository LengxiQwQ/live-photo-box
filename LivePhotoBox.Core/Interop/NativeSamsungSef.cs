using System;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

/// <summary>
/// Native implementation of Samsung SEF trailer parsing.
/// </summary>
internal static class NativeSamsungSef
{
    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_samsung_sef_parse", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_samsung_sef_parse(
        nint context, byte* input, nuint inputSize, out ulong outVideoOffset, out ulong outVideoSize);

    /// <summary>
    /// Parses the SEF trailer from the given bytes to locate the MotionPhoto_Data offset and size.
    /// The input bytes can be the entire file or just the end portion of the file (e.g., last 4096 bytes).
    /// If providing a partial buffer, the returned offset will be relative to the provided buffer.
    /// </summary>
    public static unsafe bool TryParse(byte[] tailBuffer, out long videoOffset, out long videoSize, out string? error)
    {
        videoOffset = 0;
        videoSize = 0;
        error = null;
        nint context = nint.Zero;

        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult result;
            ulong outOffset, outSize;

            fixed (byte* pIn = tailBuffer)
            {
                result = lpb_samsung_sef_parse(
                    context, pIn, (nuint)tailBuffer.Length, out outOffset, out outSize);
            }

            if (result == NativeResult.Ok)
            {
                videoOffset = (long)outOffset;
                videoSize = (long)outSize;
                return true;
            }

            error = ReadLastError(context) ?? $"Native call failed: {result}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (context != nint.Zero) { try { NativeMethods.DestroyContext(context); } catch { } }
        }
    }

    private static string? ReadLastError(nint context)
    {
        NativeResult sizeResult = NativeMethods.GetLastError(
            context, nint.Zero, 0, out nuint requiredSize);
        if (sizeResult != NativeResult.BufferTooSmall || requiredSize <= 1)
            return null;

        nint buffer = Marshal.AllocHGlobal(checked((nint)requiredSize));
        try
        {
            return NativeMethods.GetLastError(context, buffer, requiredSize, out _) == NativeResult.Ok
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using LivePhotoBox.Models;

namespace LivePhotoBox.Interop;

/// <summary>
/// Native implementation of JPEG metadata editor (APP1 XMP, etc.)
/// </summary>
internal static class NativeJpegEditor
{
    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_jpeg_inject_xmp", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_jpeg_inject_xmp(
        nint context, byte* input, nuint inputSize, byte* xmpXml, nuint xmpXmlSize, byte* output, nuint outputSize, out nuint outWritten);

    /// <summary>
    /// Injects or replaces the APP1 XMP segment in the given JPEG image bytes.
    /// Preserves all other segments and trailing data.
    /// </summary>
    public static unsafe bool TryInjectXmp(byte[] imageBytes, byte[]? xmpXmlBytes, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;

        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            // Probe for size (or allocate slightly larger)
            int expectedSize = imageBytes.Length + (xmpXmlBytes?.Length ?? 0) + 128;
            byte[] outBuf = new byte[expectedSize];
            NativeResult result;
            nuint written;

            fixed (byte* pIn = imageBytes)
            fixed (byte* pXmp = xmpXmlBytes)
            fixed (byte* pOut = outBuf)
            {
                result = lpb_jpeg_inject_xmp(
                    context, pIn, (nuint)imageBytes.Length, 
                    pXmp, (nuint)(xmpXmlBytes?.Length ?? 0), 
                    pOut, (nuint)outBuf.Length, out written);
            }

            if (result == NativeResult.BufferTooSmall)
            {
                outBuf = new byte[(int)written];
                fixed (byte* pIn = imageBytes)
                fixed (byte* pXmp = xmpXmlBytes)
                fixed (byte* pOut = outBuf)
                {
                    result = lpb_jpeg_inject_xmp(
                        context, pIn, (nuint)imageBytes.Length, 
                        pXmp, (nuint)(xmpXmlBytes?.Length ?? 0), 
                        pOut, (nuint)outBuf.Length, out written);
                }
            }

            if (result == NativeResult.Ok)
            {
                if (written == 0) return true;
                output = new byte[(int)written];
                Array.Copy(outBuf, output, (int)written);
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

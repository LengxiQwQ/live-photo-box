using System;
using System.Runtime.InteropServices;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Interop;

/// <summary>
/// Native implementation of HeifBoxParser logic.
/// </summary>
internal static class NativeHeifBoxParser
{
    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_heif_locate_exif_item", ExactSpelling = true)]
    private static extern NativeResult lpb_heif_locate_exif_item(
        nint context,
        byte[] input,
        nuint inputSize,
        out ulong outOffset,
        out ulong outLength);

    /// <summary>
    /// Try to locate the Exif item offset and length via the Native C++ parser.
    /// </summary>
    public static bool TryLocateExifItem(byte[] data, out long offset, out long length, out string? error)
    {
        offset = 0;
        length = 0;
        error = null;
        nint context = nint.Zero;

        try
        {
            NativeResult createResult = NativeMethods.CreateContext(nint.Zero, out context);
            if (createResult != NativeResult.Ok || context == nint.Zero)
            {
                error = $"Native context creation failed: {createResult}.";
                return false;
            }

            var result = lpb_heif_locate_exif_item(
                context,
                data,
                (nuint)data.Length,
                out ulong cOffset,
                out ulong cLength);

            if (result == NativeResult.Ok)
            {
                offset = (long)cOffset;
                length = (long)cLength;
                return true;
            }

            error = ReadLastError(context) ?? $"Native call failed: {result}";
            return false;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (context != nint.Zero)
            {
                try { NativeMethods.DestroyContext(context); }
                catch (Exception ex) when (
                    ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                {
                    // Ignore
                }
            }
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

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LivePhotoBox.Interop;

public static class NativeAppleMakerNoteWriter
{
    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_strip_live_photo_entries")]
    private static extern NativeResult LpbAppleStripLivePhotoEntries(
        nint context,
        ref byte data,
        nuint dataSize);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_write_content_identifier", CharSet = CharSet.Ansi)]
    private static extern NativeResult LpbAppleWriteContentIdentifier(
        nint context,
        ref byte data,
        nuint dataSize,
        string contentId);

    public static bool TryStripLivePhotoEntries(byte[] imageBytes, out string? error)
    {
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult res = LpbAppleStripLivePhotoEntries(
                context,
                ref MemoryMarshal.GetArrayDataReference(imageBytes),
                (nuint)imageBytes.Length);

            if (res != NativeResult.Ok)
            {
                error = ReadLastError(context) ?? $"Native error {res}";
                return false;
            }
            return true;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    public static bool TryWriteContentIdentifier(byte[] imageBytes, string contentId, out string? error)
    {
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult res = LpbAppleWriteContentIdentifier(
                context,
                ref MemoryMarshal.GetArrayDataReference(imageBytes),
                (nuint)imageBytes.Length,
                contentId);

            if (res != NativeResult.Ok)
            {
                error = ReadLastError(context) ?? $"Native error {res}";
                return false;
            }
            return true;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_inject_makernote_jpeg")]
    private static extern unsafe NativeResult lpb_apple_inject_makernote_jpeg(
        nint context, byte* input, nuint inputSize, byte* makernote, nuint makernoteSize, byte* output, nuint outputSize, out nuint outWritten);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_inject_makernote_heic")]
    private static extern unsafe NativeResult lpb_apple_inject_makernote_heic(
        nint context, byte* input, nuint inputSize, byte* makernote, nuint makernoteSize, byte* output, nuint outputSize, out nuint outWritten);

    public static unsafe bool TryInjectMakerNoteIntoJpeg(byte[] imageBytes, byte[] makerNote, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            int expectedSize = imageBytes.Length + makerNote.Length + 1024;
            byte[] outBuf = new byte[expectedSize];
            NativeResult res;
            nuint written;

            fixed (byte* pIn = imageBytes)
            fixed (byte* pMn = makerNote)
            fixed (byte* pOut = outBuf)
            {
                res = lpb_apple_inject_makernote_jpeg(
                    context, pIn, (nuint)imageBytes.Length, 
                    pMn, (nuint)makerNote.Length, 
                    pOut, (nuint)outBuf.Length, out written);
            }

            if (res == NativeResult.BufferTooSmall)
            {
                outBuf = new byte[(int)written];
                fixed (byte* pIn = imageBytes)
                fixed (byte* pMn = makerNote)
                fixed (byte* pOut = outBuf)
                {
                    res = lpb_apple_inject_makernote_jpeg(
                        context, pIn, (nuint)imageBytes.Length, 
                        pMn, (nuint)makerNote.Length, 
                        pOut, (nuint)outBuf.Length, out written);
                }
            }

            if (res == NativeResult.Ok)
            {
                output = new byte[(int)written];
                Array.Copy(outBuf, output, (int)written);
                return true;
            }
            error = ReadLastError(context) ?? $"Native error {res}";
            return false;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    public static unsafe bool TryInjectMakerNoteIntoHeic(byte[] imageBytes, byte[] makerNote, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            int expectedSize = imageBytes.Length + makerNote.Length + 1024;
            byte[] outBuf = new byte[expectedSize];
            NativeResult res;
            nuint written;

            fixed (byte* pIn = imageBytes)
            fixed (byte* pMn = makerNote)
            fixed (byte* pOut = outBuf)
            {
                res = lpb_apple_inject_makernote_heic(
                    context, pIn, (nuint)imageBytes.Length, 
                    pMn, (nuint)makerNote.Length, 
                    pOut, (nuint)outBuf.Length, out written);
            }

            if (res == NativeResult.BufferTooSmall)
            {
                outBuf = new byte[(int)written];
                fixed (byte* pIn = imageBytes)
                fixed (byte* pMn = makerNote)
                fixed (byte* pOut = outBuf)
                {
                    res = lpb_apple_inject_makernote_heic(
                        context, pIn, (nuint)imageBytes.Length, 
                        pMn, (nuint)makerNote.Length, 
                        pOut, (nuint)outBuf.Length, out written);
                }
            }

            if (res == NativeResult.Ok)
            {
                output = new byte[(int)written];
                Array.Copy(outBuf, output, (int)written);
                return true;
            }
            error = ReadLastError(context) ?? $"Native error {res}";
            return false;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
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

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

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_samsung_sef_build_trailer", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_samsung_sef_build_trailer(
        nint context, byte* videoData, nuint videoSize, int isHeic, ulong imageSize, byte* output, nuint outputSize, out nuint outWritten);

    public static unsafe byte[]? BuildTrailer(byte[] videoData, string imageType, long imageSize = 0)
    {
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return null;
            
            int isHeic = string.Equals(imageType, "heic", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            
            // Calculate a safe buffer size based on the structure sizes (tag sizes + SEF section size)
            // C# trailer is 12 bytes payload for HEIC, plus 44 bytes tag overhead + 44 bytes SEF section overhead.
            // For HEIC there's also the mpvd/sefd box headers (~16 bytes).
            // Just allocate video length + 256 bytes to be safe for metadata overhead.
            nuint allocSize = (nuint)(videoData.Length + 256);
            byte[] output = new byte[allocSize];
            
            NativeResult result;
            nuint written;
            
            fixed (byte* pVideo = videoData)
            fixed (byte* pOutput = output)
            {
                result = lpb_samsung_sef_build_trailer(
                    context, pVideo, (nuint)videoData.Length, isHeic, (ulong)imageSize, pOutput, allocSize, out written);
            }
            
            if (result == NativeResult.Ok)
            {
                byte[] finalBuffer = new byte[written];
                Array.Copy(output, finalBuffer, (int)written);
                return finalBuffer;
            }
            
            return null;
        }
        catch (Exception)
        {
            return null;
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

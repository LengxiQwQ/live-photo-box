using System;
using System.Runtime.InteropServices;
using System.Text;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Interop;

/// <summary>
/// Native implementation of Mp4MdtaKeyStripper logic.
/// </summary>
internal static class NativeMp4MdtaKeyStripper
{
    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_mp4_strip_uuid_box", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_mp4_strip_uuid_box(
        nint context, byte* input, nuint inputSize, byte* userType16, byte* output, nuint outputSize, out nuint outWritten);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_mp4_strip_stsd_tracks", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_mp4_strip_stsd_tracks(
        nint context, byte* input, nuint inputSize, nint* keyFragments, nuint fragmentCount, byte* output, nuint outputSize, out nuint outWritten);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_mp4_strip_mdta_keys", ExactSpelling = true)]
    private static extern unsafe NativeResult lpb_mp4_strip_mdta_keys(
        nint context, byte* input, nuint inputSize,
        nint* nameStarts, nuint nameStartsCount,
        nint* nameContains, nuint nameContainsCount,
        nint* valueContains, nuint valueContainsCount,
        byte* output, nuint outputSize, out nuint outWritten);

    public static unsafe bool TryStripUuidBox(byte[] input, string userType, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        if (userType.Length > 16) userType = userType.Substring(0, 16);
        byte[] userTypeBytes = new byte[16];
        Encoding.ASCII.GetBytes(userType).CopyTo(userTypeBytes, 0);

        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            byte[] outBuf = new byte[input.Length];
            NativeResult result;
            nuint written;
            fixed (byte* pIn = input)
            fixed (byte* pUser = userTypeBytes)
            fixed (byte* pOut = outBuf)
            {
                result = lpb_mp4_strip_uuid_box(context, pIn, (nuint)input.Length, pUser, pOut, (nuint)outBuf.Length, out written);
            }

            if (result == NativeResult.Ok)
            {
                if (written == 0) return true; // No changes
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

    public static unsafe bool TryStripTracks(byte[] input, string[] stsdKeyFragments, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        nint[] ptrs = new nint[stsdKeyFragments.Length];
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            for (int i = 0; i < stsdKeyFragments.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemAnsi(stsdKeyFragments[i]);

            byte[] outBuf = new byte[input.Length];
            NativeResult result;
            nuint written;
            fixed (byte* pIn = input)
            fixed (nint* pFrags = ptrs)
            fixed (byte* pOut = outBuf)
            {
                result = lpb_mp4_strip_stsd_tracks(context, pIn, (nuint)input.Length, pFrags, (nuint)ptrs.Length, pOut, (nuint)outBuf.Length, out written);
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
            foreach (var ptr in ptrs) if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr);
            if (context != nint.Zero) { try { NativeMethods.DestroyContext(context); } catch { } }
        }
    }

    public static unsafe bool TryStripMdtaKeys(byte[] input, string[] nameStarts, string[] nameContains, string[] valueContains, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        
        nint[] ptrsNameStarts = new nint[nameStarts.Length];
        nint[] ptrsNameContains = new nint[nameContains.Length];
        nint[] ptrsValueContains = new nint[valueContains.Length];

        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            for (int i = 0; i < nameStarts.Length; i++) ptrsNameStarts[i] = Marshal.StringToCoTaskMemAnsi(nameStarts[i]);
            for (int i = 0; i < nameContains.Length; i++) ptrsNameContains[i] = Marshal.StringToCoTaskMemAnsi(nameContains[i]);
            for (int i = 0; i < valueContains.Length; i++) ptrsValueContains[i] = Marshal.StringToCoTaskMemAnsi(valueContains[i]);

            byte[] outBuf = new byte[input.Length];
            NativeResult result;
            nuint written;
            fixed (byte* pIn = input)
            fixed (nint* pNS = ptrsNameStarts)
            fixed (nint* pNC = ptrsNameContains)
            fixed (nint* pVC = ptrsValueContains)
            fixed (byte* pOut = outBuf)
            {
                result = lpb_mp4_strip_mdta_keys(context, pIn, (nuint)input.Length,
                    pNS, (nuint)nameStarts.Length,
                    pNC, (nuint)nameContains.Length,
                    pVC, (nuint)valueContains.Length,
                    pOut, (nuint)outBuf.Length, out written);
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
            foreach (var ptr in ptrsNameStarts) if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in ptrsNameContains) if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr);
            foreach (var ptr in ptrsValueContains) if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr);
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

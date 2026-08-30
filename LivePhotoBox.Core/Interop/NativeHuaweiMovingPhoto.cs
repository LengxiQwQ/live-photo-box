namespace LivePhotoBox.Interop
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>Managed ownership wrapper for the Native Huawei Moving Photo binary writer.</summary>
    internal static class NativeHuaweiMovingPhoto
    {
        internal static bool TryBuildTail(
            int coverFrame,
            int totalFrames,
            long mp4Size,
            int originalCoverMs,
            int originalDurationMs,
            string prefix,
            out byte[] output,
            out string? error)
        {
            output = new byte[60];
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

                unsafe
                {
                    fixed (byte* outputPtr = output)
                    {
                        NativeResult buildResult = NativeMethods.HuaweiBuildTail(
                            context,
                            coverFrame,
                            totalFrames,
                            checked((ulong)mp4Size),
                            originalCoverMs,
                            originalDurationMs,
                            prefix,
                            outputPtr,
                            60,
                            out _);

                        if (buildResult != NativeResult.Ok)
                        {
                            error = ReadLastError(context) ?? $"Native write failed: {buildResult}.";
                            return false;
                        }
                    }
                }

                return true;
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
                    catch { /* best effort */ }
                }
            }
        }

        internal static bool TryPatchHeicFtyp(
            byte[] heicData,
            out string? error)
        {
            ArgumentNullException.ThrowIfNull(heicData);
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

                unsafe
                {
                    fixed (byte* dataPtr = heicData)
                    {
                        NativeResult patchResult = NativeMethods.HuaweiPatchHeicFtyp(
                            context,
                            dataPtr,
                            (nuint)heicData.Length);

                        if (patchResult != NativeResult.Ok)
                        {
                            error = ReadLastError(context) ?? $"Native write failed: {patchResult}.";
                            return false;
                        }
                    }
                }

                return true;
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
                    catch { /* best effort */ }
                }
            }
        }

        internal static bool TryPatchMp4(
            byte[] mp4Data,
            out string? error)
        {
            ArgumentNullException.ThrowIfNull(mp4Data);
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

                unsafe
                {
                    fixed (byte* dataPtr = mp4Data)
                    {
                        NativeResult patchResult = NativeMethods.HuaweiPatchMp4(
                            context,
                            dataPtr,
                            (nuint)mp4Data.Length);

                        if (patchResult != NativeResult.Ok)
                        {
                            error = ReadLastError(context) ?? $"Native write failed: {patchResult}.";
                            return false;
                        }
                    }
                }

                return true;
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
                    catch { /* best effort */ }
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
}

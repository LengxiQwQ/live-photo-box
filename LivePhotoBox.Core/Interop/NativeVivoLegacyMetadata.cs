namespace LivePhotoBox.Interop
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>Managed ownership wrapper for the Native vivo legacy byte writer.</summary>
    internal static class NativeVivoLegacyMetadata
    {
        internal static bool TryRewriteImage(
            byte[] input,
            byte[] vivoJson,
            bool replaceExisting,
            out byte[] output,
            out string? error) =>
            TryRewrite(input, vivoJson, replaceExisting, video: false, out output, out error);

        internal static bool TryRewriteVideo(
            byte[] input,
            byte[] vivoJson,
            out byte[] output,
            out string? error) =>
            TryRewrite(input, vivoJson, replaceExisting: false, video: true, out output, out error);

        private static unsafe bool TryRewrite(
            byte[] input,
            byte[] vivoJson,
            bool replaceExisting,
            bool video,
            out byte[] output,
            out string? error)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(vivoJson);
            output = [];
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

                fixed (byte* inputPtr = input)
                fixed (byte* jsonPtr = vivoJson)
                {
                    // The rewritten payload can never exceed input + two JSON copies +
                    // 64 bytes of vivo tail/uuid framing. Allocate once so large videos
                    // are parsed and copied by Native only once.
                    output = new byte[checked(input.Length + vivoJson.Length * 2 + 64)];
                    NativeResult writeResult;
                    nuint writtenSize;
                    fixed (byte* outputPtr = output)
                    {
                        writeResult = Invoke(
                            video, context, inputPtr, (nuint)input.Length,
                            jsonPtr, (nuint)vivoJson.Length, replaceExisting,
                            outputPtr, (nuint)output.Length, out writtenSize);
                    }
                    if (writeResult != NativeResult.Ok || writtenSize > (nuint)output.Length)
                    {
                        error = ReadLastError(context) ?? $"Native write failed: {writeResult}.";
                        output = [];
                        return false;
                    }

                    if (writtenSize != (nuint)output.Length)
                        Array.Resize(ref output, checked((int)writtenSize));
                }

                return true;
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                error = ex.Message;
                output = [];
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
                        // The operation result has already been captured.
                    }
                }
            }
        }

        private static unsafe NativeResult Invoke(
            bool video,
            nint context,
            byte* input,
            nuint inputSize,
            byte* vivoJson,
            nuint vivoJsonSize,
            bool replaceExisting,
            byte* output,
            nuint outputSize,
            out nuint requiredSize) =>
            video
                ? NativeMethods.RewriteVivoVideoMetadata(
                    context, input, inputSize, vivoJson, vivoJsonSize,
                    output, outputSize, out requiredSize)
                : NativeMethods.RewriteVivoImageMetadata(
                    context, input, inputSize, vivoJson, vivoJsonSize,
                    replaceExisting ? 1 : 0, output, outputSize, out requiredSize);

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

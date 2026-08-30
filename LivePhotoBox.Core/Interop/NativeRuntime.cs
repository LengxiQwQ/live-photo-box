namespace LivePhotoBox.Interop
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Describes the currently loadable LivePhotoBox native runtime.
    /// </summary>
    public sealed class NativeRuntimeInfo
    {
        internal NativeRuntimeInfo(
            bool isAvailable,
            uint abiVersion,
            string? version,
            ulong capabilities,
            string? diagnostic)
        {
            IsAvailable = isAvailable;
            AbiVersion = abiVersion;
            Version = version;
            Capabilities = capabilities;
            Diagnostic = diagnostic;
        }

        /// <summary>Gets whether the native runtime was loaded and passed its ABI health check.</summary>
        public bool IsAvailable { get; }

        /// <summary>Gets the ABI version reported by the native runtime.</summary>
        public uint AbiVersion { get; }

        /// <summary>Gets the product version reported by the native runtime.</summary>
        public string? Version { get; }

        /// <summary>Gets the native capability bit mask.</summary>
        public ulong Capabilities { get; }

        /// <summary>Gets a diagnostic message when the runtime is unavailable.</summary>
        public string? Diagnostic { get; }
    }

    /// <summary>
    /// Performs a non-mutating health check of the LivePhotoBox native runtime.
    /// </summary>
    public static class NativeRuntime
    {
        /// <summary>The ABI version supported by this managed interop layer.</summary>
        public const uint SupportedAbiVersion = 1;

        /// <summary>Foundation capability exposed by the Phase 1 native runtime.</summary>
        public const ulong FoundationCapability = 1UL << 0;

        /// <summary>Reserved capability bits for complete product-facing protocol workflows.</summary>
        public const ulong GoogleV1Capability = 1UL << 8;
        public const ulong GoogleV2Capability = 1UL << 9;
        public const ulong OppoCapability = 1UL << 10;
        public const ulong VivoX300Capability = 1UL << 11;
        public const ulong VivoLegacyCapability = 1UL << 12;
        public const ulong HuaweiHonorCapability = 1UL << 13;
        public const ulong SamsungJpegCapability = 1UL << 14;
        public const ulong SamsungHeicCapability = 1UL << 15;
        public const ulong AppleCapability = 1UL << 16;

        /// <summary>
        /// Loads the native runtime, validates the ABI, and creates a temporary context.
        /// </summary>
        public static NativeRuntimeInfo Probe()
        {
            nint context = nint.Zero;
            try
            {
                uint abiVersion = NativeMethods.GetAbiVersion();
                if (abiVersion != SupportedAbiVersion)
                {
                    return Unavailable(
                        $"Native ABI mismatch. Managed={SupportedAbiVersion}, Native={abiVersion}.",
                        abiVersion);
                }

                NativeResult createResult = NativeMethods.CreateContext(nint.Zero, out context);
                if (createResult != NativeResult.Ok || context == nint.Zero)
                {
                    return Unavailable($"Native context creation failed: {createResult}.", abiVersion);
                }

                var runtimeInfo = new NativeRuntimeInfoData
                {
                    StructSize = checked((uint)Marshal.SizeOf<NativeRuntimeInfoData>())
                };
                NativeResult infoResult = NativeMethods.GetRuntimeInfo(context, ref runtimeInfo);
                if (infoResult != NativeResult.Ok)
                {
                    string? nativeError = ReadLastError(context);
                    return Unavailable(
                        $"Native runtime query failed: {infoResult}. {nativeError}".TrimEnd(),
                        abiVersion);
                }

                string? version = Marshal.PtrToStringUTF8(NativeMethods.GetVersion());
                return new NativeRuntimeInfo(
                    isAvailable: true,
                    abiVersion: runtimeInfo.AbiVersion,
                    version,
                    runtimeInfo.Capabilities,
                    diagnostic: null);
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException)
            {
                return Unavailable(ex.Message);
            }
            finally
            {
                if (context != nint.Zero)
                {
                    try
                    {
                        NativeMethods.DestroyContext(context);
                    }
                    catch (Exception ex) when (
                        ex is DllNotFoundException or
                        BadImageFormatException or
                        EntryPointNotFoundException)
                    {
                        // Probe must remain non-throwing for an incomplete or incompatible runtime.
                    }
                }
            }
        }

        private static NativeRuntimeInfo Unavailable(string diagnostic, uint abiVersion = 0)
            => new(
                isAvailable: false,
                abiVersion,
                version: null,
                capabilities: 0,
                diagnostic);

        private static string? ReadLastError(nint context)
        {
            NativeResult sizeResult = NativeMethods.GetLastError(
                context,
                nint.Zero,
                0,
                out nuint requiredSize);
            if (sizeResult != NativeResult.BufferTooSmall || requiredSize <= 1)
            {
                return null;
            }

            nint buffer = Marshal.AllocHGlobal(checked((nint)requiredSize));
            try
            {
                NativeResult readResult = NativeMethods.GetLastError(
                    context,
                    buffer,
                    requiredSize,
                    out _);
                return readResult == NativeResult.Ok
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

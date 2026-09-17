namespace LivePhotoBox.Interop
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Describes the currently loadable LivePhotoBox native runtime.
    /// </summary>
    public enum NativeRuntimeOperationClass
    {
        JpegCodec = 1,
        HeicCodec = 2,
        VideoRemux = 3,
        VideoTranscodeSdrH264 = 4,
        VideoTranscodeSdrHevc = 5,
        VideoTranscodeHdr10Bit = 6
    }

    public enum NativeRuntimeBackend
    {
        Unknown = 0,
        LibJpegTurbo = 1,
        LibHeif = 2,
        ProjectIsoBmff = 3,
        WindowsMediaFoundation = 4,
        MinimalLibav = 5
    }

    public sealed class NativeRuntimeCapabilityIdentity
    {
        internal NativeRuntimeCapabilityIdentity(
            NativeRuntimeOperationClass operation, NativeRuntimeBackend backend,
            int codec, bool isAvailable, int hardwareMode, bool fallbackOccurred,
            string backendVersion, string fallbackReason)
        {
            Operation = operation;
            Backend = backend;
            Codec = codec;
            IsAvailable = isAvailable;
            HardwareMode = hardwareMode;
            FallbackOccurred = fallbackOccurred;
            BackendVersion = backendVersion;
            FallbackReason = fallbackReason;
        }

        public NativeRuntimeOperationClass Operation { get; }
        public NativeRuntimeBackend Backend { get; }
        public int Codec { get; }
        public bool IsAvailable { get; }
        public int HardwareMode { get; }
        public bool FallbackOccurred { get; }
        public string BackendVersion { get; }
        public string FallbackReason { get; }
    }

    public sealed class NativeRuntimeInfo
    {
        internal NativeRuntimeInfo(
            bool isAvailable,
            uint abiVersion,
            string? version,
            string? jpegBackendVersion,
            string? heicBackendVersion,
            ulong capabilities,
            IReadOnlyList<NativeRuntimeCapabilityIdentity> capabilityIdentities,
            string? diagnostic)
        {
            IsAvailable = isAvailable;
            AbiVersion = abiVersion;
            Version = version;
            JpegBackendVersion = jpegBackendVersion;
            HeicBackendVersion = heicBackendVersion;
            Capabilities = capabilities;
            CapabilityIdentities = capabilityIdentities;
            Diagnostic = diagnostic;
        }

        /// <summary>Gets whether the native runtime was loaded and passed its ABI health check.</summary>
        public bool IsAvailable { get; }

        /// <summary>Gets the ABI version reported by the native runtime.</summary>
        public uint AbiVersion { get; }

        /// <summary>Gets the product version reported by the native runtime.</summary>
        public string? Version { get; }

        /// <summary>Gets the Native JPEG backend identity when available.</summary>
        public string? JpegBackendVersion { get; }

        /// <summary>Gets the Native HEIC backend identity when available.</summary>
        public string? HeicBackendVersion { get; }

        /// <summary>Gets the native capability bit mask.</summary>
        public ulong Capabilities { get; }

        /// <summary>Factual identities for current packaged capability owners.</summary>
        public IReadOnlyList<NativeRuntimeCapabilityIdentity> CapabilityIdentities { get; }

        /// <summary>Gets a diagnostic message when the runtime is unavailable.</summary>
        public string? Diagnostic { get; }
    }

    /// <summary>
    /// Performs a non-mutating health check of the LivePhotoBox native runtime.
    /// </summary>
    public static class NativeRuntime
    {
        /// <summary>The ABI version supported by this managed interop layer.</summary>
        public const uint SupportedAbiVersion = NativeMethods.RequiredAbiVersion;

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
        public const ulong JpegBackendCapability = 1UL << 17;
        public const ulong HeicBackendCapability = 1UL << 18;
        public const ulong HevcDecoderCapability = 1UL << 19;
        public const ulong HevcEncoderCapability = 1UL << 20;
        public const ulong HdrPixelSurfaceCapability = 1UL << 21;
        public const ulong HeicSecondaryImageEncoderCapability = 1UL << 22;

        /// <summary>The fixed native ABI capacity for auxiliary item facts.</summary>
    internal const int MaxAuxiliaryItems = 8;
    internal const int MaxHeifDependencies = 64;

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
                string? jpegBackendVersion = (runtimeInfo.Capabilities & JpegBackendCapability) != 0
                    ? Marshal.PtrToStringUTF8(NativeMethods.GetJpegBackendVersion())
                    : null;
                string? heicBackendVersion = (runtimeInfo.Capabilities & HeicBackendCapability) != 0
                    ? Marshal.PtrToStringUTF8(NativeMethods.GetHeicBackendVersion())
                    : null;
                var identities = new List<NativeRuntimeCapabilityIdentity>();
                foreach (NativeRuntimeOperationClass operation in Enum.GetValues<NativeRuntimeOperationClass>())
                {
                    var identity = new NativeRuntimeCapabilityIdentityData
                    {
                        StructSize = checked((uint)Marshal.SizeOf<NativeRuntimeCapabilityIdentityData>())
                    };
                    NativeResult identityResult = NativeMethods.GetRuntimeCapabilityIdentity(
                        context, (int)operation, ref identity);
                    if (identityResult != NativeResult.Ok)
                    {
                        return Unavailable($"Native capability identity query failed: {identityResult}.", abiVersion);
                    }
                    identities.Add(MapIdentity(identity));
                }
                return new NativeRuntimeInfo(
                    isAvailable: true,
                    abiVersion: runtimeInfo.AbiVersion,
                    version,
                    jpegBackendVersion,
                    heicBackendVersion,
                    runtimeInfo.Capabilities,
                    identities,
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
                jpegBackendVersion: null,
                heicBackendVersion: null,
                capabilities: 0,
                capabilityIdentities: Array.Empty<NativeRuntimeCapabilityIdentity>(),
                diagnostic);

        private static unsafe NativeRuntimeCapabilityIdentity MapIdentity(NativeRuntimeCapabilityIdentityData native)
        {
            byte* version = native.BackendVersion;
            byte* reason = native.FallbackReason;
            return new NativeRuntimeCapabilityIdentity(
                (NativeRuntimeOperationClass)native.Operation,
                (NativeRuntimeBackend)native.Backend,
                native.Codec,
                native.IsAvailable != 0,
                native.HardwareMode,
                native.FallbackOccurred != 0,
                Marshal.PtrToStringUTF8((nint)version) ?? string.Empty,
                Marshal.PtrToStringUTF8((nint)reason) ?? string.Empty);
        }

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

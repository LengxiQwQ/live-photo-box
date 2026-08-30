using System;
using System.Collections.Generic;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Models
{
    /// <summary>Controls how Live Photo Box chooses between managed and Native processing.</summary>
    public enum ProcessingBackendMode
    {
        /// <summary>Uses only stable Native capabilities and falls back to Legacy elsewhere.</summary>
        Auto = 0,

        /// <summary>Always uses the existing managed implementation.</summary>
        Legacy = 1,

        /// <summary>Uses per-protocol preferences, including preview Native capabilities.</summary>
        Custom = 2
    }

    /// <summary>Per-protocol preference used while the global mode is Custom.</summary>
    public enum ProcessingBackendPreference
    {
        /// <summary>Use the managed implementation.</summary>
        Legacy = 0,

        /// <summary>Prefer Native when the requested operation is available, otherwise fall back.</summary>
        PreferNative = 1
    }

    /// <summary>Product maturity of a Native protocol capability.</summary>
    public enum NativeBackendMaturity
    {
        /// <summary>The runtime does not currently expose this capability.</summary>
        Unavailable = 0,

        /// <summary>The capability is available for opt-in testing.</summary>
        Preview = 1,

        /// <summary>The capability is eligible for automatic selection.</summary>
        Stable = 2
    }

    /// <summary>Stable definition of a user-selectable protocol backend.</summary>
    public sealed record ProcessingBackendProtocolDefinition(
        string Key,
        string DisplayName,
        ulong NativeCapability,
        NativeBackendMaturity MaturityWhenAvailable,
        params string[] Aliases);

    /// <summary>Persisted global mode and per-protocol preferences.</summary>
    public sealed class ProcessingBackendSettings
    {
        /// <summary>Gets or sets the global backend mode.</summary>
        public ProcessingBackendMode Mode { get; set; } = ProcessingBackendMode.Auto;

        /// <summary>Gets the per-protocol preferences, keyed by catalog key.</summary>
        public Dictionary<string, ProcessingBackendPreference> Protocols { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the saved preference for a protocol, defaulting to Legacy.</summary>
        public ProcessingBackendPreference GetPreference(string protocolKey) =>
            Protocols.TryGetValue(protocolKey, out ProcessingBackendPreference preference)
                ? preference
                : ProcessingBackendPreference.Legacy;
    }

    /// <summary>Canonical protocol list shared by the GUI, CLI, and backend router.</summary>
    public static class ProcessingBackendProtocolCatalog
    {
        /// <summary>Gets all product-facing protocol backend definitions.</summary>
        public static IReadOnlyList<ProcessingBackendProtocolDefinition> All { get; } =
        [
            new("google-v1", "Google MicroVideo V1", NativeRuntime.GoogleV1Capability, NativeBackendMaturity.Preview,
                "v1", "microvideo", "micro-video"),
            new("google-v2", "Google Motion Photo V2 / Xiaomi", NativeRuntime.GoogleV2Capability, NativeBackendMaturity.Preview,
                "v2", "motionphoto", "motion-photo", "xiaomi"),
            new("oppo", "OPPO / OnePlus", NativeRuntime.OppoCapability, NativeBackendMaturity.Preview,
                "oneplus", "olive", "o-live"),
            new("vivo-x300", "vivo X300", NativeRuntime.VivoX300Capability, NativeBackendMaturity.Preview,
                "vivo", "vivo-new", "x300"),
            new("vivo-legacy", "vivo <= X200 dual-file", NativeRuntime.VivoLegacyCapability, NativeBackendMaturity.Preview,
                "vivo-old", "vivo-x200", "x200"),
            new("huawei-honor", "HUAWEI / Honor", NativeRuntime.HuaweiHonorCapability, NativeBackendMaturity.Preview,
                "huawei", "honor"),
            new("samsung-jpeg", "Samsung JPEG", NativeRuntime.SamsungJpegCapability, NativeBackendMaturity.Preview,
                "samsung", "samsung-jpg"),
            new("samsung-heic", "Samsung HEIC", NativeRuntime.SamsungHeicCapability, NativeBackendMaturity.Preview,
                "samsung-heif"),
            new("apple", "Apple Live Photo", NativeRuntime.AppleCapability, NativeBackendMaturity.Preview,
                "apple-live", "live-photo")
        ];

        /// <summary>Resolves a canonical key or CLI alias.</summary>
        public static bool TryResolve(string value, out ProcessingBackendProtocolDefinition? definition)
        {
            foreach (ProcessingBackendProtocolDefinition candidate in All)
            {
                if (string.Equals(candidate.Key, value, StringComparison.OrdinalIgnoreCase))
                {
                    definition = candidate;
                    return true;
                }

                foreach (string alias in candidate.Aliases)
                {
                    if (string.Equals(alias, value, StringComparison.OrdinalIgnoreCase))
                    {
                        definition = candidate;
                        return true;
                    }
                }
            }

            definition = null;
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// Stores backend defaults in one human-readable file shared by the GUI and CLI.
    /// </summary>
    public static class ProcessingBackendSettingsService
    {
        /// <summary>Raised after the shared backend configuration changes in this process.</summary>
        public static event EventHandler? Changed;

        private const string SettingsPathEnvironmentVariable = "LIVEPHOTOBOX_BACKEND_SETTINGS_PATH";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        /// <summary>Gets the active shared backend settings file.</summary>
        public static string SettingsPath
        {
            get
            {
                string? overridden = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(overridden))
                    return Path.GetFullPath(overridden);

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox",
                    "backend-settings.json");
            }
        }

        /// <summary>Loads settings, returning safe defaults for missing or malformed files.</summary>
        public static ProcessingBackendSettings Load()
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return new ProcessingBackendSettings();

            try
            {
                string json = File.ReadAllText(path);
                PersistedSettings? persisted = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
                var result = new ProcessingBackendSettings
                {
                    Mode = ParseMode(persisted?.Mode)
                };

                if (persisted?.Protocols != null)
                {
                    foreach ((string key, string value) in persisted.Protocols)
                    {
                        if (ProcessingBackendProtocolCatalog.TryResolve(key, out ProcessingBackendProtocolDefinition? definition)
                            && definition != null)
                        {
                            result.Protocols[definition.Key] = ParsePreference(value);
                        }
                    }
                }

                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LogService.Warn(
                    "Backend settings could not be loaded; using Auto with Legacy protocol defaults.",
                    details: ex.Message,
                    source: LogSource.Settings);
                return new ProcessingBackendSettings();
            }
        }

        /// <summary>Saves settings atomically next to the final configuration file.</summary>
        public static void Save(ProcessingBackendSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            string path = SettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Backend settings path must have a parent directory.");

            Directory.CreateDirectory(directory);
            var protocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessingBackendProtocolDefinition definition in ProcessingBackendProtocolCatalog.All)
            {
                protocols[definition.Key] = FormatPreference(settings.GetPreference(definition.Key));
            }

            var persisted = new PersistedSettings
            {
                Mode = FormatMode(settings.Mode),
                Protocols = protocols
            };
            string json = JsonSerializer.Serialize(persisted, JsonOptions);
            string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
                Changed?.Invoke(null, EventArgs.Empty);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>Updates the global mode while preserving per-protocol preferences.</summary>
        public static void SetMode(ProcessingBackendMode mode)
        {
            ProcessingBackendSettings settings = Load();
            settings.Mode = mode;
            Save(settings);
        }

        /// <summary>Updates one protocol preference while preserving all other settings.</summary>
        public static void SetPreference(string protocolKey, ProcessingBackendPreference preference)
        {
            if (!ProcessingBackendProtocolCatalog.TryResolve(protocolKey, out ProcessingBackendProtocolDefinition? definition)
                || definition == null)
            {
                throw new ArgumentException($"Unknown backend protocol: {protocolKey}", nameof(protocolKey));
            }

            ProcessingBackendSettings settings = Load();
            settings.Protocols[definition.Key] = preference;
            Save(settings);
        }

        /// <summary>Deletes the shared configuration so the next read uses Auto defaults.</summary>
        public static void Reset()
        {
            string path = SettingsPath;
            if (File.Exists(path))
                File.Delete(path);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Gets the runtime-backed availability of a protocol.</summary>
        public static NativeBackendMaturity GetNativeMaturity(
            ProcessingBackendProtocolDefinition definition,
            NativeRuntimeInfo? runtimeInfo = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            runtimeInfo ??= NativeRuntime.Probe();
            if (!runtimeInfo.IsAvailable
                || (runtimeInfo.Capabilities & definition.NativeCapability) == 0)
            {
                return NativeBackendMaturity.Unavailable;
            }

            return definition.MaturityWhenAvailable;
        }

        /// <summary>
        /// Resolves whether a request should prefer Native. Unsupported operations still fall back
        /// through the operation-level router when it is introduced.
        /// </summary>
        public static bool ShouldPreferNative(
            ProcessingBackendSettings settings,
            ProcessingBackendProtocolDefinition definition,
            NativeRuntimeInfo? runtimeInfo = null)
        {
            NativeBackendMaturity maturity = GetNativeMaturity(definition, runtimeInfo);
            return settings.Mode switch
            {
                ProcessingBackendMode.Legacy => false,
                ProcessingBackendMode.Auto => maturity == NativeBackendMaturity.Stable,
                ProcessingBackendMode.Custom =>
                    maturity != NativeBackendMaturity.Unavailable
                    && settings.GetPreference(definition.Key) == ProcessingBackendPreference.PreferNative,
                _ => false
            };
        }

        /// <summary>Parses a global mode name used by the JSON file and CLI.</summary>
        public static bool TryParseMode(string value, out ProcessingBackendMode mode)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "auto": mode = ProcessingBackendMode.Auto; return true;
                case "legacy": mode = ProcessingBackendMode.Legacy; return true;
                case "custom": mode = ProcessingBackendMode.Custom; return true;
                default: mode = ProcessingBackendMode.Auto; return false;
            }
        }

        /// <summary>Formats a mode for configuration and CLI output.</summary>
        public static string FormatMode(ProcessingBackendMode mode) => mode switch
        {
            ProcessingBackendMode.Legacy => "legacy",
            ProcessingBackendMode.Custom => "custom",
            _ => "auto"
        };

        /// <summary>Formats a protocol preference for configuration and CLI output.</summary>
        public static string FormatPreference(ProcessingBackendPreference preference) =>
            preference == ProcessingBackendPreference.PreferNative ? "native" : "legacy";

        private static ProcessingBackendMode ParseMode(string? value) =>
            value != null && TryParseMode(value, out ProcessingBackendMode mode)
                ? mode
                : ProcessingBackendMode.Auto;

        private static ProcessingBackendPreference ParsePreference(string? value) =>
            string.Equals(value, "native", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "prefer-native", StringComparison.OrdinalIgnoreCase)
                ? ProcessingBackendPreference.PreferNative
                : ProcessingBackendPreference.Legacy;

        private sealed class PersistedSettings
        {
            public string Mode { get; set; } = "auto";
            public Dictionary<string, string> Protocols { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

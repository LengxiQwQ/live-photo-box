using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Stores the one product-wide branch switch. It is deliberately not a
/// protocol/backend matrix: Rebuilt is the default Native media branch and
/// Legacy is retained only as an explicit compatibility branch.
/// </summary>
public static class ProcessingBackendSettingsService
{
    public static event EventHandler? Changed;

    private const string SettingsPathEnvironmentVariable = "LIVEPHOTOBOX_BACKEND_SETTINGS_PATH";
    private const string MutexName = "Local\\LivePhotoBox.ProcessingPipeline.v3";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string SettingsPath
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(overridden)
                ? Path.GetFullPath(overridden)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "backend-settings.json");
        }
    }

    public static ProcessingBackendSettings Load()
    {
        string path = SettingsPath;
        if (!File.Exists(path)) return new ProcessingBackendSettings();
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The processing-pipeline settings root must be a JSON object.");

            int schemaVersion = root.TryGetProperty("schemaVersion", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int version) ? version : 1;
            if (schemaVersion >= ProcessingBackendSettings.CurrentSchemaVersion)
                return ReadCurrent(root);

            ProcessingBackendSettings migrated = MigrateLegacySettings(root);
            try
            {
                Save(migrated);
                return Load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LogService.Warn("Processing-pipeline settings migration could not be written back to disk; continuing with migrated settings.",
                    ex.Message, LogSource.Settings);
                return migrated;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warn("Processing-pipeline settings could not be loaded; using Rebuilt by default.",
                ex.Message, LogSource.Settings);
            return new ProcessingBackendSettings();
        }
    }

    public static void Save(ProcessingBackendSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using Mutex mutex = new(false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                LogService.Warn("Processing-pipeline settings mutex was abandoned by a previous process.",
                    source: LogSource.Settings);
            }

            SaveUnderLock(settings);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Atomically updates the one branch switch.</summary>
    public static void SetMode(ProcessingPipelineMode mode)
    {
        using Mutex mutex = new(false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                LogService.Warn("Processing-pipeline settings mutex was abandoned by a previous process.",
                    source: LogSource.Settings);
            }

            ProcessingBackendSettings settings = LoadUnderLock();
            settings.Mode = mode;
            SaveUnderLock(settings);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Removes persisted configuration so the default Rebuilt branch applies.</summary>
    public static void Reset()
    {
        using Mutex mutex = new(false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                LogService.Warn("Processing-pipeline settings mutex was abandoned by a previous process.",
                    source: LogSource.Settings);
            }

            string path = SettingsPath;
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string FormatMode(ProcessingPipelineMode mode) =>
        mode == ProcessingPipelineMode.Rebuilt ? "rebuilt" : "legacy";

    public static bool TryParseMode(string? value, out ProcessingPipelineMode mode)
    {
        if (string.Equals(value?.Trim(), "rebuilt", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessingPipelineMode.Rebuilt;
            return true;
        }
        if (string.Equals(value?.Trim(), "legacy", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessingPipelineMode.Legacy;
            return true;
        }

        mode = ProcessingPipelineMode.Rebuilt;
        return false;
    }

    private static void SaveUnderLock(ProcessingBackendSettings settings)
    {
        string path = SettingsPath;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Processing-pipeline settings path must have a parent directory.");

        Directory.CreateDirectory(directory);
        ProcessingBackendSettings current = LoadUnderLock();
        settings.Revision = Math.Max(settings.Revision, current.Revision) + 1;
        string json = JsonSerializer.Serialize(new PersistedSettings
        {
            SchemaVersion = ProcessingBackendSettings.CurrentSchemaVersion,
            Revision = settings.Revision,
            Mode = FormatMode(settings.Mode)
        }, JsonOptions);
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
    }

    private static ProcessingBackendSettings LoadUnderLock()
    {
        string path = SettingsPath;
        if (!File.Exists(path)) return new ProcessingBackendSettings();
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("The processing-pipeline settings root must be a JSON object.");

            return root.TryGetProperty("schemaVersion", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int version)
                && version >= ProcessingBackendSettings.CurrentSchemaVersion
                ? ReadCurrent(root)
                : MigrateLegacySettings(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warn("Processing-pipeline settings were malformed; using Rebuilt by default.", ex.Message,
                LogSource.Settings);
            return new ProcessingBackendSettings();
        }
    }

    private static ProcessingBackendSettings ReadCurrent(JsonElement root)
    {
        var result = new ProcessingBackendSettings();
        if (root.TryGetProperty("revision", out JsonElement revision)
            && revision.ValueKind == JsonValueKind.Number
            && revision.TryGetInt64(out long value))
            result.Revision = Math.Max(0, value);
        if (root.TryGetProperty("mode", out JsonElement mode)
            && mode.ValueKind == JsonValueKind.String
            && TryParseMode(mode.GetString(), out ProcessingPipelineMode parsed))
            result.Mode = parsed;
        return result;
    }

    private static ProcessingBackendSettings MigrateLegacySettings(JsonElement root)
    {
        var result = new ProcessingBackendSettings();
        if (root.TryGetProperty("mode", out JsonElement oldMode)
            && oldMode.ValueKind == JsonValueKind.String
            && string.Equals(oldMode.GetString(), "legacy", StringComparison.OrdinalIgnoreCase))
            result.Mode = ProcessingPipelineMode.Legacy;
        LogService.Warn("Migrated legacy backend settings to Rebuilt processing-pipeline mode.",
            source: LogSource.Settings);
        return result;
    }

    private sealed class PersistedSettings
    {
        public int SchemaVersion { get; set; }
        public long Revision { get; set; }
        public string Mode { get; set; } = "rebuilt";
    }
}

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// Manages persisted processing settings. Rebuilt is the sole native media
/// pipeline; legacy mode does not exist and historical settings are inert.
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

            return ReadSettings(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warn("Processing-pipeline settings could not be loaded; using default Rebuilt settings.",
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

    /// <summary>Removes persisted configuration so default settings apply.</summary>
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
            Revision = settings.Revision
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

            return ReadSettings(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warn("Processing-pipeline settings were malformed; using default Rebuilt settings.", ex.Message,
                LogSource.Settings);
            return new ProcessingBackendSettings();
        }
    }

    private static ProcessingBackendSettings ReadSettings(JsonElement root)
    {
        var result = new ProcessingBackendSettings();
        if (root.TryGetProperty("revision", out JsonElement revision)
            && revision.ValueKind == JsonValueKind.Number
            && revision.TryGetInt64(out long value))
            result.Revision = Math.Max(0, value);

        // Note: Any old "mode" property (including "legacy") is intentionally ignored.
        return result;
    }

    private sealed class PersistedSettings
    {
        public int SchemaVersion { get; set; }
        public long Revision { get; set; }
    }
}

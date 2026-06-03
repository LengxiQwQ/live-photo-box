using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 负责崩溃日志文件的写入操作
    /// </summary>
    internal static class CrashLogWriter
    {
        private const string CrashLogSearchPattern = "crash-*.log";
        private static readonly JsonSerializerOptions SessionStateSerializerOptions = new() { WriteIndented = true };

        public static string? WriteCrashLog(string source, Exception? exception, IEnumerable<(string Key, string Value)>? extraFields = null)
        {
            try
            {
                string logPath = CreateCrashLogPath();

                using FileStream stream = new(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using StreamWriter writer = new(stream, new UTF8Encoding(false));

                WriteHeader(writer, source, extraFields);
                WriteEnvironment(writer);
                WriteAppState(writer);
                WriteRecentLogs(writer);
                WriteException(writer, exception);

                writer.Flush();
                stream.Flush(true);
                return logPath;
            }
            catch (Exception ex)
            {
                AppLogService.Error("Failed to write crash log", ex, LogSource.System);
                return null;
            }
        }

        public static string? WriteRecoveredCrashLog(AppStateSnapshot sessionState)
        {
            try
            {
                string logPath = CreateCrashLogPath(
                    sessionState.LastUpdatedAt == default ? null : sessionState.LastUpdatedAt,
                    "-recovered");

                using FileStream stream = new(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using StreamWriter writer = new(stream, new UTF8Encoding(false));

                WriteHeader(writer, "Recovered.PreviousUncleanShutdown",
                [
                    ("RecoveredFromSessionId", sessionState.SessionId),
                    ("SessionStartedAt", sessionState.StartedAt == default ? "(unknown)" : sessionState.StartedAt.ToString("O")),
                    ("LastUpdatedAt", sessionState.LastUpdatedAt == default ? "(unknown)" : sessionState.LastUpdatedAt.ToString("O")),
                    ("RecoveryReason", "Previous app session ended without a clean shutdown marker.")
                ]);

                WriteEnvironment(writer);
                WriteRecoveredAppState(writer, sessionState);
                WriteRecentLogs(writer);
                WriteException(writer, new InvalidOperationException("No managed exception was captured. Crash log recovered from session state."));

                writer.Flush();
                stream.Flush(true);
                return logPath;
            }
            catch (Exception ex)
            {
                AppLogService.Error("Failed to write recovered crash log", ex, LogSource.System);
                return null;
            }
        }

        public static IReadOnlyList<string> GetCrashLogPaths()
        {
            string logDir = GetLogDirectory();
            if (!Directory.Exists(logDir)) return [];

            return Directory.EnumerateFiles(logDir, CrashLogSearchPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }

        public static string? GetLatestCrashLogPath() =>
            GetCrashLogPaths().FirstOrDefault(path => !IsRecoveredCrashLogPath(path));

        public static string? GetLatestRecoveredCrashLogPath() =>
            GetCrashLogPaths().FirstOrDefault(IsRecoveredCrashLogPath);

        public static IReadOnlyList<string> GetCrashDumpPaths()
        {
            string dumpDir = GetDumpDirectory();
            if (!Directory.Exists(dumpDir)) return [];

            return Directory.EnumerateFiles(dumpDir, "*.dmp", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }

        public static string? GetLatestCrashDumpPath() => GetCrashDumpPaths().FirstOrDefault();

        public static int DeleteAllCrashLogs()
        {
            int deleted = 0;
            foreach (var path in GetCrashLogPaths())
            {
                try { File.Delete(path); deleted++; } catch { }
            }
            return deleted;
        }

        public static int DeleteAllCrashArtifacts()
        {
            int deleted = DeleteAllCrashLogs();
            foreach (var path in GetCrashDumpPaths())
            {
                try { File.Delete(path); deleted++; } catch { }
            }
            return deleted;
        }

        public static string? GenerateTestCrashLog()
        {
            return WriteCrashLog("Manual.TestCrashLog",
                new InvalidOperationException("This is a manually generated test crash log."),
                [("IsTestLog", bool.TrueString)]);
        }

        #region Private Methods

        private static bool IsRecoveredCrashLogPath(string path) =>
            Path.GetFileName(path).Contains("-recovered.", StringComparison.OrdinalIgnoreCase);

        private static string CreateCrashLogPath(DateTimeOffset? timestamp = null, string suffix = "")
        {
            string logDir = GetLogDirectory();
            Directory.CreateDirectory(logDir);
            DateTimeOffset ts = timestamp ?? DateTimeOffset.Now;
            return Path.Combine(logDir, $"crash-{ts:yyyyMMdd-HHmmss-fff}{suffix}.log");
        }

        private static string GetLogDirectory()
        {
            try
            {
                string localPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                return Path.Combine(localPath, "Logs");
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "Logs");
            }
        }

        private static string GetDumpDirectory()
        {
            try
            {
                return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Logs", "Dumps");
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "Logs", "Dumps");
            }
        }

        private static void WriteHeader(StreamWriter writer, string source, IEnumerable<(string Key, string Value)>? extraFields)
        {
            AssemblyName assemblyName = Assembly.GetExecutingAssembly().GetName();
            writer.WriteLine("═══════════════════════════════════════════════════════════════");
            writer.WriteLine("                    LivePhotoBox Crash Report");
            writer.WriteLine("═══════════════════════════════════════════════════════════════");
            writer.WriteLine($"Timestamp:    {DateTimeOffset.Now:O}");
            writer.WriteLine($"Source:       {source}");
            writer.WriteLine($"AppVersion:  {assemblyName.Version}");
            writer.WriteLine($"LogCount:    {AppLogService.TotalLogCount}");

            if (extraFields != null)
            {
                foreach (var (key, value) in extraFields)
                    writer.WriteLine($"{key}:  {value}");
            }
            writer.WriteLine();
        }

        private static void WriteEnvironment(StreamWriter writer)
        {
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine("                        Environment");
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine($"ProcessId:           {Environment.ProcessId}");
            writer.WriteLine($"MachineName:         {Environment.MachineName}");
            writer.WriteLine($"OSVersion:           {Environment.OSVersion}");
            writer.WriteLine($"OSDescription:      {RuntimeInformation.OSDescription}");
            writer.WriteLine($"Framework:           {RuntimeInformation.FrameworkDescription}");
            writer.WriteLine($"Architecture:       {RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}");
            writer.WriteLine($"ProcessorCount:     {Environment.ProcessorCount}");
            writer.WriteLine($"SystemUptime:       {FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
            writer.WriteLine($"CurrentCulture:     {CultureInfo.CurrentCulture.Name}");
            writer.WriteLine($"TimeZone:           {TimeZoneInfo.Local.DisplayName}");
            writer.WriteLine();
        }

        private static void WriteAppState(StreamWriter writer)
        {
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine("                      Current App State");
            writer.WriteLine("───────────────────────────────────────────────────────────────");

            try
            {
                writer.WriteLine($"MainWindowCreated:   {App.MainWindow != null}");

                if (App.MainWindow is MainWindow window)
                {
                    var vm = window.ViewModel;
                    writer.WriteLine($"CurrentPageTag:      {vm.CurrentStatusPageTag ?? "(null)"}");
                    writer.WriteLine($"CurrentStatus:      {vm.CurrentPageStatus}");
                    writer.WriteLine();
                    writer.WriteLine("[Combo]");
                    writer.WriteLine($"  Status:           {vm.Combo.Status}");
                    writer.WriteLine($"  IsProcessing:     {vm.Combo.IsProcessing}");
                    writer.WriteLine($"  Tasks:            {vm.Combo.Tasks.Count}");
                    writer.WriteLine($"  Progress:         {vm.Combo.ComboProgress:F1}%");
                    writer.WriteLine($"  InputDir:         {TruncatePath(vm.Combo.InputDirectory)}");
                    writer.WriteLine($"  OutputDir:        {TruncatePath(vm.Combo.OutputDirectory)}");
                    writer.WriteLine();
                    writer.WriteLine("[Split]");
                    writer.WriteLine($"  Status:           {vm.Split.Status}");
                    writer.WriteLine($"  Tasks:            {vm.Split.Tasks.Count}");
                    writer.WriteLine($"  Progress:         {vm.Split.Progress:F1}%");
                    writer.WriteLine();
                    writer.WriteLine("[Repair]");
                    writer.WriteLine($"  Status:           {vm.Repair.Status}");
                    writer.WriteLine();
                }
            }
            catch (Exception ex)
            {
                writer.WriteLine($"StateCaptureError: {ex.Message}");
            }

            writer.WriteLine();
        }

        private static void WriteRecoveredAppState(StreamWriter writer, AppStateSnapshot state)
        {
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine("                    Recovered App State");
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine($"SessionId:          {state.SessionId}");
            writer.WriteLine($"StartedAt:          {state.StartedAt:O}");
            writer.WriteLine($"LastUpdatedAt:      {state.LastUpdatedAt:O}");
            writer.WriteLine($"CurrentPageTag:     {state.CurrentPageTag}");
            writer.WriteLine();
            writer.WriteLine("[Combo]");
            writer.WriteLine($"  Status:           {state.ComboStatus}");
            writer.WriteLine($"  Tasks:            {state.ComboTaskCount}");
            writer.WriteLine($"  Progress:         {state.ComboProgress:F1}%");
            writer.WriteLine($"  InputDir:         {TruncatePath(state.ComboInputDir)}");
            writer.WriteLine($"  OutputDir:        {TruncatePath(state.ComboOutputDir)}");
            writer.WriteLine();
            writer.WriteLine("[Split]");
            writer.WriteLine($"  Status:           {state.SplitStatus}");
            writer.WriteLine($"  Tasks:            {state.SplitTaskCount}");
            writer.WriteLine($"  Progress:         {state.SplitProgress:F1}%");
            writer.WriteLine();
            writer.WriteLine("[Repair]");
            writer.WriteLine($"  Status:           {state.RepairStatus}");
            writer.WriteLine($"  Tasks:            {state.RepairTaskCount}");
            writer.WriteLine($"  Progress:         {state.RepairProgress:F1}%");
            writer.WriteLine();
        }

        private static void WriteRecentLogs(StreamWriter writer)
        {
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine("                    Recent Application Logs");
            writer.WriteLine("───────────────────────────────────────────────────────────────");

            var recentLogs = AppLogService.GetRecentLogs(50);
            if (recentLogs.Count == 0)
            {
                writer.WriteLine("(no logs available)");
            }
            else
            {
                foreach (var entry in recentLogs)
                {
                    string levelStr = entry.Level.ToString().ToUpper().PadRight(8);
                    writer.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{levelStr}] [{entry.Source,-8}] {entry.Message}");
                    if (!string.IsNullOrEmpty(entry.Details))
                        writer.WriteLine($"  Details: {entry.Details}");
                    if (!string.IsNullOrEmpty(entry.ExceptionType))
                        writer.WriteLine($"  Exception: {entry.ExceptionType}");
                }
            }

            writer.WriteLine();
        }

        private static void WriteException(StreamWriter writer, Exception? exception)
        {
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine("                         Exception");
            writer.WriteLine("───────────────────────────────────────────────────────────────");
            writer.WriteLine(exception?.ToString() ?? "(null)");
            writer.WriteLine();
        }

        private static string FormatDuration(TimeSpan duration) =>
            $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";

        private static string TruncatePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(empty)";
            if (path.Length <= 60) return path;
            return "..." + path.Substring(path.Length - 57);
        }

        #endregion
    }
}

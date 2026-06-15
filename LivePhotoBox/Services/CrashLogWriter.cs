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
    /// 负责日志写入操作 - 统一写入 app.log
    /// 崩溃和普通日志都写入同一个文件
    /// </summary>
    internal static class CrashLogWriter
    {
        private const string CrashLogSearchPattern = "crash-*.log";
        private static readonly JsonSerializerOptions SessionStateSerializerOptions = new() { WriteIndented = true };

        public static string? WriteCrashLog(string source, Exception? exception, IEnumerable<(string Key, string Value)>? extraFields = null)
        {
            try
            {
                // 获取当前日志文件路径
                string? currentLogPath = AppLogService.GetCurrentLogFilePath();

                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine("                    LivePhotoBox 崩溃报告");
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"来源: {source}");
                sb.AppendLine($"应用版本: {Assembly.GetExecutingAssembly().GetName().Version}");

                if (extraFields != null)
                {
                    foreach (var (key, value) in extraFields)
                        sb.AppendLine($"{key}: {value}");
                }

                sb.AppendLine();
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine("                        异常信息");
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine(exception?.ToString() ?? "(null)");
                sb.AppendLine();

                string crashInfo = sb.ToString();

                // 写入 app.log
                if (!string.IsNullOrEmpty(currentLogPath))
                {
                    try
                    {
                        File.AppendAllText(currentLogPath, crashInfo, Encoding.UTF8);
                    }
                    catch
                    {
                        // 如果写入 app.log 失败，尝试写入到备用文件
                    }
                }

                // 同时返回一个兼容性的 crash 文件路径（如果需要）
                string crashLogPath = CreateCrashLogPath();
                File.WriteAllText(crashLogPath, crashInfo, Encoding.UTF8);
                return crashLogPath;
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

                var sb = new StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine("                    LivePhotoBox 崩溃报告 (恢复)");
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine($"时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"来源: Recovered.PreviousUncleanShutdown");
                sb.AppendLine($"应用版本: {Assembly.GetExecutingAssembly().GetName().Version}");
                sb.AppendLine($"恢复的会话ID: {sessionState.SessionId}");
                sb.AppendLine($"会话开始时间: {sessionState.StartedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"最后更新时间: {sessionState.LastUpdatedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"恢复原因: 上一个应用会话未正常关闭");
                sb.AppendLine();
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine("                        异常信息");
                sb.AppendLine("───────────────────────────────────────────────────────────────");
                sb.AppendLine("No managed exception was captured. Crash log recovered from session state.");
                sb.AppendLine();

                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
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

        /// <summary>
        /// 获取最新的日志文件（app.log）
        /// </summary>
        public static string? GetLatestAppLogPath()
        {
            string logDir = GetLogDirectory();
            if (!Directory.Exists(logDir)) return null;

            return Directory.GetFiles(logDir, "app-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

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
                new InvalidOperationException("这是一条手动生成的测试崩溃日志。"),
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

        #endregion
    }
}

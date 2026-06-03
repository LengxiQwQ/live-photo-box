using LivePhotoBox.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 应用日志服务 - 统一记录所有日志
    /// </summary>
    public static class AppLogService
    {
        #region Constants & Fields

        private const int MaxLogEntries = 10000;
        private const int MaxRecentMessages = 100;
        private const string LogFileName = "app.log";

        private static readonly ConcurrentQueue<AppLogEntry> _logEntries = new();
        private static readonly ConcurrentDictionary<string, int> _sourceCounts = new();
        private static readonly object _fileLock = new();
        private static long _totalLogCount;
        private static string? _logFilePath;
        private static string? _logDirectory;
        private static readonly ManualResetEventSlim _flushEvent = new(false);
        private static readonly CancellationTokenSource _cts = new();

        #endregion

        #region Public API - Initialization & Control

        /// <summary>
        /// 初始化日志服务
        /// </summary>
        public static void Initialize()
        {
            _logDirectory = GetLogDirectory();
            Directory.CreateDirectory(_logDirectory!);
            _logFilePath = Path.Combine(_logDirectory, LogFileName);

            Log(LogSource.App, LogLevel.Info, "AppLogService initialized.");

            Task.Run(BackgroundFlushLoop);
        }

        /// <summary>
        /// 刷新日志到磁盘
        /// </summary>
        public static void Flush()
        {
            _flushEvent.Set();
        }

        /// <summary>
        /// 关闭日志服务
        /// </summary>
        public static void Shutdown()
        {
            _cts.Cancel();
            _flushEvent.Set();
            WriteAllPendingToFile();
        }

        #endregion

        #region Public API - Logging
        public static void Log(
            LogSource source,
            LogLevel level,
            string message,
            string? details = null,
            Exception? exception = null,
            string? filePath = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var entry = new AppLogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Level = level,
                Source = source,
                Message = message,
                Details = details,
                ExceptionType = exception?.GetType().Name,
                StackTrace = exception?.StackTrace,
                FilePath = filePath ?? $"{sourceFilePath}:{lineNumber}",
                OperationId = memberName
            };

            EnqueueLog(entry);
        }

        /// <summary>
        /// 记录追踪级别日志
        /// </summary>
        public static void Trace(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Trace, message, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录调试级别日志
        /// </summary>
        public static void Debug(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Debug, message, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录信息级别日志
        /// </summary>
        public static void Info(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Info, message, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        public static void Warn(string message, string? details = null, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Warning, message, details, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录错误级别日志
        /// </summary>
        public static void Error(string message, Exception? exception = null, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Error, message, exception: exception, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录关键错误日志
        /// </summary>
        public static void Critical(string message, Exception? exception = null, LogSource source = LogSource.App,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(source, LogLevel.Critical, message, exception: exception, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录 Combo 模块日志
        /// </summary>
        public static void Combo(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(LogSource.Combo, level, message, exception: ex, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录 Split 模块日志
        /// </summary>
        public static void Split(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(LogSource.Split, level, message, exception: ex, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录 Repair 模块日志
        /// </summary>
        public static void Repair(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(LogSource.Repair, level, message, exception: ex, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录 Scan 模块日志
        /// </summary>
        public static void Scan(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(LogSource.Scan, level, message, exception: ex, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        /// <summary>
        /// 记录文件操作日志
        /// </summary>
        public static void FileOp(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? memberName = null,
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int lineNumber = 0)
            => Log(LogSource.File, level, message, exception: ex, filePath: $"{sourceFilePath}:{lineNumber}", memberName: memberName);

        #endregion

        #region Public API - Query
        public static IReadOnlyList<AppLogEntry> GetRecentLogs(int count = 100)
        {
            var result = new List<AppLogEntry>();
            foreach (var entry in _logEntries)
            {
                result.Add(entry);
                if (result.Count >= count)
                    break;
            }
            result.Reverse();
            return result;
        }

        /// <summary>
        /// 获取指定来源的日志统计
        /// </summary>
        public static IReadOnlyDictionary<string, int> GetSourceStats()
        {
            return _sourceCounts;
        }

        /// <summary>
        /// 获取日志总数
        /// </summary>
        public static long TotalLogCount => Interlocked.Read(ref _totalLogCount);

        /// <summary>
        /// 获取日志目录
        /// </summary>
        public static string GetLogDirectoryPath() => _logDirectory ?? GetLogDirectory();

        #endregion

        #region Private Helpers

        private static void EnqueueLog(AppLogEntry entry)
        {
            _logEntries.Enqueue(entry);
            Interlocked.Increment(ref _totalLogCount);

            _sourceCounts.AddOrUpdate(
                entry.Source.ToString(),
                1,
                (_, count) => count + 1);

            while (_logEntries.Count > MaxLogEntries && _logEntries.TryDequeue(out _)) { }

            if (entry.Level >= LogLevel.Warning)
            {
                _flushEvent.Set();
            }
        }

        private static async Task BackgroundFlushLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                _flushEvent.Wait(TimeSpan.FromSeconds(30));
                if (_cts.Token.IsCancellationRequested) break;

                WriteAllPendingToFile();
                _flushEvent.Reset();
            }
        }

        private static void WriteAllPendingToFile()
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            var entries = new List<AppLogEntry>();
            while (_logEntries.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0) return;

            try
            {
                lock (_fileLock)
                {
                    var sb = new StringBuilder();
                    foreach (var entry in entries)
                    {
                        sb.AppendLine(entry.FormattedMessage);
                        if (!string.IsNullOrEmpty(entry.Details))
                            sb.AppendLine($"  Details: {entry.Details}");
                        if (!string.IsNullOrEmpty(entry.ExceptionType))
                            sb.AppendLine($"  Exception: {entry.ExceptionType}");
                        if (!string.IsNullOrEmpty(entry.StackTrace))
                        {
                            sb.AppendLine("  StackTrace:");
                            foreach (var line in entry.StackTrace.Split('\n'))
                                sb.AppendLine($"    {line.Trim()}");
                        }
                    }

                    File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);

                    RotateLogIfNeeded();
                }
            }
            catch { }
        }

        private static void RotateLogIfNeeded()
        {
            if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath))
                return;

            try
            {
                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length > 10 * 1024 * 1024) // 10MB
                {
                    var archivePath = Path.Combine(
                        Path.GetDirectoryName(_logFilePath)!,
                        $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Move(_logFilePath, archivePath);
                }
            }
            catch { }
        }

        private static string GetLogDirectory()
        {
            try
            {
                return Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "Logs");
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox",
                    "Logs");
            }
        }

        #endregion
    }
}

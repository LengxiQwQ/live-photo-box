using LivePhotoBox.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;
// Packaged mode detection and WWAHost activation require Windows.ApplicationModel.
// In net9.0 console / unpackaged WinUI, these types are present but the probe
// (Package.Current) always throws — guarded by try/catch below.
using Windows.ApplicationModel;

/*
 * LogService.cs
 *
 * 统一日志服务。
 *
 *   - 每次会话 = 一个 .log 文件，文件名 app-YYYYMMDD-HHmmssfff-<pid>.log
 *   - 常规信息、崩溃报告、会话标记写入同一文件流；崩溃报告直接追加日志尾部
 *   - 崩溃检测读取上次会话日志尾部标记，无需额外状态文件
 *   - 线程安全：ConcurrentQueue 入队，异步批量 flush + 同步加锁写盘
 *   - 保留策略：目录始终 ≤100 个日志文件（含当前会话）+ 5 个 dump 文件
 */

namespace LivePhotoBox.Services
{
    // 统一日志服务（设计要点见文件头）。
    // 崩溃报告直接追加到同一日志流，不另建独立崩溃文件、无 JSON 状态；
    // 崩溃检测读上次会话日志尾部标记；关键/崩溃条目同步立即写盘。
    public static class LogService
    {
        #region Constants

        private const int MaxLogFiles = 100;
        private const int MaxDumpFiles = 5;
        private const int MaxMemoryEntries = 1000;
        private const int CrashContextLineCount = 50;
        private static string LogFilePrefix = "app";
        private const string LogFileExtension = ".log";
        private const string CleanShutdownMarker = "CLEAN SHUTDOWN";

        #endregion

        #region P/Invoke (for crash diagnostics)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        #endregion

        #region Fields

        private static readonly ConcurrentQueue<AppLogEntry> _entries = new();
        private static readonly object _fileLock = new();
        private static readonly ManualResetEventSlim _flushSignal = new(false);
        private static readonly CancellationTokenSource _shutdownCts = new();

        private static string? _currentLogPath;
        private static string? _logDirectory;
        private static long _totalCount;
        private static bool _initialized;

        #endregion

        #region Public State

        // True if the previous application session did not end with a clean shutdown
        // (i.e. the last log file is missing the CLEAN SHUTDOWN marker).
        // Set by <see cref="Initialize"/>.
        public static bool LastSessionCrashed { get; private set; }

        // Path to the log file from the previous session (crashed or not).
        // Useful for showing the user which file to inspect after a crash.
        public static string? PreviousLogPath { get; private set; }

        #endregion

        #region Initialization & Shutdown

        // Initializes the logging service. Must be called once at application startup,
        // before any logging calls.
        // Actions:
        // 1. Creates the Logs directory
        // 2. Rotates old log files (keeps last 15) and dumps (keeps last 5)
        // 3. Detects whether the previous session crashed
        // 4. Opens a new log file for this session
        // 5. Starts the background flush loop
        public static void Initialize(string subDirectory = "", string logFilePrefix = "app")
        {
            if (_initialized) return;
            _initialized = true;

            LogFilePrefix = logFilePrefix;
            _logDirectory = string.IsNullOrEmpty(subDirectory)
                ? ResolveLogDirectory()
                : Path.Combine(ResolveLogDirectory(), subDirectory);
            Directory.CreateDirectory(_logDirectory);

            // Detect previous crash BEFORE creating the new file
            DetectPreviousCrash();

            // Rotate old files
            CleanupOldLogFiles();
            CleanupOldDumpFiles();

            // Create new session log file
            _currentLogPath = Path.Combine(_logDirectory, GenerateLogFileName());
            File.WriteAllText(_currentLogPath,
                $"=== Live Photo Box Session Started [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [v{GetAppVersion()}] ===\n",
                Encoding.UTF8);

            // Seed the log and flush immediately so startup is persisted
            Enqueue(LogSource.System, LogLevel.Info, "LogService initialized.");
            FlushPendingEntries();

            // Log system information (OS, runtime, process architecture, memory)
            LogSystemInfo();
            FlushPendingEntries();

            // Start async flush loop
            Task.Run(BackgroundFlushLoop);
        }

        // Gracefully shuts down the logging service.
        // Writes the CLEAN SHUTDOWN marker, flushes all pending entries, and stops the flush loop.
        public static void MarkCleanShutdown()
        {
            if (string.IsNullOrEmpty(_currentLogPath)) return;

            try
            {
                // Flush any remaining queued entries first
                FlushPendingEntries();

                // Write the clean shutdown marker
                lock (_fileLock)
                {
                    File.AppendAllText(_currentLogPath,
                        $"\n=== Session Ended ({CleanShutdownMarker}) [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] ===\n",
                        Encoding.UTF8);
                }
            }
            catch { /* Best-effort */ }

            Shutdown();
        }

        // Force-flush all pending entries to disk immediately.
        // Called by crash handlers to ensure the log is as complete as possible.
        public static void ForceFlush()
        {
            FlushPendingEntries();
        }

        private static void Shutdown()
        {
            _shutdownCts.Cancel();
            _flushSignal.Set();
            FlushPendingEntries();
        }

        #endregion

        #region Core Logging API

        // Low-level log method. All convenience methods delegate here.
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
            // If both message and exception are empty/nil, there's nothing to log
            if (string.IsNullOrWhiteSpace(message) && exception == null) return;

            // If message is empty but exception is provided, use exception message as fallback
            string effectiveMessage = string.IsNullOrWhiteSpace(message)
                ? exception?.Message ?? "(no message)"
                : message;

            Enqueue(source, level, effectiveMessage, details, exception, filePath ?? $"{sourceFilePath}:{lineNumber}", memberName);

            // Warning or above → signal flush so it hits disk sooner
            if (level >= LogLevel.Warning)
            {
                _flushSignal.Set();
            }
        }

        // ── Convenience methods ──

        public static void Trace(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Trace, message, filePath: $"{f}:{l}", memberName: m);

        public static void Debug(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Debug, message, filePath: $"{f}:{l}", memberName: m);

        public static void Info(string message, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Info, message, filePath: $"{f}:{l}", memberName: m);

        public static void Warn(string message, string? details = null, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Warning, message, details, filePath: $"{f}:{l}", memberName: m);

        public static void Error(string message, Exception? exception = null, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Error, message, exception: exception, filePath: $"{f}:{l}", memberName: m);

        public static void Critical(string message, Exception? exception = null, LogSource source = LogSource.App,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(source, LogLevel.Critical, message, exception: exception, filePath: $"{f}:{l}", memberName: m);

        // ── Module-specific shortcuts ──

        public static void Merge(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.Merge, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        public static void Split(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.Split, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        public static void Repair(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.Repair, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        public static void Scan(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.Scan, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        public static void FileOp(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.File, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        public static void History(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.History, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

        #endregion

        #region Crash Section

        // Writes a formatted crash-report section directly into the current log file.
        // This method flushes any pending queue entries first, then appends the crash
        // section synchronously — it does NOT go through the async queue, because
        // the process may terminate immediately after.
        // The crash section includes:
        // - Header (timestamp, source, version)
        // - Exception details (type, message, stack trace)
        // - Optional extra fields (e.g. IsTerminating)
        // - The last ~50 log entries that were still in the memory queue (crash context)
        // - System memory snapshot
        public static void WriteCrashSection(string source, Exception? exception,
            IEnumerable<(string Key, string Value)>? extraFields = null)
        {
            try
            {
                // 1. Flush existing queue so the crash section appears after all prior logs
                FlushPendingEntries();

                if (string.IsNullOrEmpty(_currentLogPath)) return;

                // 2. Snapshot the in-memory entries since last flush (the real crash context)
                var recentContext = GetRecentEntries(CrashContextLineCount);

                // 3. Build the crash section
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("=== CRASH REPORT ===");
                sb.AppendLine($"Timestamp:  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"Source:     {source}");
                sb.AppendLine($"Version:    {GetAppVersion()}");

                if (extraFields != null)
                {
                    foreach (var (key, value) in extraFields)
                        sb.AppendLine($"{key}: {value}");
                }

                // System memory snapshot
                try
                {
                    var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                    if (GlobalMemoryStatusEx(ref mem))
                    {
                        sb.AppendLine($"Memory:     Total={mem.ullTotalPhys / (1024 * 1024)}MB, " +
                            $"Avail={mem.ullAvailPhys / (1024 * 1024)}MB, " +
                            $"Load={mem.dwMemoryLoad}%");
                    }
                }
                catch { /* not critical */ }

                sb.AppendLine();
                sb.AppendLine("--- Exception ---");
                sb.AppendLine(exception?.ToString() ?? "(null)");
                sb.AppendLine();

                if (recentContext.Count > 0)
                {
                    sb.AppendLine($"--- Last {CrashContextLineCount} log entries before crash ---");
                    foreach (var entry in recentContext)
                        sb.AppendLine($"  {entry.ToString()}");
                    sb.AppendLine();
                }

                sb.AppendLine("=== END CRASH REPORT ===");
                sb.AppendLine();

                // 4. Write synchronously to disk
                lock (_fileLock)
                {
                    File.AppendAllText(_currentLogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Absolute last resort — we must not throw from a crash handler
            }
        }

        // Generates a test crash section for verifying the crash-reporting pipeline.
        public static void GenerateTestCrashSection()
        {
            WriteCrashSection("Manual.TestCrashLog",
                new InvalidOperationException("Manually triggered test crash log."),
                [("IsTestLog", bool.TrueString)]);
        }

        #endregion

        #region Query API

        // Returns up to <paramref name="count"/> recent log entries from the in-memory queue.
        public static IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 100)
        {
            var result = new List<AppLogEntry>();
            foreach (var entry in _entries)
            {
                result.Add(entry);
                if (result.Count >= count) break;
            }
            result.Reverse();
            return result;
        }

        // Total number of log entries processed this session.
        public static long TotalCount => Interlocked.Read(ref _totalCount);

        // Path to the currently active log file.
        public static string? CurrentLogPath => _currentLogPath;

        // Path to the Logs directory.
        public static string LogDirectory => _logDirectory ?? ResolveLogDirectory();

        // Returns the path to the most recent app-*.log file (current or previous session).
        // Returns null if no log files exist.
        public static string? GetLatestLogPath()
        {
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
                return null;

            return Directory.GetFiles(_logDirectory, $"{LogFilePrefix}-*{LogFileExtension}")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        // Returns the path to the most recent .dmp (crash dump) file, or null if none exist.
        public static string? GetLatestDumpPath()
        {
            var dumpDir = Path.Combine(LogDirectory, "Dumps");
            if (!Directory.Exists(dumpDir)) return null;

            return Directory.GetFiles(dumpDir, "*.dmp")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        // Deletes all app-*.log files and crash dump files (used by "Clear All Logs" in settings).
        // The current log file will be recreated.
        public static int DeleteAllLogFiles()
        {
            int deleted = 0;
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
                return 0;

            string? currentPath = _currentLogPath;
            foreach (var path in Directory.GetFiles(_logDirectory, $"{LogFilePrefix}-*{LogFileExtension}"))
            {
                // Skip the currently active file — it's locked
                if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(path); deleted++; }
                catch { }
            }

            // Also delete crash dump files (*.dmp) in the Dumps subdirectory
            var dumpDir = Path.Combine(_logDirectory, "Dumps");
            if (Directory.Exists(dumpDir))
            {
                foreach (var path in Directory.GetFiles(dumpDir, "*.dmp"))
                {
                    try { File.Delete(path); deleted++; }
                    catch { }
                }
            }

            return deleted;
        }

        #endregion

        #region Private — Queue & Flush

        private static void Enqueue(
            LogSource source,
            LogLevel level,
            string message,
            string? details = null,
            Exception? exception = null,
            string? filePath = null,
            string? memberName = null)
        {
            var entry = new AppLogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Level = level,
                Source = source,
                Message = message,
                Details = details,
                ExceptionType = exception?.GetType().Name,
                StackTrace = exception?.StackTrace,
                FilePath = filePath,
                OperationId = memberName
            };

            _entries.Enqueue(entry);
            Interlocked.Increment(ref _totalCount);

            // Prevent unbounded memory growth
            while (_entries.Count > MaxMemoryEntries && _entries.TryDequeue(out _)) { }

            if (level >= LogLevel.Warning)
                _flushSignal.Set();
        }

        private static async Task BackgroundFlushLoop()
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                _flushSignal.Wait(TimeSpan.FromSeconds(5));
                if (_shutdownCts.Token.IsCancellationRequested) break;

                FlushPendingEntries();
                _flushSignal.Reset();
            }
        }

        private static void FlushPendingEntries()
        {
            if (string.IsNullOrEmpty(_currentLogPath)) return;

            var batch = new List<AppLogEntry>();
            while (_entries.TryDequeue(out var entry))
                batch.Add(entry);

            if (batch.Count == 0) return;

            try
            {
                lock (_fileLock)
                {
                    var sb = new StringBuilder();
                    foreach (var entry in batch)
                    {
                        if (!string.IsNullOrEmpty(entry.FilePath))
                        {
                            sb.AppendLine($"{entry.FormattedMessage} [{ShortFilePath(entry.FilePath)}]");
                        }
                        else
                        {
                            sb.AppendLine(entry.FormattedMessage);
                        }
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
                    File.AppendAllText(_currentLogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { /* Best-effort */ }
        }

        #endregion

        #region Private — Crash Detection

        // Scans the most recent previous log file for a CLEAN SHUTDOWN marker.
        // Sets <see cref="LastSessionCrashed"/> and <see cref="PreviousLogPath"/>.
        private static void DetectPreviousCrash()
        {
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
                return;

            // Find the most recent app-*.log (this is from the previous session,
            // since we haven't created the current one yet)
            var previousLog = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}-*{LogFileExtension}")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (previousLog == null)
                return; // First ever run — no previous session

            PreviousLogPath = previousLog;

            try
            {
                // Read the last ~2 KB of the file to find the shutdown marker
                const int tailBytes = 2048;
                string tail;
                using (var fs = new FileStream(previousLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long startPos = Math.Max(0, fs.Length - tailBytes);
                    fs.Seek(startPos, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    tail = reader.ReadToEnd();
                }

                if (!tail.Contains(CleanShutdownMarker, StringComparison.Ordinal))
                {
                    LastSessionCrashed = true;
                }
            }
            catch
            {
                // If we can't read the file, err on the side of caution
                LastSessionCrashed = false;
            }
        }

        #endregion

        #region Private — Helpers

        private static string GetAppVersion()
        {
            // 优先取入口程序集版本（GUI 或 CLI），退化到 Core 自身版本。
            // ToString(3) 只取 Major.Minor.Build 三位：项目版本号是 3 段语义
            // （如 2.1.5），尾随的 Revision 恒为 0，打进日志既冗余又不像给人看的版本号。
            return System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString(3)
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                ?? "0.0.0";
        }

        // Logs system information (OS, runtime, CPU, memory, app path, culture) at startup,
        // before any module-specific initialization runs. All data here is available
        // without WMI queries — the hardware detection that follows fills in GPU details.
        private static void LogSystemInfo()
        {
            try
            {
                Enqueue(LogSource.System, LogLevel.Info, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
                Enqueue(LogSource.System, LogLevel.Info, $"Runtime: {RuntimeInformation.FrameworkDescription}");
                Enqueue(LogSource.System, LogLevel.Info, $"Process: {RuntimeInformation.ProcessArchitecture}");
                Enqueue(LogSource.System, LogLevel.Info, $"CPU: {Environment.ProcessorCount} logical cores");
                Enqueue(LogSource.System, LogLevel.Info, $"App Path: {AppContext.BaseDirectory}");
                Enqueue(LogSource.System, LogLevel.Info, $"Language: {System.Globalization.CultureInfo.CurrentUICulture}");

                var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    double totalGB = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availGB = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    Enqueue(LogSource.System, LogLevel.Info, $"Memory: {totalGB:F1} GB total, {availGB:F1} GB available ({mem.dwMemoryLoad}% in use)");
                }
            }
            catch { }
        }

        private static string ShortFilePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            int lastColon = filePath.LastIndexOf(':');
            if (lastColon <= 0) return filePath;
            var pathPart = filePath.Substring(0, lastColon);
            var linePart = filePath.Substring(lastColon + 1);
            return $"{Path.GetFileName(pathPart)}:{linePart}";
        }

        private static string GenerateLogFileName()
        {
            // 秒级分辨率在同一秒并发启动多个进程（CLI 脚本/CI 并行、多 GUI 实例）时
            // 会生成相同文件名 → File.WriteAllText 相互覆盖 → 日志丢失。
            // 追加毫秒 + 进程 ID：同秒内 PID 必然唯一（进程间不共享），毫秒提升可读性与排序精度。
            return $"{LogFilePrefix}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}{LogFileExtension}";
        }

        private static void CleanupOldLogFiles()
        {
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
                return;

            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}-*{LogFileExtension}")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // MaxLogFiles 是"目录里日志总数上限"（含当前会话文件）。
                // 清理在 Initialize 创建当前文件之前执行，故历史文件只保留 MaxLogFiles - 1 个，
                // 创建当前文件后总数恰好 ≤ MaxLogFiles，不会越界累积。
                if (logFiles.Count >= MaxLogFiles)
                {
                    foreach (var file in logFiles.Skip(MaxLogFiles - 1))
                    {
                        try { file.Delete(); }
                        catch { /* File may be locked by another process */ }
                    }
                }
            }
            catch { }
        }

        private static void CleanupOldDumpFiles()
        {
            if (string.IsNullOrEmpty(_logDirectory)) return;

            var dumpDir = Path.Combine(_logDirectory, "Dumps");
            if (!Directory.Exists(dumpDir)) return;

            try
            {
                var dumpFiles = Directory.GetFiles(dumpDir, "*.dmp")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (dumpFiles.Count > MaxDumpFiles)
                {
                    foreach (var file in dumpFiles.Skip(MaxDumpFiles))
                    {
                        try { file.Delete(); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string ResolveLogDirectory()
        {
            // ── 打包模式（商店版本）──────────────────────────────────────────
            // 使用 ApplicationData 的标准隔离路径，位于：
            //   %LOCALAPPDATA%\Packages\<PackageFamily>\LocalState\Logs
            // ────────────────────────────────────────────────────────────────
            if (IsPackagedMode())
            {
                return Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "Logs");
            }

            // ── 非打包模式（便携版/安装版）────────────────────────────────────
            // 使用固定的 %LOCALAPPDATA%\LivePhotoBox\Logs。
            //
            // 注意：WinAppSDK 1.5+ 在非打包模式下 ApplicationData.Current
            // 也能成功调用，但返回的是一个合成包标识路径
            // （%LOCALAPPDATA%\<合成Hash>\LocalState），它会随 WinAppSDK
            // 版本或部署状态变化，导致同一应用在不同系统上日志路径不一致。
            // 这里统一用固定路径，确保所有非打包版本的日志位置一致可预测。
            // ────────────────────────────────────────────────────────────────
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LivePhotoBox",
                "Logs");
        }

        // Detect whether the process is running inside an MSIX/AppX packaged context.
        // Cached after first call; the answer never changes for the lifetime of the process.
        private static bool? _isPackagedMode;
        private static bool IsPackagedMode()
        {
            if (_isPackagedMode.HasValue) return _isPackagedMode.Value;
            try
            {
                _ = global::Windows.ApplicationModel.Package.Current;
                _isPackagedMode = true;
            }
            catch
            {
                _isPackagedMode = false;
            }
            return _isPackagedMode.Value;
        }

        #endregion
    }
}

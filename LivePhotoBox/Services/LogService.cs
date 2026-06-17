using LivePhotoBox.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

// For Package.Current.Id.Version — matches AboutPage's version display
using Windows.ApplicationModel;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// Unified logging service.
    /// All logs — normal entries, crash reports, session markers — are written
    /// to a single log file per session. No separate crash files, no JSON state.
    ///
    /// Design principles:
    /// - One session = one log file
    /// - Crash reports are appended inline to the same log stream
    /// - Crash detection reads the previous session's log tail (no external state)
    /// - Thread-safe, async flush with immediate sync write for critical/crash entries
    /// - Max 15 log files + 5 dumps retained; older ones auto-deleted
    /// </summary>
    public static class LogService
    {
        #region Constants

        private const int MaxLogFiles = 15;
        private const int MaxDumpFiles = 5;
        private const int MaxMemoryEntries = 1000;
        private const int CrashContextLineCount = 50;
        private const string LogFilePrefix = "app";
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

        /// <summary>
        /// True if the previous application session did not end with a clean shutdown
        /// (i.e. the last log file is missing the CLEAN SHUTDOWN marker).
        /// Set by <see cref="Initialize"/>.
        /// </summary>
        public static bool LastSessionCrashed { get; private set; }

        /// <summary>
        /// Path to the log file from the previous session (crashed or not).
        /// Useful for showing the user which file to inspect after a crash.
        /// </summary>
        public static string? PreviousLogPath { get; private set; }

        #endregion

        #region Initialization & Shutdown

        /// <summary>
        /// Initializes the logging service. Must be called once at application startup,
        /// before any logging calls.
        ///
        /// Actions:
        /// 1. Creates the Logs directory
        /// 2. Rotates old log files (keeps last 15) and dumps (keeps last 5)
        /// 3. Detects whether the previous session crashed
        /// 4. Opens a new log file for this session
        /// 5. Starts the background flush loop
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _logDirectory = ResolveLogDirectory();
            Directory.CreateDirectory(_logDirectory);

            // Detect previous crash BEFORE creating the new file
            DetectPreviousCrash();

            // Rotate old files
            CleanupOldLogFiles();
            CleanupOldDumpFiles();

            // Create new session log file
            _currentLogPath = Path.Combine(_logDirectory, GenerateLogFileName());
            File.WriteAllText(_currentLogPath,
                $"=== LivePhotoBox Session Started [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [v{GetAppVersion()}] ===\n",
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

        /// <summary>
        /// Gracefully shuts down the logging service.
        /// Writes the CLEAN SHUTDOWN marker, flushes all pending entries, and stops the flush loop.
        /// </summary>
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

        /// <summary>
        /// Force-flush all pending entries to disk immediately.
        /// Called by crash handlers to ensure the log is as complete as possible.
        /// </summary>
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

        /// <summary>
        /// Low-level log method. All convenience methods delegate here.
        /// </summary>
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
            if (string.IsNullOrWhiteSpace(message)) return;

            Enqueue(source, level, message, details, exception, filePath ?? $"{sourceFilePath}:{lineNumber}", memberName);

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

        public static void Combo(string message, LogLevel level = LogLevel.Info, Exception? ex = null,
            [CallerMemberName] string? m = null, [CallerFilePath] string? f = null, [CallerLineNumber] int l = 0)
            => Log(LogSource.Combo, level, message, exception: ex, filePath: $"{f}:{l}", memberName: m);

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

        #endregion

        #region Crash Section

        /// <summary>
        /// Writes a formatted crash-report section directly into the current log file.
        /// This method flushes any pending queue entries first, then appends the crash
        /// section synchronously — it does NOT go through the async queue, because
        /// the process may terminate immediately after.
        ///
        /// The crash section includes:
        /// - Header (timestamp, source, version)
        /// - Exception details (type, message, stack trace)
        /// - Optional extra fields (e.g. IsTerminating)
        /// - The last ~50 log entries that were still in the memory queue (crash context)
        /// - System memory snapshot
        /// </summary>
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
                        sb.AppendLine($"  {entry.FormattedMessage}");
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

        /// <summary>
        /// Generates a test crash section for verifying the crash-reporting pipeline.
        /// </summary>
        public static void GenerateTestCrashSection()
        {
            WriteCrashSection("Manual.TestCrashLog",
                new InvalidOperationException("Manually triggered test crash log."),
                [("IsTestLog", bool.TrueString)]);
        }

        #endregion

        #region Query API

        /// <summary>
        /// Returns up to <paramref name="count"/> recent log entries from the in-memory queue.
        /// </summary>
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

        /// <summary>
        /// Total number of log entries processed this session.
        /// </summary>
        public static long TotalCount => Interlocked.Read(ref _totalCount);

        /// <summary>
        /// Path to the currently active log file.
        /// </summary>
        public static string? CurrentLogPath => _currentLogPath;

        /// <summary>
        /// Path to the Logs directory.
        /// </summary>
        public static string LogDirectory => _logDirectory ?? ResolveLogDirectory();

        /// <summary>
        /// Returns the path to the most recent app-*.log file (current or previous session).
        /// Returns null if no log files exist.
        /// </summary>
        public static string? GetLatestLogPath()
        {
            if (string.IsNullOrEmpty(_logDirectory) || !Directory.Exists(_logDirectory))
                return null;

            return Directory.GetFiles(_logDirectory, $"{LogFilePrefix}-*{LogFileExtension}")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Returns the path to the most recent .dmp (crash dump) file, or null if none exist.
        /// </summary>
        public static string? GetLatestDumpPath()
        {
            var dumpDir = Path.Combine(LogDirectory, "Dumps");
            if (!Directory.Exists(dumpDir)) return null;

            return Directory.GetFiles(dumpDir, "*.dmp")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Deletes all app-*.log files (used by "Clear All Logs" in settings).
        /// The current log file will be recreated.
        /// </summary>
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

        /// <summary>
        /// Scans the most recent previous log file for a CLEAN SHUTDOWN marker.
        /// Sets <see cref="LastSessionCrashed"/> and <see cref="PreviousLogPath"/>.
        /// </summary>
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
            try
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            }
        }

        /// <summary>
        /// Logs system information (OS, runtime, CPU, memory, app path, culture) at startup,
        /// before any module-specific initialization runs. All data here is available
        /// without WMI queries — the hardware detection that follows fills in GPU details.
        /// </summary>
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
            return $"{LogFilePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}{LogFileExtension}";
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

                if (logFiles.Count > MaxLogFiles)
                {
                    foreach (var file in logFiles.Skip(MaxLogFiles))
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

using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using XamlUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace LivePhotoBox.Services
{
    public static class CrashLogService
    {
        private const string CrashLogSearchPattern = "crash-*.log";
        private const int MaxBreadcrumbCount = 30;
        private const string HasPendingCrashKey = "HasPendingCrash";
        private const string PendingCrashLogPathKey = "PendingCrashLogPath";
        private static readonly object SyncRoot = new();
        private static readonly Queue<string> Breadcrumbs = [];
        private static bool _initialized;

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
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        public static void Initialize(Application app)
        {
            if (_initialized)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                app.UnhandledException += OnApplicationUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
                TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
                _initialized = true;
            }

            RecordBreadcrumb("Crash logger initialized.");
        }

        public static void RecordBreadcrumb(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (SyncRoot)
            {
                Breadcrumbs.Enqueue($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");

                while (Breadcrumbs.Count > MaxBreadcrumbCount)
                {
                    Breadcrumbs.Dequeue();
                }
            }
        }

        private static void OnApplicationUnhandledException(object sender, XamlUnhandledExceptionEventArgs e)
        {
            string? logPath = WriteCrashLog(
                "Microsoft.UI.Xaml.Application.UnhandledException",
                e.Exception,
                [("Handled", e.Handled.ToString(CultureInfo.InvariantCulture))]);

            MarkPendingCrash(logPath);
        }

        private static void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        {
            string? logPath = WriteCrashLog(
                "AppDomain.CurrentDomain.UnhandledException",
                e.ExceptionObject as Exception,
                [("IsTerminating", e.IsTerminating.ToString(CultureInfo.InvariantCulture))]);

            MarkPendingCrash(logPath);
        }

        private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            string? logPath = WriteCrashLog(
                "TaskScheduler.UnobservedTaskException",
                e.Exception,
                [("ObservedBeforeSet", e.Observed.ToString(CultureInfo.InvariantCulture))]);

            MarkPendingCrash(logPath);
            e.SetObserved();
        }

        public static string GetLogDirectoryPath()
        {
            return GetLogDirectory();
        }

        public static string EnsureLogDirectoryPath()
        {
            string logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);
            return logDirectory;
        }

        public static IReadOnlyList<string> GetCrashLogPaths()
        {
            string logDirectory = GetLogDirectory();
            if (!Directory.Exists(logDirectory))
            {
                return [];
            }

            return Directory.EnumerateFiles(logDirectory, CrashLogSearchPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }

        public static string? GenerateTestCrashLog()
        {
            return WriteCrashLog(
                "Manual.TestCrashLog",
                new InvalidOperationException("This is a manually generated test crash log."),
                [("IsTestLog", bool.TrueString)]);
        }

        public static string? GetLatestCrashLogPath()
        {
            return GetCrashLogPaths().FirstOrDefault();
        }

        public static int DeleteAllCrashLogs()
        {
            IReadOnlyList<string> crashLogPaths = GetCrashLogPaths();
            int deletedCount = 0;

            foreach (string crashLogPath in crashLogPaths)
            {
                try
                {
                    File.Delete(crashLogPath);
                    deletedCount++;
                }
                catch
                {
                }
            }

            if (deletedCount > 0)
            {
                ClearPendingCrash();
            }

            return deletedCount;
        }

        public static async Task ShowPendingCrashDialogAsync(XamlRoot xamlRoot)
        {
            string? logPath = GetPendingCrashLogPath();
            if (string.IsNullOrWhiteSpace(logPath) || xamlRoot == null)
            {
                return;
            }

            ClearPendingCrash();

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("CrashDialog_Title"),
                Content = ResourceService.Format("CrashDialog_Content", Path.GetFileName(logPath)),
                PrimaryButtonText = ResourceService.GetString("CrashDialog_OpenFolderButton"),
                SecondaryButtonText = ResourceService.GetString("CrashDialog_ExportButton"),
                CloseButtonText = ResourceService.GetString("CrashDialog_CloseButton"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();

            if (!File.Exists(logPath))
            {
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                FilePickerService.OpenFolderInExplorer(EnsureLogDirectoryPath());
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await FilePickerService.ExportFileCopyAsync(logPath, Path.GetFileName(logPath));
            }
        }

        private static string? WriteCrashLog(string source, Exception? exception, IEnumerable<(string Key, string Value)>? extraFields = null)
        {
            try
            {
                string logDirectory = GetLogDirectory();
                Directory.CreateDirectory(logDirectory);

                string logPath = Path.Combine(
                    logDirectory,
                    $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.log");

                using FileStream stream = new(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using StreamWriter writer = new(stream, new UTF8Encoding(false));

                WriteHeader(writer, source, extraFields);
                WriteEnvironment(writer);
                WriteAppState(writer);
                WriteBreadcrumbs(writer);
                WriteException(writer, exception);

                writer.Flush();
                stream.Flush(true);
                return logPath;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetPendingCrashLogPath()
        {
            bool hasPendingCrash = AppSettingsService.GetValue(HasPendingCrashKey, false);
            if (!hasPendingCrash)
            {
                return null;
            }

            string path = AppSettingsService.GetValue(PendingCrashLogPathKey, string.Empty);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ClearPendingCrash();
                return null;
            }

            return path;
        }

        private static void MarkPendingCrash(string? logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            AppSettingsService.SetValue(HasPendingCrashKey, true);
            AppSettingsService.SetValue(PendingCrashLogPathKey, logPath);
        }

        private static void ClearPendingCrash()
        {
            AppSettingsService.SetValue(HasPendingCrashKey, false);
            AppSettingsService.SetValue(PendingCrashLogPathKey, string.Empty);
        }

        private static string GetLogDirectory()
        {
            string logDirectory;
            string legacyLogDirectory;

            try
            {
                string localFolderPath = ApplicationData.Current.LocalFolder.Path;
                logDirectory = Path.Combine(localFolderPath, "Logs");
                legacyLogDirectory = Path.Combine(localFolderPath, "Logs", "Crash");
            }
            catch
            {
                string localAppDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox");

                logDirectory = Path.Combine(localAppDataPath, "Logs");
                legacyLogDirectory = Path.Combine(localAppDataPath, "Logs", "Crash");
            }

            MigrateLegacyCrashLogs(legacyLogDirectory, logDirectory);
            return logDirectory;
        }

        private static void MigrateLegacyCrashLogs(string legacyLogDirectory, string logDirectory)
        {
            if (string.Equals(legacyLogDirectory, logDirectory, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacyLogDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(logDirectory);

                foreach (string legacyLogPath in Directory.EnumerateFiles(legacyLogDirectory, CrashLogSearchPattern, SearchOption.TopDirectoryOnly))
                {
                    string targetLogPath = Path.Combine(logDirectory, Path.GetFileName(legacyLogPath));
                    if (File.Exists(targetLogPath))
                    {
                        continue;
                    }

                    File.Move(legacyLogPath, targetLogPath);
                }

                if (!Directory.EnumerateFileSystemEntries(legacyLogDirectory).Any())
                {
                    Directory.Delete(legacyLogDirectory);
                }
            }
            catch
            {
            }
        }

        private static void WriteHeader(StreamWriter writer, string source, IEnumerable<(string Key, string Value)>? extraFields)
        {
            AssemblyName assemblyName = Assembly.GetExecutingAssembly().GetName();

            writer.WriteLine("LivePhotoBox Crash Log");
            writer.WriteLine($"Timestamp: {DateTimeOffset.Now:O}");
            writer.WriteLine($"Source: {source}");
            writer.WriteLine($"AppVersion: {assemblyName.Version}");

            if (extraFields != null)
            {
                foreach (var (key, value) in extraFields)
                {
                    writer.WriteLine($"{key}: {value}");
                }
            }

            writer.WriteLine();
        }

        private static void WriteEnvironment(StreamWriter writer)
        {
            writer.WriteLine("[Environment]");
            writer.WriteLine($"ProcessId: {Environment.ProcessId}");
            writer.WriteLine($"ProcessPath: {Environment.ProcessPath}");
            writer.WriteLine($"MachineName: {Environment.MachineName}");
            writer.WriteLine($"UserName: {Environment.UserName}");
            writer.WriteLine($"UserDomainName: {Environment.UserDomainName}");
            writer.WriteLine($"CurrentDirectory: {Environment.CurrentDirectory}");
            writer.WriteLine($"SystemDirectory: {Environment.SystemDirectory}");
            writer.WriteLine($"OSVersion: {Environment.OSVersion}");
            writer.WriteLine($"OSDescription: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            writer.WriteLine($"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            writer.WriteLine($"OSArchitecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            writer.WriteLine($"ProcessArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            writer.WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");
            writer.WriteLine($"ProcessorCount: {Environment.ProcessorCount}");
            writer.WriteLine($"SystemPageSize: {FormatByteSize(Environment.SystemPageSize)}");
            writer.WriteLine($"ManagedThreadId: {Thread.CurrentThread.ManagedThreadId}");
            writer.WriteLine($"CurrentCulture: {CultureInfo.CurrentCulture.Name}");
            writer.WriteLine($"CurrentUICulture: {CultureInfo.CurrentUICulture.Name}");
            writer.WriteLine($"TimeZone: {TimeZoneInfo.Local.DisplayName}");
            writer.WriteLine($"SystemUptime: {FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
            writer.WriteLine();

            writer.WriteLine("[Hardware]");
            writer.WriteLine($"ProcessorName: {GetProcessorName()}");
            writer.WriteLine($"ProcessorIdentifier: {GetProcessorIdentifier()}");
            writer.WriteLine($"TotalPhysicalMemory: {FormatNullableByteSize(GetTotalPhysicalMemory())}");
            writer.WriteLine($"AvailablePhysicalMemory: {FormatNullableByteSize(GetAvailablePhysicalMemory())}");

            var systemDriveInfo = GetSystemDriveInfo();
            writer.WriteLine($"SystemDrive: {systemDriveInfo.RootPath}");
            writer.WriteLine($"SystemDriveTotalSize: {FormatNullableByteSize(systemDriveInfo.TotalSize)}");
            writer.WriteLine($"SystemDriveAvailableFreeSpace: {FormatNullableByteSize(systemDriveInfo.AvailableFreeSpace)}");
            writer.WriteLine();

            writer.WriteLine("[RuntimeMemory]");
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            writer.WriteLine($"ManagedHeapTotalMemory: {FormatByteSize(GC.GetTotalMemory(false))}");
            writer.WriteLine($"GCHeapSize: {FormatNullableByteSize(gcMemoryInfo.HeapSizeBytes)}");
            writer.WriteLine($"GCMemoryLoad: {FormatNullableByteSize(gcMemoryInfo.MemoryLoadBytes)}");
            writer.WriteLine($"GCHighMemoryLoadThreshold: {FormatNullableByteSize(gcMemoryInfo.HighMemoryLoadThresholdBytes)}");
            writer.WriteLine($"GCTotalAvailableMemory: {FormatNullableByteSize(gcMemoryInfo.TotalAvailableMemoryBytes)}");
            writer.WriteLine();
        }

        private static string GetProcessorName()
        {
            return GetRegistryValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")
                ?? "(unknown)";
        }

        private static string GetProcessorIdentifier()
        {
            return GetRegistryValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier")
                ?? "(unknown)";
        }

        private static string? GetRegistryValue(string subKeyPath, string valueName)
        {
            try
            {
                using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey(subKeyPath);
                return registryKey?.GetValue(valueName)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static ulong? GetTotalPhysicalMemory()
        {
            try
            {
                MemoryStatusEx memoryStatus = CreateMemoryStatusEx();
                return GlobalMemoryStatusEx(ref memoryStatus)
                    ? memoryStatus.ullTotalPhys
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static ulong? GetAvailablePhysicalMemory()
        {
            try
            {
                MemoryStatusEx memoryStatus = CreateMemoryStatusEx();
                return GlobalMemoryStatusEx(ref memoryStatus)
                    ? memoryStatus.ullAvailPhys
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static MemoryStatusEx CreateMemoryStatusEx()
        {
            return new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
        }

        private static (string RootPath, long? TotalSize, long? AvailableFreeSpace) GetSystemDriveInfo()
        {
            try
            {
                string rootPath = Path.GetPathRoot(Environment.SystemDirectory) ?? "(unknown)";
                if (string.IsNullOrWhiteSpace(rootPath) || rootPath == "(unknown)")
                {
                    return ("(unknown)", null, null);
                }

                DriveInfo driveInfo = new(rootPath);
                if (!driveInfo.IsReady)
                {
                    return (rootPath, null, null);
                }

                return (rootPath, driveInfo.TotalSize, driveInfo.AvailableFreeSpace);
            }
            catch
            {
                return ("(unknown)", null, null);
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        }

        private static string FormatByteSize(long bytes)
        {
            return FormatByteSize((double)bytes);
        }

        private static string FormatNullableByteSize(long bytes)
        {
            return bytes < 0 ? "(unknown)" : FormatByteSize(bytes);
        }

        private static string FormatNullableByteSize(long? bytes)
        {
            return bytes.HasValue ? FormatNullableByteSize(bytes.Value) : "(unknown)";
        }

        private static string FormatNullableByteSize(ulong? bytes)
        {
            return bytes.HasValue ? FormatByteSize((double)bytes.Value) : "(unknown)";
        }

        private static string FormatByteSize(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int unitIndex = 0;
            double size = bytes;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:F2} {units[unitIndex]}";
        }

        private static void WriteAppState(StreamWriter writer)
        {
            writer.WriteLine("[AppState]");

            try
            {
                writer.WriteLine($"MainWindowCreated: {App.MainWindow != null}");

                if (App.MainWindow is MainWindow window)
                {
                    AppViewModel viewModel = window.ViewModel;
                    writer.WriteLine($"CurrentStatusPageTag: {viewModel.CurrentStatusPageTag ?? "(null)"}");
                    writer.WriteLine($"CurrentPageStatus: {viewModel.CurrentPageStatusForLog}");
                    writer.WriteLine($"ComboStatus: {viewModel.ComboStatusForLog}");
                    writer.WriteLine($"SplitStatus: {viewModel.SplitStatusForLog}");
                    writer.WriteLine($"RepairStatus: {viewModel.RepairStatusForLog}");
                    writer.WriteLine($"IsProcessing: {viewModel.IsProcessing}");
                    writer.WriteLine($"IsPaused: {viewModel.IsPaused}");
                    writer.WriteLine($"SelectedModeIndex: {viewModel.SelectedModeIndex}");
                    writer.WriteLine($"InputDirectory: {viewModel.InputDirectory}");
                    writer.WriteLine($"OutputDirectory: {viewModel.OutputDirectory}");
                    writer.WriteLine($"SplitInputDirectory: {viewModel.SplitInputDirectory}");
                    writer.WriteLine($"SplitOutputDirectory: {viewModel.SplitOutputDirectory}");
                    writer.WriteLine($"ComboTaskCount: {viewModel.ComboTasks.Count}");
                    writer.WriteLine($"SplitTaskCount: {viewModel.SplitTasks.Count}");
                    writer.WriteLine($"ComboProgress: {viewModel.ComboProgress}");
                    writer.WriteLine($"SplitProgress: {viewModel.SplitProgress}");
                    writer.WriteLine($"ProgressText: {viewModel.ProgressText}");
                    writer.WriteLine($"SplitProgressText: {viewModel.SplitProgressText}");
                }
            }
            catch (Exception ex)
            {
                writer.WriteLine($"StateCaptureError: {ex}");
            }

            writer.WriteLine();
        }

        private static void WriteBreadcrumbs(StreamWriter writer)
        {
            writer.WriteLine("[Breadcrumbs]");

            lock (SyncRoot)
            {
                if (Breadcrumbs.Count == 0)
                {
                    writer.WriteLine("(empty)");
                }
                else
                {
                    foreach (string breadcrumb in Breadcrumbs)
                    {
                        writer.WriteLine(breadcrumb);
                    }
                }
            }

            writer.WriteLine();
        }

        private static void WriteException(StreamWriter writer, Exception? exception)
        {
            writer.WriteLine("[Exception]");
            writer.WriteLine(exception?.ToString() ?? "(null)");
        }
    }
}

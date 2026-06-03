using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LogSource = LivePhotoBox.Models.LogSource;
using XamlUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 崩溃日志服务 - 对外 API 入口
    /// 负责初始化、异常处理注册、对话框显示
    /// </summary>
    public static class CrashLogService
    {
        private const string HasPendingCrashKey = "HasPendingCrash";
        private const string PendingCrashLogPathKey = "PendingCrashLogPath";
        private static readonly object SyncRoot = new();
        private static bool _initialized;

        #region P/Invoke

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
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

        private delegate int WerRegisterAppLocalDumpDelegate([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string localAppDataRelativePath);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private static bool TryRegisterAppLocalDump(string localAppDataRelativePath)
        {
            IntPtr hModule = LoadLibrary("KernelBase.dll");
            if (hModule == IntPtr.Zero) return false;

            try
            {
                IntPtr proc = GetProcAddress(hModule, "WerRegisterAppLocalDump");
                if (proc == IntPtr.Zero) return false;

                var del = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<WerRegisterAppLocalDumpDelegate>(proc);
                try { _ = del(localAppDataRelativePath); return true; }
                catch { return false; }
            }
            finally { FreeLibrary(hModule); }
        }

        #endregion

        #region Initialization

        public static void Initialize(Application app)
        {
            if (_initialized) return;

            lock (SyncRoot)
            {
                if (_initialized) return;

                AppLogService.Initialize();
                SessionStateManager.RecoverPreviousSessionIfNeeded();
                SessionStateManager.StartNewSession();
                app.UnhandledException += OnApplicationUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
                TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
                TryRegisterAppLocalDump("Logs\\Dumps");
                _initialized = true;
            }

            AppLogService.Info("CrashLogService initialized.", LogSource.System);
        }

        #endregion

        #region Public API - Breadcrumbs

        public static void MarkCleanShutdown() => SessionStateManager.MarkCleanShutdown();

        #endregion

        #region Public API - Crash Logs

        public static string GetLogDirectoryPath() => GetLogDirectory();

        public static string EnsureLogDirectoryPath()
        {
            string dir = GetLogDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string EnsureDumpDirectoryPath()
        {
            string dir = GetDumpDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string? GenerateTestCrashLog() => CrashLogWriter.GenerateTestCrashLog();

        public static int DeleteAllCrashLogs()
        {
            int deleted = CrashLogWriter.DeleteAllCrashLogs();
            if (deleted > 0) ClearPendingCrash();
            return deleted;
        }

        public static int DeleteAllCrashArtifacts()
        {
            return CrashLogWriter.DeleteAllCrashArtifacts();
        }

        public static IReadOnlyList<string> GetCrashLogPaths() => CrashLogWriter.GetCrashLogPaths();

        public static string? GetLatestCrashLogPath() => CrashLogWriter.GetLatestCrashLogPath();

        public static string? GetLatestRecoveredCrashLogPath() => CrashLogWriter.GetLatestRecoveredCrashLogPath();

        public static IReadOnlyList<string> GetCrashDumpPaths() => CrashLogWriter.GetCrashDumpPaths();

        public static string? GetLatestCrashDumpPath() => CrashLogWriter.GetLatestCrashDumpPath();

        #endregion

        #region Private - Exception Handlers

        private static void OnApplicationUnhandledException(object sender, XamlUnhandledExceptionEventArgs e)
        {
            AppLogService.Critical($"Unhandled UI Exception: {e.Exception?.Message}", e.Exception, LogSource.System);
            string? logPath = CrashLogWriter.WriteCrashLog("Microsoft.UI.Xaml.Application.UnhandledException", e.Exception,
                [("Handled", e.Handled.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            MarkPendingCrash(logPath);
        }

        private static void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            AppLogService.Critical($"AppDomain Unhandled Exception: {exception?.Message}", exception, LogSource.System);
            string? logPath = CrashLogWriter.WriteCrashLog("AppDomain.CurrentDomain.UnhandledException", exception,
                [("IsTerminating", e.IsTerminating.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            MarkPendingCrash(logPath);
        }

        private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogService.Error($"Unobserved Task Exception: {e.Exception?.Message}", e.Exception, LogSource.System);
            string? logPath = CrashLogWriter.WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception,
                [("ObservedBeforeSet", e.Observed.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            MarkPendingCrash(logPath);
            e.SetObserved();
        }

        #endregion

        #region Public API - Dialogs

        public static async Task ShowPendingCrashDialogAsync(XamlRoot xamlRoot)
        {
            string? logPath = GetPendingCrashLogPath();
            if (string.IsNullOrWhiteSpace(logPath) || xamlRoot == null) return;

            ClearPendingCrash();
            await ShowCrashDialogAsync(xamlRoot, logPath);
        }

        public static async Task ShowCrashDialogAsync(XamlRoot xamlRoot, string logPath)
        {
            if (xamlRoot == null || string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return;

            Button CreateButton(string resourceKey) => new()
            {
                Content = ResourceService.GetString(resourceKey),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var openFolderBtn = CreateButton("CrashDialog_OpenFolderButton");
            var exportBtn = CreateButton("CrashDialog_ExportButton");
            var reportBtn = CreateButton("CrashDialog_ReportIssueButton");

            openFolderBtn.Click += (_, _) =>
            {
                AppLogService.Info("Open crash log folder requested", LogSource.System);
                FilePickerService.OpenFolderInExplorer(EnsureLogDirectoryPath());
            };

            exportBtn.Click += async (_, _) =>
            {
                if (!File.Exists(logPath)) return;
                AppLogService.Info($"Export crash log: {Path.GetFileName(logPath)}", LogSource.System);
                await FilePickerService.ExportFileCopyAsync(logPath, Path.GetFileName(logPath));
            };

            reportBtn.Click += async (_, _) =>
            {
                AppLogService.Info("Report issue requested", LogSource.System);
                await FeedbackService.OpenIssuePageAsync();
            };

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("CrashDialog_Title"),
                Content = new StackPanel
                {
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = ResourceService.Format("CrashDialog_Content", Path.GetFileName(logPath)),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel { Spacing = 12, Children = { openFolderBtn, exportBtn, reportBtn } }
                    }
                },
                CloseButtonText = ResourceService.GetString("CrashDialog_CloseButton"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();
        }

        #endregion

        #region Private - Pending Crash

        public static string? GetPendingCrashLogPath()
        {
            if (!AppSettingsService.GetValue(HasPendingCrashKey, false)) return null;

            string path = AppSettingsService.GetValue(PendingCrashLogPathKey, string.Empty);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ClearPendingCrash();
                return null;
            }
            return path;
        }

        public static void MarkPendingCrash(string? logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath)) return;
            AppSettingsService.SetValue(HasPendingCrashKey, true);
            AppSettingsService.SetValue(PendingCrashLogPathKey, logPath);
        }

        public static void ClearPendingCrash()
        {
            AppSettingsService.SetValue(HasPendingCrashKey, false);
            AppSettingsService.SetValue(PendingCrashLogPathKey, string.Empty);
        }

        #endregion

        #region Private - Directory Helpers

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

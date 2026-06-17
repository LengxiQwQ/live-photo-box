using LivePhotoBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LogSource = LivePhotoBox.Models.LogSource;
using XamlUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// Crash handler — registers exception handlers and shows crash-report dialogs.
    ///
    /// All crash REPORTING (writing to log) is delegated to <see cref="LogService"/>.
    /// This class only handles:
    /// - Exception handler registration
    /// - WER local dump registration
    /// - Crash dialog UI (ContentDialog)
    /// </summary>
    public static class CrashHandler
    {
        private static bool _initialized;
        private static readonly object _initLock = new();

        #region P/Invoke — WER Local Dump

        private delegate int WerRegisterAppLocalDumpDelegate(
            [MarshalAs(UnmanagedType.LPWStr)] string localAppDataRelativePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private static bool TryRegisterAppLocalDump(string localAppDataRelativePath)
        {
            IntPtr hModule = LoadLibrary("KernelBase.dll");
            if (hModule == IntPtr.Zero) return false;

            try
            {
                IntPtr proc = GetProcAddress(hModule, "WerRegisterAppLocalDump");
                if (proc == IntPtr.Zero) return false;

                var del = Marshal.GetDelegateForFunctionPointer<WerRegisterAppLocalDumpDelegate>(proc);
                try { _ = del(localAppDataRelativePath); return true; }
                catch { return false; }
            }
            finally { FreeLibrary(hModule); }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Registers exception handlers and WER local dump.
        /// Must be called once at application startup, AFTER <see cref="LogService.Initialize"/>.
        /// </summary>
        public static void Initialize(Application app)
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // Register exception handlers
                app.UnhandledException += OnApplicationUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
                TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;

                // Register WER local dump (for native crashes only)
                TryRegisterAppLocalDump("Logs\\Dumps");

                _initialized = true;
            }

            LogService.Info("CrashHandler initialized.", LogSource.System);
        }

        #endregion

        #region Exception Handlers

        private static void OnApplicationUnhandledException(object sender, XamlUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            LogService.Critical($"Unhandled UI Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("Microsoft.UI.Xaml.Application.UnhandledException", ex,
            [
                ("Handled", e.Handled.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            LogService.ForceFlush();
        }

        private static void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogService.Critical($"AppDomain Unhandled Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("AppDomain.CurrentDomain.UnhandledException", ex,
            [
                ("IsTerminating", e.IsTerminating.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            LogService.ForceFlush();
        }

        private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var ex = e.Exception;
            LogService.Error($"Unobserved Task Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("TaskScheduler.UnobservedTaskException", ex,
            [
                ("ObservedBeforeSet", e.Observed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            LogService.ForceFlush();
            e.SetObserved();
        }

        #endregion

        #region Dialogs

        /// <summary>
        /// Shows the crash dialog on startup if the previous session crashed.
        /// Reads the previous log path from <see cref="LogService"/>.
        /// </summary>
        public static async Task ShowPendingCrashDialogAsync(XamlRoot xamlRoot)
        {
            if (xamlRoot == null) return;
            if (!LogService.LastSessionCrashed) return;

            string? logPath = LogService.PreviousLogPath;
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return;

            await ShowCrashDialogAsync(xamlRoot, logPath);
        }

        /// <summary>
        /// Shows the crash report dialog for a specific log file.
        /// </summary>
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
                LogService.Info("Open crash log folder requested", LogSource.System);
                FilePickerService.OpenFolderInExplorer(LogService.LogDirectory);
            };

            exportBtn.Click += async (_, _) =>
            {
                if (!File.Exists(logPath)) return;
                LogService.Info($"Export crash log: {Path.GetFileName(logPath)}", LogSource.System);
                await FilePickerService.ExportFileCopyAsync(logPath, Path.GetFileName(logPath));
            };

            reportBtn.Click += async (_, _) =>
            {
                LogService.Info("Report issue requested", LogSource.System);
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
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            await dialog.ShowAsync();
        }

        #endregion
    }
}

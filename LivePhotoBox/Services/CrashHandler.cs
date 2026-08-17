using LivePhotoBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LogSource = LivePhotoBox.Models.LogSource;
using XamlUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace LivePhotoBox.Services
{
    /*
     * CrashHandler.cs
     *
     * 全局崩溃处理服务。注册各类未处理异常处理器，生成转储文件（.dmp），
     * 并弹出崩溃报告对话框。
     *
     *   - 异常处理器注册（App / AppDomain / TaskScheduler）
     *   - WER 本地转储（原生崩溃 → Windows Error Reporting 生成 .dmp）
     *   - MiniDumpWriteDump（托管崩溃 → dbghelp.dll 生成 .dmp）
     *   - 崩溃对话框 UI（ContentDialog）
     *
     * 崩溃报告（写日志）委托给 LogService。
     */
    public static class CrashHandler
    {
        private static bool _initialized;
        private static readonly object _initLock = new();

        #region P/Invoke — MiniDumpWriteDump (dbghelp.dll)

        // MINIDUMP_TYPE flags — 包含最常用的诊断信息。
        // MiniDumpNormal: 线程栈、模块列表、系统信息
        // MiniDumpWithDataSegs: 全局变量/静态变量的数据段
        // MiniDumpWithFullMemory: 完整进程内存（体积大，仅在整进程崩溃时用）
        // MiniDumpWithHandleData: 句柄信息
        // MiniDumpWithThreadInfo: 扩展线程信息
        // MiniDumpWithUnloadedModules: 已卸载模块（对诊断加载/卸载崩溃有用）
        [Flags]
        private enum MiniDumpType : uint
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000,
            MiniDumpWithModuleHeaders = 0x00080000,
            MiniDumpFilterTriage = 0x00100000,
            MiniDumpWithAvxXStateContext = 0x00200000,
            MiniDumpWithIptTrace = 0x00400000,
        }

        private struct MiniDumpExceptionInformation
        {
            public uint ThreadId;
            public IntPtr ExceptionPointers;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ClientPointers;
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            SafeHandle hFile,
            MiniDumpType dumpType,
            ref MiniDumpExceptionInformation exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        // 无异常上下文的简化重载（用于非异常场景如 TaskScheduler 崩溃）
        [DllImport("dbghelp.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            uint processId,
            SafeHandle hFile,
            MiniDumpType dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        /// <summary>
        /// 写入 MiniDump 文件到 Dumps 目录。捕获完整调用栈、线程、模块和句柄信息。
        /// 使用 MiniDumpWithFullMemory 会生成巨大的文件（数百 MB），因此折叠内存相关标志。
        /// </summary>
        /// <param name="source">崩溃来源标识（如 "App.UnhandledException"）</param>
        /// <param name="exceptionPointers">
        /// 异常上下文指针（来自 Exception 的 HResult/内部指针）。
        /// 为 IntPtr.Zero 时使用无异常上下文的简化 dump。
        /// </param>
        /// <returns>生成的 .dmp 文件完整路径，失败返回 null</returns>
        private static string? WriteCrashDump(string source, IntPtr exceptionPointers)
        {
            try
            {
                string dumpDir = Path.Combine(LogService.LogDirectory, "Dumps");
                Directory.CreateDirectory(dumpDir);

                string fileName = $"crash-{source}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp";
                // 清理 source 中不能用于文件名的字符
                foreach (char c in Path.GetInvalidFileNameChars())
                    fileName = fileName.Replace(c, '-');
                string dumpPath = Path.Combine(dumpDir, fileName);

                using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var process = Process.GetCurrentProcess();

                // 基础标志：线程栈 + 数据段 + 句柄 + 线程信息 + 模块 + 已卸载模块
                const MiniDumpType flags =
                    MiniDumpType.MiniDumpNormal
                    | MiniDumpType.MiniDumpWithDataSegs
                    | MiniDumpType.MiniDumpWithHandleData
                    | MiniDumpType.MiniDumpWithThreadInfo
                    | MiniDumpType.MiniDumpWithModuleHeaders
                    | MiniDumpType.MiniDumpWithUnloadedModules
                    | MiniDumpType.MiniDumpWithIndirectlyReferencedMemory
                    | MiniDumpType.MiniDumpIgnoreInaccessibleMemory;

                bool result;
                if (exceptionPointers != IntPtr.Zero)
                {
                    var expInfo = new MiniDumpExceptionInformation
                    {
                        ThreadId = GetCurrentThreadId(),
                        ExceptionPointers = exceptionPointers,
                        ClientPointers = false
                    };
                    result = MiniDumpWriteDump(
                        process.Handle, (uint)process.Id, fs.SafeFileHandle,
                        flags, ref expInfo,
                        IntPtr.Zero, IntPtr.Zero);
                }
                else
                {
                    result = MiniDumpWriteDump(
                        process.Handle, (uint)process.Id, fs.SafeFileHandle,
                        flags,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }

                if (result)
                {
                    long size = new FileInfo(dumpPath).Length;
                    LogService.Info(
                        $"Crash dump written: {fileName} ({size / 1024} KB)", LogSource.System);
                    return dumpPath;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    LogService.Error(
                        $"MiniDumpWriteDump failed: win32 error {err}", null, LogSource.System);
                    // 写入失败 → 清理空文件 / 部分文件
                    try { File.Delete(dumpPath); } catch { }
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"WriteCrashDump failed: {ex.Message}", ex, LogSource.System);
                return null;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        #endregion

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

        // 尝试注册 WER (Windows Error Reporting) 本地转储。
        // 通过动态加载 KernelBase.dll 中的 WerRegisterAppLocalDump 函数指针实现，
        // 避免对旧版 Windows 的直接依赖。注册后应用发生原生崩溃时会在指定目录生成 .dmp 文件。
        // localAppDataRelativePath: 相对 LocalAppData 的转储目录路径
        // 返回: 注册成功返回 true
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

        // Registers exception handlers and WER local dump.
        // Must be called once at application startup, AFTER <see cref="LogService.Initialize"/>.
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

                // Register WER local dump (for native crashes only).
                // WerRegisterAppLocalDump 接受相对 %LOCALAPPDATA% 的路径，
                // 需要根据 LogDirectory 计算出正确的相对路径，确保 .dmp 文件
                // 写入与 LogService.CleanupOldDumpFiles() 清理的目录一致。
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dumpDir = Path.Combine(LogService.LogDirectory, "Dumps");
                string werDumpRelativePath = dumpDir.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase)
                    ? dumpDir.Substring(localAppData.Length).TrimStart(Path.DirectorySeparatorChar)
                    : "Logs\\Dumps"; // fallback — shouldn't happen on normal installs
                TryRegisterAppLocalDump(werDumpRelativePath);

                _initialized = true;
            }

            LogService.Info("CrashHandler initialized.", LogSource.System);
        }

        #endregion

        #region Exception Handlers

        // 崩溃退出码。操作系统用非零值判断进程是否异常退出。
        // 使用 0xE0000000 (E = Error) 避免与 OS 原生崩溃码（如 0xC0000005）混淆。
        private const int CrashExitCode = unchecked((int)0xE0000001);

        private static void OnApplicationUnhandledException(object sender, XamlUnhandledExceptionEventArgs e)
        {
            var ex = e.Exception;
            LogService.Critical($"Unhandled UI Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("Microsoft.UI.Xaml.Application.UnhandledException", ex,
            [
                ("Handled", e.Handled.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            // MiniDump：捕获完整线程栈、模块、句柄
            WriteCrashDump("WinUI-UnhandledException", IntPtr.Zero);
            // WER dump（原生层）+ 日志刷盘
            LogService.ForceFlush();

            // 不设置 e.Handled = true，让 WinUI 正常终止进程。
            // 确保退出码非零。
            Environment.ExitCode = CrashExitCode;
        }

        private static void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogService.Critical($"AppDomain Unhandled Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("AppDomain.CurrentDomain.UnhandledException", ex,
            [
                ("IsTerminating", e.IsTerminating.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            // MiniDump：捕获完整线程栈、模块、句柄
            WriteCrashDump("AppDomain-UnhandledException", IntPtr.Zero);
            LogService.ForceFlush();

            // AppDomain 未处理异常后 CLR 必然终止进程，确保退出码非零。
            Environment.ExitCode = CrashExitCode;
        }

        private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var ex = e.Exception;
            LogService.Error($"Unobserved Task Exception: {ex?.Message}", ex, LogSource.System);

            LogService.WriteCrashSection("TaskScheduler.UnobservedTaskException", ex,
            [
                ("ObservedBeforeSet", e.Observed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);

            // MiniDump：火-and-forget Task 崩溃，捕获线程栈用于定位丢失的 await
            WriteCrashDump("TaskScheduler-UnobservedTask", IntPtr.Zero);
            LogService.ForceFlush();

            // 不要调用 e.SetObserved()。
            // .NET 6+ 默认行为：未观察的任务异常会终止进程。SetObserved() 会阻止这个行为，
            // 导致异常被静默吞掉 → 进程看起来"正常退出"（ExitCode=0）→ 用户无法感知崩溃。
            // 不调用 SetObserved() 让 CLR 走默认的进程终止路径，产生非零退出码。
            Environment.ExitCode = CrashExitCode;
        }

        #endregion

        #region Dialogs

        // Shows the crash dialog on startup if the previous session crashed.
        // Reads the previous log path from <see cref="LogService"/>.
        public static async Task ShowPendingCrashDialogAsync(XamlRoot xamlRoot)
        {
            if (xamlRoot == null) return;
            if (!LogService.LastSessionCrashed) return;

            string? logPath = LogService.PreviousLogPath;
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return;

            await ShowCrashDialogAsync(xamlRoot, logPath);
        }

        // Shows the crash report dialog for a specific log file.
        // If logPath is null or file doesn't exist, shows "Not detected" in place of the file name.
        public static async Task ShowCrashDialogAsync(XamlRoot xamlRoot, string? logPath)
        {
            if (xamlRoot == null) return;

            bool hasFile = !string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath);

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

            exportBtn.IsEnabled = hasFile;
            if (hasFile)
            {
                string capturedPath = logPath!;
                exportBtn.Click += async (_, _) =>
                {
                    LogService.Info($"Export crash log: {Path.GetFileName(capturedPath)}", LogSource.System);
                    await FilePickerService.ExportFileCopyAsync(capturedPath, Path.GetFileName(capturedPath));
                };
            }

            reportBtn.Click += async (_, _) =>
            {
                LogService.Info("Report issue requested", LogSource.System);
                await FeedbackService.OpenIssuePageAsync();
            };

            var openLogLink = new HyperlinkButton
            {
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = null,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14
            };

            if (hasFile)
            {
                string capturedPath = logPath!;
                var logFileName = Path.GetFileName(capturedPath);
                openLogLink.Content = logFileName;
                openLogLink.Click += (_, _) =>
                {
                    LogService.Info($"Open crash log file: {logFileName}", LogSource.System);
                    _ = FilePickerService.OpenFileAsync(capturedPath);
                };
            }
            else
            {
                openLogLink.Content = ResourceService.GetString("SettingsPage_CrashNoCrashValue");
                openLogLink.IsEnabled = false;
            }

            var crashContent = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = ResourceService.GetString("CrashDialog_Content"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = ResourceService.GetString("CrashDialog_LogFileLabel"),
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            openLogLink
                        }
                    },
                    new StackPanel { Spacing = 12, Children = { openFolderBtn, exportBtn, reportBtn } }
                }
            };

            await DialogService.ShowSingleAsync(
                xamlRoot,
                ResourceService.GetString("CrashDialog_Title"),
                crashContent,
                ResourceService.GetString("CrashDialog_CloseButton"));
        }

        #endregion
    }
}

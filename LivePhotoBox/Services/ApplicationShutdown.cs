using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

/// <summary>One close operation; its deadline never depends on the XAML dispatcher.</summary>
internal static class ApplicationShutdown
{
    internal static WorkLifetime Work { get; } = new();
    internal static bool IsClosing => Work.IsStopping;
    private static ShutdownDeadline? _deadline;
    private static int _started;
    private static Action? _afterDrain;
    private static readonly System.Collections.Generic.List<Func<Task>> AfterDrain = [];
    internal static void RegisterAfterDrain(Func<Task> callback) => AfterDrain.Add(callback);
    internal static bool ReadyToClose { get; private set; }
    internal static bool HadCleanupFailure { get; private set; }
    private static string _stage = "running";
    private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    internal static void Trace(string stage)
    {
        Volatile.Write(ref _stage, stage);
        LogService.Info($"Shutdown: {stage}; Tick={Stopwatch.GetTimestamp()}; Thread={Environment.CurrentManagedThreadId}; UI={_dispatcher?.HasThreadAccess}; Active=[{string.Join(",", Work.ActiveOperations)}]; Debugger={Debugger.IsAttached}");
        LogService.ForceFlush();
    }
    internal static void RecordCleanupFailure(Exception error)
    {
        HadCleanupFailure = true;
        LogService.Error("Shutdown participant failed; continuing other cleanup.", error);
    }

    internal static void Request(Action? afterDrain = null)
    {
        _dispatcher ??= App.MainWindow?.DispatcherQueue;
        if (Interlocked.Exchange(ref _started, 1) != 0) { Trace("request.duplicate"); return; }
        Trace("request");
        _afterDrain = afterDrain;
        // Begin the deadline before cancellation callbacks or UI cleanup.
        _deadline = new ShutdownDeadline(TimeSpan.FromSeconds(10), WriteTimeoutDiagnostic);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _deadline?.ProcessExiting();
        Task cancellation = Work.StopAsync();
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
                if (module.ModuleName.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Contains("Microsoft.ui.xaml", StringComparison.OrdinalIgnoreCase))
                    LogService.Info($"Shutdown module: {module.ModuleName}; Version={module.FileVersionInfo.FileVersion}; Path={module.FileName}");
        }
        catch (Exception error) { LogService.Warn($"Shutdown module inventory unavailable: {error.Message}"); }
        // Closing has no deferral. Return from the cancelled native close
        // before hiding/closing again; otherwise WinUI can restore visibility
        // while finishing its original close dispatch.
        if (App.MainWindow is MainWindow main)
        {
            bool queued = main.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () => { Trace("stop.dispatch.begin"); _ = StopAsync(cancellation); });
            Trace($"stop.dispatch.accepted={queued}");
            if (!queued) RecordCleanupFailure(new InvalidOperationException("Shutdown dispatcher rejected cleanup; recovery retained until deadline."));
        }
        else _ = StopAsync(cancellation);
    }

    private static async Task StopAsync(Task cancellation)
    {
        try
        {
            Trace("hide.begin");
            if (App.MainWindow is MainWindow window) window.HideForShutdown();
            Trace("hide.end");
            AppViewModelStop();
            Trace("cancel.begin");
            try { await cancellation; }
            catch (Exception error) { HadCleanupFailure = true; LogService.Error("Shutdown cancellation callback failed.", error); }
            Trace("cancel.end.drain.begin");
            await Work.WaitForIdleAsync();
            Trace("drain.end.thumbnails.begin");
            await ThumbnailScheduler.StopAsync();
            Trace("thumbnails.end.vm.begin");
            if (App.MainWindow is MainWindow main)
            {
                main.ViewModel.Cleanup();
                Trace("vm.end.window.resources.begin");
                main.ReleaseWindowResourcesForShutdown();
                Trace("window.resources.end");
            }
            foreach (var callback in AfterDrain) await callback();
            _afterDrain?.Invoke();
            Trace("afterDrain.end");
            if (App.MainWindow is MainWindow finalWindow)
            {
                await QueuedUiOperation.RunAsync(action =>
                {
                    bool queued = finalWindow.DispatcherQueue.TryEnqueue(() => action());
                    Trace($"close.dispatch.accepted={queued}");
                    return queued;
                }, () =>
                {
                    Trace("close.dispatch.begin");
                    ReadyToClose = true;
                    finalWindow.Close();
                    Trace("close.returned");
                }).ConfigureAwait(false);
            }
            else Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception error)
        {
            ReadyToClose = false;
            HadCleanupFailure = true;
            LogService.Error($"Shutdown failed; recovery retained: {error}", error);
            // Do not abandon a live writer. The deadline owns forced termination.
        }
    }

    private static void AppViewModelStop()
    {
        if (App.MainWindow is not MainWindow window) return;
        window.ViewModel.RequestStop();
        DialogService.CancelForShutdown();
    }

    private static void WriteTimeoutDiagnostic()
    {
        // No UI, async flush, or log-service lock may prevent the deadline.
        try
        {
            LogService.RecordShutdownTimeout();
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoBox", "Logs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "shutdown-timeout.txt"),
                $"Forced shutdown after 10 seconds. UTC={DateTimeOffset.UtcNow:O}; Tick={Stopwatch.GetTimestamp()}; Stage={Volatile.Read(ref _stage)}; Active=[{string.Join(",", Work.ActiveOperations)}]; Debugger={Debugger.IsAttached}\nDebugger pauses may consume the deadline. ExitCode=1460. Recovery records retained.\n");
        }
        catch { /* Termination must not depend on writable diagnostics. */ }
    }

    internal static void OnWindowClosed()
    {
        Trace("closed");
        if (ReadyToClose && !HadCleanupFailure) LogService.MarkCleanShutdown();
    }

}

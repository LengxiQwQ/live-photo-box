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
    internal static void RecordCleanupFailure(Exception error)
    {
        HadCleanupFailure = true;
        LogService.Error("Shutdown participant failed; continuing other cleanup.", error);
    }

    internal static void Request(Action? afterDrain = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        LogService.Info($"Shutdown requested. Monotonic={System.Diagnostics.Stopwatch.GetTimestamp()}");
        _afterDrain = afterDrain;
        // Begin the deadline before cancellation callbacks or UI cleanup.
        _deadline = new ShutdownDeadline(TimeSpan.FromSeconds(10), WriteTimeoutDiagnostic);
        Task cancellation = Work.StopAsync();
        // Closing has no deferral. Return from the cancelled native close
        // before hiding/closing again; otherwise WinUI can restore visibility
        // while finishing its original close dispatch.
        if (App.MainWindow is MainWindow main)
            main.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () => _ = StopAsync(cancellation));
        else _ = StopAsync(cancellation);
    }

    private static async Task StopAsync(Task cancellation)
    {
        try
        {
            if (App.MainWindow is MainWindow window) window.HideForShutdown();
            LogService.Info($"Shutdown window hidden. Monotonic={System.Diagnostics.Stopwatch.GetTimestamp()}");
            AppViewModelStop();
            try { await cancellation; }
            catch (Exception error) { HadCleanupFailure = true; LogService.Error("Shutdown cancellation callback failed.", error); }
            await Work.WaitForIdleAsync();
            await ThumbnailScheduler.StopAsync();
            if (App.MainWindow is MainWindow main)
            {
                main.ViewModel.Cleanup();
                main.ReleaseWindowResourcesForShutdown();
            }
            foreach (var callback in AfterDrain) await callback();
            _afterDrain?.Invoke();
            ReadyToClose = true;
            if (App.MainWindow is MainWindow finalWindow)
                finalWindow.DispatcherQueue.TryEnqueue(() => finalWindow.Close());
            else Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception error)
        {
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
                $"Forced shutdown after 10 seconds. UTC={DateTimeOffset.UtcNow:O}\nRecovery records retained.\n");
        }
        catch { /* Termination must not depend on writable diagnostics. */ }
    }

    internal static void OnWindowClosed()
    {
        if (ReadyToClose && !HadCleanupFailure) LogService.MarkCleanShutdown();
    }

}

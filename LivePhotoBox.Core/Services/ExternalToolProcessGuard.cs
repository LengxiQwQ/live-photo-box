using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 外部工具进程的统一故障边界：限制单次执行时间、精准终止进程树，
    /// 并将同一操作的总执行次数限制为两次，避免故障文件触发无限重试。
    /// </summary>
    public static class ExternalToolProcessGuard
    {
        public const int MaxAttempts = 2;

        public sealed record RunResult(
            int ExitCode,
            string StandardOutput,
            string StandardError,
            int Attempts,
            bool TimedOut)
        {
            public bool IsSuccess => !TimedOut && ExitCode == 0;
        }

        /// <summary>
        /// 启动并监管一个短时外部工具操作。超时、启动失败或非零退出时最多再执行一次。
        /// </summary>
        public static async Task<RunResult> RunAsync(
            Func<ProcessStartInfo> startInfoFactory,
            TimeSpan timeout,
            string operation,
            CancellationToken cancellationToken = default,
            Action<int>? prepareAttempt = null)
        {
            ArgumentNullException.ThrowIfNull(startInfoFactory);
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            RunResult? lastResult = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepareAttempt?.Invoke(attempt);

                using var process = new Process { StartInfo = startInfoFactory() };
                try
                {
                    process.Start();

                    Task<string> stdoutTask = process.StartInfo.RedirectStandardOutput
                        ? process.StandardOutput.ReadToEndAsync(cancellationToken)
                        : Task.FromResult(string.Empty);
                    Task<string> stderrTask = process.StartInfo.RedirectStandardError
                        ? process.StandardError.ReadToEndAsync(cancellationToken)
                        : Task.FromResult(string.Empty);

                    using var timeoutCts = new CancellationTokenSource(timeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeoutCts.Token);

                    try
                    {
                        await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await KillProcessTreeAsync(process).ConfigureAwait(false);
                        string timedOutStdout = await ObserveOutputAsync(stdoutTask).ConfigureAwait(false);
                        string timedOutStderr = await ObserveOutputAsync(stderrTask).ConfigureAwait(false);
                        lastResult = new RunResult(-1, timedOutStdout, timedOutStderr, attempt, TimedOut: true);

                        LogService.Warn(
                            $"External tool timed out: {operation} (attempt {attempt}/{MaxAttempts}, timeout={timeout.TotalSeconds:F0}s)",
                            source: LogSource.System);
                        if (attempt < MaxAttempts)
                            continue;
                        return lastResult;
                    }
                    catch (OperationCanceledException)
                    {
                        await KillProcessTreeAsync(process).ConfigureAwait(false);
                        throw;
                    }

                    string stdout = await ObserveOutputAsync(stdoutTask).ConfigureAwait(false);
                    string stderr = await ObserveOutputAsync(stderrTask).ConfigureAwait(false);
                    lastResult = new RunResult(process.ExitCode, stdout, stderr, attempt, TimedOut: false);
                    if (lastResult.IsSuccess || attempt == MaxAttempts)
                        return lastResult;

                    LogService.Warn(
                        $"External tool failed: {operation} (attempt {attempt}/{MaxAttempts}, exit={process.ExitCode}); retrying once",
                        source: LogSource.System);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    await KillProcessTreeAsync(process).ConfigureAwait(false);
                    LogService.Warn(
                        $"External tool launch failed: {operation} (attempt {attempt}/{MaxAttempts}): {ex.Message}",
                        ex.ToString(),
                        LogSource.System);
                    if (attempt == MaxAttempts)
                        break;
                }
            }

            if (lastResult != null)
                return lastResult;

            throw new InvalidOperationException(
                $"External tool failed after {MaxAttempts} attempts: {operation}", lastException);
        }

        /// <summary>终止指定外部工具及其子进程，并短暂等待句柄真正退出。</summary>
        public static async Task KillProcessTreeAsync(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }

            try
            {
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task<string> ObserveOutputAsync(Task<string> outputTask)
        {
            try { return await outputTask.ConfigureAwait(false); }
            catch { return string.Empty; }
        }
    }
}

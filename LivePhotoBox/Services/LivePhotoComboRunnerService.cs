using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoComboRunOptions
    {
        public required string OutputDirectory { get; init; }
        public required int SelectedModeIndex { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    }

    public static class LivePhotoComboRunnerService
    {
        public static async Task RunAsync(
            IReadOnlyCollection<ComboTask> tasks,
            LivePhotoComboRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<ComboTask>? onTaskStarted,
            Action<ComboTask, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);

            int completedCount = 0;
            DateTimeOffset nextAllowedBatchStartTime = DateTimeOffset.MinValue;
            int batchSize = Math.Max(1, options.MaxDegreeOfParallelism);

            foreach (var batch in tasks.Where(task => task.Status != ProcessStatus.Success).Chunk(batchSize))
            {
                await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var now = DateTimeOffset.UtcNow;
                var delay = nextAllowedBatchStartTime - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                nextAllowedBatchStartTime = DateTimeOffset.UtcNow + options.TaskStartInterval;

                var runningTasks = batch.Select(async task =>
                {
                    await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    onTaskStarted?.Invoke(task);

                    var result = await ProcessSinglePairAsync(
                        task.ImagePath, task.VideoPath, task.BaseName, options, pauseEvent, cancellationToken)
                        .ConfigureAwait(false);
                    int currentCompleted = Interlocked.Increment(ref completedCount);
                    onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                });

                await Task.WhenAll(runningTasks).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Async pause-wait that does NOT block a thread-pool thread.
        /// The paused worker is represented as an uncompleted Task rather than
        /// a parked OS thread, so cancellation and Set() both propagate cleanly.
        /// </summary>
        private static async Task WaitPauseAsync(ManualResetEventSlim pauseEvent, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = token.Register(() => tcs.TrySetCanceled(token));
            try
            {
                var waitTask = Task.Run(() => { pauseEvent.Wait(); tcs.TrySetResult(true); }, token);
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                reg.Dispose();
            }
        }

        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            LivePhotoComboRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken token)
        {
            string workingImagePath = imagePath;
            try
            {
                token.ThrowIfCancellationRequested();

                if (HeicConverterService.IsHeicFile(imagePath))
                {
                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, options.OutputDirectory, token);
                    // HEIC 转换耗时较长，完成后检查用户是否点了暂停
                    await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }

                string outputName = LivePhotoComboService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = Path.Combine(options.OutputDirectory, outputName);

                // 合成前再检查一次暂停，提供更即时的暂停响应
                await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await LivePhotoComboService.WriteLivePhotoAsync(workingImagePath, videoPath, finalOutputPath, options.SelectedModeIndex, token);

                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Combo($"Combo task failed for {baseName}: {ex.Message}", LogLevel.Error, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                if (workingImagePath != imagePath && File.Exists(workingImagePath))
                {
                    try { File.Delete(workingImagePath); }
                    catch { }
                }
            }
        }
    }
}

using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoMergeRunOptions
    {
        public required string OutputDirectory { get; init; }
        public required int SelectedModeIndex { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    }

    public static class LivePhotoMergeRunnerService
    {
        public static async Task RunAsync(
            IReadOnlyCollection<MergeTask> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<MergeTask>? onTaskStarted,
            Action<MergeTask, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            string tempDir = Path.Combine(options.OutputDirectory, "Temp");
            Directory.CreateDirectory(tempDir);

            try
            {
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
                            task.ImagePath, task.VideoPath, task.BaseName, options, tempDir,
                            pauseEvent, cancellationToken)
                            .ConfigureAwait(false);
                        int currentCompleted = Interlocked.Increment(ref completedCount);
                        onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                    });

                    await Task.WhenAll(runningTasks).ConfigureAwait(false);
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { LogService.Merge($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }
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
            LivePhotoMergeRunOptions options,
            string tempDir,
            ManualResetEventSlim pauseEvent,
            CancellationToken token)
        {
            var protocol = LivePhotoProtocol.FromIndex(options.SelectedModeIndex);
            string workingImagePath = imagePath;
            string workingVideoPath = videoPath;
            var tempFiles = new List<string>();
            try
            {
                token.ThrowIfCancellationRequested();

                if (HeicConverterService.IsHeicFile(imagePath))
                {
                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, tempDir, token);
                    tempFiles.Add(workingImagePath);
                    await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }

                bool forceMp4 = ComputeForceMp4(options.SelectedModeIndex);
                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(videoPath, tempDir, token, forceMp4);
                if (vt) tempFiles.Add(workingVideoPath);

                string prepared = await protocol.PrepareImageAsync(workingImagePath, tempDir, token);
                if (prepared != workingImagePath)
                {
                    workingImagePath = prepared;
                    tempFiles.Add(workingImagePath);
                }

                string outputName = LivePhotoMergeService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = Path.Combine(options.OutputDirectory, outputName);

                await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await LivePhotoMergeService.WriteLivePhotoAsync(workingImagePath, workingVideoPath, finalOutputPath, options.SelectedModeIndex, token);

                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Merge($"Merge task failed for {baseName}: {ex.Message}", LogLevel.Error, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                foreach (var f in tempFiles)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        /// <summary>
        /// Determine whether to force MP4 conversion.
        /// Google protocols (V1/V2) respect the user setting; OPPO always forces MP4.
        /// </summary>
        private static bool ComputeForceMp4(int selectedModeIndex)
        {
            // OPPO protocol (Id=2) always needs MP4
            if (selectedModeIndex == 2) return true;
            // Google V1 (Id=0) / V2 (Id=1) respect user's toggle (default false / off)
            return AppSettingsService.GetValue("IsGoogleProtocolForceMp4", false);
        }
    }
}

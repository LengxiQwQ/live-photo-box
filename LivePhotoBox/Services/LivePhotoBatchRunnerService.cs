using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoBatchRunOptions
    {
        public required string OutputDirectory { get; init; }
        public required int SelectedModeIndex { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    }

    public static class LivePhotoBatchRunnerService
    {
        public static async Task RunAsync(
            IReadOnlyCollection<MergeTask> tasks,
            LivePhotoBatchRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<MergeTask>? onTaskStarted,
            Action<MergeTask, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);

            int completedCount = 0;
            DateTimeOffset nextAllowedBatchStartTime = DateTimeOffset.MinValue;
            int batchSize = Math.Max(1, options.MaxDegreeOfParallelism);

            foreach (var batch in tasks.Where(task => task.Status != ProcessStatus.Success).Chunk(batchSize))
            {
                pauseEvent.Wait(cancellationToken);
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
                    pauseEvent.Wait(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    onTaskStarted?.Invoke(task);

                    var result = await ProcessSinglePairAsync(task.ImagePath, task.VideoPath, task.BaseName, options, cancellationToken).ConfigureAwait(false);
                    int currentCompleted = Interlocked.Increment(ref completedCount);
                    onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                });

                await Task.WhenAll(runningTasks).ConfigureAwait(false);
            }
        }

        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            LivePhotoBatchRunOptions options,
            CancellationToken token)
        {
            string workingImagePath = imagePath;
            try
            {
                token.ThrowIfCancellationRequested();

                if (HeicConverterService.IsHeicFile(imagePath))
                {
                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, options.OutputDirectory, token);
                }

                string outputName = LivePhotoCompositionService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = Path.Combine(options.OutputDirectory, outputName);

                await LivePhotoCompositionService.WriteLivePhotoAsync(workingImagePath, videoPath, finalOutputPath, options.SelectedModeIndex, token);

                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                if (workingImagePath != imagePath && File.Exists(workingImagePath))
                {
                    try
                    {
                        File.Delete(workingImagePath);
                    }
                    catch { }
                }
            }
        }
    }
}

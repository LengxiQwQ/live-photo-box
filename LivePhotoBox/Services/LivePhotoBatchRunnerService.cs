using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoBatchRunOptions
    {
        public required string OutputDirectory { get; init; }
        public required int SelectedModeIndex { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 20);
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
            AppLogService.Combo($"Batch processing started. TaskCount={tasks.Count}, OutputDir={options.OutputDirectory}, Mode={options.SelectedModeIndex}");
            Directory.CreateDirectory(options.OutputDirectory);

            int completedCount = 0;
            int successCount = 0;
            int failCount = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken
            };

            try
            {
                await Parallel.ForEachAsync(tasks, parallelOptions, async (task, token) =>
                {
                    if (task.Status == ProcessStatus.Success)
                    {
                        return;
                    }

                    pauseEvent.Wait(token);
                    token.ThrowIfCancellationRequested();

                    onTaskStarted?.Invoke(task);

                    var result = await ProcessSinglePairAsync(task.ImagePath, task.VideoPath, task.BaseName, options, token);
                    int currentCompleted = Interlocked.Increment(ref completedCount);
                    
                    if (result.IsSuccess)
                        Interlocked.Increment(ref successCount);
                    else
                        Interlocked.Increment(ref failCount);
                    
                    onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                });
                
                AppLogService.Combo($"Batch processing completed. Total={completedCount}, Success={successCount}, Failed={failCount}");
            }
            catch (OperationCanceledException)
            {
                AppLogService.Combo($"Batch processing cancelled. Completed={completedCount}, Success={successCount}, Failed={failCount}", LogLevel.Info);
                throw;
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"Batch processing error: {ex.Message}", LogLevel.Error, ex);
                throw;
            }
        }

        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            LivePhotoBatchRunOptions options,
            CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                string outputName = LivePhotoCompositionService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = Path.Combine(options.OutputDirectory, outputName);

                await LivePhotoCompositionService.WriteLivePhotoAsync(imagePath, videoPath, finalOutputPath, options.SelectedModeIndex, token);

                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"Failed to process pair {baseName}: {ex.Message}", LogLevel.Warning, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
        }
    }
}

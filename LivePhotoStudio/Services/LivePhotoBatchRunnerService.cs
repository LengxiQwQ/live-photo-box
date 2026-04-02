using LivePhotoStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoStudio.Services
{
    public sealed class LivePhotoBatchRunOptions
    {
        public required string OutputDirectory { get; init; }
        public required int SelectedModeIndex { get; init; }
        public required bool KeepOriginal { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 20);
    }

    public static class LivePhotoBatchRunnerService
    {
        public static async Task RunAsync(
            IReadOnlyCollection<LivePhotoMergeTask> tasks,
            LivePhotoBatchRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<LivePhotoMergeTask>? onTaskStarted,
            Action<LivePhotoMergeTask, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);

            int completedCount = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken
            };

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
                onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
            });
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

                string resultStatus = ResourceService.GetString("Task_Success");
                if (!options.KeepOriginal)
                {
                    try
                    {
                        var imageFile = await StorageFile.GetFileFromPathAsync(imagePath);
                        await imageFile.DeleteAsync(StorageDeleteOption.Default);

                        var videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
                        await videoFile.DeleteAsync(StorageDeleteOption.Default);

                        resultStatus += ResourceService.GetString("Task_Recycled");
                    }
                    catch (Exception ex)
                    {
                        resultStatus += ResourceService.Format("Task_CleanFail", ex.Message);
                    }
                }

                return (true, resultStatus);
            }
            catch (Exception ex)
            {
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
        }
    }
}

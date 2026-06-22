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
            string tempDir = Path.Combine(options.OutputDirectory, "Temp");
            Directory.CreateDirectory(tempDir);

            try
            {
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

                        var result = await ProcessSinglePairAsync(task.ImagePath, task.VideoPath, task.BaseName, options, tempDir, cancellationToken).ConfigureAwait(false);
                        int currentCompleted = Interlocked.Increment(ref completedCount);
                        onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                    });

                    await Task.WhenAll(runningTasks).ConfigureAwait(false);
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { LogService.Combo($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }
            }
        }

        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            LivePhotoBatchRunOptions options,
            string tempDir,
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
                }

                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(videoPath, tempDir, token);
                if (vt) tempFiles.Add(workingVideoPath);

                string prepared = await protocol.PrepareImageAsync(workingImagePath, tempDir, token);
                if (prepared != workingImagePath)
                {
                    workingImagePath = prepared;
                    tempFiles.Add(workingImagePath);
                }

                string outputName = LivePhotoCompositionService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = Path.Combine(options.OutputDirectory, outputName);

                await LivePhotoCompositionService.WriteLivePhotoAsync(workingImagePath, workingVideoPath, finalOutputPath, options.SelectedModeIndex, token);

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
                foreach (var f in tempFiles)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }
}

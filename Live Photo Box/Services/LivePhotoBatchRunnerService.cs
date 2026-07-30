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
    // 批量测试处理的运行配置项。
    public sealed class LivePhotoBatchRunOptions
    {
        // 输出目录路径。
        public required string OutputDirectory { get; init; }
        // 选中的合成协议索引。
        public required int SelectedModeIndex { get; init; }
        // 最大并行任务数（默认取 CPU 核心数与 5 的较小值）。
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        // 每批任务启动的间隔时间。
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    }

    // 批量测试处理运行器。
    // 用于开发测试场景，按批并行处理 MergeTask 列表，
    // 与 <see cref="LivePhotoMergeRunnerService"/> 逻辑相似但更简洁。
    public static class LivePhotoBatchRunnerService
    {
        // 批量运行实况照片合成任务。
        // 将任务分批并行执行，支持暂停/取消/进度回调。
        // tasks: 待处理的合成任务集合。
        // options: 运行配置（输出目录、协议索引、并行度等）。
        // pauseEvent: 暂停信号量，Wait 阻塞直到 Set。
        // cancellationToken: 取消令牌。
        // onTaskStarted: 每个任务开始时的回调。
        // onTaskCompleted: 每个任务完成时的回调（参数：task, success, details, completedCount）。
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
                catch (Exception ex) { LogService.Merge($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }
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
                    if (protocol is MotionPhotoV2Protocol or HuaweiMovingPhotoProtocol)
                    {
                        // Native HEIC path — no JPEG conversion needed.
                        // V2: XMP + mpvd box.  HUAWEI: ftyp tmap patch + LIVE_ tail.
                    }
                    else
                    {
                        workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, tempDir, token);
                        tempFiles.Add(workingImagePath);
                    }
                }

                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(videoPath, tempDir, token);
                if (vt) tempFiles.Add(workingVideoPath);

                string prepared = await protocol.PrepareImageAsync(workingImagePath, tempDir, token);
                if (prepared != workingImagePath)
                {
                    workingImagePath = prepared;
                    tempFiles.Add(workingImagePath);
                }

                string outputName = LivePhotoCompositionService.CreateOutputFileName(baseName, options.SelectedModeIndex, imagePath);
                string finalOutputPath = PathHelper.GetUniqueFilePath(options.OutputDirectory, outputName);

                await LivePhotoCompositionService.WriteLivePhotoAsync(workingImagePath, workingVideoPath, finalOutputPath, options.SelectedModeIndex, token);

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
    }
}
